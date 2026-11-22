//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Buffers;
using System.Net.Sockets;

namespace RetroIO.Network.Nat
{
    /// <summary>
    /// The states of one guest TCP connection through the NAT relay.
    /// </summary>
    public enum NatTcpState
    {
        /// <summary>
        /// The guest's SYN came; the host connect is under way; nothing sent to the guest yet.
        /// </summary>
        HostConnecting,

        /// <summary>
        /// The host connect worked and the SYN-ACK went to the guest; waiting for its ACK.
        /// </summary>
        SynAckSent,

        /// <summary>
        /// A host client came in through a port forward and the guest's MAC is not known yet:
        /// an ARP request is out (design 7.7).
        /// </summary>
        GuestResolving,

        /// <summary>
        /// A host client came in through a port forward: our SYN went to the guest from the
        /// gateway address; waiting for its SYN-ACK (design 7.7).
        /// </summary>
        SynSent,

        /// <summary>
        /// Data flows both ways.
        /// </summary>
        Established,

        /// <summary>
        /// Both sides sent FIN and both are acknowledged; a late FIN still gets its ACK.
        /// </summary>
        TimeWait,

        /// <summary>
        /// Finished; the router removes it.
        /// </summary>
        Closed,
    }

    /// <summary>
    /// One guest TCP connection that the NAT ends inside the emulator and relays to an ordinary
    /// host socket (host networking design, section 7.4).
    /// </summary>
    /// <remarks>
    /// Only the NAT thread touches this object. Sequence numbers: <see cref="SendUnacked"/> is
    /// the oldest byte sent to the guest and not yet acknowledged; <see cref="SendNext"/> the next
    /// one to send; <see cref="ReceiveNext"/> the next byte expected from the guest. The bytes
    /// toward the guest are kept from <see cref="SendUnacked"/> on, so a retransmit can resend
    /// them. Both buffers come from <see cref="ArrayPool{T}.Shared"/> and go back in
    /// <see cref="Release"/>.
    /// </remarks>
    public sealed class NatTcpConnection
    {
        // ── Constants ──

        /// <summary>
        /// Bytes held toward the host and toward the guest, each way.
        /// </summary>
        public const int BufferBytes = 65535;

        // ── Constructors ──

        /// <summary>
        /// Initializes a connection for a guest SYN.
        /// </summary>
        /// <param name="guestMac">
        /// The guest's MAC, from the SYN's frame.
        /// </param>
        /// <param name="guestAddress">
        /// The guest's address.
        /// </param>
        /// <param name="guestPort">
        /// The guest's port.
        /// </param>
        /// <param name="remoteAddress">
        /// The address the guest connects to (as the guest sees it).
        /// </param>
        /// <param name="remotePort">
        /// The port the guest connects to.
        /// </param>
        /// <param name="guestInitialSequence">
        /// The sequence number of the guest's SYN.
        /// </param>
        /// <param name="guestMss">
        /// The guest's MSS option, or 536 when it sent none.
        /// </param>
        /// <param name="ourInitialSequence">
        /// The sequence number our SYN-ACK will carry.
        /// </param>
        public NatTcpConnection(ReadOnlySpan<byte> guestMac, uint guestAddress, ushort guestPort, uint remoteAddress, ushort remotePort,
            uint guestInitialSequence, int guestMss, uint ourInitialSequence)
        {
            GuestMac = guestMac.Slice(0, 6).ToArray();
            GuestAddress = guestAddress;
            GuestPort = guestPort;
            RemoteAddress = remoteAddress;
            RemotePort = remotePort;
            GuestInitialSequence = guestInitialSequence;
            ReceiveNext = guestInitialSequence + 1;
            GuestMss = guestMss;
            OurInitialSequence = ourInitialSequence;
            SendUnacked = ourInitialSequence;
            SendNext = ourInitialSequence;
            ToHost = ArrayPool<byte>.Shared.Rent(BufferBytes);
            ToGuest = ArrayPool<byte>.Shared.Rent(BufferBytes);
            State = NatTcpState.HostConnecting;
        }

        // ── Properties ──

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
        /// Gets the address the guest connects to, as the guest sees it.
        /// </summary>
        public uint RemoteAddress { get; }

        /// <summary>
        /// Gets the port the guest connects to.
        /// </summary>
        public ushort RemotePort { get; }

        /// <summary>
        /// Gets the sequence number of the guest's SYN.
        /// </summary>
        public uint GuestInitialSequence { get; }

        /// <summary>
        /// Gets the guest's MSS.
        /// </summary>
        public int GuestMss { get; set; }

        /// <summary>
        /// Gets the sequence number of our SYN-ACK.
        /// </summary>
        public uint OurInitialSequence { get; }

        /// <summary>
        /// Gets or sets the state.
        /// </summary>
        public NatTcpState State { get; set; }

        /// <summary>
        /// Gets or sets the host socket (non-blocking).
        /// </summary>
        public Socket? Host { get; set; }

        /// <summary>
        /// Gets or sets the next byte expected from the guest.
        /// </summary>
        public uint ReceiveNext { get; set; }

        /// <summary>
        /// Gets or sets the oldest byte sent to the guest and not acknowledged.
        /// </summary>
        public uint SendUnacked { get; set; }

        /// <summary>
        /// Gets or sets the next byte to send to the guest.
        /// </summary>
        public uint SendNext { get; set; }

        /// <summary>
        /// Gets or sets the window the guest last offered.
        /// </summary>
        public int GuestWindow { get; set; }

        /// <summary>
        /// Gets the buffer of guest bytes waiting for the host socket.
        /// </summary>
        public byte[] ToHost { get; private set; }

        /// <summary>
        /// Gets or sets how many bytes of <see cref="ToHost"/> are waiting.
        /// </summary>
        public int ToHostCount { get; set; }

        /// <summary>
        /// Gets the buffer of host bytes from <see cref="SendUnacked"/> on.
        /// </summary>
        public byte[] ToGuest { get; private set; }

        /// <summary>
        /// Gets or sets how many bytes of <see cref="ToGuest"/> are held (sent or not).
        /// </summary>
        public int ToGuestCount { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether the host socket ended its stream.
        /// </summary>
        public bool HostEnded { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether our FIN went to the guest.
        /// </summary>
        public bool FinSent { get; set; }

        /// <summary>
        /// Gets or sets the sequence number our FIN takes.
        /// </summary>
        public uint FinSequence { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether the guest acknowledged our FIN.
        /// </summary>
        public bool FinAcked { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether the guest's FIN came.
        /// </summary>
        public bool GuestFinished { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether the host socket got its send side shut.
        /// </summary>
        public bool HostShutSend { get; set; }

        /// <summary>
        /// Gets or sets the retransmit timeout, in polls (emulated time).
        /// </summary>
        public long RetransmitPolls { get; set; }

        /// <summary>
        /// Gets or sets the poll count at which the oldest unacknowledged segment is resent.
        /// </summary>
        public long RetransmitAt { get; set; }

        /// <summary>
        /// Gets or sets how many times in a row the oldest segment was resent.
        /// </summary>
        public int Retries { get; set; }

        /// <summary>
        /// Gets or sets the poll count at which a zero window is probed with one byte.
        /// </summary>
        public long PersistAt { get; set; }

        /// <summary>
        /// Gets or sets the poll count at which a TIME-WAIT ends.
        /// </summary>
        public long TimeWaitEndsAt { get; set; }

        /// <summary>
        /// Gets the bytes in flight to the guest (sent, not acknowledged), our FIN not counted.
        /// </summary>
        public int InFlight => (int)(SendNext - SendUnacked) - (FinSent && !FinAcked ? 1 : 0);

        /// <summary>
        /// Gets the free space toward the host: the window we offer the guest.
        /// </summary>
        public int FreeToHost => BufferBytes - ToHostCount;

        // ── Public methods ──

        /// <summary>
        /// True when this connection belongs to the four numbers of a segment.
        /// </summary>
        /// <param name="guestAddress">
        /// Guest address.
        /// </param>
        /// <param name="guestPort">
        /// Guest port.
        /// </param>
        /// <param name="remoteAddress">
        /// Remote address as the guest sees it.
        /// </param>
        /// <param name="remotePort">
        /// Remote port.
        /// </param>
        /// <returns>
        /// <c>true</c> on a match.
        /// </returns>
        public bool Matches(uint guestAddress, ushort guestPort, uint remoteAddress, ushort remotePort)
        {
            return GuestAddress == guestAddress && GuestPort == guestPort && RemoteAddress == remoteAddress && RemotePort == remotePort;
        }

        /// <summary>
        /// Closes the host socket and gives the buffers back. Safe to call twice.
        /// </summary>
        public void Release()
        {
            Socket? host = Host;
            Host = null;
            if (host != null)
            {
                try
                {
                    host.Close();
                }
                catch (ObjectDisposedException)
                {
                    // Already closed: nothing left to release.
                }
            }
            if (ToHost.Length > 0)
            {
                ArrayPool<byte>.Shared.Return(ToHost);
                ToHost = Array.Empty<byte>();
            }
            if (ToGuest.Length > 0)
            {
                ArrayPool<byte>.Shared.Return(ToGuest);
                ToGuest = Array.Empty<byte>();
            }
            State = NatTcpState.Closed;
        }
    }
}
