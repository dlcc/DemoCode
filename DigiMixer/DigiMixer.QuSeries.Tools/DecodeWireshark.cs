﻿// See https://aka.ms/new-console-template for more information
using DigiMixer.Core;
using DigiMixer.Diagnostics;
using DigiMixer.QuSeries.Core;
using System.Net;
using System.Net.Sockets;

public class DecodeWireshark : Tool
{
    public override async Task<int> Execute()
    {
        var file = @"c:\users\skeet\Downloads\Wireshark\test1.pcapng";

        var dump = WiresharkDump.Load(file);
        var packets = dump.IPV4Packets.ToList();
        Console.WriteLine($"Packets: {packets.Count}");

        var clientAddr = IPAddress.Parse("192.168.1.221");
        var mixerAddr = IPAddress.Parse("192.168.1.60");

        var controlPackets = packets.Where(ClientMixerPacket).Where(packet => packet.Type == ProtocolType.Tcp).ToList();

        /*
        foreach (var cp in controlPackets)
        {
            Console.WriteLine(cp);
        }*/

        Console.WriteLine($"Control packets: {controlPackets.Count}");

        var clientProcessor = new MessageProcessor<QuControlMessage>(
            quMessage => LogMessage("Mixer->Client", quMessage),
            65540);
        var mixerProcessor = new MessageProcessor<QuControlMessage>(
            quMessage => LogMessage("Client->Mixer", quMessage),
            65540);

        foreach (var packet in controlPackets)
        {
            var processor = packet.Dest.Address.Equals(clientAddr)
                ? clientProcessor : mixerProcessor;
            await processor.Process(packet.Data, default);
        }
        return 0;

        void LogMessage(string description, QuControlMessage message) => Console.Write(message);

        bool ClientMixerPacket(IPV4Packet packet) =>
            (packet.Source.Address.Equals(clientAddr) || packet.Source.Address.Equals(mixerAddr)) &&
            (packet.Dest.Address.Equals(clientAddr) || packet.Dest.Address.Equals(mixerAddr));
    }
}