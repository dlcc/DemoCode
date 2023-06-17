﻿using System.Runtime.InteropServices;

namespace DigiMixer.QuSeries.Core;

/// <summary>
/// A message with a single 16-bit value, either directly from a client to the mixer,
/// or from the mixer broadcasting a client's message to other clients.
/// </summary>
public class QuValueMessage : QuControlMessage
{
    public byte ClientId { get; }
    public byte Section { get; }
    public ushort RawValue { get; }
    public int Address { get; }

    public QuValueMessage(byte clientId, byte section, int address, ushort rawValue) =>
        (ClientId, Section, Address, RawValue) = (clientId, section, address, rawValue);

    internal QuValueMessage(ReadOnlySpan<byte> data) : this(
        data[0],
        data[1],
        MemoryMarshal.Cast<byte, int>(data.Slice(2))[0],
        MemoryMarshal.Cast<byte, ushort>(data.Slice(6))[0])
    {
    }

    public override int Length => 9;

    public override byte[] ToByteArray()
    {
        var message = new byte[Length];
        message[0] = 0xf7;
        message[1] = ClientId;
        message[2] = Section;
        var span = message.AsSpan();
        // Note offsets of 3 and 7 rather than 2 and 6, as the message includes the fixed-length indicator.
        MemoryMarshal.Cast<byte, int>(span.Slice(3))[0] = Address;
        MemoryMarshal.Cast<byte, ushort>(span.Slice(7))[0] = RawValue;
        return message;
    }

    public override string ToString() =>
        $"Value: Client: {ClientId:X2}; Section: {Section:X2}; Address: {Address:X8}; RawValue: {RawValue:X4}";
}
