//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System.Threading.Channels;

namespace RetroIO.Protocols.DriveWire.Server;

/// <summary>
/// Reads the client's bytes one at a time, with a time limit while a transaction is in progress.
/// </summary>
/// <remarks>
/// A background task copies everything the stream delivers into a queue, and the protocol takes bytes from the queue. The
/// time limit is applied to the queue, never to the stream, so giving up on a late byte cannot lose a byte that arrives at
/// the same moment: it stays in the queue and is read as the start of the next transaction.
/// </remarks>
internal sealed class DriveWireByteReader : IAsyncDisposable
{
    private readonly Stream _stream;
    private readonly Channel<byte> _queue;
    private readonly CancellationTokenSource _pumpStop = new CancellationTokenSource();
    private readonly Task _pump;
    private long _bytesReceived;

    /// <summary>
    /// Starts copying the stream into the queue.
    /// </summary>
    /// <param name="stream">
    /// The stream from the client.
    /// </param>
    public DriveWireByteReader(Stream stream)
    {
        _stream = stream;
        _queue = Channel.CreateUnbounded<byte>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
        _pump = Task.Run(PumpAsync);
    }

    /// <summary>
    /// Gets the number of bytes received from the stream.
    /// </summary>
    public long BytesReceived => Interlocked.Read(ref _bytesReceived);

    /// <summary>
    /// Takes the next byte.
    /// </summary>
    /// <param name="timeout">
    /// How long to wait, or null to wait until a byte arrives or the stream ends.
    /// </param>
    /// <param name="cancellationToken">
    /// Stops the wait.
    /// </param>
    /// <returns>
    /// The byte, or -1 when the stream has ended and every byte was taken.
    /// </returns>
    /// <exception cref="TimeoutException">
    /// Thrown when no byte arrived within <paramref name="timeout"/>.
    /// </exception>
    public async ValueTask<int> ReadByteAsync(TimeSpan? timeout, CancellationToken cancellationToken)
    {
        if (_queue.Reader.TryRead(out byte ready))
        {
            return ready;
        }

        if (timeout == null)
        {
            while (await _queue.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (_queue.Reader.TryRead(out byte value))
                {
                    return value;
                }
            }

            return -1;
        }

        using CancellationTokenSource limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(timeout.Value);
        try
        {
            while (await _queue.Reader.WaitToReadAsync(limit.Token).ConfigureAwait(false))
            {
                if (_queue.Reader.TryRead(out byte value))
                {
                    return value;
                }
            }

            return -1;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("No byte from the client within " + timeout.Value.TotalMilliseconds + " ms.");
        }
    }

    /// <summary>
    /// Fills a buffer with the next bytes, each within the time limit.
    /// </summary>
    /// <param name="buffer">
    /// The buffer to fill.
    /// </param>
    /// <param name="timeout">
    /// How long to wait for each byte.
    /// </param>
    /// <param name="cancellationToken">
    /// Stops the wait.
    /// </param>
    /// <returns>
    /// True when the buffer was filled; false when the stream ended first.
    /// </returns>
    /// <exception cref="TimeoutException">
    /// Thrown when a byte did not arrive within <paramref name="timeout"/>.
    /// </exception>
    public async ValueTask<bool> ReadExactlyAsync(Memory<byte> buffer, TimeSpan timeout, CancellationToken cancellationToken)
    {
        for (int i = 0; i < buffer.Length; i++)
        {
            int value = await ReadByteAsync(timeout, cancellationToken).ConfigureAwait(false);
            if (value < 0)
            {
                return false;
            }

            buffer.Span[i] = (byte)value;
        }

        return true;
    }

    /// <summary>
    /// Stops the copying task.
    /// </summary>
    /// <returns>
    /// A task that completes when the copying task has ended.
    /// </returns>
    public async ValueTask DisposeAsync()
    {
        _pumpStop.Cancel();
        try
        {
            await _pump.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The pump was stopped on purpose.
        }

        _pumpStop.Dispose();
    }

    private async Task PumpAsync()
    {
        byte[] chunk = new byte[1024];
        try
        {
            while (!_pumpStop.IsCancellationRequested)
            {
                int count = await _stream.ReadAsync(chunk, _pumpStop.Token).ConfigureAwait(false);
                if (count == 0)
                {
                    break;
                }

                Interlocked.Add(ref _bytesReceived, count);
                for (int i = 0; i < count; i++)
                {
                    _queue.Writer.TryWrite(chunk[i]);
                }
            }
        }
        catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException || ex is OperationCanceledException)
        {
            // The connection was closed or reset. That ends the stream exactly like a clean close does: the protocol sees -1
            // once every byte already received has been taken.
        }
        finally
        {
            _queue.Writer.TryComplete();
        }
    }
}
