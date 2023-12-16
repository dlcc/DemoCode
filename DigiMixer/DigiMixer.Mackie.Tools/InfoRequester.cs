﻿using DigiMixer.Core;
using DigiMixer.Diagnostics;
using DigiMixer.Mackie.Core;
using Google.Protobuf;
using Microsoft.Extensions.Logging.Abstractions;

namespace DigiMixer.Mackie.Tools;

public class InfoRequester(string Address, string Port, string File, string InfoItems) : Tool
{
    public override async Task<int> Execute()
    {
        string address = Address;
        int port = int.Parse(Port);
        string file = File;
        List<byte> infoItems = InfoItems.Split('-').Select(byte.Parse).ToList();

        MessageCollection mc = new MessageCollection();
        using var controller = new MackieController(NullLogger.Instance, address, port);
        controller.MessageSent += (sender, message) => RecordMessage(message, true);
        controller.MessageReceived += (sender, message) => RecordMessage(message, false);

        controller.MapCommand(MackieCommand.ClientHandshake, _ => new byte[] { 0x10, 0x40, 0xf0, 0x1d, 0xbc, 0xa2, 0x88, 0x1c });
        controller.MapCommand(MackieCommand.GeneralInfo, _ => new byte[] { 0, 0, 0, 2, 0, 0, 0x40, 0 });
        controller.MapCommand(MackieCommand.ChannelInfoControl, message => new MackieMessageBody(message.Body.Data.Slice(0, 4)));
        await controller.Connect(default);
        controller.Start();

        // From MackieMixerApi.Connect
        CancellationToken cancellationToken = default;
        await controller.SendRequest(MackieCommand.KeepAlive, MackieMessageBody.Empty, cancellationToken);
        await controller.SendRequest(MackieCommand.ClientHandshake, MackieMessageBody.Empty, cancellationToken);

        foreach (var item in infoItems)
        {
            try
            {
                await controller.SendRequest(MackieCommand.GeneralInfo, new byte[] { 0, 0, 0, item }, cancellationToken);
            }
            catch (MackieResponseException)
            {
                Console.WriteLine($"Request failed - error response received.");
            }
        }

        Console.WriteLine($"Captured {mc.Messages.Count} mesages");

        using var output = System.IO.File.Create(file);
        mc.WriteTo(output);

        void RecordMessage(MackieMessage message, bool outbound)
        {
            mc.Messages.Add(Message.FromMackieMessage(message, outbound, null));
            // Immediate uninterpreted display, truncated after 16 bytes of data.
            var padding = outbound ? "" : "    ";
            if (message.Body.Data.Length == 0)
            {
                Console.WriteLine($"{DateTime.UtcNow:HH:mm:ss.ffffff} {padding} {message.Sequence} {message.Type} {message.Command} (empty)");
            }
            else
            {
                var dataLength = $"({message.Body.Data.Length} bytes)";
                var data = Formatting.ToHex(message.Body.Data);
                if (data.Length > 47)
                {
                    data = data.Substring(0, 47) + "...";
                }
                Console.WriteLine($"{DateTime.UtcNow:HH:mm:ss.ffffff} {padding} {message.Sequence} {message.Type} {message.Command}: {dataLength}: {data}");
            }
        }
        return 0;
    }
}
