//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using System.Threading;

namespace RetroIO.Protocols.Econet.Server.Transport;

/// <summary>
/// Per-server outbound AUN sequence counter for file-server DATA replies.
///
/// This mirrors PiEconetBridge's <c>get_local_seq()</c> exactly: every outbound FS reply DATA
/// carries a FRESH server sequence number, incremented by 4 on each call
/// (PiFS <c>fs_aun_send()</c> at <c>utilities/fs.c:1291</c>; <c>get_local_seq</c> does
/// <c>local.seq += 4</c> at <c>utilities/econet-hpbridge.c:9974/9981</c>). The reply does NOT echo
/// the client's request sequence.
///
/// Why this matters against BeebEm/AUN: BeebEm ACKs a *server-originated* DATA reply by quoting the
/// reply's (server) sequence — the bridge matches that ACK to stop retransmitting
/// (<c>econet-hpbridge.c:3725, 5891</c>). If the reply instead echoes the client's own request seq,
/// BeebEm does not treat it as a new server transaction and never ACKs it, which is exactly the
/// "reply not acknowledged" symptom captured in the 2026-06-29 RetroFS trace. Independent, non-AI
/// confirmation: RISC OS PRM Vol 5a Ch.122 (AUN) and J.G.Harston's BeebWiki "Econet over IP"
/// ("this 32-bit number increments by 4 on each transaction; Acks and Naks quote back the sequence").
///
/// The counter is a single per-server value (PiFS keys <c>get_local_seq</c> on the file server's own
/// station, not per client), so a shared instance is correct. Access is thread-safe because
/// <see cref="EconetFileServerService"/> may dispatch datagrams concurrently.
/// </summary>
public sealed class AunOutboundSequence
{
    // Stored as long so Interlocked.Add is legal; we only ever expose the low 32 bits.
    private long _sequence;

    /// <summary>
    /// Create a new outbound sequence generator. The first <see cref="Next"/> returns
    /// <paramref name="initial"/> + 4.
    /// </summary>
    public AunOutboundSequence(uint initial = 0)
    {
        _sequence = initial;
    }

    /// <summary>
    /// Advance by 4 (Econet convention) and return the new sequence value.
    /// </summary>
    public uint Next()
    {
        return unchecked((uint)Interlocked.Add(ref _sequence, 4));
    }
}
