//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System.Collections.Concurrent;
using RetroIO.Protocols.Econet.Server;

namespace RetroIO.Protocols.Econet.Server.Transport;

/// <summary>
/// Retransmits a file-server reply until the client returns a matching AUN ACK,
/// aligned with PiEconetBridge's AUN despatcher (econet-hpbridge.c).
///
/// PiEconetBridge model:
///   * EB_CONFIG_AUN_RETX    = 1000 ms between (re)transmissions of an AUN DATA packet.
///   * EB_CONFIG_AUN_RETRIES = 5    total transmissions (the "(p->tx)++ is less than RETRIES" loop),
///                                  ~1 s apart, then the packet is dropped.
///   * Only ECONET_AUN_DATA is retransmitted; ACK/NAK are single-shot.
///   * When the inbound AUN listener sees the matching ACK it removes the queued packet,
///     so retransmission stops immediately (BeebEm dislikes a new packet arriving before
///     it has ACKed the previous one — replies are effectively serialised per station).
///
/// RetroFS only emits replies (one per request), and the BBC's NFS is half-duplex, so the
/// initial transmission happens in the service; this class performs the remaining
/// (MaxTransmits - 1) retransmits on the same 1 s timer and cancels on the matching ACK.
/// </summary>
public sealed class AunReplyRetransmitter
{
    private readonly record struct ReplyKey(byte Net, byte Station, uint Sequence);

    private sealed class Pending
    {
        public required EconetMessage Reply { get; init; }
        public required CancellationTokenSource Cts { get; init; }
    }

    private readonly ConcurrentDictionary<ReplyKey, Pending> _pending = new();
    private readonly TimeSpan _retransmitInterval; // EB_CONFIG_AUN_RETX
    private readonly int _maxTransmits;             // EB_CONFIG_AUN_RETRIES (total, including initial)

    /// <summary>
    /// Creates the retransmitter.
    /// </summary>
    /// <param name="retransmitIntervalMs">
    /// The time between two transmissions, in milliseconds. At least 1.
    /// </param>
    /// <param name="maxTransmits">
    /// The total number of transmissions, including the first. At least 1.
    /// </param>
    public AunReplyRetransmitter(int retransmitIntervalMs, int maxTransmits)
    {
        _retransmitInterval = TimeSpan.FromMilliseconds(Math.Max(1, retransmitIntervalMs));
        _maxTransmits = Math.Max(1, maxTransmits);
    }

    /// <summary>
    /// Begin watching a freshly-sent reply (counts as transmission 1) and resend it on the
    /// retransmit timer until the matching ACK arrives or the transmit budget is exhausted.
    /// </summary>
    public void Track(EconetMessage reply)
    {
        // _maxTransmits == 1 means single-shot (initial send only) — nothing to retransmit.
        if (reply.Transport == null || _maxTransmits <= 1)
            return;

        ReplyKey key = new ReplyKey(reply.ClientNet, reply.ClientStation, reply.Sequence);

        if (_pending.TryRemove(key, out Pending? stale))
            stale.Cts.Cancel();

        CancellationTokenSource cts = new CancellationTokenSource();
        Pending pending = new Pending { Reply = reply, Cts = cts };
        _pending[key] = pending;

        _ = RetransmitLoopAsync(key, pending, cts.Token);
    }

    /// <summary>
    /// Called when an inbound AUN ACK arrives; stops retransmitting the matching reply.
    /// </summary>
    public void Acknowledge(byte net, byte station, uint sequence)
    {
        ReplyKey key = new ReplyKey(net, station, sequence);
        if (_pending.TryRemove(key, out Pending? pending))
            pending.Cts.Cancel();
    }

    private async Task RetransmitLoopAsync(ReplyKey key, Pending pending, CancellationToken token)
    {
        try
        {
            // Transmission 1 was the initial send in the service; do up to (_maxTransmits - 1) more,
            // each EB_CONFIG_AUN_RETX apart, mirroring PiEconetBridge's "(p->tx)++ < RETRIES" loop.
            for (int tx = 1; tx < _maxTransmits; tx++)
            {
                await Task.Delay(_retransmitInterval, token).ConfigureAwait(false);

                // Only resend if THIS pending entry is still active (not ACKed, not replaced).
                if (!_pending.TryGetValue(key, out Pending? current) || !ReferenceEquals(current, pending))
                    return;

                if (pending.Reply.Transport != null)
                    await pending.Reply.Transport.SendAsync(pending.Reply).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // ACK arrived (or shutdown) — stop quietly.
        }
        catch
        {
            // Swallow transient send failures; the client can always re-issue the request.
        }
        finally
        {
            if (_pending.TryGetValue(key, out Pending? current) && ReferenceEquals(current, pending))
                _pending.TryRemove(key, out _);
        }
    }
}
