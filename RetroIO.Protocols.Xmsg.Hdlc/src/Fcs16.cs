using System;

namespace RetroIO.Protocols.Xmsg.Hdlc
{
    /// <summary>
    /// Reflected CRC-16-CCITT frame-check-sequence helper used by HDLC/LAPB framing.
    /// </summary>
    /// <remarks>
    /// <para><b>Algorithm</b></para>
    /// HDLC feeds bits least-significant first, so the reflected form of CRC-CCITT is
    /// used (XMSG-PROTOCOL.md section 2):
    ///  - polynomial <c>0x8408</c> (bit reversal of <c>0x1021</c>).
    ///  - initial value <c>0xFFFF</c>.
    ///  - the transmitted FCS is the one's complement of the running CRC, stored low
    ///    byte first.
    /// A frame is <c>addr(1) + ctrl(1) + info(0..n) + fcs_lo(1) + fcs_hi(1)</c>; it is
    /// valid when the CRC folded over the body (all bytes except the last two) and
    /// one's-complemented equals the received FCS word. Equivalently, folding the CRC
    /// over the whole frame including the two FCS bytes yields the good residue
    /// <c>0xF0B8</c>.
    /// </remarks>
    public static class Fcs16
    {
        /// <summary>
        /// Reflected CRC-16-CCITT polynomial (bit reversal of <c>0x1021</c>).
        /// </summary>
        private const ushort Polynomial = 0x8408;

        /// <summary>
        /// The residue a whole valid frame (body plus its two FCS bytes) folds to.
        /// </summary>
        public const ushort GoodResidue = 0xF0B8;

        /// <summary>
        /// Folds the reflected CRC-16 over a run of bytes, starting from <c>0xFFFF</c>.
        /// </summary>
        /// <param name="data">
        /// The bytes to fold the CRC over.
        /// </param>
        /// <returns>
        /// The running CRC value (not yet one's-complemented).
        /// </returns>
        public static ushort Compute(ReadOnlySpan<byte> data)
        {
            ushort crc = 0xFFFF;
            for (int i = 0; i < data.Length; i++)
            {
                crc = (ushort)(crc ^ data[i]);
                for (int bit = 0; bit < 8; bit++)
                {
                    if ((crc & 0x0001) != 0)
                    {
                        crc = (ushort)((crc >> 1) ^ Polynomial);
                    }
                    else
                    {
                        crc = (ushort)(crc >> 1);
                    }
                }
            }

            return crc;
        }

        /// <summary>
        /// Validates the trailing FCS of an unstuffed LAPB frame.
        /// </summary>
        /// <param name="frame">
        /// The full unstuffed frame: <c>addr + ctrl + info + fcs_lo + fcs_hi</c>. Must be
        /// at least four bytes (address, control and the two FCS bytes).
        /// </param>
        /// <returns>
        /// <c>true</c> when the recomputed FCS matches the two trailing FCS bytes.
        /// </returns>
        public static bool IsValid(ReadOnlySpan<byte> frame)
        {
            if (frame.Length < 4)
            {
                return false;
            }

            int bodyLength = frame.Length - 2;
            ushort received = (ushort)(frame[bodyLength] | (frame[bodyLength + 1] << 8));
            ushort calculated = (ushort)(Compute(frame.Slice(0, bodyLength)) ^ 0xFFFF);
            return received == calculated;
        }
    }
}
