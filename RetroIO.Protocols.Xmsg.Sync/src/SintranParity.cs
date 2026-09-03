using System;

namespace RetroIO.Protocols.Xmsg.Sync
{
    /// <summary>
    /// The parity bit SINTRAN text files carry in bit 7, and how to take it off.
    /// </summary>
    /// <remarks>
    /// <para><b>Measured, not assumed</b></para>
    /// <para>
    /// From 587 contiguous bytes of a real file read off a live SINTRAN
    /// (<c>DOC/captures/FA-READ-WRITE-2026-08-04/capture-read.txt</c>), written up in
    /// <c>DOC/SINTRAN-FILE-PARITY-BIT-MEASURED-2026-08-09.md</c>:
    /// </para>
    ///  - Bit 7 is set when the low seven bits hold an ODD number of ones, so the total is even.
    ///    That is EVEN parity.
    ///  - It is content-determined: the same string appears three times in that file with an
    ///    identical bit-7 pattern each time.
    ///  - <b>The same file also carries plain, unparitied text.</b> 120 of the 587 bytes are
    ///    characters parity WOULD have marked, left alone. The first 53 bytes conform completely
    ///    and the rest only 78%.
    /// <para><b>Why there is no per-extension rule here</b></para>
    /// <para>
    /// Task #33 originally asked for parity set and cleared automatically per file ending. The
    /// mixture above is INSIDE one file, so no rule keyed on <c>:SYMB</c> or <c>:LIST</c> can
    /// describe it, and a table like that would be wrong on its own evidence. Hence: strip, which
    /// always works, and do not write parity back unless someone has shown a tool needs it.
    /// </para>
    /// <para>
    /// <see cref="Strip"/> is safe on any text, parity-marked or not - it is what SINTRAN's own
    /// programs do (<c>BZERO 7</c>, "CLEAR PARITY BIT"). <see cref="ApplyEven"/> exists for
    /// completeness and is deliberately NOT used by default: nothing has yet been shown to
    /// require it, and writing it into a file that did not have it changes bytes for no proven
    /// reason.
    /// </para>
    /// </remarks>
    public static class SintranParity
    {
        /// <summary>
        /// Clears bit 7 across a buffer, recovering plain 7-bit text.
        /// </summary>
        /// <param name="data">
        /// The bytes to strip, edited in place.
        /// </param>
        /// <returns>
        /// How many bytes actually had bit 7 set.
        /// </returns>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="data"/> is null.
        /// </exception>
        /// <remarks>
        /// Safe whether or not the content carries parity: a byte that never had bit 7 set is
        /// left exactly as it was. The count is returned so a caller can report "this file did
        /// carry parity" without scanning twice.
        /// </remarks>
        public static int Strip(Span<byte> data)
        {
            int stripped = 0;
            for (int i = 0; i < data.Length; i++)
            {
                if ((data[i] & 0x80) != 0)
                {
                    data[i] = (byte)(data[i] & 0x7F);
                    stripped++;
                }
            }

            return stripped;
        }

        /// <summary>
        /// Sets bit 7 across a buffer to give every byte even parity.
        /// </summary>
        /// <param name="data">
        /// The bytes to mark, edited in place. Any bit 7 already present is recomputed, so the
        /// result does not depend on what was there.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="data"/> is null.
        /// </exception>
        /// <remarks>
        /// NOT used by default. See the remarks on <see cref="SintranParity"/>: real SINTRAN files
        /// hold a mixture of marked and plain text, so nothing so far shows this is needed, and
        /// applying it rewrites bytes on a guess. Turn it on only once some tool has been shown to
        /// require it.
        /// </remarks>
        public static void ApplyEven(Span<byte> data)
        {
            for (int i = 0; i < data.Length; i++)
            {
                byte low = (byte)(data[i] & 0x7F);
                data[i] = HasEvenParity(low) ? low : (byte)(low | 0x80);
            }
        }

        /// <summary>
        /// Counts how many bytes would change if even parity were applied.
        /// </summary>
        /// <param name="data">
        /// The bytes to examine. Not modified.
        /// </param>
        /// <returns>
        /// The number of bytes whose bit 7 does not match even parity of their low seven bits.
        /// Zero means the whole buffer already carries even parity.
        /// </returns>
        /// <remarks>
        /// For reporting what a file actually holds, rather than assuming. On the measured file
        /// this returns 120 out of 587 - which is what proved the content is a MIXTURE and killed
        /// the per-extension idea.
        /// </remarks>
        public static int CountParityMismatches(ReadOnlySpan<byte> data)
        {
            int mismatches = 0;
            for (int i = 0; i < data.Length; i++)
            {
                bool bitSet = (data[i] & 0x80) != 0;
                bool shouldBeSet = !HasEvenParity((byte)(data[i] & 0x7F));
                if (bitSet != shouldBeSet)
                {
                    mismatches++;
                }
            }

            return mismatches;
        }

        /// <summary>
        /// Whether a seven-bit value already holds an even number of one bits.
        /// </summary>
        /// <param name="low">
        /// The value, with bit 7 clear.
        /// </param>
        /// <returns>
        /// <see langword="true"/> when the population count is even, so no parity bit is needed.
        /// </returns>
        private static bool HasEvenParity(byte low)
        {
            // Fold the bits down onto bit 0. Cheaper than a loop and has no table to get wrong.
            int v = low;
            v ^= v >> 4;
            v ^= v >> 2;
            v ^= v >> 1;
            return (v & 1) == 0;
        }
    }
}
