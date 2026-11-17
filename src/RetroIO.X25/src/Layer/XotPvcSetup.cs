//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Text;

namespace RetroIO.Protocols.X25.PacketLayer
{
    /// <summary>
    /// The XOT PVC setup packet (RFC 1613 6.5): what two XOT ends exchange, right after the TCP connection opens, to join
    /// a permanent virtual circuit across it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Layout, after the XOT header: GFI and LCN (2 octets), PTI 0xF5, version 0x81, status, initiator interface name
    /// length N, initiator LCN (2 octets, high first), responder interface name length M, responder LCN (2 octets),
    /// sender incoming window, sender outgoing window, sender incoming maximum packet size, sender outgoing maximum packet
    /// size, the initiator interface name (N octets), the responder interface name (M octets). "There MUST NOT be any
    /// padding or trailing zero octets between or after the interface names."
    /// </para>
    /// <para>
    /// Packet sizes are "encoded as they are in the packet size facility, (i.e., the base-2 log of the size in octets, so
    /// 7 represents a maximum packet size of 128 octets)". The flow control values are "from the perspective of the local
    /// interface of the XOT implementation that sent the PVC setup packet".
    /// </para>
    /// <para>
    /// The RFC does not say what goes in the GFI and LCN octets of this packet. They are kept as given, so a caller sets
    /// them and a parser reports what arrived; nothing here assumes a value.
    /// </para>
    /// </remarks>
    public sealed class XotPvcSetup
    {
        /// <summary>The packet type identifier, chosen "from the unused X.25 PTI values".</summary>
        public const byte PacketType = 0xF5;

        /// <summary>The PVC setup version.</summary>
        public const byte Version = 0x81;

        /// <summary>Status 0x00: sent by the initiator, and by a responder that accepts ("successful if the responder
        /// returns a status of 0x00").</summary>
        public const byte StatusOk = 0x00;

        /// <summary>0x13 No such destination interface.</summary>
        public const byte StatusNoSuchInterface = 0x13;

        /// <summary>0x14 Destination interface is not up.</summary>
        public const byte StatusInterfaceNotUp = 0x14;

        /// <summary>0x15 Non-X.25 destination interface.</summary>
        public const byte StatusNotX25 = 0x15;

        /// <summary>0x16 No such destination PVC.</summary>
        public const byte StatusNoSuchPvc = 0x16;

        /// <summary>0x17 Destination PVC configuration mismatch (also: the PVC already has an XOT connection).</summary>
        public const byte StatusConfigurationMismatch = 0x17;

        /// <summary>0x18 Mismatched flow control values.</summary>
        public const byte StatusFlowControlMismatch = 0x18;

        /// <summary>0x19 Can't support flow control values.</summary>
        public const byte StatusFlowControlUnsupported = 0x19;

        /// <summary>0x1A PVC setup protocol error.</summary>
        public const byte StatusProtocolError = 0x1A;

        private const int FixedLength = 15;

        /// <summary>The GFI and LCN octets (see the remarks: not defined by the RFC).</summary>
        public ushort GfiLcn { get; set; }

        /// <summary>The status field.</summary>
        public byte Status { get; set; }

        /// <summary>The initiator's interface name, ASCII.</summary>
        public string InitiatorInterface { get; set; } = string.Empty;

        /// <summary>The PVC's logical channel on the initiator's interface.</summary>
        public ushort InitiatorLci { get; set; }

        /// <summary>The responder's interface name, ASCII.</summary>
        public string ResponderInterface { get; set; } = string.Empty;

        /// <summary>The PVC's logical channel on the responder's interface.</summary>
        public ushort ResponderLci { get; set; }

        /// <summary>The sender's incoming window.</summary>
        public byte IncomingWindow { get; set; }

        /// <summary>The sender's outgoing window.</summary>
        public byte OutgoingWindow { get; set; }

        /// <summary>The sender's incoming maximum packet size, in octets (sent as its base-2 log).</summary>
        public int IncomingPacketSize { get; set; }

        /// <summary>The sender's outgoing maximum packet size, in octets.</summary>
        public int OutgoingPacketSize { get; set; }

        /// <summary>
        /// Builds the packet (without the XOT header).
        /// </summary>
        /// <returns>The octets.</returns>
        /// <exception cref="ArgumentException">A name is longer than 255 octets or not ASCII, or a packet size is not a
        /// power of two from 16 to 4096.</exception>
        public byte[] Build()
        {
            byte[] initiator = Name(InitiatorInterface, nameof(InitiatorInterface));
            byte[] responder = Name(ResponderInterface, nameof(ResponderInterface));
            byte[] p = new byte[FixedLength + initiator.Length + responder.Length];
            p[0] = (byte)(GfiLcn >> 8);
            p[1] = (byte)GfiLcn;
            p[2] = PacketType;
            p[3] = Version;
            p[4] = Status;
            p[5] = (byte)initiator.Length;
            p[6] = (byte)(InitiatorLci >> 8);
            p[7] = (byte)InitiatorLci;
            p[8] = (byte)responder.Length;
            p[9] = (byte)(ResponderLci >> 8);
            p[10] = (byte)ResponderLci;
            p[11] = IncomingWindow;
            p[12] = OutgoingWindow;
            p[13] = Log2(IncomingPacketSize, nameof(IncomingPacketSize));
            p[14] = Log2(OutgoingPacketSize, nameof(OutgoingPacketSize));
            initiator.CopyTo(p, FixedLength);
            responder.CopyTo(p, FixedLength + initiator.Length);
            return p;
        }

        /// <summary>
        /// Tells whether a packet is a PVC setup packet: long enough to hold its type, and type 0xF5.
        /// </summary>
        /// <param name="packet">The packet, without the XOT header.</param>
        /// <returns>True for a PVC setup packet.</returns>
        public static bool IsSetup(ReadOnlySpan<byte> packet)
        {
            return packet.Length >= 3 && packet[2] == PacketType;
        }

        /// <summary>
        /// Reads a PVC setup packet.
        /// </summary>
        /// <param name="packet">The packet, without the XOT header.</param>
        /// <param name="setup">The fields, when it could be read.</param>
        /// <returns>True when the packet is exactly a version 0x81 setup packet; false for anything else - wrong type or
        /// version, too short, the names not filling the rest exactly (the RFC forbids padding), or a packet size code
        /// outside 4..12 - which the responder answers with 0x1A, "PVC setup protocol error".</returns>
        public static bool TryParse(ReadOnlySpan<byte> packet, out XotPvcSetup setup)
        {
            setup = new XotPvcSetup();
            if (packet.Length < FixedLength || packet[2] != PacketType || packet[3] != Version)
            {
                return false;
            }

            int n = packet[5];
            int m = packet[8];
            if (packet.Length != FixedLength + n + m || packet[13] < 4 || packet[13] > 12 || packet[14] < 4 || packet[14] > 12)
            {
                return false;
            }

            setup.GfiLcn = (ushort)((packet[0] << 8) | packet[1]);
            setup.Status = packet[4];
            setup.InitiatorLci = (ushort)((packet[6] << 8) | packet[7]);
            setup.ResponderLci = (ushort)((packet[9] << 8) | packet[10]);
            setup.IncomingWindow = packet[11];
            setup.OutgoingWindow = packet[12];
            setup.IncomingPacketSize = 1 << packet[13];
            setup.OutgoingPacketSize = 1 << packet[14];
            setup.InitiatorInterface = Encoding.ASCII.GetString(packet.Slice(FixedLength, n));
            setup.ResponderInterface = Encoding.ASCII.GetString(packet.Slice(FixedLength + n, m));
            return true;
        }

        /// <summary>
        /// The responder's check (RFC 1613 6.5: "the responding XOT verifies the configurations are compatible"): the
        /// status to send back, from what arrived and this end's own PVC.
        /// </summary>
        /// <param name="localWindow">This end's window on the PVC (both directions).</param>
        /// <param name="localPacketSize">This end's maximum packet size on the PVC (both directions).</param>
        /// <param name="maxWindow">The largest window this end can run: 7 with modulo 8.</param>
        /// <param name="maxPacketSize">The largest packet this end's link can carry.</param>
        /// <returns><see cref="StatusOk"/>, <see cref="StatusFlowControlUnsupported"/> when the asked values are beyond what
        /// this end can run, or <see cref="StatusFlowControlMismatch"/> when they are not complementary to its own ("the
        /// incoming value of one local interface corresponds to the outgoing value of the connecting local interface, and
        /// vice-versa").</returns>
        public byte CheckFlowControl(byte localWindow, int localPacketSize, byte maxWindow, int maxPacketSize)
        {
            if (IncomingWindow < 1 || OutgoingWindow < 1 || IncomingWindow > maxWindow || OutgoingWindow > maxWindow
                || IncomingPacketSize > maxPacketSize || OutgoingPacketSize > maxPacketSize)
            {
                return StatusFlowControlUnsupported;
            }

            // The sender's incoming is this end's outgoing, and the other way round.
            if (OutgoingWindow != localWindow || IncomingWindow != localWindow
                || OutgoingPacketSize != localPacketSize || IncomingPacketSize != localPacketSize)
            {
                return StatusFlowControlMismatch;
            }

            return StatusOk;
        }

        private static byte[] Name(string name, string field)
        {
            byte[] b = Encoding.ASCII.GetBytes(name);
            if (b.Length > 255 || Encoding.ASCII.GetString(b) != name)
            {
                throw new ArgumentException("an interface name is ASCII and at most 255 octets", field);
            }

            return b;
        }

        private static byte Log2(int size, string field)
        {
            for (byte e = 4; e <= 12; e++)
            {
                if (size == 1 << e)
                {
                    return e;
                }
            }

            throw new ArgumentException("a packet size is a power of two from 16 to 4096", field);
        }
    }
}
