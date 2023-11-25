﻿using DigiMixer.Core;

namespace DigiMixer.UCNet.Core.Messages;

public abstract class MeterMessageBase<T> : UCNetMessage where T : struct
{
    public string MeterType { get; }
    private readonly byte[] data;
    private readonly byte[] rowMappingData;

    public ReadOnlySpan<byte> Data => data;
    public ReadOnlySpan<byte> RowMappingData => rowMappingData;

    public int RowCount => rowMappingData.Length / 6;
    public IEnumerable<MeterMessageRow<ushort>> Rows =>
        Enumerable.Range(0, RowCount)
            .Select(index => new MeterMessageRow<ushort>(rowMappingData, index, x => Data.Slice(x * 2).ReadUInt16()));

    protected MeterMessageBase(string meterType, byte[] data, byte[] rowMappingData, MessageMode mode) : base(mode)
    {
        this.MeterType = meterType;
        this.data = data;
        this.rowMappingData = rowMappingData;
    }

    protected abstract int ValueSize { get; }
    protected abstract T ReadValue(int index);

    protected override int BodyLength => data.Length + rowMappingData.Length + 9;

    protected override void WriteBody(Span<byte> span)
    {
        int typeLength = span.WriteString(MeterType);
        int dataStart = typeLength + 2;
        span.Slice(dataStart).WriteUInt16((ushort) (data.Length / ValueSize));
        span.Slice(dataStart + 2).WriteBytes(data);
        int rowMappingStart = dataStart + 2 + data.Length;
        span[rowMappingStart] = (byte) RowCount;
        span.Slice(rowMappingStart + 1).WriteBytes(rowMappingData);
    }

    public override string ToString() => $"{Type}: Type={MeterType}: Rows: {RowCount}; Data length={Data.Length}: {Formatting.ToHex(data)}";
}
