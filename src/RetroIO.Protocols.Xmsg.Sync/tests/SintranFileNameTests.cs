//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;

using RetroIO.Protocols.Xmsg.Sync;

using Xunit;

namespace RetroIO.Protocols.Xmsg.Sync.Tests
{
    /// <summary>
    /// Mapping a Windows file name onto a legal SINTRAN name and type.
    /// </summary>
    /// <remarks>
    /// The limits are the manual's: name 1-16 characters, type 1-4, letters, digits and the
    /// hyphen. See <see cref="SintranFileName"/> for the citations.
    /// </remarks>
    public sealed class SintranFileNameTests
    {
        /// <summary>
        /// An ordinary source file becomes a name and a separate type.
        /// </summary>
        /// <remarks>
        /// The type is a FIELD on SINTRAN, not a suffix of the name. Carrying the extension into
        /// the name would produce a file the compiler cannot find by type.
        /// </remarks>
        [Fact]
        public void AnExtensionBecomesTheTypeNotPartOfTheName()
        {
            string name;
            string type;
            string problem;

            Assert.True(SintranFileName.TryConvert("Program.symb", out name, out type, out problem));

            Assert.Equal("PROGRAM", name);
            Assert.Equal("SYMB", type);
            Assert.Equal("PROGRAM:SYMB", SintranFileName.ToFileSpec(name, type));
        }

        /// <summary>
        /// A file with no extension converts with an empty type.
        /// </summary>
        [Fact]
        public void AFileWithNoExtensionHasNoType()
        {
            string name;
            string type;
            string problem;

            Assert.True(SintranFileName.TryConvert("MAKEFILE", out name, out type, out problem));

            Assert.Equal("MAKEFILE", name);
            Assert.Equal(string.Empty, type);
            Assert.Equal("MAKEFILE", SintranFileName.ToFileSpec(name, type));
        }

        /// <summary>
        /// Only the LAST dot separates the type.
        /// </summary>
        [Fact]
        public void OnlyTheFinalDotSeparatesTheType()
        {
            string name;
            string type;
            string problem;

            // "MY.PROG" is 7 characters and legal apart from the dot, so this also proves the
            // rejection below is about the character and not the length.
            Assert.False(SintranFileName.TryConvert("MY.PROG.symb", out name, out type, out problem));
            Assert.Contains("'.'", problem);
        }

        /// <summary>
        /// A name of exactly 16 characters is accepted; 17 is not.
        /// </summary>
        [Fact]
        public void TheSixteenCharacterLimitIsTheBoundary()
        {
            string name;
            string type;
            string problem;

            Assert.True(SintranFileName.TryConvert(
                "ABCDEFGHIJKLMNOP.symb", out name, out type, out problem));
            Assert.Equal("ABCDEFGHIJKLMNOP", name);

            Assert.False(SintranFileName.TryConvert(
                "ABCDEFGHIJKLMNOPQ.symb", out name, out type, out problem));
        }

        /// <summary>
        /// An over-long name is refused with a reason, and NOT truncated.
        /// </summary>
        /// <remarks>
        /// Truncating is the tempting behaviour and the wrong one: two local files that shorten to
        /// the same SINTRAN name would overwrite each other on the machine, and the symptom would
        /// be "my edits sometimes do not arrive" - about as hard to diagnose as it gets.
        /// </remarks>
        [Fact]
        public void AnOverLongNameIsRefusedWithAReasonRatherThanTruncated()
        {
            string name;
            string type;
            string problem;

            Assert.False(SintranFileName.TryConvert(
                "PROGRAM-WITH-A-VERY-LONG-NAME.symb", out name, out type, out problem));

            Assert.Equal(string.Empty, name);
            Assert.Contains("29", problem);          // says how long it actually is
            Assert.Contains("16", problem);          // and what the limit is
            Assert.Contains("overwrite", problem);   // and why truncating is not done for you
        }

        /// <summary>
        /// A type over four characters is refused.
        /// </summary>
        [Fact]
        public void AnOverLongTypeIsRefused()
        {
            string name;
            string type;
            string problem;

            Assert.False(SintranFileName.TryConvert("PROGRAM.SYMBOL", out name, out type, out problem));
            Assert.Contains("SYMBOL", problem);
            Assert.Contains("4", problem);
        }

        /// <summary>
        /// The hyphen is legal; underscores and spaces are not.
        /// </summary>
        /// <remarks>
        /// The hyphen matters because ND names use it everywhere - <c>NEW-SYST</c>,
        /// <c>ND-PATCH-SIN</c>. The underscore is the one a Windows developer will reach for by
        /// habit, so it needs a clear refusal rather than a silent substitution.
        /// </remarks>
        [Fact]
        public void HyphensAreLegalAndUnderscoresAreNot()
        {
            string name;
            string type;
            string problem;

            Assert.True(SintranFileName.TryConvert("NEW-SYST.symb", out name, out type, out problem));
            Assert.Equal("NEW-SYST", name);

            Assert.False(SintranFileName.TryConvert("NEW_SYST.symb", out name, out type, out problem));
            Assert.Contains("_", problem);

            Assert.False(SintranFileName.TryConvert("NEW SYST.symb", out name, out type, out problem));
        }

        /// <summary>
        /// Digits are legal, and the name is upper-cased.
        /// </summary>
        [Fact]
        public void NamesAreUpperCasedAndDigitsAreLegal()
        {
            string name;
            string type;
            string problem;

            Assert.True(SintranFileName.TryConvert("test1.Symb", out name, out type, out problem));
            Assert.Equal("TEST1", name);
            Assert.Equal("SYMB", type);
        }

        /// <summary>
        /// An empty name, or one that is only an extension, is refused.
        /// </summary>
        [Fact]
        public void EmptyAndExtensionOnlyNamesAreRefused()
        {
            string name;
            string type;
            string problem;

            Assert.False(SintranFileName.TryConvert(string.Empty, out name, out type, out problem));
            Assert.False(SintranFileName.TryConvert(".symb", out name, out type, out problem));
        }

        /// <summary>
        /// The file specification carries no quotes.
        /// </summary>
        /// <remarks>
        /// Quoting differs per command - COPY-FILE wants the whole specification quoted, while
        /// CREATE-FILE and RENAME-FILE want it bare - so it belongs to the caller. Adding quotes
        /// here would be wrong for half of them.
        /// </remarks>
        [Fact]
        public void TheFileSpecIsNotQuoted()
        {
            Assert.Equal("PROGRAM:SYMB", SintranFileName.ToFileSpec("PROGRAM", "SYMB"));
            Assert.DoesNotContain("\"", SintranFileName.ToFileSpec("PROGRAM", "SYMB"));
        }

        /// <summary>
        /// Nulls are rejected.
        /// </summary>
        [Fact]
        public void NullsAreRejected()
        {
            string name;
            string type;
            string problem;

            Assert.Throws<ArgumentNullException>(
                () => SintranFileName.TryConvert(null!, out name, out type, out problem));
            Assert.Throws<ArgumentNullException>(() => SintranFileName.ToFileSpec(null!, "SYMB"));
            Assert.Throws<ArgumentNullException>(() => SintranFileName.ToFileSpec("PROGRAM", null!));
        }
    }
}
