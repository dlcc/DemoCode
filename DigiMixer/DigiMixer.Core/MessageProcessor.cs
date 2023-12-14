﻿namespace DigiMixer.Core;

/// <summary>
/// Message processor with internal buffer, used to handle incoming TCP streams.
/// </summary>
public sealed class MessageProcessor<TMessage> where TMessage : class
{
    public delegate TMessage? Parser(ReadOnlySpan<byte> data);

    private readonly Parser messageParser;
    private readonly Func<TMessage, int> messageLengthExtractor;
    private readonly Func<TMessage, CancellationToken, Task> messageAction;
    private readonly Memory<byte> buffer;

    /// <summary>
    /// The amount of unprocessed data left in the buffer.
    /// </summary>
    public int UnprocessedLength { get; private set; }

    /// <summary>
    /// The total number of messages processed.
    /// </summary>
    public long MessagesProcessed { get; private set; }

    public MessageProcessor(Parser messageParser, Func<TMessage, int> messageLengthExtractor, Func<TMessage, CancellationToken, Task> messageAction, int bufferSize = 65540)
    {
        this.messageParser = messageParser;
        this.messageLengthExtractor = messageLengthExtractor;
        this.messageAction = messageAction;
        buffer = new byte[bufferSize];
    }

    public MessageProcessor(Parser messageParser, Func<TMessage, int> messageLengthExtractor, Action<TMessage> messageAction, int bufferSize = 65540)
        : this(messageParser, messageLengthExtractor, (message, cancellationToken) => { messageAction(message); return Task.CompletedTask; }, bufferSize)
    {
    }

    /// <summary>
    /// Synchronously processes the data from <paramref name="data"/>, retaining any data
    /// that isn't part of a message. The data may contain multiple messages, and each will be
    /// processed separately.
    /// </summary>
    /// <remarks>
    /// This is currently synchronous, which seems to be "okay"; we could potentially change it to be asynchronous
    /// later.
    /// </remarks>
    public async Task Process(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        data.CopyTo(buffer.Slice(UnprocessedLength));
        UnprocessedLength += data.Length;
        int start = 0;
        while (messageParser(buffer.Slice(start, UnprocessedLength - start).Span) is TMessage message)
        {
            MessagesProcessed++;
            await messageAction(message, cancellationToken);
            start += messageLengthExtractor(message);
        }
        // If we've consumed the whole buffer, reset to the start. (No copying required.)
        if (start == UnprocessedLength)
        {
            UnprocessedLength = 0;
        }
        // Otherwise, copy whatever's left.
        else
        {
            buffer.Slice(start, UnprocessedLength - start).CopyTo(buffer);
            UnprocessedLength -= start;
        }
    }
}
