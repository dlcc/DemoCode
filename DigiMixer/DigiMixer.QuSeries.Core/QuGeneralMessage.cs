﻿using DigiMixer.Core;
using System.Buffers.Binary;

namespace DigiMixer.QuSeries.Core;

/// <summary>
/// A general-purpose message from/to a Qu mixer, with a type and arbitrary-length data.
/// </summary>
public class QuGeneralMessage : QuControlMessage
{
    public byte Type { get; }
    private readonly byte[] data;
    public ReadOnlySpan<byte> Data => data;

    public override int Length => data.Length + 4;

    internal QuGeneralMessage(byte type, byte[] data)
    {
        Type = type;
        this.data = data;
    }

    public override void CopyTo(Span<byte> buffer)
    {
        buffer[0] = 0x7f;
        buffer[1] = Type;
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.Slice(2), (ushort) data.Length);
        data.CopyTo(buffer.Slice(4));
    }

    private const int FullDataLength = 10;
    public override string ToString()
    {
        var hexSpan = Data.Length <= FullDataLength ? Data : Data.Slice(0, FullDataLength);
        string dataDescription = Formatting.ToHex(hexSpan) + (Data.Length <= FullDataLength ? "" : $"... ({Data.Length} bytes)");
        return $"General: {Type}: {dataDescription}";
    }

    internal bool HasNonZeroData()
    {
        for (int i = 0; i < data.Length; i++)
        {
            if (data[i] != 0)
            {
                return true;
            }
        }
        return false;
    }
}
