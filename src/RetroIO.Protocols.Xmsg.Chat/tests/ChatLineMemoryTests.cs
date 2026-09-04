using System;
using RetroIO.Protocols.Xmsg.Chat;
using Xunit;

namespace RetroIO.Protocols.Xmsg.Chat.Tests
{
    /// <summary>
    /// Tests for the dedup window that makes a mesh safe to wire.
    /// </summary>
    public sealed class ChatLineMemoryTests
    {
        /// <summary>
        /// The first sight of a line is new; the second is not.
        /// </summary>
        [Fact]
        public void ASecondCopyOfTheSameLineIsNotNew()
        {
            ChatLineMemory memory = new ChatLineMemory(16);

            Assert.True(memory.IsNew(103, 1234));
            Assert.False(memory.IsNew(103, 1234));
        }

        /// <summary>
        /// The same id from a different machine is a different line.
        /// </summary>
        /// <remarks>
        /// Every origin numbers from its own counter, so id 1 exists on all of them at once. A
        /// memory that keyed on the id alone would silently swallow other machines' traffic, and
        /// the symptom - some lines just missing - is very hard to trace back to here.
        /// </remarks>
        [Fact]
        public void TheSameIdFromADifferentOriginIsADifferentLine()
        {
            ChatLineMemory memory = new ChatLineMemory(16);

            Assert.True(memory.IsNew(103, 1));
            Assert.True(memory.IsNew(102, 1));
            Assert.True(memory.IsNew(100, 1));
        }

        /// <summary>
        /// Different lines from one machine are all new.
        /// </summary>
        [Fact]
        public void SuccessiveLinesFromOneOriginAreAllNew()
        {
            ChatLineMemory memory = new ChatLineMemory(16);

            for (ushort id = 1; id <= 10; id++)
            {
                Assert.True(memory.IsNew(103, id));
            }
        }

        /// <summary>
        /// A line pushed out of the window is treated as new again.
        /// </summary>
        /// <remarks>
        /// This is the deliberate limit of a fixed window and is written down so nobody reads it as
        /// a defect later. A duplicate arrives within a few hops of the original, so it is inside
        /// the window; a copy arriving after the window has turned over completely would be
        /// delivered twice. The cure is a bigger window, not a different design.
        /// </remarks>
        [Fact]
        public void ALineOlderThanTheWindowIsNewAgain()
        {
            ChatLineMemory memory = new ChatLineMemory(4);

            Assert.True(memory.IsNew(103, 1));
            Assert.True(memory.IsNew(103, 2));
            Assert.True(memory.IsNew(103, 3));
            Assert.True(memory.IsNew(103, 4));

            // Still inside the window.
            Assert.False(memory.IsNew(103, 1));

            // Four more push line 1 out.
            Assert.True(memory.IsNew(103, 5));
            Assert.True(memory.IsNew(103, 6));
            Assert.True(memory.IsNew(103, 7));
            Assert.True(memory.IsNew(103, 8));

            Assert.True(memory.IsNew(103, 1));
        }

        /// <summary>
        /// A fresh memory does not mistake a real line for a remembered one.
        /// </summary>
        /// <remarks>
        /// The arrays start as zeros, so origin 0 with id 0 is exactly the pair an empty ring would
        /// appear to hold. Counting used slots rather than scanning the whole array is what stops
        /// that, and this test is what stops the count being removed as redundant.
        /// </remarks>
        [Fact]
        public void AnEmptyMemoryDoesNotAlreadyHoldZeroZero()
        {
            ChatLineMemory memory = new ChatLineMemory(8);

            Assert.True(memory.IsNew(0, 0));
        }

        /// <summary>
        /// Clearing makes every line look new again.
        /// </summary>
        [Fact]
        public void ClearForgetsEverything()
        {
            ChatLineMemory memory = new ChatLineMemory(8);

            Assert.True(memory.IsNew(103, 7));
            Assert.False(memory.IsNew(103, 7));

            memory.Clear();

            Assert.True(memory.IsNew(103, 7));
        }

        /// <summary>
        /// A memory must have room for at least one line.
        /// </summary>
        [Fact]
        public void ZeroCapacityIsRefused()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new ChatLineMemory(0));
        }

        /// <summary>
        /// The mesh this exists to make safe: one line reaching a node by two paths is delivered
        /// once.
        /// </summary>
        /// <remarks>
        /// Wire D102-D103 as well as D100-D102 and D100-D103, and a line typed on D103 reaches D102
        /// directly AND through D100. Both copies carry the SAME (origin, id) because D103 stamped
        /// it once, which is what lets D102 tell them apart from two different lines.
        /// </remarks>
        [Fact]
        public void OneLineArrivingByTwoPathsIsDeliveredOnce()
        {
            ChatLineMemory d102 = new ChatLineMemory(32);

            const ushort originD103 = 103;
            const ushort lineId = 4242;

            bool deliveredDirect = d102.IsNew(originD103, lineId);
            bool deliveredViaD100 = d102.IsNew(originD103, lineId);

            Assert.True(deliveredDirect);
            Assert.False(deliveredViaD100);
        }
    }
}
