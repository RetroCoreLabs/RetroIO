using System;
using NUnit.Framework;
using Microsoft.Extensions.Logging;
using RetroIO.Link.Lapb;
using RetroIO.Link.Lapb.DeleteMe;

namespace RetroIO.Protocols.X25.Tests
{
    /// <summary>
    /// Tests for LapbWindowManager - window enforcement, frame tracking, Go-Back-N
    /// </summary>
    [TestFixture]
    public class LapbWindowManagerTests
    {
        private ILogger _logger = null!;
        private ILoggerFactory _loggerFactory = null!;

        [SetUp]
        public void Setup()
        {
            _loggerFactory = LoggerFactory.Create(builder =>
            {
                builder.AddConsole();
                builder.SetMinimumLevel(LogLevel.Debug);
            });
            _logger = _loggerFactory.CreateLogger<LapbWindowManagerTests>();
        }

        [TearDown]
        public void TearDown()
        {
            _loggerFactory?.Dispose();
        }

        #region Constructor Tests

        [Test]
        public void Constructor_ValidParameters_Succeeds()
        {
            // Arrange & Act
            var manager = new LapbWindowManager(_logger, windowSize: 7, modulo: 8);

            // Assert
            Assert.That(manager.WindowSize, Is.EqualTo(7));
            Assert.That(manager.Modulo, Is.EqualTo(8));
            Assert.That(manager.UnackedFrameCount, Is.EqualTo(0));
        }

        [Test]
        public void Constructor_WindowSizeTooLarge_ThrowsException()
        {
            // Act & Assert
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                new LapbWindowManager(_logger, windowSize: 8, modulo: 8));
        }

        [Test]
        public void Constructor_WindowSizeZero_ThrowsException()
        {
            // Act & Assert
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                new LapbWindowManager(_logger, windowSize: 0, modulo: 8));
        }

        #endregion

        #region CanSend Tests

        [Test]
        public void CanSend_WindowEmpty_ReturnsTrue()
        {
            // Arrange
            var manager = new LapbWindowManager(_logger, windowSize: 7);

            // Act
            bool canSend = manager.CanSend(vs: 0, va: 0);

            // Assert
            Assert.That(canSend, Is.True);
        }

        [Test]
        public void CanSend_WindowNotFull_ReturnsTrue()
        {
            // Arrange
            var manager = new LapbWindowManager(_logger, windowSize: 7);

            // Act - 6 outstanding frames (7-1=6, less than K=7)
            bool canSend = manager.CanSend(vs: 6, va: 0);

            // Assert
            Assert.That(canSend, Is.True);
        }

        [Test]
        public void CanSend_WindowFull_ReturnsFalse()
        {
            // Arrange
            var manager = new LapbWindowManager(_logger, windowSize: 7);

            // Act - 7 outstanding frames (V(S)=7, V(A)=0, outstanding=7=K)
            bool canSend = manager.CanSend(vs: 7, va: 0);

            // Assert
            Assert.That(canSend, Is.False);
        }

        [Test]
        public void CanSend_WindowFullWithWrap_ReturnsFalse()
        {
            // Arrange
            var manager = new LapbWindowManager(_logger, windowSize: 7);

            // Act - Wrap case: V(S)=2, V(A)=3, outstanding=(2-3+8)%8=7=K
            bool canSend = manager.CanSend(vs: 2, va: 3);

            // Assert
            Assert.That(canSend, Is.False);
        }

        [Test]
        public void CanSend_AfterWrap_ReturnsTrue()
        {
            // Arrange
            var manager = new LapbWindowManager(_logger, windowSize: 7);

            // Act - V(S)=1, V(A)=3, outstanding=(1-3+8)%8=6 < K=7
            bool canSend = manager.CanSend(vs: 1, va: 3);

            // Assert
            Assert.That(canSend, Is.True);
        }

        #endregion

        #region TrackFrame Tests

        [Test]
        public void TrackFrame_SingleFrame_IncreasesCount()
        {
            // Arrange
            var manager = new LapbWindowManager(_logger, windowSize: 7);
            byte[] frameData = new byte[] { 0x01, 0x00, 0xAA, 0xBB };

            // Act
            manager.TrackFrame(ns: 0, nr: 0, frameData);

            // Assert
            Assert.That(manager.UnackedFrameCount, Is.EqualTo(1));
        }

        [Test]
        public void TrackFrame_MultipleFrames_TracksAll()
        {
            // Arrange
            var manager = new LapbWindowManager(_logger, windowSize: 7);

            // Act
            for (byte i = 0; i < 5; i++)
            {
                byte[] frame = new byte[] { 0x01, (byte)(i << 1), 0xAA };
                manager.TrackFrame(ns: i, nr: 0, frame);
            }

            // Assert
            Assert.That(manager.UnackedFrameCount, Is.EqualTo(5));
        }

        [Test]
        public void TrackFrame_NullFrame_DoesNotTrack()
        {
            // Arrange
            var manager = new LapbWindowManager(_logger, windowSize: 7);

            // Act
            manager.TrackFrame(ns: 0, nr: 0, null!);

            // Assert
            Assert.That(manager.UnackedFrameCount, Is.EqualTo(0));
        }

        #endregion

        #region AcknowledgeFrames Tests

        [Test]
        public void AcknowledgeFrames_SingleFrame_RemovesFrame()
        {
            // Arrange
            var manager = new LapbWindowManager(_logger, windowSize: 7);
            byte[] frameData = new byte[] { 0x01, 0x00, 0xAA };
            manager.TrackFrame(ns: 0, nr: 0, frameData);

            // Act - N(R)=1 acknowledges N(S)=0
            int acked = manager.AcknowledgeFrames(nr: 1, va: 0);

            // Assert
            Assert.That(acked, Is.EqualTo(1));
            Assert.That(manager.UnackedFrameCount, Is.EqualTo(0));
        }

        [Test]
        public void AcknowledgeFrames_MultipleFrames_RemovesAll()
        {
            // Arrange
            var manager = new LapbWindowManager(_logger, windowSize: 7);
            for (byte i = 0; i < 4; i++)
            {
                byte[] frame = new byte[] { 0x01, (byte)(i << 1), 0xAA };
                manager.TrackFrame(ns: i, nr: 0, frame);
            }

            // Act - N(R)=4 acknowledges N(S)=0,1,2,3
            int acked = manager.AcknowledgeFrames(nr: 4, va: 0);

            // Assert
            Assert.That(acked, Is.EqualTo(4));
            Assert.That(manager.UnackedFrameCount, Is.EqualTo(0));
        }

        [Test]
        public void AcknowledgeFrames_PartialAck_LeavesRemaining()
        {
            // Arrange
            var manager = new LapbWindowManager(_logger, windowSize: 7);
            for (byte i = 0; i < 5; i++)
            {
                byte[] frame = new byte[] { 0x01, (byte)(i << 1), 0xAA };
                manager.TrackFrame(ns: i, nr: 0, frame);
            }

            // Act - N(R)=3 acknowledges N(S)=0,1,2 but leaves 3,4
            int acked = manager.AcknowledgeFrames(nr: 3, va: 0);

            // Assert
            Assert.That(acked, Is.EqualTo(3));
            Assert.That(manager.UnackedFrameCount, Is.EqualTo(2));
        }

        [Test]
        public void AcknowledgeFrames_WrapAround_HandlesCorrectly()
        {
            // Arrange
            var manager = new LapbWindowManager(_logger, windowSize: 7);
            // Track frames at N(S)=6,7,0,1
            byte[] ns6 = new byte[] { 0x01, 6 << 1, 0x06 };
            byte[] ns7 = new byte[] { 0x01, 7 << 1, 0x07 };
            byte[] ns0 = new byte[] { 0x01, 0 << 1, 0x00 };
            byte[] ns1 = new byte[] { 0x01, 1 << 1, 0x01 };
            manager.TrackFrame(ns: 6, nr: 0, ns6);
            manager.TrackFrame(ns: 7, nr: 0, ns7);
            manager.TrackFrame(ns: 0, nr: 0, ns0);
            manager.TrackFrame(ns: 1, nr: 0, ns1);

            // Act - N(R)=1 acknowledges N(S)=6,7,0 (wrap case, V(A)=6)
            int acked = manager.AcknowledgeFrames(nr: 1, va: 6);

            // Assert
            Assert.That(acked, Is.EqualTo(3));
            Assert.That(manager.UnackedFrameCount, Is.EqualTo(1)); // Only N(S)=1 remains
        }

        #endregion

        #region GetFramesForRetransmission Tests

        [Test]
        public void GetFramesForRetransmission_AllFrames_ReturnsAll()
        {
            // Arrange
            var manager = new LapbWindowManager(_logger, windowSize: 7);
            for (byte i = 0; i < 3; i++)
            {
                byte[] frame = new byte[] { 0x01, (byte)(i << 1), (byte)(0xA0 + i) };
                manager.TrackFrame(ns: i, nr: 0, frame);
            }

            // Act - Retransmit all from N(R)=0
            var frames = manager.GetFramesForRetransmission(nr: 0, currentVs: 3, newNr: 5);

            // Assert
            Assert.That(frames.Count, Is.EqualTo(3));
        }

        [Test]
        public void GetFramesForRetransmission_UpdatesNR()
        {
            // Arrange
            var manager = new LapbWindowManager(_logger, windowSize: 7);
            byte[] frame = new byte[] { 0x01, 0x00, 0xAA }; // I-frame with N(S)=0, N(R)=0
            manager.TrackFrame(ns: 0, nr: 0, frame);

            // Act - Retransmit with new N(R)=3
            var frames = manager.GetFramesForRetransmission(nr: 0, currentVs: 1, newNr: 3);

            // Assert
            Assert.That(frames.Count, Is.EqualTo(1));
            // Check that N(R) in control byte was updated
            // I-frame control: N(R) is bits 7-5
            byte control = frames[0][1];
            byte extractedNR = (byte)((control >> 5) & 0x07);
            Assert.That(extractedNR, Is.EqualTo(3));
        }

        [Test]
        public void GetFramesForRetransmission_PartialRetransmit_ReturnsSubset()
        {
            // Arrange
            var manager = new LapbWindowManager(_logger, windowSize: 7);
            for (byte i = 0; i < 5; i++)
            {
                byte[] frame = new byte[] { 0x01, (byte)(i << 1), (byte)(0xA0 + i) };
                manager.TrackFrame(ns: i, nr: 0, frame);
            }

            // Act - Retransmit from N(R)=2 to V(S)=5 (frames 2,3,4)
            var frames = manager.GetFramesForRetransmission(nr: 2, currentVs: 5, newNr: 0);

            // Assert
            Assert.That(frames.Count, Is.EqualTo(3));
        }

        #endregion

        #region IsValidNR Tests

        [Test]
        public void IsValidNR_NoOutstanding_OnlyVAValid()
        {
            // Arrange
            var manager = new LapbWindowManager(_logger, windowSize: 7);

            // Act & Assert - Only V(A)=0 is valid when no outstanding
            Assert.That(manager.IsValidNR(nr: 0, va: 0, vs: 0), Is.True);
            Assert.That(manager.IsValidNR(nr: 1, va: 0, vs: 0), Is.False);
        }

        [Test]
        public void IsValidNR_WithinWindow_Valid()
        {
            // Arrange
            var manager = new LapbWindowManager(_logger, windowSize: 7);

            // Act & Assert - V(A)=0, V(S)=5, valid range [0,5]
            Assert.That(manager.IsValidNR(nr: 0, va: 0, vs: 5), Is.True);
            Assert.That(manager.IsValidNR(nr: 3, va: 0, vs: 5), Is.True);
            Assert.That(manager.IsValidNR(nr: 5, va: 0, vs: 5), Is.True);
            Assert.That(manager.IsValidNR(nr: 6, va: 0, vs: 5), Is.False);
        }

        [Test]
        public void IsValidNR_WrapAround_Valid()
        {
            // Arrange
            var manager = new LapbWindowManager(_logger, windowSize: 7);

            // Act & Assert - V(A)=6, V(S)=2 (wrap), valid: 6,7,0,1,2
            Assert.That(manager.IsValidNR(nr: 6, va: 6, vs: 2), Is.True);
            Assert.That(manager.IsValidNR(nr: 7, va: 6, vs: 2), Is.True);
            Assert.That(manager.IsValidNR(nr: 0, va: 6, vs: 2), Is.True);
            Assert.That(manager.IsValidNR(nr: 2, va: 6, vs: 2), Is.True);
            Assert.That(manager.IsValidNR(nr: 3, va: 6, vs: 2), Is.False);
            Assert.That(manager.IsValidNR(nr: 5, va: 6, vs: 2), Is.False);
        }

        #endregion

        #region Clear Tests

        [Test]
        public void Clear_RemovesAllFrames()
        {
            // Arrange
            var manager = new LapbWindowManager(_logger, windowSize: 7);
            for (byte i = 0; i < 5; i++)
            {
                byte[] frame = new byte[] { 0x01, (byte)(i << 1), 0xAA };
                manager.TrackFrame(ns: i, nr: 0, frame);
            }
            Assert.That(manager.UnackedFrameCount, Is.EqualTo(5));

            // Act
            manager.Clear();

            // Assert
            Assert.That(manager.UnackedFrameCount, Is.EqualTo(0));
        }

        #endregion

        #region RetryLimit Tests

        [Test]
        public void IsRetryLimitExceeded_UnderLimit_ReturnsFalse()
        {
            // Arrange
            var manager = new LapbWindowManager(_logger, windowSize: 7);
            byte[] frame = new byte[] { 0x01, 0x00, 0xAA };
            manager.TrackFrame(ns: 0, nr: 0, frame);

            // Act - Frame only transmitted once
            bool exceeded = manager.IsRetryLimitExceeded(ns: 0, maxRetries: 3);

            // Assert
            Assert.That(exceeded, Is.False);
        }

        [Test]
        public void AnyRetryLimitExceeded_NoneExceeded_ReturnsFalse()
        {
            // Arrange
            var manager = new LapbWindowManager(_logger, windowSize: 7);
            for (byte i = 0; i < 3; i++)
            {
                byte[] frame = new byte[] { 0x01, (byte)(i << 1), 0xAA };
                manager.TrackFrame(ns: i, nr: 0, frame);
            }

            // Act
            bool anyExceeded = manager.AnyRetryLimitExceeded(maxRetries: 5);

            // Assert
            Assert.That(anyExceeded, Is.False);
        }

        #endregion

        #region GetOldestUnackedFrame Tests

        [Test]
        public void GetOldestUnackedFrame_NoFrames_ReturnsNull()
        {
            // Arrange
            var manager = new LapbWindowManager(_logger, windowSize: 7);

            // Act
            var oldest = manager.GetOldestUnackedFrame();

            // Assert
            Assert.That(oldest, Is.Null);
        }

        [Test]
        public void GetOldestUnackedFrame_HasFrames_ReturnsOldest()
        {
            // Arrange
            var manager = new LapbWindowManager(_logger, windowSize: 7);

            // Track frames with small delay to ensure ordering
            byte[] frame0 = new byte[] { 0x01, 0x00, 0x00 };
            manager.TrackFrame(ns: 0, nr: 0, frame0);
            System.Threading.Thread.Sleep(10);

            byte[] frame1 = new byte[] { 0x01, 0x02, 0x01 };
            manager.TrackFrame(ns: 1, nr: 0, frame1);

            // Act
            var oldest = manager.GetOldestUnackedFrame();

            // Assert
            Assert.That(oldest, Is.Not.Null);
            Assert.That(oldest!.NS, Is.EqualTo(0));
        }

        #endregion
    }
}
