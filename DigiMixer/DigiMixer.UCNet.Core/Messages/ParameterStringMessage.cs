﻿using System.Text;

namespace DigiMixer.UCNet.Core.Messages;

public class ParameterStringMessage : UCNetMessage
{
    public string Key { get; }
    public string Value { get; }

    public ParameterStringMessage(string key, string value, MessageMode mode = MessageMode.FileRequest) : base(mode)
    {
        Key = key;
        Value = value;
    }

    protected override int BodyLength => Encoding.UTF8.GetByteCount(Key) + 3 + Encoding.UTF8.GetByteCount(Value);

    public override MessageType Type => MessageType.ParameterValue;

    protected override void WriteBody(Span<byte> span)
    {
        int keyLength = Encoding.UTF8.GetBytes(Key, span);
        Encoding.UTF8.GetBytes(Value, span.Slice(keyLength + 3));
    }

    public static ParameterStringMessage FromRawBody(MessageMode mode, ReadOnlySpan<byte> body)
    {
        int endOfKey = body.IndexOf((byte) 0);
        if (endOfKey == -1)
        {
            throw new ArgumentException("End of key not found in parameter value message .");
        }
        string key = Encoding.UTF8.GetString(body[..endOfKey]);
        string value = Encoding.UTF8.GetString(body[(endOfKey + 3)..]);
        return new ParameterStringMessage(key, value, mode);
    }
    public override string ToString() => $"ParameterString: {Key}={Value}";
}
