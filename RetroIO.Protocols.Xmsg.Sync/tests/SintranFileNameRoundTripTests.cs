using System;

using RetroIO.Protocols.Xmsg.Sync;

using Xunit;

namespace RetroIO.Protocols.Xmsg.Sync.Tests
{
    /// <summary>
    /// Names crossing between Windows and SINTRAN in both directions.
    /// </summary>
    /// <remarks>
    /// <para><b>The separator swaps with the direction, and nothing else does</b></para>
    /// <para>
    /// <c>A.MODE</c> goes out as <c>A:MODE</c> and comes back as <c>A.MODE</c>. A round trip that
    /// does not return the name it started with means an edit stops arriving, or a build output
    /// lands under a name nothing local can open.
    /// </para>
    /// <para><b>The limits are from the manual, not from a capture</b></para>
    /// <para>
    /// <c>ND-60.050.06 SINTRAN III Users Guide</c>: "filename may consist of 1 to 16 characters,
    /// filetype may consist of 1 to 4 characters". A capture would only show which names happened
    /// to be used.
    /// </para>
    /// </remarks>
    public sealed class SintranFileNameRoundTripTests
    {
        /// <summary>
        /// A name survives the trip out and back unchanged.
        /// </summary>
        [Theory]
        [InlineData("A.MODE", "A", "MODE")]
        [InlineData("B.LIST", "B", "LIST")]
        [InlineData("C.SYMB", "C", "SYMB")]
        [InlineData("LOAD-MODE.BATC", "LOAD-MODE", "BATC")]
        [InlineData("PROGRAM.BPUN", "PROGRAM", "BPUN")]
        public void ANameSurvivesTheRoundTrip(string windows, string name, string type)
        {
            string outName;
            string outType;
            string problem;

            Assert.True(SintranFileName.TryConvert(windows, out outName, out outType, out problem), problem);
            Assert.Equal(name, outName);
            Assert.Equal(type, outType);

            // Out to the machine: the colon form.
            Assert.Equal(name + ":" + type, SintranFileName.ToFileSpec(outName, outType));

            // Back from the machine: the dot form, and the name we started with.
            string backName;
            string backType;
            Assert.True(SintranFileName.TryParseFileSpec(
                name + ":" + type, out backName, out backType, out problem), problem);
            Assert.Equal(windows, SintranFileName.ToWindowsFileName(backName, backType));
        }

        /// <summary>
        /// A type over four characters is refused in BOTH directions.
        /// </summary>
        /// <remarks>
        /// Ronny's requirement: do not transfer a file whose type will not fit. Refusing is the
        /// whole point - a truncated type puts the file where the compiler will not look for it.
        /// </remarks>
        [Fact]
        public void ATypeOverFourCharactersIsRefusedBothWays()
        {
            string name;
            string type;
            string problem;

            Assert.False(SintranFileName.TryConvert(
                "PROGRAM.SYMBOL", out name, out type, out problem));
            Assert.Contains("4", problem);

            Assert.False(SintranFileName.TryParseFileSpec(
                "PROGRAM:SYMBOL", out name, out type, out problem));
            Assert.Contains("4", problem);

            // Exactly four is fine.
            Assert.True(SintranFileName.TryConvert("PROGRAM.SYMB", out name, out type, out problem));
            Assert.Equal("SYMB", type);
        }

        /// <summary>
        /// A name over sixteen characters is refused in BOTH directions.
        /// </summary>
        [Fact]
        public void ANameOverSixteenCharactersIsRefusedBothWays()
        {
            string name;
            string type;
            string problem;

            // Seventeen characters.
            Assert.False(SintranFileName.TryConvert(
                "ABCDEFGHIJKLMNOPQ.SYMB", out name, out type, out problem));
            Assert.Contains("16", problem);

            Assert.False(SintranFileName.TryParseFileSpec(
                "ABCDEFGHIJKLMNOPQ:SYMB", out name, out type, out problem));
            Assert.Contains("16", problem);

            // Exactly sixteen is fine.
            Assert.True(SintranFileName.TryConvert(
                "ABCDEFGHIJKLMNOP.SYMB", out name, out type, out problem));
            Assert.Equal("ABCDEFGHIJKLMNOP", name);
        }

        /// <summary>
        /// The two directions split on different separators, and a second dot is refused.
        /// </summary>
        /// <remarks>
        /// <para>
        /// A Windows name may hold several dots, so only the LAST one separates the type. A
        /// SINTRAN name cannot hold a colon at all, so the FIRST one does.
        /// </para>
        /// <para>
        /// That leaves <c>A.B.SYMB</c>: the type is <c>SYMB</c> and the name would be <c>A.B</c>,
        /// which SINTRAN cannot hold - only letters, digits and the hyphen are legal. It is
        /// REFUSED with that reason rather than having the dot quietly swapped for something else,
        /// because the caller then chooses a name instead of discovering one.
        /// </para>
        /// </remarks>
        [Fact]
        public void TheSplitRuleDiffersWithTheDirection()
        {
            string name;
            string type;
            string problem;

            // The LAST dot separates, so the type is SYMB and the name is "A.B" - which is not a
            // name SINTRAN can hold.
            Assert.False(SintranFileName.TryConvert("A.B.SYMB", out name, out type, out problem));
            Assert.Contains(".", problem);

            // The FIRST colon separates coming back.
            Assert.True(SintranFileName.TryParseFileSpec("A:SYMB", out name, out type, out problem));
            Assert.Equal("A", name);
            Assert.Equal("SYMB", type);

            // A hyphen is legal in both directions, which is the normal way ND names are written.
            Assert.True(SintranFileName.TryConvert("A-B.SYMB", out name, out type, out problem));
            Assert.Equal("A-B", name);
            Assert.Equal("A-B.SYMB", SintranFileName.ToWindowsFileName(name, type));
        }

        /// <summary>
        /// A file with no type at all keeps having none.
        /// </summary>
        [Fact]
        public void AFileWithNoTypeIsCarriedWithoutOne()
        {
            string name;
            string type;
            string problem;

            Assert.True(SintranFileName.TryConvert("README", out name, out type, out problem));
            Assert.Equal("README", name);
            Assert.Equal(string.Empty, type);
            Assert.Equal("README", SintranFileName.ToFileSpec(name, type));
            Assert.Equal("README", SintranFileName.ToWindowsFileName(name, type));
        }

        /// <summary>
        /// A name the machine could not have produced is refused rather than carried to Windows.
        /// </summary>
        [Fact]
        public void RubbishComingBackIsRefused()
        {
            string name;
            string type;
            string problem;

            Assert.False(SintranFileName.TryParseFileSpec(
                string.Empty, out name, out type, out problem));
            Assert.False(SintranFileName.TryParseFileSpec(
                ":SYMB", out name, out type, out problem));
            Assert.False(SintranFileName.TryParseFileSpec(
                "NAME WITH SPACES:SYMB", out name, out type, out problem));

            Assert.Throws<ArgumentNullException>(
                () => SintranFileName.TryParseFileSpec(null!, out name, out type, out problem));
        }
    }
}
