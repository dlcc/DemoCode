﻿namespace DigiMixer.UCNet.Core.Messages;

public class UdpMetersMessage : UCNetMessage
{
    public int MeterPort { get; }

    public UdpMetersMessage(int meterPort, MessageMode mode = MessageMode.UdpMeters) : base(mode)
    {
        MeterPort = meterPort;
    }

    public override MessageType Type => MessageType.UdpMeters;

    protected override int BodyLength => 8;

    protected override void WriteBody(Span<byte> span)
    {
        span.WriteUInt16((ushort) MeterPort);
    }

    internal static UdpMetersMessage FromRawBody(MessageMode mode, ReadOnlySpan<byte> body) =>
        new UdpMetersMessage(body.ReadUInt16(), mode);

    public override string ToString() => $"UdpMeters: Port={MeterPort}";
}
