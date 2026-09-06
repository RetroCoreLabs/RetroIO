using System;

namespace RetroIO.Protocols.X25.Models
{
    /// <summary>
    /// The negotiated flow control parameters of one virtual call.
    /// </summary>
    /// <remarks>
    /// Directions are named the way X.25 names them, which is NOT "mine" and "yours": the first
    /// octet of each facility always describes traffic flowing FROM THE CALLED DTE, and the second
    /// traffic flowing FROM THE CALLING DTE, whichever end is reading it. So the caller's send
    /// window is <see cref="WindowFromCalling"/> and the answerer's send window is
    /// <see cref="WindowFromCalled"/>. Getting this backwards silently swaps the two windows on an
    /// asymmetric call.
    /// </remarks>
    public readonly struct X25FlowControlParameters
    {
        /// <summary>Window for data travelling from the called DTE (the answering end).</summary>
        public byte WindowFromCalled { get; }

        /// <summary>Window for data travelling from the calling DTE (the originating end).</summary>
        public byte WindowFromCalling { get; }

        /// <summary>Maximum user data field length, in octets, from the called DTE.</summary>
        public int PacketSizeFromCalled { get; }

        /// <summary>Maximum user data field length, in octets, from the calling DTE.</summary>
        public int PacketSizeFromCalling { get; }

        /// <summary>True when a window size facility was actually present.</summary>
        public bool HasWindowSize { get; }

        /// <summary>True when a packet size facility was actually present.</summary>
        public bool HasPacketSize { get; }

        public X25FlowControlParameters(
            byte windowFromCalled, byte windowFromCalling, bool hasWindowSize,
            int packetSizeFromCalled, int packetSizeFromCalling, bool hasPacketSize)
        {
            WindowFromCalled = windowFromCalled;
            WindowFromCalling = windowFromCalling;
            HasWindowSize = hasWindowSize;
            PacketSizeFromCalled = packetSizeFromCalled;
            PacketSizeFromCalling = packetSizeFromCalling;
            HasPacketSize = hasPacketSize;
        }
    }

    /// <summary>
    /// Encodes and decodes the X.25 flow control parameter negotiation facilities - packet size and
    /// window size - in the facility field of call set-up packets.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this exists.</b> Until 2026-09-05 the XOT path hard-coded its packet-layer send window
    /// to 7 and sent no facilities at all. That is wrong twice over. X.25 6.10 says
    /// <i>"the standard window size W is 2 for each direction of data transmission"</i>, so a peer
    /// that was never told otherwise is entitled to authorise only 2 - and putting 7 packets into a
    /// window of 2 is a P(S) outside the window, which X.25 4.4.1.3 makes a local procedure error
    /// that resets the circuit. It only ever worked here because the demo switches happen to be
    /// configured with window_size = 7. RFC 1613 6.1 is also explicit that an XOT call packet
    /// "MUST contain both Packet Size and Window Size facilities".
    /// </para>
    /// <para>
    /// <b>Coding, from the standard rather than from memory.</b> X.25 7.2 Table 7-1 divides facility
    /// codes into four classes by bits 8 and 7 of the code octet, which is what says how many
    /// parameter octets follow: 00 one, 01 two, 10 three, 11 variable with a length octet. Both
    /// facilities here are class B (two octets):
    /// </para>
    /// <list type="bullet">
    ///   <item>packet size, code 01000010 - X.25 7.3.1.1: size from the called DTE in bits 4 to 1 of
    ///   the first octet, from the calling DTE in bits 4 to 1 of the second, bits 8 to 5 zero. The
    ///   four bits are "the logarithm to base 2 of the maximum number of octets of the data field"
    ///   (X.75 5.4.3.5, same coding), so 128 octets is 7.</item>
    ///   <item>window size, code 01000011 - X.25 7.3.1.2: window from the called DTE in bits 7 to 1
    ///   of the first octet, from the calling DTE in bits 7 to 1 of the second, bit 8 zero, binary
    ///   coded, "a value of zero is not allowed".</item>
    /// </list>
    /// </remarks>
    public static class X25FlowControlFacilities
    {
        /// <summary>Facility code for packet size (X.25 Table 7-2, 01000010).</summary>
        public const byte FACILITY_PACKET_SIZE = 0x42;

        /// <summary>Facility code for window size (X.25 Table 7-2, 01000011).</summary>
        public const byte FACILITY_WINDOW_SIZE = 0x43;

        /// <summary>
        /// The window every network must provide, and the one to assume when nothing was negotiated.
        /// </summary>
        /// <remarks>
        /// X.25 6.10: "the default window sizes for normal and extended sequence numbering are 2".
        /// 7.3.1.2 adds "All Administrations will provide a window size of 2". This is the safe
        /// assumption for a peer that sent us no window size facility - never 7.
        /// </remarks>
        public const byte DEFAULT_WINDOW_SIZE = 2;

        /// <summary>
        /// The packet size every network must provide (X.25 6.9), in octets.
        /// </summary>
        public const int DEFAULT_PACKET_SIZE = 128;

        /// <summary>
        /// Builds a facility field carrying both flow control facilities.
        /// </summary>
        /// <param name="windowFromCalled">Window for traffic from the called DTE, 1 to 7 for modulo 8.</param>
        /// <param name="windowFromCalling">Window for traffic from the calling DTE, 1 to 7 for modulo 8.</param>
        /// <param name="packetSizeFromCalled">Data field length from the called DTE, a power of two from 16 to 4096.</param>
        /// <param name="packetSizeFromCalling">Data field length from the calling DTE, a power of two from 16 to 4096.</param>
        /// <returns>The facility field, ready to hand to the call packet builder.</returns>
        /// <exception cref="ArgumentOutOfRangeException">A window is zero, or a packet size is not a usable power of two.</exception>
        public static byte[] Encode(
            byte windowFromCalled,
            byte windowFromCalling,
            int packetSizeFromCalled = DEFAULT_PACKET_SIZE,
            int packetSizeFromCalling = DEFAULT_PACKET_SIZE)
        {
            // "A value of zero is not allowed" - X.25 7.3.1.2. Bit 8 must stay clear, so 127 is the
            // widest value the field can carry at all.
            if (windowFromCalled == 0 || windowFromCalled > 127)
                throw new ArgumentOutOfRangeException(nameof(windowFromCalled), windowFromCalled, "Window size must be 1 to 127.");
            if (windowFromCalling == 0 || windowFromCalling > 127)
                throw new ArgumentOutOfRangeException(nameof(windowFromCalling), windowFromCalling, "Window size must be 1 to 127.");

            byte calledLog = PacketSizeToLog2(packetSizeFromCalled, nameof(packetSizeFromCalled));
            byte callingLog = PacketSizeToLog2(packetSizeFromCalling, nameof(packetSizeFromCalling));

            return new byte[]
            {
                FACILITY_PACKET_SIZE, calledLog, callingLog,
                FACILITY_WINDOW_SIZE, windowFromCalled, windowFromCalling
            };
        }

        /// <summary>
        /// Reads the flow control facilities out of a facility field.
        /// </summary>
        /// <param name="facilities">The facility field of a call set-up packet; may be empty.</param>
        /// <returns>
        /// What was found. When a facility is absent its <c>Has...</c> flag is false and the value
        /// carries the standard default, so a caller can use the result unconditionally.
        /// </returns>
        /// <remarks>
        /// Walks the whole field by class rather than scanning for the two codes we care about: a
        /// facility we do not understand still has a known length from bits 8 and 7 of its code, so
        /// stepping over it correctly is what keeps us aligned on the ones we do want. Scanning for
        /// a byte value would happily match a parameter octet of some other facility.
        /// </remarks>
        public static X25FlowControlParameters Decode(ReadOnlySpan<byte> facilities)
        {
            byte windowCalled = DEFAULT_WINDOW_SIZE;
            byte windowCalling = DEFAULT_WINDOW_SIZE;
            int packetCalled = DEFAULT_PACKET_SIZE;
            int packetCalling = DEFAULT_PACKET_SIZE;
            bool haveWindow = false;
            bool havePacket = false;

            int i = 0;
            while (i < facilities.Length)
            {
                byte code = facilities[i];

                // X.25 Table 7-1: bits 8 and 7 give the parameter length.
                int paramLength;
                int paramStart = i + 1;
                switch (code & 0xC0)
                {
                    case 0x00: paramLength = 1; break;
                    case 0x40: paramLength = 2; break;
                    case 0x80: paramLength = 3; break;
                    default:
                        // Class D: the octet after the code is the length of what follows it.
                        if (paramStart >= facilities.Length)
                            return Build();
                        paramLength = facilities[paramStart];
                        paramStart++;
                        break;
                }

                if (paramStart + paramLength > facilities.Length)
                    break; // Truncated field - stop rather than read past the end.

                if (code == FACILITY_WINDOW_SIZE && paramLength == 2)
                {
                    // Bit 8 of each octet must be zero, so mask it off rather than trusting the peer.
                    windowCalled = (byte)(facilities[paramStart] & 0x7F);
                    windowCalling = (byte)(facilities[paramStart + 1] & 0x7F);
                    haveWindow = true;
                }
                else if (code == FACILITY_PACKET_SIZE && paramLength == 2)
                {
                    packetCalled = Log2ToPacketSize(facilities[paramStart] & 0x0F);
                    packetCalling = Log2ToPacketSize(facilities[paramStart + 1] & 0x0F);
                    havePacket = true;
                }

                i = paramStart + paramLength;
            }

            return Build();

            X25FlowControlParameters Build() => new X25FlowControlParameters(
                windowCalled, windowCalling, haveWindow,
                packetCalled, packetCalling, havePacket);
        }

        /// <summary>
        /// Converts a data field length in octets to the base-2 logarithm the facility carries.
        /// </summary>
        private static byte PacketSizeToLog2(int octets, string parameterName)
        {
            switch (octets)
            {
                case 16: return 4;
                case 32: return 5;
                case 64: return 6;
                case 128: return 7;
                case 256: return 8;
                case 512: return 9;
                case 1024: return 10;
                case 2048: return 11;
                case 4096: return 12;
                default:
                    throw new ArgumentOutOfRangeException(parameterName, octets,
                        "Packet size must be a power of two from 16 to 4096 octets.");
            }
        }

        /// <summary>
        /// Converts the facility's base-2 logarithm back to a length in octets.
        /// </summary>
        /// <remarks>
        /// A value outside the range the standard defines is treated as the default rather than
        /// shifted blindly: 1 &lt;&lt; 15 from a corrupt field would otherwise become a packet size no
        /// buffer here expects.
        /// </remarks>
        private static int Log2ToPacketSize(int log2)
        {
            if (log2 < 4 || log2 > 12)
                return DEFAULT_PACKET_SIZE;
            return 1 << log2;
        }
    }
}
