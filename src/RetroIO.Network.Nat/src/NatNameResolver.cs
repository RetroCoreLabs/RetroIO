//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace RetroIO.Network.Nat
{
    /// <summary>
    /// The name lookup fallback of the built-in NAT, for a host with no usable name server
    /// address: it answers the guest's A queries (name to IPv4 address) from the host's own
    /// resolver (host networking design, section 7.3, DNS row).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The host's resolver call waits for its answer, so it runs on one helper thread of its
    /// own, never on the NAT thread (the same split the design gives the outside ping, 7.6).
    /// The NAT thread hands queries in with <see cref="Enqueue"/> and takes finished answers
    /// with <see cref="TryTakeAnswer"/>; the helper only builds the answer message.
    /// </para>
    /// <para>
    /// The answers follow RFC 1035: the header's ID, opcode and RD bit are copied, QR and RA
    /// are set, the question is copied back, and each IPv4 address of the name becomes one
    /// answer record (type A, class IN, a 60-second TTL of our own choosing, pointing at the
    /// question's name with the compression pointer C00Ch). Anything other than one A question
    /// of class IN gets RCODE 4, "Not Implemented"; a name the host does not know gets RCODE 3,
    /// "Name Error"; any other failure RCODE 2, "Server Failure". Only name-to-address lookups
    /// work this way, and <c>net.status</c> says so.
    /// </para>
    /// </remarks>
    public sealed class NatNameResolver
    {
        // ── Constants ──

        /// <summary>
        /// The TTL put on answers, in seconds.
        /// </summary>
        public const uint AnswerTtlSeconds = 60;

        /// <summary>
        /// The most queries waiting at once; more are dropped and counted.
        /// </summary>
        public const int MaxWaiting = 64;

        private const int MaxMessageBytes = 512;

        // ── Fields ──

        private readonly ConcurrentQueue<NatNameQuery> _queries = new ConcurrentQueue<NatNameQuery>();

        private readonly ConcurrentQueue<NatNameQuery> _answers = new ConcurrentQueue<NatNameQuery>();

        private readonly AutoResetEvent _wake = new AutoResetEvent(false);

        private readonly Thread _thread;

        private volatile bool _running = true;

        private int _waiting;

        private long _dropped;

        private long _answered;

        // ── Constructors ──

        /// <summary>
        /// Initializes the resolver and starts its helper thread.
        /// </summary>
        public NatNameResolver()
        {
            _thread = new Thread(Run) { IsBackground = true, Name = "RetroIO NAT name lookups" };
            _thread.Start();
        }

        // ── Properties ──

        /// <summary>
        /// Gets queries dropped because too many were waiting.
        /// </summary>
        public long Dropped => Interlocked.Read(ref _dropped);

        /// <summary>
        /// Gets queries answered (with addresses or with an error code).
        /// </summary>
        public long Answered => Interlocked.Read(ref _answered);

        // ── Public methods ──

        /// <summary>
        /// Hands a guest's query to the helper thread.
        /// </summary>
        /// <param name="query">
        /// The query; its message is copied by the caller.
        /// </param>
        public void Enqueue(NatNameQuery query)
        {
            if (Interlocked.Increment(ref _waiting) > MaxWaiting || query.Message.Length > MaxMessageBytes)
            {
                Interlocked.Decrement(ref _waiting);
                Interlocked.Increment(ref _dropped);
                return;
            }
            _queries.Enqueue(query);
            _wake.Set();
        }

        /// <summary>
        /// Takes one finished answer, if any.
        /// </summary>
        /// <param name="answer">
        /// The query with its <see cref="NatNameQuery.Message"/> now the answer.
        /// </param>
        /// <returns>
        /// <c>true</c> when an answer was taken.
        /// </returns>
        public bool TryTakeAnswer(out NatNameQuery? answer)
        {
            if (_answers.TryDequeue(out answer))
            {
                Interlocked.Decrement(ref _waiting);
                return true;
            }
            return false;
        }

        /// <summary>
        /// Stops the helper thread. A lookup under way finishes first.
        /// </summary>
        public void Stop()
        {
            _running = false;
            _wake.Set();
            _thread.Join();
        }

        /// <summary>
        /// Builds the answer to one query message with the given lookup, without any thread.
        /// </summary>
        /// <param name="query">
        /// The query message as the guest sent it.
        /// </param>
        /// <param name="lookup">
        /// The name-to-addresses lookup to use.
        /// </param>
        /// <returns>
        /// The answer message, or <c>null</c> when the query is too broken to answer.
        /// </returns>
        public static byte[]? Answer(byte[] query, NatHostLookup lookup)
        {
            if (query.Length < 12 || (query[2] & 0x80) != 0)
            {
                return null;
            }
            int questions = BinaryPrimitives.ReadUInt16BigEndian(query.AsSpan(4));
            int end = 12;
            StringBuilder name = new StringBuilder();
            bool nameOk = true;
            while (end < query.Length && query[end] != 0)
            {
                int len = query[end];
                if ((len & 0xC0) != 0 || end + 1 + len > query.Length)
                {
                    nameOk = false;
                    break;
                }
                if (name.Length > 0)
                {
                    name.Append('.');
                }
                name.Append(Encoding.ASCII.GetString(query, end + 1, len));
                end += 1 + len;
            }
            if (!nameOk || end + 5 > query.Length)
            {
                return Header(query, 12, 1, 0);
            }
            int questionEnd = end + 5;
            ushort type = BinaryPrimitives.ReadUInt16BigEndian(query.AsSpan(end + 1));
            ushort cls = BinaryPrimitives.ReadUInt16BigEndian(query.AsSpan(end + 3));
            int opcode = (query[2] >> 3) & 0x0F;
            if (questions != 1 || opcode != 0 || type != 1 || cls != 1 || name.Length == 0)
            {
                byte[] notImplemented = Header(query, questionEnd, 4, 0);
                return notImplemented;
            }

            IPAddress[] addresses;
            try
            {
                addresses = lookup(name.ToString());
            }
            catch (SocketException e)
            {
                return Header(query, questionEnd, e.SocketErrorCode == SocketError.HostNotFound || e.SocketErrorCode == SocketError.NoData ? 3 : 2, 0);
            }
            catch (ArgumentException)
            {
                return Header(query, questionEnd, 3, 0);
            }

            int count = 0;
            for (int i = 0; i < addresses.Length; i++)
            {
                if (addresses[i].AddressFamily == AddressFamily.InterNetwork)
                {
                    count++;
                }
            }
            if (count == 0)
            {
                // The name is known but has no IPv4 address: no error, no answer records.
                return Header(query, questionEnd, 0, 0);
            }
            if (questionEnd + count * 16 > MaxMessageBytes)
            {
                count = (MaxMessageBytes - questionEnd) / 16;
            }
            byte[] answer = Header(query, questionEnd + count * 16, 0, count);
            int o = questionEnd;
            int written = 0;
            for (int i = 0; i < addresses.Length && written < count; i++)
            {
                if (addresses[i].AddressFamily != AddressFamily.InterNetwork)
                {
                    continue;
                }
                BinaryPrimitives.WriteUInt16BigEndian(answer.AsSpan(o), 0xC00C);
                BinaryPrimitives.WriteUInt16BigEndian(answer.AsSpan(o + 2), 1);
                BinaryPrimitives.WriteUInt16BigEndian(answer.AsSpan(o + 4), 1);
                BinaryPrimitives.WriteUInt32BigEndian(answer.AsSpan(o + 6), AnswerTtlSeconds);
                BinaryPrimitives.WriteUInt16BigEndian(answer.AsSpan(o + 10), 4);
                addresses[i].GetAddressBytes().CopyTo(answer, o + 12);
                o += 16;
                written++;
            }
            return answer;
        }

        // ── Private methods ──

        // The answer's header and question: ID, opcode and RD copied; QR and RA set; the given
        // RCODE; one question (copied) unless the question could not be read; the answer count.
        private static byte[] Header(byte[] query, int length, int rcode, int answers)
        {
            byte[] answer = new byte[length];
            int questionBytes = length < query.Length ? length : query.Length;
            Array.Copy(query, answer, questionBytes);
            answer[2] = (byte)(0x80 | (query[2] & 0x79));
            answer[3] = (byte)(0x80 | rcode);
            BinaryPrimitives.WriteUInt16BigEndian(answer.AsSpan(4), (ushort)(length > 12 ? 1 : 0));
            BinaryPrimitives.WriteUInt16BigEndian(answer.AsSpan(6), (ushort)answers);
            BinaryPrimitives.WriteUInt16BigEndian(answer.AsSpan(8), 0);
            BinaryPrimitives.WriteUInt16BigEndian(answer.AsSpan(10), 0);
            return answer;
        }

        private void Run()
        {
            while (_running)
            {
                while (_queries.TryDequeue(out NatNameQuery? query))
                {
                    byte[]? answer = Answer(query.Message, Dns.GetHostAddresses);
                    if (answer == null)
                    {
                        Interlocked.Decrement(ref _waiting);
                        continue;
                    }
                    query.Message = answer;
                    Interlocked.Increment(ref _answered);
                    _answers.Enqueue(query);
                }
                _wake.WaitOne(50);
            }
        }
    }

    /// <summary>
    /// Looks a host name up and gives its addresses (the host resolver, or a test's own).
    /// </summary>
    /// <param name="name">
    /// The name.
    /// </param>
    /// <returns>
    /// Its addresses.
    /// </returns>
    public delegate IPAddress[] NatHostLookup(string name);

    /// <summary>
    /// One guest name query on its way through <see cref="NatNameResolver"/>: who asked, and the
    /// message (the query going in, the answer coming out).
    /// </summary>
    public sealed class NatNameQuery
    {
        /// <summary>
        /// Initializes a query.
        /// </summary>
        /// <param name="guestMac">
        /// The guest's MAC.
        /// </param>
        /// <param name="guestAddress">
        /// The guest's address.
        /// </param>
        /// <param name="guestPort">
        /// The guest's port.
        /// </param>
        /// <param name="message">
        /// A copy of the query message.
        /// </param>
        public NatNameQuery(byte[] guestMac, uint guestAddress, ushort guestPort, byte[] message)
        {
            GuestMac = guestMac;
            GuestAddress = guestAddress;
            GuestPort = guestPort;
            Message = message;
        }

        /// <summary>
        /// Gets the guest's MAC.
        /// </summary>
        public byte[] GuestMac { get; }

        /// <summary>
        /// Gets the guest's address.
        /// </summary>
        public uint GuestAddress { get; }

        /// <summary>
        /// Gets the guest's port.
        /// </summary>
        public ushort GuestPort { get; }

        /// <summary>
        /// Gets or sets the message: the query, then the answer.
        /// </summary>
        public byte[] Message { get; set; }
    }
}
