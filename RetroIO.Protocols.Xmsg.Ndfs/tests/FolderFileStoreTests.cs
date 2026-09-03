using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

using RetroIO.Protocols.Xmsg.Ndfs;

using Xunit;

namespace RetroIO.Protocols.Xmsg.Ndfs.Tests
{
    /// <summary>
    /// Gate for the file store behind the FA file server: SINTRAN name parsing, the stable
    /// file-number promise, and the refusal to serve anything outside the folder it was given.
    /// </summary>
    public sealed class FolderFileStoreTests : IDisposable
    {
        private readonly string _folder;
        private readonly FolderFileStore _store;

        /// <summary>
        /// Creates a store over a fresh temporary folder.
        /// </summary>
        public FolderFileStoreTests()
        {
            _folder = Path.Combine(Path.GetTempPath(), "xmsg-store-" + Guid.NewGuid().ToString("N"));
            _store = new FolderFileStore(_folder);
        }

        /// <summary>
        /// Removes the temporary folder.
        /// </summary>
        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_folder))
                {
                    Directory.Delete(_folder, recursive: true);
                }
            }
            catch (IOException)
            {
                // A leftover temp folder is not worth failing a test over.
            }
        }

        /// <summary>
        /// The quotes wrap the WHOLE specification, user part included - never just the name.
        /// </summary>
        [Fact]
        public void FileName_QuotesWrapTheWholeSpecificationIncludingTheUser()
        {
            Assert.True(FaFileName.TryParse("\"(SYSTEM)MYFILE:DATA\"", out FaFileName? parsed));
            Assert.NotNull(parsed);
            Assert.Equal("SYSTEM", parsed!.User);
            Assert.Equal("MYFILE", parsed.Name);
            Assert.Equal("DATA", parsed.Type);
            Assert.Equal("(SYSTEM)MYFILE:DATA", parsed.ToString());
        }

        /// <summary>
        /// The user and the type are both optional.
        /// </summary>
        [Fact]
        public void FileName_UserAndTypeAreOptional()
        {
            Assert.True(FaFileName.TryParse("MYFILE", out FaFileName? bare));
            Assert.Null(bare!.User);
            Assert.Null(bare.Type);
            Assert.Equal("MYFILE", bare.Name);

            Assert.True(FaFileName.TryParse("MYFILE:SYMB", out FaFileName? typed));
            Assert.Null(typed!.User);
            Assert.Equal("SYMB", typed.Type);
        }

        /// <summary>
        /// A type is at most FOUR characters - the width of the 4 ASCII characters an NDFS object
        /// entry stores at bytes 18-21.
        /// </summary>
        [Fact]
        public void FileName_TypeLongerThanFourCharactersIsRefused()
        {
            Assert.True(FaFileName.TryParse("MYFILE:DATA", out FaFileName? _));
            Assert.False(FaFileName.TryParse("MYFILE:TOOLONG", out FaFileName? _));
        }

        /// <summary>
        /// A file number stays with its file, and a DELETED file's number is never handed to a
        /// different file. A sync tool caches these, so reuse would silently corrupt it.
        /// </summary>
        [Fact]
        public void FileNumbers_AreStableAndDeletedNumbersAreNeverReused()
        {
            Assert.Equal(FaStoreStatus.Ok, _store.Create("ALPHA:DATA", out ushort alpha));
            Assert.Equal(FaStoreStatus.Ok, _store.Create("BETA:DATA", out ushort beta));
            Assert.NotEqual(alpha, beta);

            // Same file, asked for again - same number.
            IReadOnlyList<FaFileInfo> listed = _store.ListFiles();
            ushort alphaAgain = 0;
            for (int i = 0; i < listed.Count; i++)
            {
                if (string.Equals(listed[i].Name.Name, "ALPHA", StringComparison.OrdinalIgnoreCase))
                {
                    alphaAgain = listed[i].FileNumber;
                }
            }

            Assert.Equal(alpha, alphaAgain);

            // Delete ALPHA and create a new file: it must NOT inherit ALPHA's number.
            Assert.Equal(FaStoreStatus.Ok, _store.Delete("ALPHA:DATA"));
            Assert.Equal(FaStoreStatus.Ok, _store.Create("GAMMA:DATA", out ushort gamma));

            Assert.NotEqual(alpha, gamma);
            Assert.NotEqual(beta, gamma);
        }

        /// <summary>
        /// A name that tries to climb out of the served folder is refused outright, not sanitised.
        /// </summary>
        [Fact]
        public void Names_ThatEscapeTheServedFolderAreRefused()
        {
            Assert.Equal(FaStoreStatus.BadName, _store.Create("../ESCAPE:DATA", out ushort _));
            Assert.Equal(FaStoreStatus.BadName, _store.Create("..\\ESCAPE:DATA", out ushort _));
            Assert.Equal(FaStoreStatus.BadName, _store.Open("../ESCAPE:DATA", forWrite: false, out ushort _));
            Assert.Equal(FaStoreStatus.BadName, _store.Delete("sub/dir:DATA"));
        }

        /// <summary>
        /// Write then read back, byte for byte, at an offset.
        /// </summary>
        [Fact]
        public void WriteThenRead_RoundTripsBytes()
        {
            Assert.Equal(FaStoreStatus.Ok, _store.Create("DATA:DATA", out ushort _));
            Assert.Equal(FaStoreStatus.Ok, _store.Open("DATA:DATA", forWrite: true, out ushort write));

            byte[] payload = Encoding.ASCII.GetBytes("HELLO FROM D19999");
            Assert.Equal(FaStoreStatus.Ok, _store.Write(write, 0, payload));
            Assert.Equal(FaStoreStatus.Ok, _store.Close(write));

            Assert.Equal(FaStoreStatus.Ok, _store.Open("DATA:DATA", forWrite: false, out ushort read));

            byte[] buffer = new byte[payload.Length];
            Assert.Equal(FaStoreStatus.Ok, _store.Read(read, 0, buffer, out int got));
            Assert.Equal(payload.Length, got);
            Assert.Equal(payload, buffer);

            // Reading past the end is zero bytes, NOT an error.
            Assert.Equal(FaStoreStatus.Ok, _store.Read(read, payload.Length + 10, buffer, out int past));
            Assert.Equal(0, past);

            Assert.Equal(FaStoreStatus.Ok, _store.Close(read));
        }

        /// <summary>
        /// A file opened for reading refuses a write.
        /// </summary>
        [Fact]
        public void WritingToAReadHandle_IsRefused()
        {
            Assert.Equal(FaStoreStatus.Ok, _store.Create("RO:DATA", out ushort _));
            Assert.Equal(FaStoreStatus.Ok, _store.Open("RO:DATA", forWrite: false, out ushort handle));

            Assert.Equal(FaStoreStatus.WrongAccess, _store.Write(handle, 0, new byte[] { 1, 2, 3 }));
            Assert.Equal(FaStoreStatus.Ok, _store.Close(handle));
        }

        /// <summary>
        /// The obvious error cases are distinct statuses rather than one catch-all.
        /// </summary>
        [Fact]
        public void MissingFilesAndStaleHandles_ReportDistinctStatuses()
        {
            Assert.Equal(FaStoreStatus.NoSuchFile, _store.Open("GHOST:DATA", forWrite: false, out ushort _));
            Assert.Equal(FaStoreStatus.NoSuchFile, _store.Delete("GHOST:DATA"));
            Assert.Equal(FaStoreStatus.NotOpen, _store.Close(9999));
            Assert.Equal(FaStoreStatus.NotOpen, _store.Read(9999, 0, new byte[4], out int _));

            Assert.Equal(FaStoreStatus.Ok, _store.Create("TWICE:DATA", out ushort _));
            Assert.Equal(FaStoreStatus.AlreadyExists, _store.Create("TWICE:DATA", out ushort _));
        }

        /// <summary>
        /// A host file whose extension is longer than a SINTRAN type is reported whole rather than
        /// truncated into what would be a different file.
        /// </summary>
        [Fact]
        public void HostFileWithOverlongExtension_IsNotTruncated()
        {
            File.WriteAllText(Path.Combine(_folder, "README.markdown"), "x");

            IReadOnlyList<FaFileInfo> listed = _store.ListFiles();
            FaFileInfo entry = Assert.Single(listed);

            Assert.Equal("README.markdown", entry.Name.Name);
            Assert.Null(entry.Name.Type);
        }
    }
}
