﻿using DigiMixer.Core;
using DigiMixer.QuSeries.Core;
using Microsoft.Extensions.Logging;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;

namespace DigiMixer.QuSeries;

public class QuMixer
{
    public static IMixerApi CreateMixerApi(ILogger logger, string host, int port = 51326) =>
        new AutoReceiveMixerApi(new QuMixerApi(logger, host, port));
}

internal class QuMixerApi : IMixerApi
{
    /// <summary>
    /// The output channel ID for the left side of the main output.
    /// </summary>
    internal static ChannelId MainOutputLeft { get; } = ChannelId.Output(100);

    /// <summary>
    /// The output channel ID for the right side of the main output.
    /// </summary>
    internal static ChannelId MainOutputRight { get; } = ChannelId.Output(101);

    private readonly DelegatingReceiver receiver = new();
    private readonly ILogger logger;
    private readonly string host;
    private readonly int port;
    private readonly LinkedList<PacketListener> temporaryListeners = new();
    private readonly LinkedList<Action<QuControlPacket>> packetHandlers = new();

    private CancellationTokenSource? cts;
    private QuControlClient? controlClient;
    private QuMeterClient? meterClient;
    private Task? controlClientTask;
    private Task? meterClientTask;
    private int? mixerUdpPort;

    private MixerInfo currentMixerInfo = new MixerInfo(null, null, null);

    internal QuMixerApi(ILogger logger, string host, int port)
    {
        this.logger = logger;
        this.host = host;
        this.port = port;
        AddPacketHandlers();
    }

    public async Task Connect()
    {
        Dispose();

        cts = new CancellationTokenSource();
        meterClient = new QuMeterClient(logger, host);
        meterClient.PacketReceived += HandleMeterPacket;
        meterClientTask = meterClient.Start();
        controlClient = new QuControlClient(logger, host, port);
        controlClient.PacketReceived += HandleControlPacket;
        controlClientTask = controlClient.Start();

        await controlClient.SendAsync(QuPackets.InitialHandshakeRequest(meterClient.LocalUdpPort), cts.Token);
        await controlClient.SendAsync(QuPackets.RequestControlPackets, cts.Token);
    }

    public async Task<MixerChannelConfiguration> DetectConfiguration()
    {
        var packet = await RequestData(QuPackets.RequestFullData, QuPackets.FullDataType, TimeSpan.FromSeconds(2));
        var data = new FullDataPacket(packet);

        var inputs = Enumerable.Range(1, data.InputCount).Select(i => ChannelId.Input(i));
        var outputs = Enumerable.Range(1, data.MixCount).Select(i => ChannelId.Output(i))
            .Append(MainOutputLeft).Append(MainOutputRight);

        var stereoPairs = new List<StereoPair>();
        for (int i = 1; i <= data.InputCount; i+=2)
        {
            if (data.InputLinked(i))
            {
                stereoPairs.Add(new StereoPair(ChannelId.Input(i), ChannelId.Input(i + 1), StereoFlags.FullyIndependent));
            }
        }
        stereoPairs.Add(new StereoPair(MainOutputLeft, MainOutputRight, StereoFlags.None));
        return new MixerChannelConfiguration(inputs, outputs, stereoPairs);
    }

    public void RegisterReceiver(IMixerReceiver receiver) => this.receiver.RegisterReceiver(receiver);

    public async Task RequestAllData(IReadOnlyList<ChannelId> channelIds)
    {
        await SendPacket(QuPackets.RequestNetworkInformation);
        await SendPacket(QuPackets.RequestVersionInformation);
        await SendPacket(QuPackets.RequestFullData);
    }

    public async Task SendKeepAlive()
    {
        if (meterClient is not null && cts is not null && mixerUdpPort is int port)
        {
            await meterClient.SendKeepAliveAsync(port, cts.Token);
        }
    }

    public async Task SetFaderLevel(ChannelId inputId, ChannelId outputId, FaderLevel level)
    {
        int address = outputId == MainOutputLeft
            ? 0x07_00_04_07 | ((inputId.Value - 1) << 16)
            : (outputId.Value - 1) << 24 | ((inputId.Value - 1) << 16) | 0x0c_0a;
        var packet = new QuValuePacket(4, 4, address, QuConversions.FaderLevelToRaw(level));
        await SendPacket(packet);
    }

    public async Task SetFaderLevel(ChannelId outputId, FaderLevel level)
    {
        int networkChannelId = QuConversions.ChannelIdToNetwork(outputId);
        int address = 0x07_00_04_07 | (networkChannelId << 16);
        var packet = new QuValuePacket(4, 4, address, QuConversions.FaderLevelToRaw(level));
        await SendPacket(packet);
    }

    public async Task SetMuted(ChannelId channelId, bool muted)
    {
        int networkChannelId = QuConversions.ChannelIdToNetwork(channelId);
        int address = 0x07_00_00_06 | (networkChannelId << 16);
        var packet = new QuValuePacket(4, 4, address, (ushort) (muted ? 1 : 0));
        await SendPacket(packet);
    }

    private async Task<QuGeneralPacket> RequestData(QuControlPacket requestPacket, byte expectedResponseType, TimeSpan timeout)
    {
        if (controlClient is null || cts is null)
        {
            throw new InvalidOperationException("Not connected");
        }
        // TODO: thread safety...
        var listener = new PacketListener(packet => packet is QuGeneralPacket qgp && qgp.Type == expectedResponseType, timeout);
        temporaryListeners.AddLast(listener);
        await controlClient.SendAsync(requestPacket, cts.Token);
        return (QuGeneralPacket) await listener.Task;
    }

    private async Task SendPacket(QuControlPacket packet)
    {
        if (controlClient is not null)
        {
            await controlClient.SendAsync(packet, cts?.Token ?? default);
        }
    }

    private void HandleControlPacket(object? sender, QuControlPacket packet)
    {
        if (QuPackets.IsInitialHandshakeResponse(packet, out var mixerUdpPort))
        {
            this.mixerUdpPort = mixerUdpPort;
            return;
        }
        var node = temporaryListeners.First;
        while (node is not null)
        {
            if (node.Value.HandlePacket(packet))
            {
                node.Value.Dispose();
                temporaryListeners.Remove(node);
            }
            node = node.Next;
        }
        foreach (var handler in packetHandlers)
        {
            handler(packet);
        }
    }

    private void HandleMeterPacket(object? sender, QuGeneralPacket packet)
    {
        if (packet.Type == QuPackets.InputMeterType)
        {
            var data = packet.Data;
            // TODO: Don't hard code this. (Where should we remember it?)
            var meters = new (ChannelId, MeterLevel)[32];
            for (int i = 0; i < meters.Length; i++)
            {
                var slice = data.Slice(i * 20, 20);
                meters[i] = (ChannelId.Input(i + 1), QuConversions.RawToMeterLevel(MemoryMarshal.Read<ushort>(slice.Slice(6, 2))));
            }
            receiver.ReceiveMeterLevels(meters);
        }
        else if (packet.Type == QuPackets.OutputMeterType)
        {
            var data = packet.Data;
            // TODO: Don't hard code this. (Where should we remember it?)
            var meters = new (ChannelId, MeterLevel)[6];
            for (int i = 0; i < 4; i++)
            {
                var slice = data.Slice(i * 20, 20);
                meters[i] = (ChannelId.Output(i + 1), QuConversions.RawToMeterLevel(MemoryMarshal.Read<ushort>(slice.Slice(10, 2))));
            }
            // TODO: Stereo meters
            meters[4] = (MainOutputLeft, QuConversions.RawToMeterLevel(MemoryMarshal.Read<ushort>(data.Slice(200, 20).Slice(10, 2))));
            meters[5] = (MainOutputRight, QuConversions.RawToMeterLevel(MemoryMarshal.Read<ushort>(data.Slice(220, 20).Slice(10, 2))));
            receiver.ReceiveMeterLevels(meters);
        }
    }

    private void HandleFullDataPacket(QuGeneralPacket packet)
    {
        var data = new FullDataPacket(packet);
        for (int channel = 1; channel <= data.InputCount; channel++)
        {
            var channelId = ChannelId.Input(channel);
            receiver.ReceiveMuteStatus(channelId, data.InputMuted(channel));
            receiver.ReceiveChannelName(channelId, data.GetInputName(channel));
            receiver.ReceiveFaderLevel(channelId, MainOutputLeft, data.InputFaderLevel(channel));

            for (int mix = 1; mix <= data.MixCount; mix++)
            {
                var mixId = ChannelId.Output(mix);
                receiver.ReceiveFaderLevel(channelId, mixId, data.InputMixFaderLevel(channel, mix));
            }
        }

        for (int mix = 1; mix <= data.MixCount; mix++)
        {
            var mixId = ChannelId.Output(mix);
            receiver.ReceiveMuteStatus(mixId, data.MixMuted(mix));
            receiver.ReceiveChannelName(mixId, data.GetMixName(mix));
            receiver.ReceiveFaderLevel(mixId, data.MixFaderLevel(mix));
        }

        receiver.ReceiveMuteStatus(MainOutputLeft, data.MainMuted());
        receiver.ReceiveChannelName(MainOutputLeft, data.GetMainName() ?? "Main");
        receiver.ReceiveFaderLevel(MainOutputLeft, data.MainFaderLevel());
    }

    private void HandleNetworkInformationPacket(QuGeneralPacket packet)
    {
        // Network information packet:
        // IP address (4 bytes)
        // Subnet mask (4 bytes)
        // Gateway (4 bytes)
        // DHCP enabled (1 byte)
        // Name (15 bytes)
        // ???? (4 bytes)
        var data = packet.Data;
        string name = Encoding.ASCII.GetString(data.Slice(13, 15));
        currentMixerInfo = new MixerInfo(currentMixerInfo.Model, name, currentMixerInfo.Version);
        receiver.ReceiveMixerInfo(currentMixerInfo);
    }

    private void HandleVersionInformationPacket(QuGeneralPacket packet)
    {
        // Note: the model is probably encoded in the first two bytes, but that's hard to check...
        var data = packet.Data;
        int firmwareMajor = data[3];
        int firmwareMinor = data[2];
        ushort revision = MemoryMarshal.Read<ushort>(data.Slice(4, 2));
        currentMixerInfo = new MixerInfo("Qu-???", currentMixerInfo.Name, $"{firmwareMajor}.{firmwareMinor} rev {revision}");
        receiver.ReceiveMixerInfo(currentMixerInfo);
    }

    private void HandleValuePacket(QuValuePacket packet)
    {
        // logger.LogInformation("Received value packet: Client: {client}; Section: {section}; Address:{address}; Value:{value}", packet.ClientId, packet.Section, $"0x{packet.Address:x8}", packet.RawValue);
        
        // Fader and mute
        if (packet.Section == 4 && (packet.Address & 0xff_00_00_00) == 0x07_00_00_00)
        {
            int networkChannel = (packet.Address & 0x00_ff_00_00) >> 16;
            ChannelId? possibleChannelId = QuConversions.NetworkToChannelId(networkChannel);
            if (possibleChannelId is not ChannelId channelId)
            {
                return;
            }
            if ((packet.Address & 0xff) == 0x07)
            {
                if (channelId.IsInput)
                {
                    receiver.ReceiveFaderLevel(channelId, MainOutputLeft, QuConversions.RawToFaderLevel(packet.RawValue));
                }
                else
                {
                    receiver.ReceiveFaderLevel(channelId, QuConversions.RawToFaderLevel(packet.RawValue));
                }
            }
            else if ((packet.Address & 0xff) == 0x06)
            {
                receiver.ReceiveMuteStatus(channelId, packet.RawValue == 1);
            }
        }
        else if (packet.Section == 4 && (packet.Address & 0xff_ff) == 0x0c_0a)
        {
            int mix = (packet.Address >> 24) + 1;
            int input = ((packet.Address & 0xff_00_00) >> 16) + 1;
            receiver.ReceiveFaderLevel(ChannelId.Input(input), ChannelId.Output(mix), QuConversions.RawToFaderLevel(packet.RawValue));
        }
    }

    private void AddPacketHandlers()
    {
        AddGeneralHandlerForType(QuPackets.FullDataType, HandleFullDataPacket);
        AddGeneralHandlerForType(QuPackets.NetworkInformationType, HandleNetworkInformationPacket);
        AddGeneralHandlerForType(QuPackets.VersionInformationType, HandleVersionInformationPacket);
        packetHandlers.AddLast(packet =>
        {
            if (packet is QuValuePacket qvp)
            {
                HandleValuePacket(qvp);
            }
        });

        void AddGeneralHandlerForType(byte type, Action<QuGeneralPacket> action)
        {
            packetHandlers.AddLast(packet =>
            {
                if (packet is QuGeneralPacket general && general.Type == type)
                {
                    action(general);
                }
            });
        }
    }

    public void Dispose()
    {
        controlClient?.Dispose();
        controlClient = null;
        controlClientTask = null;
        meterClient?.Dispose();
        meterClient = null;
        meterClientTask = null;
        mixerUdpPort = null;
    }

    private class PacketListener : IDisposable
    {
        private readonly TaskCompletionSource<QuControlPacket> tcs;
        private readonly CancellationTokenSource cts;
        private readonly Func<QuControlPacket, bool> predicate;
        private readonly CancellationTokenRegistration ctr;

        internal Task<QuControlPacket> Task => tcs.Task;

        internal PacketListener(Func<QuControlPacket, bool> predicate, TimeSpan timeout)
        {
            tcs = new TaskCompletionSource<QuControlPacket>();
            cts = new CancellationTokenSource(timeout);
            ctr = cts.Token.Register(() => tcs.TrySetCanceled());
            this.predicate = predicate;
        }

        internal bool HandlePacket(QuControlPacket packet)
        {
            // If we've already cancelled the task, the listener is done.
            if (tcs.Task.IsCanceled)
            {
                return true;
            }
            if (!predicate(packet))
            {
                return false;
            }
            tcs.TrySetResult(packet);
            return true;
        }

        public void Dispose()
        {
            ctr.Unregister();
            cts.Dispose();
        }
    }
}
