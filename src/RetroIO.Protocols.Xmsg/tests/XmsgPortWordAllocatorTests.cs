//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;

using RetroIO.Protocols.Xmsg;

using Xunit;

namespace RetroIO.Protocols.Xmsg.Tests
{
    /// <summary>
    /// Proves the port-word allocator mints the same shape of value the kernel does.
    /// </summary>
    public sealed class XmsgPortWordAllocatorTests
    {
        /// <summary>
        /// A minted word splits back into the port number it was asked for and a mintable random.
        /// </summary>
        [Fact]
        public void Next_RoundTripsThroughTrySplit()
        {
            XmsgPortWordAllocator allocator = new XmsgPortWordAllocator(0x1234);

            for (int port = 1; port <= 32; port++)
            {
                ushort word = allocator.Next(port);

                int portNumber;
                int random;
                Assert.True(XmsgPortWordAllocator.TrySplit(word, out portNumber, out random));
                Assert.Equal(port, portNumber);
                Assert.InRange(random, 1, 126);
            }
        }

        /// <summary>
        /// Successive draws follow the generator, so a capture of our traffic reads like a
        /// capture of a real kernel's.
        /// </summary>
        [Fact]
        public void Next_FollowsTheGeneratorSequence()
        {
            XmsgPortWordAllocator allocator = new XmsgPortWordAllocator(0x4321);
            XmsgRandomGenerator reference = new XmsgRandomGenerator(0x4321);

            for (int i = 0; i < 40; i++)
            {
                ushort word = allocator.Next(5);
                Assert.Equal(reference.Next(), word & 0x7F);
            }
        }

        /// <summary>
        /// The port-0 sink is not a minted port word, and neither is a low7 of 127.
        /// </summary>
        [Fact]
        public void TrySplit_RejectsReservedWords()
        {
            int portNumber;
            int random;

            Assert.False(XmsgPortWordAllocator.TrySplit(0, out portNumber, out random));
            Assert.False(XmsgPortWordAllocator.TrySplit((ushort)((5 << 7) | 0x7F), out portNumber, out random));
            Assert.True(XmsgPortWordAllocator.TrySplit((ushort)((5 << 7) | 43), out portNumber, out random));
            Assert.Equal(5, portNumber);
            Assert.Equal(43, random);
        }

        /// <summary>
        /// The captured wire values decode to the port numbers the registry reports.
        /// </summary>
        [Fact]
        public void TrySplit_MatchesTheCapturedSessionPorts()
        {
            int portNumber;
            int random;

            XmsgPortWordAllocator.TrySplit(342, out portNumber, out random);   // *TADADM
            Assert.Equal(2, portNumber);
            Assert.Equal(86, random);

            XmsgPortWordAllocator.TrySplit(683, out portNumber, out random);   // the client
            Assert.Equal(5, portNumber);
            Assert.Equal(43, random);

            XmsgPortWordAllocator.TrySplit(1218, out portNumber, out random);  // terminal port
            Assert.Equal(9, portNumber);
            Assert.Equal(66, random);
        }

        /// <summary>
        /// A port number outside nine bits is rejected rather than silently wrapping into the
        /// random field.
        /// </summary>
        [Fact]
        public void Next_RejectsOutOfRangePortNumbers()
        {
            XmsgPortWordAllocator allocator = new XmsgPortWordAllocator(1);

            Assert.Throws<ArgumentOutOfRangeException>(() => allocator.Next(0));
            Assert.Throws<ArgumentOutOfRangeException>(() => allocator.Next(512));
        }
    }
}
