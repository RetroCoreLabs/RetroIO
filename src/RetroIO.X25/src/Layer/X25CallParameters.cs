//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;

namespace RetroIO.Protocols.X25.PacketLayer
{
    /// <summary>
    /// The flow control values one virtual call runs with, in the terms of the end that holds the circuit: "out" is the
    /// direction this end sends in, "in" the direction it receives in.
    /// </summary>
    /// <remarks>
    /// <para>
    /// X.25 6.9 and 6.12 negotiate the window and packet size of a call end to end. A switch relays a call between two
    /// circuits, one on each side, so it has to carry these values from the circuit the call came in on to the circuit it
    /// goes out on (and the answer back), swapping the directions: what the caller sends is what the callee receives.
    /// <see cref="Swapped"/> does that.
    /// </para>
    /// </remarks>
    public readonly struct X25CallParameters
    {
        /// <summary>
        /// Creates a set of values.
        /// </summary>
        /// <param name="windowOut">
        /// The window for packets this end sends.
        /// </param>
        /// <param name="windowIn">
        /// The window for packets this end receives.
        /// </param>
        /// <param name="packetSizeOut">
        /// The largest user data field, in octets, of a packet this end sends.
        /// </param>
        /// <param name="packetSizeIn">
        /// The largest user data field, in octets, of a packet this end receives.
        /// </param>
        public X25CallParameters(byte windowOut, byte windowIn, int packetSizeOut, int packetSizeIn)
            : this(windowOut, windowIn, packetSizeOut, packetSizeIn, null)
        {
        }

        /// <summary>
        /// Creates a set of values that also carries the call's other facilities.
        /// </summary>
        /// <param name="windowOut">
        /// The window for packets this end sends.
        /// </param>
        /// <param name="windowIn">
        /// The window for packets this end receives.
        /// </param>
        /// <param name="packetSizeOut">
        /// The largest user data field, in octets, of a packet this end sends.
        /// </param>
        /// <param name="packetSizeIn">
        /// The largest user data field, in octets, of a packet this end receives.
        /// </param>
        /// <param name="otherFacilities">
        /// Every facility element other than window and packet size, byte for byte (see <see cref="OtherFacilities"/>); none
        /// when null.
        /// </param>
        public X25CallParameters(byte windowOut, byte windowIn, int packetSizeOut, int packetSizeIn, byte[]? otherFacilities)
            : this(windowOut, windowIn, packetSizeOut, packetSizeIn, otherFacilities, null)
        {
        }

        /// <summary>
        /// Creates a set of values that also carries the call's other facilities and its X.75 network utility field.
        /// </summary>
        /// <param name="windowOut">The window for packets this end sends.</param>
        /// <param name="windowIn">The window for packets this end receives.</param>
        /// <param name="packetSizeOut">The largest user data field, in octets, of a packet this end sends.</param>
        /// <param name="packetSizeIn">The largest user data field, in octets, of a packet this end receives.</param>
        /// <param name="otherFacilities">Every facility element other than window and packet size; none when null.</param>
        /// <param name="networkUtilities">The X.75 network utility field (see <see cref="NetworkUtilities"/>); none when null.</param>
        public X25CallParameters(byte windowOut, byte windowIn, int packetSizeOut, int packetSizeIn, byte[]? otherFacilities, byte[]? networkUtilities)
        {
            WindowSizeOut = windowOut;
            WindowSizeIn = windowIn;
            PacketSizeOut = packetSizeOut;
            PacketSizeIn = packetSizeIn;
            _otherFacilities = otherFacilities;
            _networkUtilities = networkUtilities;
        }

        // Null in a default-constructed value; the property turns that into an empty array.
        private readonly byte[]? _otherFacilities;

        // Null in a default-constructed value; the property turns that into an empty array.
        private readonly byte[]? _networkUtilities;

        /// <summary>
        /// Gets the X.75 network utility field of the call, without its length octet (X.75 4.2.1.5; decode it with
        /// <see cref="Models.X75Utilities.TryDecode"/>).
        /// </summary>
        /// <remarks>
        /// Only a circuit on a port between networks (<see cref="X25Config.IsSte"/>) has one. For an incoming call it is what
        /// the calling STE sent in its Call Request; for an outgoing call, once connected, what the called STE sent in its
        /// Call Connected; for a call to be placed, what to send. A switch builds the onward field from it (the transit
        /// network list, the call identifier) - the layer itself only carries it.
        /// </remarks>
        public byte[] NetworkUtilities => _networkUtilities ?? Array.Empty<byte>();

        /// <summary>
        /// Returns the same values with another X.75 network utility field.
        /// </summary>
        /// <param name="networkUtilities">The field; none when null.</param>
        /// <returns>The values with that field.</returns>
        public X25CallParameters WithNetworkUtilities(byte[]? networkUtilities)
        {
            return new X25CallParameters(WindowSizeOut, WindowSizeIn, PacketSizeOut, PacketSizeIn, _otherFacilities, networkUtilities);
        }

        /// <summary>
        /// Gets every facility element of the call other than window and packet size, byte for byte and in the order they
        /// arrived: reverse charging and fast select, closed user groups, NUI, charging, throughput class, markers and
        /// everything after them, extended codes (<see cref="Models.X25Facilities.PassThrough"/>).
        /// </summary>
        /// <remarks>
        /// <para>
        /// For an incoming call these are what the caller put in its Call Request; for an outgoing call, once connected, what
        /// the called side put in its Call Connected. A switch does not act on them; it hands them to the onward call so the
        /// far end receives what was sent (PLAN B1). They have no direction, so <see cref="Swapped"/> leaves them as they are.
        /// </para>
        /// </remarks>
        public byte[] OtherFacilities => _otherFacilities ?? Array.Empty<byte>();

        /// <summary>
        /// Gets the window for packets this end sends.
        /// </summary>
        public byte WindowSizeOut { get; }

        /// <summary>
        /// Gets the window for packets this end receives.
        /// </summary>
        public byte WindowSizeIn { get; }

        /// <summary>
        /// Gets the largest user data field, in octets, of a packet this end sends.
        /// </summary>
        public int PacketSizeOut { get; }

        /// <summary>
        /// Gets the largest user data field, in octets, of a packet this end receives.
        /// </summary>
        public int PacketSizeIn { get; }

        /// <summary>
        /// Returns the same values as seen from the other end of the call: what this end sends is what the other receives.
        /// </summary>
        /// <returns>
        /// The values with the two directions exchanged.
        /// </returns>
        public X25CallParameters Swapped()
        {
            return new X25CallParameters(WindowSizeIn, WindowSizeOut, PacketSizeIn, PacketSizeOut, _otherFacilities, _networkUtilities);
        }

        /// <summary>
        /// Finds the NUI selection element (X.25 7.3.7, code C6) among the other facilities, before the first marker.
        /// </summary>
        /// <param name="identifier">
        /// The parameter field of the element: the network user identifier, in the Administration's format.
        /// </param>
        /// <returns>
        /// True when the call carries an NUI selection.
        /// </returns>
        public bool TryGetNuiSelection(out byte[] identifier)
        {
            return TryGetFacility(NuiSelectionCode, out identifier);
        }

        /// <summary>
        /// Finds an element among the other facilities, before the first marker, by its facility code (X.25 Table 7-2).
        /// </summary>
        /// <param name="code">
        /// The facility code.
        /// </param>
        /// <param name="parameter">
        /// The element's parameter field: one, two or three octets for classes A, B and C, the counted octets for class D
        /// (X.25 7.1).
        /// </param>
        /// <returns>
        /// True when the element is there and whole.
        /// </returns>
        public bool TryGetFacility(byte code, out byte[] parameter)
        {
            byte[] field = OtherFacilities;
            int i = 0;
            while (i < field.Length)
            {
                byte here = field[i];
                if (here == 0x00)
                {
                    break;
                }

                // Same class rule as WithoutNuiSelection (X.25 7.1). Classes A to C have their parameter straight after the
                // code; class D has a length octet first.
                int length;
                int first = i + 1;
                switch (here >> 6)
                {
                    case 0: length = 2; break;
                    case 1: length = 3; break;
                    case 2: length = 4; break;
                    default:
                        length = i + 1 < field.Length ? 2 + field[i + 1] : field.Length - i;
                        first = i + 2;
                        break;
                }

                if (here == code && first <= field.Length && i + length <= field.Length)
                {
                    parameter = new byte[i + length - first];
                    Array.Copy(field, first, parameter, 0, parameter.Length);
                    return true;
                }

                i += length;
            }

            parameter = Array.Empty<byte>();
            return false;
        }

        /// <summary>
        /// Returns the values without any NUI selection element in <see cref="OtherFacilities"/>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// X.25 6.21: "Network user identifier is never transmitted to the remote DTE." A switch reads it for billing and
        /// passes the call on without it, in the Call Request and in the Call Accepted (6.21.3 allows it in both). Only
        /// elements before the first marker are X.25 facilities; a C6 after a marker belongs to another set and stays.
        /// </para>
        /// </remarks>
        /// <returns>
        /// The same values with every NUI selection element removed; this value itself when there was none.
        /// </returns>
        public X25CallParameters WithoutNuiSelection()
        {
            return Without(NuiSelectionCode);
        }

        /// <summary>
        /// Returns the values without the charging information request (code 04) in <see cref="OtherFacilities"/>.
        /// </summary>
        /// <remarks>
        /// X.25 Table 7-2 allows "Charging information - requesting service" in a Call Request and a Call Accepted only:
        /// not in an Incoming Call and not in a Call Connected. The request is to the DTE's own DCE (6.22), which answers
        /// it at the end of the call; a switch therefore never passes it on, in either direction. Read from the PDF page
        /// (the text copy of the table is scrambled), 2026-11-17.
        /// </remarks>
        /// <returns>
        /// The same values with every charging request element removed; this value itself when there was none.
        /// </returns>
        public X25CallParameters WithoutChargingRequest()
        {
            return Without(ChargingRequestCode);
        }

        // The values without every element of one facility code before the first marker (X.25 7.1).
        private X25CallParameters Without(byte removedCode)
        {
            byte[] field = OtherFacilities;
            if (field.Length == 0)
            {
                return this;
            }

            byte[] kept = new byte[field.Length];
            int keptLen = 0;
            int i = 0;
            while (i < field.Length)
            {
                byte code = field[i];

                // A marker (code 00, X.25 7.1) ends the X.25 facilities: what follows is another set's coding, where C6
                // means something else. Everything from the marker on is kept as it is.
                if (code == 0x00)
                {
                    break;
                }

                // Element length from the class in bits 8 and 7 of the code (X.25 7.1): A = one parameter octet, B = two,
                // C = three, D = a length octet then that many.
                int length;
                switch (code >> 6)
                {
                    case 0: length = 2; break;
                    case 1: length = 3; break;
                    case 2: length = 4; break;
                    default: length = i + 1 < field.Length ? 2 + field[i + 1] : field.Length - i; break;
                }

                if (i + length > field.Length)
                {
                    // A truncated last element: not ours to repair, so it goes on as it came.
                    length = field.Length - i;
                }

                if (code != removedCode)
                {
                    Array.Copy(field, i, kept, keptLen, length);
                    keptLen += length;
                }

                i += length;
            }

            Array.Copy(field, i, kept, keptLen, field.Length - i);
            keptLen += field.Length - i;

            if (keptLen == field.Length)
            {
                return this;
            }

            byte[] result = new byte[keptLen];
            Array.Copy(kept, result, keptLen);
            return new X25CallParameters(WindowSizeOut, WindowSizeIn, PacketSizeOut, PacketSizeIn, result, _networkUtilities);
        }

        /// <summary>
        /// The facility code of NUI selection: 1100 0110 (X.25 Table 7-2, last row of its first page; parameter coding 7.3.7).
        /// </summary>
        public const byte NuiSelectionCode = 0xC6;

        /// <summary>
        /// The facility code of the charging information request: 0000 0100 (X.25 Table 7-2; parameter coding 7.3.8.1).
        /// </summary>
        public const byte ChargingRequestCode = 0x04;

        /// <summary>
        /// Encodes the NUI selection facility element (code 0xC6). The code is in class D (X.25 7.1: codes 0xC0 to 0xFF),
        /// so a length octet follows it, then the identifier.
        /// </summary>
        /// <param name="identifier">The NUI, at most 255 octets.</param>
        /// <returns>The element: 0xC6, length, identifier.</returns>
        public static byte[] NuiSelectionElement(byte[] identifier)
        {
            if (identifier.Length > 255)
            {
                throw new ArgumentException("a class D facility parameter is at most 255 octets", nameof(identifier));
            }

            byte[] e = new byte[2 + identifier.Length];
            e[0] = NuiSelectionCode;
            e[1] = (byte)identifier.Length;
            identifier.CopyTo(e, 2);
            return e;
        }

        /// <summary>
        /// Returns the values with each packet size limited to the given largest size.
        /// </summary>
        /// <param name="largestPacketSize">
        /// The largest packet size the line can carry.
        /// </param>
        /// <returns>
        /// The values with neither packet size above <paramref name="largestPacketSize"/>.
        /// </returns>
        public X25CallParameters WithPacketSizeAtMost(int largestPacketSize)
        {
            int packetOut = PacketSizeOut < largestPacketSize ? PacketSizeOut : largestPacketSize;
            int packetIn = PacketSizeIn < largestPacketSize ? PacketSizeIn : largestPacketSize;
            return new X25CallParameters(WindowSizeOut, WindowSizeIn, packetOut, packetIn, _otherFacilities, _networkUtilities);
        }
    }
}
