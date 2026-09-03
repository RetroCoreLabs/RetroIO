using RetroIO.Protocols.Xmsg.Sync;

using Xunit;

namespace RetroIO.Protocols.Xmsg.Sync.Tests
{
    /// <summary>
    /// The two forms of a file specification: the one a person types, and the one the wire carries.
    /// </summary>
    /// <remarks>
    /// Found live on 2026-08-11. The daemon handed the addressed form to the open request and it
    /// was refused - "27 characters" against a fifteen-byte compact string - and the exception took
    /// the whole node down with it.
    /// </remarks>
    public sealed class SyncWireNameTests
    {
        /// <summary>
        /// The machine, the user and the quotes all come off.
        /// </summary>
        [Fact]
        public void AnAddressedQuotedSpecReducesToTheBareName()
        {
            Assert.Equal("WATCH1:TXT", SyncFolderMap.ToWireName("D100(SYSTEM).\"WATCH1:TXT\""));
        }

        /// <summary>
        /// The unquoted form - an overwrite - reduces the same way.
        /// </summary>
        [Fact]
        public void AnAddressedBareSpecReducesToTheBareName()
        {
            Assert.Equal("WATCH1:TXT", SyncFolderMap.ToWireName("D100(SYSTEM).WATCH1:TXT"));
        }

        /// <summary>
        /// A name that is already bare is left alone.
        /// </summary>
        [Fact]
        public void ANameThatIsAlreadyBareIsUnchanged()
        {
            Assert.Equal("WATCH1:TXT", SyncFolderMap.ToWireName("WATCH1:TXT"));
        }

        /// <summary>
        /// What BuildFileSpec produces is what ToWireName has to undo - both ways round.
        /// </summary>
        /// <remarks>
        /// Pinning the pair together, so a change to one that the other does not follow fails here
        /// rather than against a live machine.
        /// </remarks>
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void ItUndoesWhatBuildFileSpecDid(bool creating)
        {
            string built = SyncFolderMap.BuildFileSpec("D100", "SYSTEM", "PROG", "SYMB", creating);

            Assert.Equal("PROG:SYMB", SyncFolderMap.ToWireName(built));
        }

        /// <summary>
        /// The user survives the trip, because something still has to send it.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <see cref="SyncFolderMap.ToWireName"/> strips the user because the OPEN request does
        /// not carry it. That is only half the job: the machine learns who is asking from the
        /// ReserveFileEntry request, so the user has to be recoverable and put on the endpoint.
        /// </para>
        /// <para>
        /// Until 2026-08-24 nothing did that. The daemon logged
        /// <c>create D100(UTILITY)."XSTART:MODE"</c>, FaWriteTarget kept its "SYSTEM" default, and
        /// the file arrived as <c>(SYSTEM)XSTART:MODE</c> - the log and the disk disagreed, and
        /// the log was the one being believed.
        /// </para>
        /// </remarks>
        [Theory]
        [InlineData("D100(UTILITY).\"XSTART:MODE\"", "UTILITY")]
        [InlineData("D100(SYSTEM).WATCH1:TXT", "SYSTEM")]
        [InlineData("D102(RT).PROG:SYMB", "RT")]
        public void TheUserIsRecoverableFromTheSpec(string spec, string expected)
        {
            Assert.Equal(expected, SyncFolderMap.ToUser(spec));
        }

        /// <summary>
        /// A specification naming no user gives an empty string, not a wrong one.
        /// </summary>
        /// <remarks>
        /// The caller leaves the endpoint's own default alone when this is empty. Returning
        /// something plausible here would put files in a user nobody asked for.
        /// </remarks>
        [Fact]
        public void ASpecWithNoUserGivesAnEmptyString()
        {
            Assert.Equal(string.Empty, SyncFolderMap.ToUser("WATCH1:TXT"));
        }

        /// <summary>
        /// Whatever BuildFileSpec put in, ToUser gets back out.
        /// </summary>
        /// <param name="creating">
        /// Whether the specification is the quoted create form.
        /// </param>
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void ItRecoversTheUserBuildFileSpecPutIn(bool creating)
        {
            string built = SyncFolderMap.BuildFileSpec("D100", "UTILITY", "PROG", "SYMB", creating);

            Assert.Equal("UTILITY", SyncFolderMap.ToUser(built));
        }
    }
}
