//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Buffers.Binary;
using System.Collections.Generic;

using RetroIO.Network.Router;

namespace RetroIO.Network.Nat
{
    // Putting a caller's fragmented packets back together before they go on (RFC 791 section 3.2,
    // "Reassembly"). The NAT has to: a host socket takes whole datagrams only.
    //
    // A packet is known by its source, destination, protocol and identification. Its data is kept
    // in 8-byte blocks, each marked when a fragment covers it; fragments may come in any order,
    // repeat or overlap (a later copy of a byte wins). The header of the fragment at offset 0 becomes
    // the header of the whole packet, with the fragment fields cleared and the length and checksum
    // set again. The last fragment (More Fragments clear) gives the length.
    //
    // A packet not complete after 60 seconds is dropped (RFC 1122 section 3.3.2 asks for a timeout
    // of 60 to 120 seconds). When its first fragment had arrived, the caller gets ICMP time
    // exceeded, code 1, "fragment reassembly time exceeded" (RFC 792).
    public sealed partial class IpNat
    {
        /// <summary>
        /// The most packets being put back together at once.
        /// </summary>
        public const int MaxReassemblies = 64;

        /// <summary>
        /// How long the fragments of one packet may take to arrive, in milliseconds.
        /// </summary>
        public const long ReassemblyTimeoutMilliseconds = 60000;

        private const int MaxIpBytes = 65535;

        private readonly List<Reassembly> _reassemblies = new List<Reassembly>();

        /// <summary>
        /// Gets how many packets are being put back together.
        /// </summary>
        public int Reassemblies => _reassemblies.Count;

        private void Reassemble(ReadOnlySpan<byte> fragment, int headerBytes, ushort fragmentField)
        {
            int offset = (fragmentField & 0x1FFF) * 8;
            bool more = (fragmentField & 0x2000) != 0;
            ReadOnlySpan<byte> data = fragment.Slice(headerBytes);
            if (offset + data.Length > MaxIpBytes - headerBytes || (more && (data.Length % 8 != 0 || data.Length == 0)))
            {
                Drop(NatDropReason.Fragment);
                return;
            }

            uint source = Ipv4.Source(fragment);
            uint destination = Ipv4.Destination(fragment);
            ushort identification = BinaryPrimitives.ReadUInt16BigEndian(fragment.Slice(4, 2));
            byte protocol = fragment[9];
            Reassembly? r = null;
            for (int i = 0; i < _reassemblies.Count; i++)
            {
                Reassembly c = _reassemblies[i];
                if (c.Source == source && c.Destination == destination && c.Identification == identification && c.Protocol == protocol)
                {
                    r = c;
                    break;
                }
            }

            if (r == null)
            {
                if (_reassemblies.Count >= MaxReassemblies)
                {
                    Drop(NatDropReason.TooManyReassemblies);
                    return;
                }

                r = new Reassembly(source, destination, identification, protocol, _now);
                _reassemblies.Add(r);
            }

            data.CopyTo(r.Data.AsSpan(offset));
            int firstBlock = offset / 8;
            int endBlock = (offset + data.Length + 7) / 8;
            for (int b = firstBlock; b < endBlock; b++)
            {
                r.Blocks[b] = true;
            }

            if (!more)
            {
                r.DataLength = offset + data.Length;
            }

            if (offset == 0)
            {
                fragment.Slice(0, headerBytes).CopyTo(r.Header);
                r.HeaderLength = headerBytes;
            }

            if (r.DataLength < 0 || r.HeaderLength == 0)
            {
                return;
            }

            int blocks = (r.DataLength + 7) / 8;
            for (int b = 0; b < blocks; b++)
            {
                if (!r.Blocks[b])
                {
                    return;
                }
            }

            _reassemblies.Remove(r);
            int total = r.HeaderLength + r.DataLength;
            if (total > MaxIpBytes)
            {
                Drop(NatDropReason.Fragment);
                return;
            }

            byte[] whole = new byte[total];
            r.Header.AsSpan(0, r.HeaderLength).CopyTo(whole);
            r.Data.AsSpan(0, r.DataLength).CopyTo(whole.AsSpan(r.HeaderLength));
            BinaryPrimitives.WriteUInt16BigEndian(whole.AsSpan(2), (ushort)total);

            // Keep Don't Fragment as the first fragment had it; clear More Fragments and the offset.
            whole[6] &= 0x40;
            whole[7] = 0;
            whole[10] = 0;
            whole[11] = 0;
            BinaryPrimitives.WriteUInt16BigEndian(whole.AsSpan(10), (ushort)~Ipv4.Sum(whole.AsSpan(0, r.HeaderLength)));
            FromRouterWhole(whole, r.HeaderLength);
        }

        private void ExpireReassemblies()
        {
            for (int i = _reassemblies.Count - 1; i >= 0; i--)
            {
                Reassembly r = _reassemblies[i];
                if (_now - r.Started < ReassemblyTimeoutMilliseconds)
                {
                    continue;
                }

                _reassemblies.RemoveAt(i);
                Drop(NatDropReason.ReassemblyTimeout);
                if (r.HeaderLength == 0)
                {
                    continue;
                }

                // Quote the first fragment's header and its first 8 data bytes, as it was sent.
                int quoted = r.HeaderLength + 8;
                byte[] quote = new byte[quoted];
                r.Header.AsSpan(0, r.HeaderLength).CopyTo(quote);
                r.Data.AsSpan(0, 8).CopyTo(quote.AsSpan(r.HeaderLength));
                SendIcmpError(r.Destination, r.Source, 11, 1, quote);
            }
        }

        // One packet being put back together.
        private sealed class Reassembly
        {
            public Reassembly(uint source, uint destination, ushort identification, byte protocol, long started)
            {
                Source = source;
                Destination = destination;
                Identification = identification;
                Protocol = protocol;
                Started = started;
            }

            public uint Source { get; }

            public uint Destination { get; }

            public ushort Identification { get; }

            public byte Protocol { get; }

            public long Started { get; }

            public byte[] Data { get; } = new byte[MaxIpBytes];

            public bool[] Blocks { get; } = new bool[(MaxIpBytes + 7) / 8];

            // The data length from the last fragment, or -1 until it arrives.
            public int DataLength { get; set; } = -1;

            public byte[] Header { get; } = new byte[60];

            // 0 until the fragment at offset 0 arrives.
            public int HeaderLength { get; set; }
        }
    }
}
