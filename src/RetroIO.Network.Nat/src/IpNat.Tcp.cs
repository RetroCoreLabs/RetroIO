//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;

using RetroIO.Network.Router;

namespace RetroIO.Network.Nat
{
    // TCP through the NAT. The caller's TCP connection ends here: this file is the far end of it, as
    // RFC 9293 describes one, and the bytes are carried on over an ordinary host connection to the
    // address the caller asked for.
    //
    // What the caller-side TCP does:
    //  - the caller's SYN starts a host connect; the SYN-ACK is sent only once the host connection
    //    is up, so a refused host connect becomes a reset to the caller's SYN.
    //  - our MSS option is 1460; the caller's MSS option is used, 536 when it sends none
    //    (RFC 9293 section 3.7.1).
    //  - no window scaling, no selective acknowledgment, no timestamps (none is offered, so none
    //    is used, RFC 7323 and RFC 2018).
    //  - data from the caller is taken in order only; a segment that is not next is answered with an
    //    ACK for what is next (RFC 9293 section 3.10.7.4 lets a receiver keep or drop it).
    //  - retransmission: go back to the oldest unacknowledged byte, timeout 1 second at the start and
    //    doubled each time up to 60 seconds (RFC 6298 sections 2 and 5.5), reset after 12 tries.
    //  - a zero window from the caller is probed with one byte when the timer runs out (RFC 9293
    //    section 3.8.6.1).
    //  - the side that closes first waits in TIME-WAIT for two maximum segment lifetimes, 4 minutes
    //    (RFC 9293 section 3.4.2, MSL 2 minutes), to acknowledge a resent FIN.
    //  - a segment for no connection gets a reset (RFC 9293 section 3.10.7.1).
    public sealed partial class IpNat
    {
        /// <summary>
        /// The most TCP connections at once; a SYN beyond it gets a reset.
        /// </summary>
        public const int MaxTcpConnections = 256;

        /// <summary>
        /// How long a connection that this side closed first stays in TIME-WAIT, in milliseconds:
        /// two maximum segment lifetimes of 2 minutes (RFC 9293 section 3.4.2).
        /// </summary>
        public const long TcpTimeWaitMilliseconds = 240000;

        /// <summary>
        /// The first retransmission timeout, in milliseconds (RFC 6298 section 2.1).
        /// </summary>
        public const long TcpInitialRetransmitMilliseconds = 1000;

        /// <summary>
        /// Retransmissions of one segment before the connection is reset.
        /// </summary>
        public const int TcpMaxRetries = 12;

        private const long TcpMaxRetransmitMilliseconds = 60000;
        private const int TcpBufferBytes = 65536;
        private const int TcpHeaderBytes = 20;
        private const int OurMss = 1460;
        private const int DefaultCallerMss = 536;
        private const byte FlagFin = 0x01;
        private const byte FlagSyn = 0x02;
        private const byte FlagRst = 0x04;
        private const byte FlagPsh = 0x08;
        private const byte FlagAck = 0x10;

        private readonly List<TcpConnection> _tcp = new List<TcpConnection>();
        private readonly Random _random = new Random();

        /// <summary>
        /// Gets how many TCP connections are open, TIME-WAIT included.
        /// </summary>
        public int TcpConnections => _tcp.Count;

        /// <summary>
        /// Gets how many host connects were refused; each refusal is a reset to the caller.
        /// </summary>
        public long HostConnectsRefused { get; private set; }

        /// <summary>
        /// Gets how many times a TCP retransmission timer ran out.
        /// </summary>
        public long TcpRetransmits { get; private set; }

        private void FromCallerTcp(ReadOnlySpan<byte> tcp, uint source, uint destination)
        {
            if (tcp.Length < TcpHeaderBytes)
            {
                Drop(NatDropReason.BadTcp);
                return;
            }

            int offset = (tcp[12] >> 4) * 4;
            if (offset < TcpHeaderBytes || offset > tcp.Length || Checksum(tcp, PseudoHeaderSum(source, destination, tcp.Length, ProtocolTcp)) != 0)
            {
                Drop(NatDropReason.BadTcp);
                return;
            }

            ushort sourcePort = BinaryPrimitives.ReadUInt16BigEndian(tcp);
            ushort destinationPort = BinaryPrimitives.ReadUInt16BigEndian(tcp.Slice(2, 2));
            uint seq = BinaryPrimitives.ReadUInt32BigEndian(tcp.Slice(4, 4));
            uint ack = BinaryPrimitives.ReadUInt32BigEndian(tcp.Slice(8, 4));
            byte flags = tcp[13];
            int window = BinaryPrimitives.ReadUInt16BigEndian(tcp.Slice(14, 2));
            ReadOnlySpan<byte> data = tcp.Slice(offset);

            TcpConnection? c = FindTcp(source, sourcePort, destination, destinationPort);
            if ((flags & FlagRst) != 0)
            {
                // RFC 9293 section 3.10.7.4: a reset counts only when its sequence number is in the
                // window. A reset is never answered.
                if (c != null && c.State != TcpState.Connecting && (uint)(seq - c.ReceiveNext) < TcpBufferBytes)
                {
                    RemoveTcp(c);
                }

                return;
            }

            if (c != null && c.State == TcpState.TimeWait && (flags & FlagSyn) != 0)
            {
                // A new connection on the same addresses and ports ends the old TIME-WAIT.
                RemoveTcp(c);
                c = null;
            }

            if (c == null)
            {
                if ((flags & (FlagSyn | FlagAck)) == FlagSyn)
                {
                    OpenTcp(tcp, offset, source, sourcePort, destination, destinationPort, seq, window);
                    return;
                }

                ResetUnknown(source, sourcePort, destination, destinationPort, seq, ack, flags, data.Length);
                return;
            }

            if (c.State == TcpState.Connecting)
            {
                // The SYN-ACK has not been sent yet, so nothing but a resent SYN can be valid here.
                return;
            }

            if ((flags & FlagSyn) != 0)
            {
                // A resent SYN while the SYN-ACK is out: send it again. In a synchronised state,
                // answer with an ACK (RFC 5961 section 4, the challenge ACK).
                if (c.State == TcpState.SynReceived)
                {
                    SendSynAck(c);
                }
                else
                {
                    SendSegment(c, FlagAck, c.SendNext, ReadOnlySpan<byte>.Empty);
                }

                return;
            }

            if ((flags & FlagAck) == 0)
            {
                // RFC 9293 section 3.10.7.4, fifth check: a segment without ACK is dropped.
                return;
            }

            if (c.State == TcpState.TimeWait)
            {
                if ((flags & FlagFin) != 0)
                {
                    SendSegment(c, FlagAck, c.SendNext, ReadOnlySpan<byte>.Empty);
                }

                return;
            }

            TakeAck(c, ack);
            c.CallerWindow = window;
            bool fin = (flags & FlagFin) != 0;
            if (data.Length > 0 || fin)
            {
                if (seq != c.ReceiveNext || c.CallerFinReceived)
                {
                    // Not the next byte (a repeat, or one past a gap), or past the caller's FIN.
                    SendSegment(c, FlagAck, c.SendNext, ReadOnlySpan<byte>.Empty);
                }
                else
                {
                    int take = Math.Min(data.Length, TcpBufferBytes - c.ToHostCount);
                    data.Slice(0, take).CopyTo(c.ToHost.AsSpan(c.ToHostCount));
                    c.ToHostCount += take;
                    c.ReceiveNext += (uint)take;
                    if (fin && take == data.Length)
                    {
                        c.ReceiveNext++;
                        c.CallerFinReceived = true;
                    }

                    SendSegment(c, FlagAck, c.SendNext, ReadOnlySpan<byte>.Empty);
                }
            }

            WriteToHost(c);
            PumpToCaller(c);
            CheckTcpDone(c);
        }

        private void OpenTcp(ReadOnlySpan<byte> tcp, int offset, uint source, ushort sourcePort, uint destination, ushort destinationPort, uint seq, int window)
        {
            if (_tcp.Count >= MaxTcpConnections)
            {
                Drop(NatDropReason.TooManyConnections);
                SendTcp(destination, destinationPort, source, sourcePort, 0, seq + 1, FlagRst | FlagAck, 0, ReadOnlySpan<byte>.Empty, false);
                return;
            }

            // The MSS option, kind 2, length 4 (RFC 9293 section 3.2). End of list is kind 0, no-op
            // is kind 1; every other kind carries its own length.
            int mss = DefaultCallerMss;
            int i = TcpHeaderBytes;
            while (i < offset)
            {
                byte kind = tcp[i];
                if (kind == 0)
                {
                    break;
                }

                if (kind == 1)
                {
                    i++;
                    continue;
                }

                if (i + 1 >= offset || tcp[i + 1] < 2 || i + tcp[i + 1] > offset)
                {
                    break;
                }

                if (kind == 2 && tcp[i + 1] == 4)
                {
                    mss = BinaryPrimitives.ReadUInt16BigEndian(tcp.Slice(i + 2, 2));
                }

                i += tcp[i + 1];
            }

            uint iss = (uint)_random.NextInt64(0, 0x100000000);
            TcpConnection c = new TcpConnection(source, sourcePort, destination, destinationPort)
            {
                ReceiveNext = seq + 1,
                Iss = iss,
                SendUnacked = iss,
                SendNext = iss,
                CallerWindow = window,
                Mss = Math.Max(1, Math.Min(mss, OurMss)),
                RetransmitInterval = TcpInitialRetransmitMilliseconds,
            };
            Socket host = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            host.Blocking = false;
            host.NoDelay = true;
            c.Host = host;
            _tcp.Add(c);
            try
            {
                host.Connect(new IPEndPoint(new IPAddress(BinaryPrimitives.ReverseEndianness(destination)), destinationPort));
                HostConnected(c);
            }
            catch (SocketException e)
            {
                if (e.SocketErrorCode != SocketError.WouldBlock && e.SocketErrorCode != SocketError.InProgress)
                {
                    HostConnectFailed(c, e.SocketErrorCode);
                }
            }
        }

        private void HostConnected(TcpConnection c)
        {
            c.State = TcpState.SynReceived;
            SendSynAck(c);
            c.RetransmitAt = _now + c.RetransmitInterval;
        }

        private void HostConnectFailed(TcpConnection c, SocketError error)
        {
            if (error == SocketError.ConnectionRefused)
            {
                HostConnectsRefused++;
                SendTcp(c.RemoteAddress, c.RemotePort, c.CallerAddress, c.CallerPort, 0, c.ReceiveNext, FlagRst | FlagAck, 0, ReadOnlySpan<byte>.Empty, false);
            }
            else
            {
                Drop(NatDropReason.HostConnectFailed);
            }

            RemoveTcp(c);
        }

        private void SendSynAck(TcpConnection c)
        {
            SendTcp(c.RemoteAddress, c.RemotePort, c.CallerAddress, c.CallerPort, c.Iss, c.ReceiveNext, FlagSyn | FlagAck, ReceiveWindow(c), ReadOnlySpan<byte>.Empty, true);
            c.SendNext = c.Iss + 1;
        }

        // Takes the caller's acknowledgment number: drops acknowledged bytes and restarts the timer.
        private void TakeAck(TcpConnection c, uint ack)
        {
            int acked = (int)(ack - c.SendUnacked);
            int flight = (int)(c.SendNext - c.SendUnacked);
            if (acked <= 0 || acked > flight)
            {
                return;
            }

            if (c.State == TcpState.SynReceived)
            {
                // The first byte acknowledged is our SYN.
                c.State = TcpState.Established;
                c.SendUnacked++;
                acked--;
            }

            int dataAcked = acked;
            if (c.FinSent && ack == c.SendNext)
            {
                dataAcked--;
            }

            if (dataAcked > 0)
            {
                Buffer.BlockCopy(c.ToCaller, dataAcked, c.ToCaller, 0, c.ToCallerCount - dataAcked);
                c.ToCallerCount -= dataAcked;
            }

            c.SendUnacked = ack;
            c.Retries = 0;
            c.RetransmitInterval = TcpInitialRetransmitMilliseconds;
            c.RetransmitAt = c.SendNext != c.SendUnacked ? _now + c.RetransmitInterval : 0;
        }

        // Sends what the caller's window allows, then the FIN once the host has ended and every byte
        // has gone out.
        private void PumpToCaller(TcpConnection c)
        {
            if (c.State != TcpState.Established)
            {
                return;
            }

            // The FIN is the last thing sent, so while it is out it is the last unacknowledged number.
            int inFlight = (int)(c.SendNext - c.SendUnacked) - (c.FinSent && c.SendNext != c.SendUnacked ? 1 : 0);
            while (inFlight < c.ToCallerCount)
            {
                int room = c.CallerWindow - inFlight;
                if (room <= 0)
                {
                    break;
                }

                int n = Math.Min(c.Mss, Math.Min(c.ToCallerCount - inFlight, room));
                SendSegment(c, FlagAck | FlagPsh, c.SendNext, c.ToCaller.AsSpan(inFlight, n));
                c.SendNext += (uint)n;
                inFlight += n;
            }

            if (c.HostEnded && !c.FinSent && inFlight == c.ToCallerCount)
            {
                SendSegment(c, FlagFin | FlagAck, c.SendNext, ReadOnlySpan<byte>.Empty);
                c.SendNext++;
                c.FinSent = true;
                c.ClosedFirst = !c.CallerFinReceived;
            }

            bool waitingOnWindow = c.ToCallerCount > inFlight && c.CallerWindow == 0;
            if (c.RetransmitAt == 0 && (c.SendNext != c.SendUnacked || waitingOnWindow))
            {
                c.RetransmitAt = _now + c.RetransmitInterval;
            }
        }

        private void WriteToHost(TcpConnection c)
        {
            if (c.State != TcpState.Established || c.Host == null)
            {
                return;
            }

            if (c.ToHostCount > 0)
            {
                int windowBefore = ReceiveWindow(c);
                int n = c.Host.Send(c.ToHost, 0, c.ToHostCount, SocketFlags.None, out SocketError error);
                if (error == SocketError.WouldBlock)
                {
                    return;
                }

                if (error != SocketError.Success)
                {
                    ResetTcp(c);
                    return;
                }

                Buffer.BlockCopy(c.ToHost, n, c.ToHost, 0, c.ToHostCount - n);
                c.ToHostCount -= n;

                // The window was nearly shut: tell the caller it is open again (RFC 9293 section
                // 3.8.6.2.2, the receiver's side of avoiding small windows).
                if (n > 0 && windowBefore < c.Mss)
                {
                    SendSegment(c, FlagAck, c.SendNext, ReadOnlySpan<byte>.Empty);
                }
            }

            if (c.ToHostCount == 0 && c.CallerFinReceived && !c.HostShutDown)
            {
                c.Host.Shutdown(SocketShutdown.Send);
                c.HostShutDown = true;
            }
        }

        private void ReadFromHost(TcpConnection c)
        {
            int space = TcpBufferBytes - c.ToCallerCount;
            if (c.Host == null || space == 0 || c.HostEnded)
            {
                return;
            }

            int n = c.Host.Receive(c.ToCaller, c.ToCallerCount, space, SocketFlags.None, out SocketError error);
            if (error == SocketError.WouldBlock)
            {
                return;
            }

            if (error != SocketError.Success)
            {
                ResetTcp(c);
                return;
            }

            if (n == 0)
            {
                c.HostEnded = true;
            }
            else
            {
                c.ToCallerCount += n;
            }

            PumpToCaller(c);
            CheckTcpDone(c);
        }

        private void CheckTcpDone(TcpConnection c)
        {
            if (c.State != TcpState.Established || !c.FinSent || c.SendUnacked != c.SendNext || !c.CallerFinReceived || c.ToHostCount != 0)
            {
                return;
            }

            if (c.ClosedFirst)
            {
                c.Host?.Close();
                c.Host = null;
                c.State = TcpState.TimeWait;
                c.RetransmitAt = 0;
                c.TimeWaitEndsAt = _now + TcpTimeWaitMilliseconds;
            }
            else
            {
                RemoveTcp(c);
            }
        }

        private void AddTcpSockets()
        {
            for (int i = 0; i < _tcp.Count; i++)
            {
                TcpConnection c = _tcp[i];
                if (c.Host == null)
                {
                    continue;
                }

                if (c.State == TcpState.Connecting)
                {
                    _writeList.Add(c.Host);
                    _errorList.Add(c.Host);
                    continue;
                }

                // Back-pressure: the host is not read while the buffer toward the caller is full, so
                // the host's own TCP slows the far end down.
                if (!c.HostEnded && c.ToCallerCount < TcpBufferBytes)
                {
                    _readList.Add(c.Host);
                }

                if (c.State == TcpState.Established && c.ToHostCount > 0)
                {
                    _writeList.Add(c.Host);
                }
            }
        }

        // A connect has finished when the socket is writable (done, or failed on Linux) or in the
        // error set (failed, on Windows). The socket's own error code says which.
        private void ServiceTcpConnects()
        {
            for (int i = 0; i < _errorList.Count; i++)
            {
                TcpConnection? c = FindTcp(_errorList[i]);
                if (c != null && c.State == TcpState.Connecting)
                {
                    HostConnectFailed(c, ConnectError(c.Host!));
                }
            }

            for (int i = 0; i < _writeList.Count; i++)
            {
                TcpConnection? c = FindTcp(_writeList[i]);
                if (c == null || c.State != TcpState.Connecting)
                {
                    continue;
                }

                SocketError error = ConnectError(c.Host!);
                if (error == SocketError.Success)
                {
                    HostConnected(c);
                }
                else
                {
                    HostConnectFailed(c, error);
                }
            }
        }

        private void ServiceTcpSockets()
        {
            for (int i = 0; i < _writeList.Count; i++)
            {
                TcpConnection? c = FindTcp(_writeList[i]);
                if (c != null && c.State == TcpState.Established)
                {
                    WriteToHost(c);
                    CheckTcpDone(c);
                }
            }

            for (int i = 0; i < _readList.Count; i++)
            {
                TcpConnection? c = FindTcp(_readList[i]);
                if (c != null && (c.State == TcpState.Established || c.State == TcpState.SynReceived))
                {
                    ReadFromHost(c);
                }
            }
        }

        private void RunTcpTimers()
        {
            for (int i = _tcp.Count - 1; i >= 0; i--)
            {
                if (i >= _tcp.Count)
                {
                    continue;
                }

                TcpConnection c = _tcp[i];
                if (c.State == TcpState.TimeWait)
                {
                    if (_now >= c.TimeWaitEndsAt)
                    {
                        RemoveTcp(c);
                    }

                    continue;
                }

                if (c.RetransmitAt == 0 || _now < c.RetransmitAt)
                {
                    continue;
                }

                c.Retries++;
                if (c.Retries > TcpMaxRetries)
                {
                    ResetTcp(c);
                    continue;
                }

                TcpRetransmits++;
                c.RetransmitInterval = Math.Min(c.RetransmitInterval * 2, TcpMaxRetransmitMilliseconds);
                c.RetransmitAt = 0;
                if (c.State == TcpState.SynReceived)
                {
                    SendSynAck(c);
                    c.RetransmitAt = _now + c.RetransmitInterval;
                    continue;
                }

                if (c.SendNext == c.SendUnacked)
                {
                    if (c.ToCallerCount > 0 && c.CallerWindow == 0)
                    {
                        // Zero-window probe: one byte past the window.
                        SendSegment(c, FlagAck | FlagPsh, c.SendNext, c.ToCaller.AsSpan(0, 1));
                        c.SendNext++;
                        c.RetransmitAt = _now + c.RetransmitInterval;
                    }

                    continue;
                }

                // Go back to the oldest byte not acknowledged and send again from there.
                c.SendNext = c.SendUnacked;
                c.FinSent = false;
                PumpToCaller(c);
            }
        }

        private void ResetUnknown(uint source, ushort sourcePort, uint destination, ushort destinationPort, uint seq, uint ack, byte flags, int dataLength)
        {
            // RFC 9293 section 3.10.7.1: with ACK, the reset takes its sequence number from the
            // acknowledgment; without, it acknowledges the segment.
            if ((flags & FlagAck) != 0)
            {
                SendTcp(destination, destinationPort, source, sourcePort, ack, 0, FlagRst, 0, ReadOnlySpan<byte>.Empty, false);
                return;
            }

            uint length = (uint)dataLength + ((flags & FlagSyn) != 0 ? 1u : 0u) + ((flags & FlagFin) != 0 ? 1u : 0u);
            SendTcp(destination, destinationPort, source, sourcePort, 0, seq + length, FlagRst | FlagAck, 0, ReadOnlySpan<byte>.Empty, false);
        }

        private void ResetTcp(TcpConnection c)
        {
            SendTcp(c.RemoteAddress, c.RemotePort, c.CallerAddress, c.CallerPort, c.SendNext, c.ReceiveNext, FlagRst | FlagAck, 0, ReadOnlySpan<byte>.Empty, false);
            RemoveTcp(c);
        }

        private void RemoveTcp(TcpConnection c)
        {
            c.Host?.Close();
            c.Host = null;
            c.State = TcpState.Closed;
            _tcp.Remove(c);
        }

        private void CloseAllTcp()
        {
            for (int i = 0; i < _tcp.Count; i++)
            {
                _tcp[i].Host?.Close();
            }

            _tcp.Clear();
        }

        private static SocketError ConnectError(Socket s)
        {
            object? value = s.GetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Error);
            return value is int code ? (SocketError)code : SocketError.SocketError;
        }

        private static int ReceiveWindow(TcpConnection c)
        {
            return Math.Min(65535, TcpBufferBytes - c.ToHostCount);
        }

        private TcpConnection? FindTcp(uint callerAddress, ushort callerPort, uint remoteAddress, ushort remotePort)
        {
            for (int i = 0; i < _tcp.Count; i++)
            {
                TcpConnection c = _tcp[i];
                if (c.CallerAddress == callerAddress && c.CallerPort == callerPort && c.RemoteAddress == remoteAddress && c.RemotePort == remotePort)
                {
                    return c;
                }
            }

            return null;
        }

        private TcpConnection? FindTcp(Socket s)
        {
            for (int i = 0; i < _tcp.Count; i++)
            {
                if (_tcp[i].Host == s)
                {
                    return _tcp[i];
                }
            }

            return null;
        }

        private void SendSegment(TcpConnection c, byte flags, uint seq, ReadOnlySpan<byte> payload)
        {
            SendTcp(c.RemoteAddress, c.RemotePort, c.CallerAddress, c.CallerPort, seq, c.ReceiveNext, flags, ReceiveWindow(c), payload, false);
        }

        // Builds one TCP segment toward a caller and gives it to the router.
        private void SendTcp(uint source, ushort sourcePort, uint destination, ushort destinationPort, uint seq, uint ack, byte flags, int window, ReadOnlySpan<byte> payload, bool mssOption)
        {
            int headerBytes = TcpHeaderBytes + (mssOption ? 4 : 0);
            int tcpLength = headerBytes + payload.Length;
            int total = IpHeaderBytes + tcpLength;
            if (_packet.Length < total)
            {
                _packet = new byte[total];
            }

            Span<byte> p = _packet.AsSpan(0, total);
            p.Slice(0, IpHeaderBytes + headerBytes).Clear();
            p[0] = 0x45;
            BinaryPrimitives.WriteUInt16BigEndian(p.Slice(2, 2), (ushort)total);
            BinaryPrimitives.WriteUInt16BigEndian(p.Slice(4, 2), ++_identification);
            p[8] = DefaultTtl;
            p[9] = ProtocolTcp;
            BinaryPrimitives.WriteUInt32BigEndian(p.Slice(12, 4), source);
            BinaryPrimitives.WriteUInt32BigEndian(p.Slice(16, 4), destination);
            BinaryPrimitives.WriteUInt16BigEndian(p.Slice(10, 2), (ushort)~Ipv4.Sum(p.Slice(0, IpHeaderBytes)));

            Span<byte> tcp = p.Slice(IpHeaderBytes);
            BinaryPrimitives.WriteUInt16BigEndian(tcp, sourcePort);
            BinaryPrimitives.WriteUInt16BigEndian(tcp.Slice(2, 2), destinationPort);
            BinaryPrimitives.WriteUInt32BigEndian(tcp.Slice(4, 4), seq);
            BinaryPrimitives.WriteUInt32BigEndian(tcp.Slice(8, 4), (flags & FlagAck) != 0 ? ack : 0);
            tcp[12] = (byte)((headerBytes / 4) << 4);
            tcp[13] = flags;
            BinaryPrimitives.WriteUInt16BigEndian(tcp.Slice(14, 2), (ushort)window);
            if (mssOption)
            {
                tcp[20] = 2;
                tcp[21] = 4;
                BinaryPrimitives.WriteUInt16BigEndian(tcp.Slice(22, 2), OurMss);
            }

            payload.CopyTo(tcp.Slice(headerBytes));
            BinaryPrimitives.WriteUInt16BigEndian(tcp.Slice(16, 2), Checksum(tcp, PseudoHeaderSum(source, destination, tcpLength, ProtocolTcp)));
            _router.SendFromUpstream(p);
        }

        private enum TcpState
        {
            // The caller's SYN is in; the host connect is not done yet.
            Connecting,

            // The host connection is up and our SYN-ACK is out.
            SynReceived,

            Established,

            // This side closed first; waiting to acknowledge a resent FIN.
            TimeWait,

            Closed,
        }

        // One caller connection and the host connection that carries it on.
        private sealed class TcpConnection
        {
            public TcpConnection(uint callerAddress, ushort callerPort, uint remoteAddress, ushort remotePort)
            {
                CallerAddress = callerAddress;
                CallerPort = callerPort;
                RemoteAddress = remoteAddress;
                RemotePort = remotePort;
            }

            public uint CallerAddress { get; }

            public ushort CallerPort { get; }

            public uint RemoteAddress { get; }

            public ushort RemotePort { get; }

            public Socket? Host { get; set; }

            public TcpState State { get; set; }

            public uint Iss { get; set; }

            // The oldest byte (or SYN or FIN) sent and not yet acknowledged.
            public uint SendUnacked { get; set; }

            public uint SendNext { get; set; }

            public uint ReceiveNext { get; set; }

            public int CallerWindow { get; set; }

            public int Mss { get; set; }

            // Bytes from the host for the caller; byte 0 is at SendUnacked once established.
            public byte[] ToCaller { get; } = new byte[TcpBufferBytes];

            public int ToCallerCount { get; set; }

            // Bytes from the caller not yet written to the host.
            public byte[] ToHost { get; } = new byte[TcpBufferBytes];

            public int ToHostCount { get; set; }

            public bool HostEnded { get; set; }

            public bool HostShutDown { get; set; }

            public bool FinSent { get; set; }

            public bool ClosedFirst { get; set; }

            public bool CallerFinReceived { get; set; }

            public long RetransmitAt { get; set; }

            public long RetransmitInterval { get; set; }

            public int Retries { get; set; }

            public long TimeWaitEndsAt { get; set; }
        }
    }
}
