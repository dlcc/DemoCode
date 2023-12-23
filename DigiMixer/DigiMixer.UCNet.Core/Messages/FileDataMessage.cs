﻿using DigiMixer.Core;

namespace DigiMixer.UCNet.Core.Messages;

public class FileDataMessage : UCNetMessage
{
    public ushort Id { get; }
    public ushort Offset { get; }
    public uint TotalSize { get; }

    private readonly byte[] data;
    public ReadOnlySpan<byte> Data => data;

    public FileDataMessage(ushort id, uint totalSize, ushort offset, byte[] data, MessageMode mode = MessageMode.FileRequest) : base(mode)
    {
        Id = id;
        TotalSize = totalSize;
        Offset = offset;
        this.data = data;
    }

    public override MessageType Type => MessageType.FileData;

    protected override int BodyLength => data.Length + 14;

    protected override void WriteBody(Span<byte> span)
    {
        throw new NotImplementedException();
    }

    internal static FileDataMessage FromRawBody(MessageMode mode, ReadOnlySpan<byte> body)
    {
        ushort id = LittleEndian.ReadUInt16(body);
        ushort offset = LittleEndian.ReadUInt16(body.Slice(2));
        ushort unknown1 = LittleEndian.ReadUInt16(body.Slice(4));
        ushort totalSize = LittleEndian.ReadUInt16(body.Slice(6));
        uint unknown2 = LittleEndian.ReadUInt32(body.Slice(8));
        ushort chunkSize = LittleEndian.ReadUInt16(body.Slice(12));

        if (body.Length != chunkSize + 14)
        {
            throw new InvalidDataException($"Invalid FileData message: chunk data size claimed to be {chunkSize} but packet body is {body.Length} bytes (with 14 byte chunk header)");
        }

        return new FileDataMessage(id, totalSize, offset, body.Slice(14).ToArray(), mode);
    }

    public override string ToString() => $"FileData: ID={Id}; TotalSize={TotalSize}; Offset={Offset}; Length={data.Length}";
}

