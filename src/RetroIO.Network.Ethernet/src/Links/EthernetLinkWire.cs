//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Net;
using System.Net.Sockets;

namespace RetroIO.Network.Ethernet
{
    /// <summary>
    /// The byte format of the links between emulators, the same as the old RetroCore code
    /// (Emulated.HW/Common/Network: TcpEthernetBackend.cs, TcpEthernetRelay.cs,
    /// UdpEthernetBackend.cs) and nd100x (src/devices/ethernet/net/eth_net.c), so a machine
    /// here shares one Ethernet segment with the old ND-100 and with nd100x.
    /// </summary>
    /// <remarks>
    /// <para>TCP (a link, or a hub's member): each side first sends five bytes, <c>52 45 54 48</c>
    /// ("RETH") and a version byte, then reads the other side's five; the link is refused when the
    /// first four differ or the version is 0. Version 1 is an ordinary member. Then each frame is
    /// two bytes of length, high byte first, and the frame itself: the Ethernet frame from the
    /// destination address on, no frame check sequence. A length of 0 or over
    /// <see cref="TcpMaxFrameBytes"/> ends the link (the old code and nd100x both do so).</para>
    /// <para>UDP multicast: one Ethernet frame is one datagram, with nothing before or after it,
    /// to the group (default <see cref="DefaultGroup"/>) and port (default
    /// <see cref="DefaultPort"/>). Every member gets every datagram, its own too.</para>
    /// <para>Port 3094 is the number of the ND Ethernet II card's PCB, chosen by the old code.</para>
    /// </remarks>
    public static class EthernetLinkWire
    {
        // ── Constants ──

        /// <summary>
        /// The TCP and UDP port when a setting names none.
        /// </summary>
        public const int DefaultPort = 3094;

        /// <summary>
        /// The version byte of an ordinary member.
        /// </summary>
        public const byte Version = 1;

        /// <summary>
        /// The length of the greeting each TCP side sends first.
        /// </summary>
        public const int HandshakeBytes = 5;

        /// <summary>
        /// The length of a TCP frame's length field.
        /// </summary>
        public const int LengthBytes = 2;

        /// <summary>
        /// The largest frame a TCP link carries (as the old code and nd100x).
        /// </summary>
        public const int TcpMaxFrameBytes = 2048;

        /// <summary>
        /// The largest datagram the UDP link takes (as the old code and nd100x).
        /// </summary>
        public const int UdpMaxFrameBytes = 1600;

        // ── Fields ──

        private static readonly byte[] Magic = { 0x52, 0x45, 0x54, 0x48 };

        // ── Properties ──

        /// <summary>
        /// Gets the multicast group when a setting names none: 239.3.9.4.
        /// </summary>
        public static IPAddress DefaultGroup { get; } = new IPAddress(new byte[] { 239, 3, 9, 4 });

        // ── Public methods ──

        /// <summary>
        /// Writes the greeting: "RETH" and the version byte.
        /// </summary>
        /// <param name="destination">
        /// At least <see cref="HandshakeBytes"/> bytes.
        /// </param>
        /// <param name="version">
        /// The version byte; <see cref="Version"/> for a member.
        /// </param>
        public static void WriteHandshake(Span<byte> destination, byte version)
        {
            Magic.CopyTo(destination);
            destination[4] = version;
        }

        /// <summary>
        /// Checks the other side's greeting.
        /// </summary>
        /// <param name="greeting">
        /// The five bytes read.
        /// </param>
        /// <returns>
        /// <c>true</c> when it starts with "RETH" and its version is at least 1.
        /// </returns>
        public static bool IsValidHandshake(ReadOnlySpan<byte> greeting)
        {
            return greeting.Length >= HandshakeBytes
                && greeting[0] == Magic[0] && greeting[1] == Magic[1] && greeting[2] == Magic[2] && greeting[3] == Magic[3]
                && greeting[4] >= 1;
        }

        /// <summary>
        /// Writes a frame's two length bytes, high byte first.
        /// </summary>
        /// <param name="destination">
        /// At least two bytes.
        /// </param>
        /// <param name="length">
        /// The frame length.
        /// </param>
        public static void WriteLength(Span<byte> destination, int length)
        {
            destination[0] = (byte)(length >> 8);
            destination[1] = (byte)length;
        }

        /// <summary>
        /// Reads a frame's two length bytes.
        /// </summary>
        /// <param name="source">
        /// The two bytes.
        /// </param>
        /// <returns>
        /// The frame length.
        /// </returns>
        public static int ReadLength(ReadOnlySpan<byte> source)
        {
            return (source[0] << 8) | source[1];
        }

        /// <summary>
        /// Sends the greeting and reads and checks the other side's, as both the old code and
        /// nd100x do: write first, then read.
        /// </summary>
        /// <param name="socket">
        /// The connected socket.
        /// </param>
        /// <returns>
        /// <c>true</c> when the other side's greeting is valid.
        /// </returns>
        public static bool Handshake(Socket socket)
        {
            Span<byte> hello = stackalloc byte[HandshakeBytes];
            WriteHandshake(hello, Version);
            SendAll(socket, hello);
            Span<byte> peer = stackalloc byte[HandshakeBytes];
            return ReceiveAll(socket, peer) && IsValidHandshake(peer);
        }

        /// <summary>
        /// Sends every byte, or throws.
        /// </summary>
        /// <param name="socket">
        /// The socket.
        /// </param>
        /// <param name="data">
        /// The bytes.
        /// </param>
        public static void SendAll(Socket socket, ReadOnlySpan<byte> data)
        {
            int sent = 0;
            while (sent < data.Length)
            {
                int n = socket.Send(data.Slice(sent));
                if (n <= 0)
                {
                    throw new SocketException((int)SocketError.ConnectionReset);
                }
                sent += n;
            }
        }

        /// <summary>
        /// Reads exactly as many bytes as <paramref name="buffer"/> holds.
        /// </summary>
        /// <param name="socket">
        /// The socket.
        /// </param>
        /// <param name="buffer">
        /// Where they go.
        /// </param>
        /// <returns>
        /// <c>false</c> when the other side closed first.
        /// </returns>
        public static bool ReceiveAll(Socket socket, Span<byte> buffer)
        {
            int got = 0;
            while (got < buffer.Length)
            {
                int n = socket.Receive(buffer.Slice(got));
                if (n <= 0)
                {
                    return false;
                }
                got += n;
            }
            return true;
        }

        /// <summary>
        /// Reads one TCP frame into <paramref name="buffer"/>.
        /// </summary>
        /// <param name="socket">
        /// The socket.
        /// </param>
        /// <param name="buffer">
        /// At least <see cref="TcpMaxFrameBytes"/> bytes.
        /// </param>
        /// <param name="length">
        /// The frame length.
        /// </param>
        /// <returns>
        /// <c>false</c> when the link closed or the length is 0 or too long.
        /// </returns>
        public static bool ReceiveFrame(Socket socket, byte[] buffer, out int length)
        {
            Span<byte> header = stackalloc byte[LengthBytes];
            length = 0;
            if (!ReceiveAll(socket, header))
            {
                return false;
            }
            length = ReadLength(header);
            return length > 0 && length <= TcpMaxFrameBytes && ReceiveAll(socket, new Span<byte>(buffer, 0, length));
        }

        /// <summary>
        /// Sends one TCP frame: its length, then the frame. The caller holds the socket's send
        /// lock, so two frames never mix.
        /// </summary>
        /// <param name="socket">
        /// The socket.
        /// </param>
        /// <param name="frame">
        /// The frame.
        /// </param>
        public static void SendFrame(Socket socket, ReadOnlySpan<byte> frame)
        {
            Span<byte> header = stackalloc byte[LengthBytes];
            WriteLength(header, frame.Length);
            SendAll(socket, header);
            SendAll(socket, frame);
        }
    }
}
