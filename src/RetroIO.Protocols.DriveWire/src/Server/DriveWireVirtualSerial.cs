//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

namespace RetroIO.Protocols.DriveWire.Server;

/// <summary>
/// The virtual serial channels of one client connection: the byte queues behind OP_SERREAD, OP_SERREADM, OP_SERWRITE,
/// OP_FASTWRITE and OP_SERWRITEM.
/// </summary>
/// <remarks>
/// <para>
/// Source: "DriveWire Protocol Version 4.0.0", "Virtual Serial Channels". The poll answer (OP_SERREAD) gives a channel as
/// "relative channel number + 1" in the low four bits of the first byte, values 1 to 15, so the poll can name channels 0
/// to 14 only. This class has those 15 channels. Data written by the client to any other channel number (the virtual window
/// channels 128 to 142, or a FASTWRITE to channel 15) is counted in <see cref="DroppedBytes"/> and not kept.
/// </para>
/// <para>
/// This class is the transport only. What a channel is connected to on the server side is the host's business: the host
/// gets the client's output through <see cref="OutputReceived"/> and sends input with <see cref="Send"/>. The
/// specification's "virtual modem" (Hayes AT commands, the 'dw' command set, TCP connect and listen) is not part of this
/// package.
/// </para>
/// <para>
/// The NitrOS-9 boot does not need any of this: the DriveWire boot list (level2/coco3/bootlists/dw.bl) has no virtual
/// serial descriptors. The interrupt poller in dwio.asm still sends OP_SERREAD regularly once the server has answered
/// OP_DWINIT with 4, and an empty poll answer (0, 0) satisfies it.
/// </para>
/// </remarks>
public sealed class DriveWireVirtualSerial
{
    /// <summary>
    /// The number of virtual serial channels, 15 (channels 0 to 14).
    /// </summary>
    public const int ChannelCount = 15;

    /// <summary>
    /// SetStat code SS.ComSt ($28), after which OP_SERSETSTAT carries 26 more bytes (NitrOS-9 defs/os9.d; the specification,
    /// "The SERSETSTAT Transaction").
    /// </summary>
    public const byte SetStatComSt = 0x28;

    /// <summary>
    /// SetStat code SS.Open ($29), which opens the channel.
    /// </summary>
    public const byte SetStatOpen = 0x29;

    /// <summary>
    /// SetStat code SS.Close ($2A), which closes the channel.
    /// </summary>
    public const byte SetStatClose = 0x2A;

    /// <summary>
    /// The number of descriptor bytes that follow SS.ComSt in OP_SERSETSTAT, 26.
    /// </summary>
    public const int ComStBytes = 26;

    private readonly object _gate = new object();
    private readonly Queue<byte>[] _input = new Queue<byte>[ChannelCount];
    private readonly bool[] _open = new bool[ChannelCount];
    private readonly bool[] _closeNotice = new bool[ChannelCount];
    private int _nextPoll;
    private long _droppedBytes;

    /// <summary>
    /// Creates 15 closed, empty channels.
    /// </summary>
    public DriveWireVirtualSerial()
    {
        for (int i = 0; i < ChannelCount; i++)
        {
            _input[i] = new Queue<byte>();
        }
    }

    /// <summary>
    /// Raised when the client writes bytes to a channel. The arguments are the channel number and the bytes.
    /// </summary>
    public event Action<int, byte[]>? OutputReceived;

    /// <summary>
    /// Raised when the client opens (true) or closes (false) a channel.
    /// </summary>
    public event Action<int, bool>? ChannelStateChanged;

    /// <summary>
    /// Gets the bytes the client wrote to channels this class does not have.
    /// </summary>
    public long DroppedBytes => Interlocked.Read(ref _droppedBytes);

    /// <summary>
    /// Gets whether the client has opened a channel.
    /// </summary>
    /// <param name="channel">
    /// The channel number, 0 to 14.
    /// </param>
    /// <returns>
    /// True when open.
    /// </returns>
    public bool IsOpen(int channel)
    {
        lock (_gate)
        {
            return channel >= 0 && channel < ChannelCount && _open[channel];
        }
    }

    /// <summary>
    /// Gets the number of input bytes waiting for the client on a channel.
    /// </summary>
    /// <param name="channel">
    /// The channel number, 0 to 14.
    /// </param>
    /// <returns>
    /// The number of bytes.
    /// </returns>
    public int Pending(int channel)
    {
        lock (_gate)
        {
            return channel >= 0 && channel < ChannelCount ? _input[channel].Count : 0;
        }
    }

    /// <summary>
    /// Queues input bytes for the client on a channel. The client fetches them with its next polls.
    /// </summary>
    /// <param name="channel">
    /// The channel number, 0 to 14.
    /// </param>
    /// <param name="data">
    /// The bytes.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when the channel number is not 0 to 14.
    /// </exception>
    public void Send(int channel, ReadOnlySpan<byte> data)
    {
        CheckChannel(channel);
        lock (_gate)
        {
            for (int i = 0; i < data.Length; i++)
            {
                _input[channel].Enqueue(data[i]);
            }
        }
    }

    /// <summary>
    /// Closes a channel from the server side. The client is told with the next poll, by the "channel closed" status.
    /// </summary>
    /// <param name="channel">
    /// The channel number, 0 to 14.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when the channel number is not 0 to 14.
    /// </exception>
    public void Close(int channel)
    {
        CheckChannel(channel);
        lock (_gate)
        {
            _closeNotice[channel] = true;
        }
    }

    /// <summary>
    /// Builds the two-byte answer to OP_SERREAD.
    /// </summary>
    /// <param name="answer">
    /// Receives the two bytes.
    /// </param>
    /// <remarks>
    /// <para>
    /// From the table in "The SERREAD / POLL Transaction": first byte 0 means nothing is waiting; 1 to 15 means the second
    /// byte is one data byte for channel (first byte - 1); 16 means the second byte is a status byte, whose high four bits
    /// 0000 mean "channel closed" and low four bits the channel; 17 to 31 means the second byte is the number of bytes
    /// waiting on channel (first byte - 17), to be fetched with OP_SERREADM.
    /// </para>
    /// <para>
    /// A pending close notice goes first. Then the channels with input are served in turn, starting after the one served
    /// last, so one busy channel cannot hold back the others. The specification describes the same aim ("This prevents a
    /// single channel from monopolizing the serial line") with a wait counter; taking the channels in turn is the simplest
    /// order that meets it. A single waiting byte is sent in the poll answer itself; more than one is announced with its
    /// count, at most 255.
    /// </para>
    /// <para>
    /// A data byte taken off the queue by this answer is gone; if the client misses the answer, the byte is lost, as the
    /// specification's one-shot poll implies.
    /// </para>
    /// </remarks>
    public void BuildPollAnswer(Span<byte> answer)
    {
        lock (_gate)
        {
            for (int i = 0; i < ChannelCount; i++)
            {
                if (_closeNotice[i])
                {
                    _closeNotice[i] = false;
                    _open[i] = false;
                    _input[i].Clear();
                    answer[0] = 16;
                    answer[1] = (byte)i;
                    return;
                }
            }

            for (int step = 0; step < ChannelCount; step++)
            {
                int channel = (_nextPoll + step) % ChannelCount;
                int count = _input[channel].Count;
                if (count == 0)
                {
                    continue;
                }

                _nextPoll = (channel + 1) % ChannelCount;
                if (count == 1)
                {
                    answer[0] = (byte)(channel + 1);
                    answer[1] = _input[channel].Dequeue();
                }
                else
                {
                    answer[0] = (byte)(channel + 17);
                    answer[1] = (byte)Math.Min(count, 255);
                }

                return;
            }

            answer[0] = 0;
            answer[1] = 0;
        }
    }

    /// <summary>
    /// Takes the bytes OP_SERREADM asks for.
    /// </summary>
    /// <param name="channel">
    /// The channel number from the request.
    /// </param>
    /// <param name="destination">
    /// Receives the bytes; its length is the count from the request.
    /// </param>
    /// <returns>
    /// True when the channel had that many bytes and they were taken. False when it had fewer, or the channel number is not
    /// one of the 15 channels, or the count is 0; then nothing is taken and the server sends nothing, as "The SERREADM
    /// Transaction" says: "requesting more bytes than are present will result in a server error, and it will simply not
    /// respond for the timeout period".
    /// </returns>
    public bool TryTake(int channel, Span<byte> destination)
    {
        lock (_gate)
        {
            if (channel < 0 || channel >= ChannelCount || destination.Length == 0 || _input[channel].Count < destination.Length)
            {
                return false;
            }

            for (int i = 0; i < destination.Length; i++)
            {
                destination[i] = _input[channel].Dequeue();
            }

            return true;
        }
    }

    /// <summary>
    /// Hands bytes the client wrote to the host.
    /// </summary>
    /// <param name="channel">
    /// The channel number from the request.
    /// </param>
    /// <param name="data">
    /// The bytes.
    /// </param>
    internal void Receive(int channel, ReadOnlySpan<byte> data)
    {
        if (channel < 0 || channel >= ChannelCount)
        {
            Interlocked.Add(ref _droppedBytes, data.Length);
            return;
        }

        OutputReceived?.Invoke(channel, data.ToArray());
    }

    /// <summary>
    /// Opens or closes a channel at the client's request (OP_SERINIT, OP_SERTERM, or OP_SERSETSTAT with SS.Open or SS.Close).
    /// </summary>
    /// <param name="channel">
    /// The channel number from the request.
    /// </param>
    /// <param name="open">
    /// True to open, false to close.
    /// </param>
    internal void SetOpen(int channel, bool open)
    {
        if (channel < 0 || channel >= ChannelCount)
        {
            return;
        }

        lock (_gate)
        {
            _open[channel] = open;
            if (!open)
            {
                _input[channel].Clear();
                _closeNotice[channel] = false;
            }
        }

        ChannelStateChanged?.Invoke(channel, open);
    }

    private static void CheckChannel(int channel)
    {
        if (channel < 0 || channel >= ChannelCount)
        {
            throw new ArgumentOutOfRangeException(nameof(channel), channel, "A virtual serial channel is 0 to 14.");
        }
    }
}
