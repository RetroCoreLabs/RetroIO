using System;
using RetroIO.Protocols.Xmsg.Fa;
using RetroIO.Protocols.Xmsg.Servers.Fa;
using Xunit;

namespace RetroIO.Protocols.Xmsg.Node.Tests
{
    /// <summary>
    /// Pins that a transfer abandoned from above reports itself finished.
    /// </summary>
    /// <remarks>
    /// The case this exists for is silence. A caller that sends its connect letter four times and
    /// gets no answer decides the transfer is over, but that decision is made ABOVE the driver - so
    /// without <see cref="FaWriteDriver.Abandon"/> the driver goes on reporting itself unfinished
    /// and everything above it waits for a wall-clock timeout that has nothing left to wait for.
    /// </remarks>
    public sealed class FaWriteDriverAbandonTests
    {
        /// <summary>
        /// Builds a driver aimed at a server, with a small payload.
        /// </summary>
        /// <returns>
        /// The driver under test.
        /// </returns>
        private static FaWriteDriver NewDriver()
        {
            FaWriteTarget target = new FaWriteTarget(100, "*FA-SERVER", "PUSHED:DATA");
            byte[] content = new byte[] { 0x41, 0x42, 0x43, 0x44 };

            return new FaWriteDriver(target, content);
        }

        /// <summary>
        /// A fresh driver has not failed.
        /// </summary>
        /// <remarks>
        /// The control. Without it the test below would pass against a driver that reports failure
        /// from the moment it is built.
        /// </remarks>
        [Fact]
        public void AFreshDriverHasNoFailure()
        {
            FaWriteDriver driver = NewDriver();

            Assert.Equal(0, driver.Failure.Length);
        }

        /// <summary>
        /// Abandoning the transfer records a failure.
        /// </summary>
        [Fact]
        public void AbandonRecordsTheFailure()
        {
            FaWriteDriver driver = NewDriver();

            driver.Abandon("node 100 answered none of 4 connect letters.");

            Assert.True(driver.Failure.Length > 0);

            // The reason has to survive into the text, or the operator is told only that something
            // went wrong and has to go back to the log to find out what.
            Assert.Contains("connect letters", driver.Failure);
        }

        /// <summary>
        /// A null reason is refused.
        /// </summary>
        [Fact]
        public void AbandonRefusesANullReason()
        {
            FaWriteDriver driver = NewDriver();

            Assert.Throws<ArgumentNullException>(delegate () { driver.Abandon(null!); });
        }
    }
}
