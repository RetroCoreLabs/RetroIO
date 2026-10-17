//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.IO;

using RetroIO.Protocols.Xmsg.Node;
using RetroIO.Protocols.Xmsg.Node.Services;

using Xunit;

namespace RetroIO.Protocols.Xmsg.Node.Tests
{
    /// <summary>
    /// Remembering the envelope seed, which is what lets this node address a machine it has met
    /// before without waiting to be spoken to.
    /// </summary>
    /// <remarks>
    /// The seed is a per-link CONSTANT - verified 0x14 for 100 to and from 102 across every
    /// session, reconnect and reboot in the capture corpus - which is the only reason storing it is
    /// sound. Nothing here computes or defaults one.
    /// </remarks>
    public sealed class LinkSeedMemoryTests
    {
        private sealed class MemorySeedStore : ILinkSeedStore
        {
            private readonly System.Collections.Generic.Dictionary<ushort, byte> _seeds
                = new System.Collections.Generic.Dictionary<ushort, byte>();

            public bool TryLoadSeed(ushort remoteNode, out byte seed)
            {
                return _seeds.TryGetValue(remoteNode, out seed);
            }

            public void SaveSeed(ushort remoteNode, byte seed)
            {
                _seeds[remoteNode] = seed;
            }
        }

        /// <summary>
        /// A node never heard from cannot be addressed, and no seed is invented for it.
        /// </summary>
        /// <remarks>
        /// The honest half. Guessing would put a Counter on the wire that the peer cannot make
        /// sense of, which is worse than saying no.
        /// </remarks>
        [Fact]
        public void ANodeNeverHeardFromStaysUnreachable()
        {
            XmsgServerHost host = new XmsgServerHost(19999, new NullResponderSequenceStore());
            host.SeedStore = new MemorySeedStore();

            Assert.False(host.OpenLinkFromRememberedSeed(100));
            Assert.False(host.CanReach(100));
        }

        /// <summary>
        /// With a remembered seed the link opens without anything being received first.
        /// </summary>
        [Fact]
        public void ARememberedSeedOpensTheLink()
        {
            MemorySeedStore seeds = new MemorySeedStore();
            seeds.SaveSeed(100, 0x14);

            XmsgServerHost host = new XmsgServerHost(19999, new NullResponderSequenceStore());
            host.SeedStore = seeds;

            Assert.True(host.OpenLinkFromRememberedSeed(100));
            Assert.True(host.CanReach(100));
        }

        /// <summary>
        /// Without a seed store the node behaves exactly as it did before.
        /// </summary>
        [Fact]
        public void WithNoStoreNothingChanges()
        {
            XmsgServerHost host = new XmsgServerHost(19999, new NullResponderSequenceStore());

            Assert.False(host.OpenLinkFromRememberedSeed(100));
        }

        /// <summary>
        /// The file store survives being reopened, which is the whole point of writing it down.
        /// </summary>
        [Fact]
        public void TheFileStoreRemembersAcrossReopening()
        {
            string path = Path.Combine(Path.GetTempPath(), "xmsg-seed-test-" + Guid.NewGuid().ToString("N") + ".state");
            try
            {
                FileLinkSeedStore first = new FileLinkSeedStore(path);
                first.SaveSeed(100, 0x14);
                first.SaveSeed(103, 0x22);

                FileLinkSeedStore reopened = new FileLinkSeedStore(path);

                byte seed;
                Assert.True(reopened.TryLoadSeed(100, out seed));
                Assert.Equal(0x14, seed);
                Assert.True(reopened.TryLoadSeed(103, out seed));
                Assert.Equal(0x22, seed);
                Assert.False(reopened.TryLoadSeed(999, out seed));
            }
            finally
            {
                if (File.Exists(path)) { File.Delete(path); }
            }
        }

        /// <summary>
        /// A missing file is not an error - a first run has no seeds and must still start.
        /// </summary>
        [Fact]
        public void AMissingFileIsEmptyRatherThanFatal()
        {
            string path = Path.Combine(Path.GetTempPath(), "xmsg-seed-missing-" + Guid.NewGuid().ToString("N") + ".state");

            FileLinkSeedStore store = new FileLinkSeedStore(path);

            byte seed;
            Assert.False(store.TryLoadSeed(100, out seed));
        }
    }
}
