//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;
using System.IO;

using NUnit.Framework;

using RetroIO.Storage.Ndfs.Structures;

namespace RetroIO.Tests
{
    /// <summary>
    /// Bit-file bit ordering, checked against sources OUTSIDE this codebase.
    /// </summary>
    /// <remarks>
    /// <para><b>Why this fixture exists</b></para>
    /// Until 2026-08-02 this library addressed the bit file as "byte N/8, bit N%8". SINTRAN
    /// actually addresses it as a 16-bit WORD: page N is bit N%16 of word N/16. On a
    /// big-endian image those two differ by a byte swap inside every word.
    /// <para><b>Why the existing suite never caught it</b></para>
    ///  - Every test was a ROUND TRIP: written with convention X, read back with convention X.
    ///    That passes for any self-consistent X, including a wrong one. A symmetric error is
    ///    invisible to symmetric tests.
    ///  - The one check made against real data was a POPCOUNT comparison, and popcount is
    ///    invariant under byte-swapping — it could not fail for this bug even in principle,
    ///    yet it is what the notes cited as "VERIFIED".
    ///  - The tests were written from the same incorrect note as the code, so they asserted
    ///    the bug as though it were the specification.
    /// <para>
    /// Every test here is therefore ASYMMETRIC: it compares this code against something the
    /// project did not produce — ND's own manual, or a pack real SINTRAN wrote.
    /// </para>
    /// <para><b>Authority</b></para>
    /// SINTRAN III System Supervisor, ND-30.003.007 EN, appendix F.2 "Bit-File":
    /// <c>PAGE = BLOCK*400B + WORD*20B + BIT</c>. 20B is 16 decimal, so one 16-bit word maps
    /// 16 pages, and page x sits at bit 0. The Norwegian edition ND-30.003.7 NO carries the
    /// identical formula; SINTRAN's allocator forms the word index with <c>SHR 4</c>.
    /// </remarks>
    [TestFixture]
    public class NdfsBitFileOrderingTests
    {
        /// <summary>
        /// Reproduces the worked example printed in ND-30.003.007 appendix F.2.
        /// </summary>
        /// <remarks>
        /// The manual shows one bit-file word holding 313B and labels which of the 16 pages it
        /// covers are Free and Used. 313B = 0b0000_0000_1100_1011, so bits 0, 1, 3, 6 and 7 are
        /// set. The vector comes from ND, not from us — under the old byte convention those two
        /// bytes decode to a different set of pages entirely.
        /// </remarks>
        [Test]
        public void ReproducesTheManualWorkedExampleForWord313Octal()
        {
            const int word = 0xCB;                       // 313 octal
            Assert.That(word, Is.EqualTo(0b11001011));

            BitFile bf = new BitFile();
            bf.Initialize(16);
            // Big-endian on disk: high byte first.
            bf.LoadBitmap(new byte[] { (byte)(word >> 8), (byte)(word & 0xFF) });

            HashSet<uint> expectedUsed = new HashSet<uint> { 0, 1, 3, 6, 7 };
            for (uint page = 0; page < 16; page++)
            {
                Assert.That(bf.IsBlockUsed(page), Is.EqualTo(expectedUsed.Contains(page)),
                    $"page {page}: the manual says "
                    + (expectedUsed.Contains(page) ? "USED" : "FREE") + " for word 313B");
            }
        }

        /// <summary>
        /// Checks the manual's formula directly: PAGE = BLOCK*400B + WORD*20B + BIT.
        /// </summary>
        /// <param name="block">
        /// Bit-file block number; 400B = 256 pages each.
        /// </param>
        /// <param name="word">
        /// Word within the block; 20B = 16 pages each.
        /// </param>
        /// <param name="bit">
        /// Bit within the word, 0 = least significant.
        /// </param>
        [TestCase(0, 0, 0)]
        [TestCase(0, 0, 15)]
        [TestCase(0, 1, 0)]
        [TestCase(0, 5, 9)]
        [TestCase(1, 0, 0)]
        [TestCase(1, 15, 15)]
        [TestCase(3, 7, 4)]
        public void SatisfiesTheManualPageFormula(int block, int word, int bit)
        {
            const int pagesPerBlock = 256;   // 400B
            const int pagesPerWord = 16;     // 20B

            uint page = (uint)(block * pagesPerBlock + word * pagesPerWord + bit);

            BitFile bf = new BitFile();
            bf.Initialize(4096);
            bf.MarkBlockUsed(page);

            byte[] raw = bf.GetBitmapData();
            int wordIndex = block * (pagesPerBlock / pagesPerWord) + word;
            int stored = (raw[wordIndex * 2] << 8) | raw[wordIndex * 2 + 1];

            Assert.That(stored, Is.EqualTo(1 << bit),
                $"page {page} (block {block}, word {word}, bit {bit})");
            Assert.That(bf.CalcUsedPages(), Is.EqualTo(1), "exactly one bit may be set");
        }

        /// <summary>
        /// Page 0 lives in the LOW byte of the first word, not the first byte.
        /// </summary>
        /// <remarks>
        /// The single assertion that most directly contradicts the old convention. A 16-bit
        /// big-endian word is stored high byte first, and page 0 is bit 0 — the least
        /// significant bit — so page 0 lands in byte 1.
        /// </remarks>
        [Test]
        public void PageZeroLivesInTheLowByte()
        {
            BitFile bf = new BitFile();
            bf.Initialize(64);
            bf.MarkBlockUsed(0);

            byte[] raw = bf.GetBitmapData();
            Assert.That(raw[0], Is.EqualTo(0x00), "page 0 must NOT be in the high byte");
            Assert.That(raw[1], Is.EqualTo(0x01));
        }

        /// <summary>
        /// Page 8 crosses into the HIGH byte of the same word.
        /// </summary>
        [Test]
        public void PageEightLivesInTheHighByte()
        {
            BitFile bf = new BitFile();
            bf.Initialize(64);
            bf.MarkBlockUsed(8);

            byte[] raw = bf.GetBitmapData();
            Assert.That(raw[0], Is.EqualTo(0x01));
            Assert.That(raw[1], Is.EqualTo(0x00));
        }

        /// <summary>
        /// Guards against a silent revert to the discredited byte scheme.
        /// </summary>
        /// <remarks>
        /// Computes what "byte N/8, bit N%8" would produce and asserts we do not produce it.
        /// This fails even if every round-trip test still passes — which is exactly the hole
        /// the original bug went through.
        /// </remarks>
        [Test]
        public void ActivelyRejectsTheOldByteConvention()
        {
            uint[] pages = new uint[] { 0, 3, 17, 40 };

            BitFile bf = new BitFile();
            bf.Initialize(64);
            for (int i = 0; i < pages.Length; i++)
                bf.MarkBlockUsed(pages[i]);

            byte[] actual = bf.GetBitmapData();

            byte[] oldScheme = new byte[actual.Length];
            for (int i = 0; i < pages.Length; i++)
                oldScheme[pages[i] / 8] |= (byte)(1 << (int)(pages[i] % 8));

            Assert.That(actual, Is.Not.EqualTo(oldScheme),
                "bitmap matches the old byte-per-8-pages convention, which SINTRAN does not use");
        }

        /// <summary>
        /// Every page of a device must be addressable, whatever its page count.
        /// </summary>
        /// <param name="totalPages">
        /// Device size in pages.
        /// </param>
        /// <remarks>
        /// A 616-page ND floppy is the real case that exposed this: ceil(616/8) = 77 bytes, but
        /// word addressing needs the 78th byte to exist or pages 608..615 fall off the end.
        /// </remarks>
        [TestCase(1u)]
        [TestCase(8u)]
        [TestCase(15u)]
        [TestCase(16u)]
        [TestCase(17u)]
        [TestCase(100u)]
        [TestCase(616u)]
        [TestCase(4095u)]
        [TestCase(38400u)]
        public void EveryPageIsAddressable(uint totalPages)
        {
            BitFile bf = new BitFile();
            bf.Initialize(totalPages);

            Assert.That(bf.GetBitmapData().Length % 2, Is.EqualTo(0),
                "the bitmap must be a whole number of 16-bit words");

            uint last = totalPages - 1;
            bf.MarkBlockUsed(last);
            Assert.That(bf.IsBlockUsed(last), Is.True, $"page {last} of {totalPages} unreachable");
            Assert.That(bf.CalcUsedPages(), Is.EqualTo(1));
        }

        /// <summary>
        /// No two pages may alias onto the same bit.
        /// </summary>
        [Test]
        public void PagesDoNotAlias()
        {
            const uint total = 616;
            BitFile bf = new BitFile();
            bf.Initialize(total);

            for (uint page = 0; page < total; page++)
            {
                bf.MarkBlockUsed(page);
                Assert.That(bf.CalcUsedPages(), Is.EqualTo(1), $"page {page} set more than one bit");
                bf.MarkBlockFree(page);
                Assert.That(bf.IsBlockUsed(page), Is.False);
            }
        }
    }
}
