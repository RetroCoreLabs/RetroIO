using System;
using System.Collections.Generic;
using NUnit.Framework;
using Microsoft.Extensions.Logging;
using RetroIO.Link;
using RetroIO.Link.Lapb;
using RetroIO.Protocols.X25.Layers;
using RetroIO.Protocols.X25.Runtime.Layers;
using RetroIO.Link.Models;
using RetroIO.Protocols.X25.Models;
using RetroIO.Protocols.X25.Runtime.Models;
using RetroIO.Protocols.X25.Sintran;

namespace RetroIO.Protocols.X25.Tests
{
    /// <summary>
    /// Tests for LapbInstance - per-link LAPB state machine
    /// </summary>
    [TestFixture]
    public class LapbInstanceTests
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
            _logger = _loggerFactory.CreateLogger<LapbInstanceTests>();
        }

        [TearDown]
        public void TearDown()
        {
            _loggerFactory?.Dispose();
        }

        #region Constructor Tests

        [Test]
        public void Constructor_DefaultParameters_InitializesCorrectly()
        {
            // Arrange & Act
            using var lapb = new LapbInstance(_logger, "TestLink1");

            // Assert
            Assert.That(lapb.State, Is.EqualTo(LapbState.Disconnected));
            Assert.That(lapb.LinkName, Is.EqualTo("TestLink1"));
            Assert.That(lapb.VS, Is.EqualTo(0));
            Assert.That(lapb.VR, Is.EqualTo(0));
            Assert.That(lapb.VA, Is.EqualTo(0));
        }

        [Test]
        public void Constructor_CustomParameters_InitializesCorrectly()
        {
            // Arrange & Act
            using var lapb = new LapbInstance(_logger, "CustomLink",
                windowSize: 4,
                maxRetries: 5,
                t1TimeoutMs: 2000,
                t2TimeoutMs: 500,
                isPrimary: false);

            // Assert
            Assert.That(lapb.State, Is.EqualTo(LapbState.Disconnected));
            Assert.That(lapb.IsPrimary, Is.False);
        }

        #endregion

        #region Connection Tests

        [Test]
        public void Connect_FromDisconnected_SendsSabm()
        {
            // Arrange
            using var lapb = new LapbInstance(_logger, "TestLink");
            LapbFrameEventArgs? sentFrame = null;
            lapb.FrameToTransmit += (s, e) => sentFrame = e;

            // Act
            lapb.Connect();

            // Assert
            Assert.That(lapb.State, Is.EqualTo(LapbState.Connecting));
            Assert.That(sentFrame, Is.Not.Null);
            Assert.That(sentFrame!.FrameType, Is.EqualTo(LapbFrameType.Unnumbered));
            Assert.That(sentFrame.Control & 0xEF, Is.EqualTo(0x2F)); // SABM (ignoring P/F)
            Assert.That(sentFrame.PF, Is.True); // P=1
        }

        [Test]
        public void Connect_FromConnected_Ignored()
        {
            // Arrange
            using var lapb = new LapbInstance(_logger, "TestLink");
            lapb.Connect();
            // Simulate UA response
            lapb.ReceiveFrame(new byte[] { 0x01, 0x73 }, 2); // UA with F=1
            Assert.That(lapb.State, Is.EqualTo(LapbState.DataTransfer));

            var frameCount = 0;
            lapb.FrameToTransmit += (s, e) => frameCount++;

            // Act
            lapb.Connect();

            // Assert - should not send anything
            Assert.That(frameCount, Is.EqualTo(0));
            Assert.That(lapb.State, Is.EqualTo(LapbState.DataTransfer));
        }

        [Test]
        public void ReceiveSabm_FromDisconnected_SendsUaAndConnects()
        {
            // Arrange
            using var lapb = new LapbInstance(_logger, "TestLink");
            LapbFrameEventArgs? sentFrame = null;
            LapbStateChangedEventArgs? stateChange = null;
            lapb.FrameToTransmit += (s, e) => sentFrame = e;
            lapb.StateChanged += (s, e) => stateChange = e;

            // Act - Receive SABM
            lapb.ReceiveFrame(new byte[] { 0x01, 0x3F }, 2); // SABM with P=1

            // Assert
            Assert.That(lapb.State, Is.EqualTo(LapbState.DataTransfer));
            Assert.That(sentFrame, Is.Not.Null);
            Assert.That(sentFrame!.Control & 0xEF, Is.EqualTo(0x63)); // UA
            Assert.That(sentFrame.PF, Is.True); // F=1
            Assert.That(stateChange, Is.Not.Null);
            Assert.That(stateChange!.NewState, Is.EqualTo(LapbState.DataTransfer));
        }

        [Test]
        public void ReceiveUa_InConnecting_TransitionsToDataTransfer()
        {
            // Arrange
            using var lapb = new LapbInstance(_logger, "TestLink");
            lapb.Connect();
            Assert.That(lapb.State, Is.EqualTo(LapbState.Connecting));

            // Act - Receive UA
            lapb.ReceiveFrame(new byte[] { 0x01, 0x73 }, 2); // UA with F=1

            // Assert
            Assert.That(lapb.State, Is.EqualTo(LapbState.DataTransfer));
            Assert.That(lapb.VS, Is.EqualTo(0));
            Assert.That(lapb.VR, Is.EqualTo(0));
        }

        [Test]
        public void ReceiveDm_InConnecting_TransitionsToDisconnected()
        {
            // Arrange
            using var lapb = new LapbInstance(_logger, "TestLink");
            lapb.Connect();
            Assert.That(lapb.State, Is.EqualTo(LapbState.Connecting));

            // Act - Receive DM
            lapb.ReceiveFrame(new byte[] { 0x01, 0x0F }, 2); // DM

            // Assert
            Assert.That(lapb.State, Is.EqualTo(LapbState.Disconnected));
        }

        #endregion

        #region Disconnection Tests

        [Test]
        public void Disconnect_FromConnected_SendsDisc()
        {
            // Arrange
            using var lapb = new LapbInstance(_logger, "TestLink");
            EstablishConnection(lapb);

            LapbFrameEventArgs? sentFrame = null;
            lapb.FrameToTransmit += (s, e) => sentFrame = e;

            // Act
            lapb.Disconnect();

            // Assert
            Assert.That(lapb.State, Is.EqualTo(LapbState.Disconnecting));
            Assert.That(sentFrame, Is.Not.Null);
            Assert.That(sentFrame!.Control & 0xEF, Is.EqualTo(0x43)); // DISC
            Assert.That(sentFrame.PF, Is.True); // P=1
        }

        [Test]
        public void ReceiveDisc_InDataTransfer_SendsUaAndDisconnects()
        {
            // Arrange
            using var lapb = new LapbInstance(_logger, "TestLink");
            EstablishConnection(lapb);

            LapbFrameEventArgs? sentFrame = null;
            lapb.FrameToTransmit += (s, e) => sentFrame = e;

            // Act - Receive DISC
            lapb.ReceiveFrame(new byte[] { 0x01, 0x53 }, 2); // DISC with P=1

            // Assert
            Assert.That(lapb.State, Is.EqualTo(LapbState.Disconnected));
            Assert.That(sentFrame, Is.Not.Null);
            Assert.That(sentFrame!.Control & 0xEF, Is.EqualTo(0x63)); // UA
        }

        #endregion

        #region I-Frame Transmission Tests

        [Test]
        public void SendData_InDataTransfer_SendsIFrame()
        {
            // Arrange
            using var lapb = new LapbInstance(_logger, "TestLink");
            EstablishConnection(lapb);

            LapbFrameEventArgs? sentFrame = null;
            lapb.FrameToTransmit += (s, e) => sentFrame = e;

            // Act
            byte[] data = new byte[] { 0x10, 0x00, 0x0B }; // X.25 data packet
            bool sent = lapb.SendData(data);

            // Assert
            Assert.That(sent, Is.True);
            Assert.That(sentFrame, Is.Not.Null);
            Assert.That(sentFrame!.FrameType, Is.EqualTo(LapbFrameType.Information));
            Assert.That((sentFrame.Control & 0x01), Is.EqualTo(0)); // I-frame indicator
            Assert.That(sentFrame.NS, Is.EqualTo(0)); // First frame N(S)=0
            Assert.That(sentFrame.Information.Length, Is.EqualTo(3));
        }

        [Test]
        public void SendData_InDataTransfer_IncrementsVS()
        {
            // Arrange
            using var lapb = new LapbInstance(_logger, "TestLink");
            EstablishConnection(lapb);

            // Act - Send 3 frames
            for (int i = 0; i < 3; i++)
            {
                lapb.SendData(new byte[] { 0x10 });
            }

            // Assert
            Assert.That(lapb.VS, Is.EqualTo(3));
            Assert.That(lapb.UnackedFrameCount, Is.EqualTo(3));
        }

        [Test]
        public void SendData_WindowFull_ReturnsFalse()
        {
            // Arrange
            using var lapb = new LapbInstance(_logger, "TestLink", windowSize: 3);
            EstablishConnection(lapb);

            // Fill the window
            for (int i = 0; i < 3; i++)
            {
                lapb.SendData(new byte[] { (byte)i });
            }

            // Act - Try to send when window full
            bool sent = lapb.SendData(new byte[] { 0xFF });

            // Assert
            Assert.That(sent, Is.False);
        }

        [Test]
        public void SendData_NotConnected_ReturnsFalse()
        {
            // Arrange
            using var lapb = new LapbInstance(_logger, "TestLink");
            Assert.That(lapb.State, Is.EqualTo(LapbState.Disconnected));

            // Act
            bool sent = lapb.SendData(new byte[] { 0x10 });

            // Assert
            Assert.That(sent, Is.False);
        }

        #endregion

        #region I-Frame Reception Tests

        [Test]
        public void ReceiveIFrame_InSequence_DeliversDataAndIncrementsVR()
        {
            // Arrange
            using var lapb = new LapbInstance(_logger, "TestLink");
            EstablishConnection(lapb);

            byte[]? receivedData = null;
            lapb.DataReceived += (s, data) => receivedData = data;

            // Act - Receive I-frame: N(S)=0, N(R)=0
            // Control: N(R)=0 in bits 7-5, N(S)=0 in bits 3-1, bit0=0
            byte[] iframe = new byte[] { 0x01, 0x00, 0xAA, 0xBB }; // I-frame with data 0xAA 0xBB
            lapb.ReceiveFrame(iframe, iframe.Length);

            // Assert
            Assert.That(lapb.VR, Is.EqualTo(1)); // V(R) incremented
            Assert.That(receivedData, Is.Not.Null);
            Assert.That(receivedData!.Length, Is.EqualTo(2));
            Assert.That(receivedData[0], Is.EqualTo(0xAA));
        }

        [Test]
        public void ReceiveIFrame_OutOfSequence_SendsRej()
        {
            // Arrange
            using var lapb = new LapbInstance(_logger, "TestLink");
            EstablishConnection(lapb);

            LapbFrameEventArgs? sentFrame = null;
            lapb.FrameToTransmit += (s, e) => sentFrame = e;

            // Act - Receive I-frame with N(S)=1 when V(R)=0 (out of sequence)
            byte[] iframe = new byte[] { 0x01, 0x02, 0xAA }; // I-frame N(S)=1
            lapb.ReceiveFrame(iframe, iframe.Length);

            // Assert
            Assert.That(lapb.VR, Is.EqualTo(0)); // V(R) not incremented
            Assert.That(sentFrame, Is.Not.Null);
            Assert.That(sentFrame!.FrameType, Is.EqualTo(LapbFrameType.Supervisory));
            Assert.That((sentFrame.Control & 0x0F), Is.EqualTo(0x09)); // REJ
        }

        [Test]
        public void ReceiveIFrame_WithValidNR_AcknowledgesFrames()
        {
            // Arrange
            using var lapb = new LapbInstance(_logger, "TestLink");
            EstablishConnection(lapb);

            // Send 3 I-frames
            for (int i = 0; i < 3; i++)
            {
                lapb.SendData(new byte[] { (byte)i });
            }
            Assert.That(lapb.UnackedFrameCount, Is.EqualTo(3));

            // Act - Receive I-frame with N(R)=2 (acknowledges frames 0 and 1)
            // Control: N(R)=2 in bits 7-5 (0x40), N(S)=0 in bits 3-1, bit0=0
            byte[] iframe = new byte[] { 0x01, 0x40, 0xCC }; // I-frame N(S)=0, N(R)=2
            lapb.ReceiveFrame(iframe, iframe.Length);

            // Assert
            Assert.That(lapb.VA, Is.EqualTo(2)); // V(A) updated to N(R)
            Assert.That(lapb.UnackedFrameCount, Is.EqualTo(1)); // Only frame 2 remains
        }

        #endregion

        #region S-Frame Tests

        [Test]
        public void ReceiveRR_AcknowledgesFrames()
        {
            // Arrange
            using var lapb = new LapbInstance(_logger, "TestLink");
            EstablishConnection(lapb);

            // Send 2 frames
            lapb.SendData(new byte[] { 0x01 });
            lapb.SendData(new byte[] { 0x02 });
            Assert.That(lapb.UnackedFrameCount, Is.EqualTo(2));

            // Act - Receive RR N(R)=2
            // RR control: N(R)=2 in bits 7-5 (0x40), bits 3-0 = 0001 (RR)
            byte[] rr = new byte[] { 0x01, 0x41 }; // RR with N(R)=2
            lapb.ReceiveFrame(rr, rr.Length);

            // Assert
            Assert.That(lapb.VA, Is.EqualTo(2));
            Assert.That(lapb.UnackedFrameCount, Is.EqualTo(0));
        }

        [Test]
        public void ReceiveRNR_SetsRemoteBusy()
        {
            // Arrange
            using var lapb = new LapbInstance(_logger, "TestLink");
            EstablishConnection(lapb);

            // Act - Receive RNR
            byte[] rnr = new byte[] { 0x01, 0x05 }; // RNR with N(R)=0
            lapb.ReceiveFrame(rnr, rnr.Length);

            // Now try to send - should fail (remote busy)
            bool sent = lapb.SendData(new byte[] { 0x01 });

            // Assert
            Assert.That(sent, Is.False);
        }

        [Test]
        public void ReceiveREJ_TriggersRetransmission()
        {
            // Arrange
            using var lapb = new LapbInstance(_logger, "TestLink");
            EstablishConnection(lapb);

            // Send 3 frames
            lapb.SendData(new byte[] { 0x01 });
            lapb.SendData(new byte[] { 0x02 });
            lapb.SendData(new byte[] { 0x03 });

            var retransmittedFrames = new List<LapbFrameEventArgs>();
            lapb.FrameToTransmit += (s, e) =>
            {
                if (e.FrameType == LapbFrameType.Information)
                    retransmittedFrames.Add(e);
            };

            // Act - Receive REJ N(R)=1 (requesting retransmission from N(S)=1)
            byte[] rej = new byte[] { 0x01, 0x29 }; // REJ with N(R)=1
            lapb.ReceiveFrame(rej, rej.Length);

            // Assert - Should retransmit frames 1 and 2
            Assert.That(retransmittedFrames.Count, Is.EqualTo(2));
        }

        #endregion

        #region Sequence Number Tests

        [Test]
        public void SequenceNumbers_WrapAtModulo8()
        {
            // Arrange
            using var lapb = new LapbInstance(_logger, "TestLink");
            EstablishConnection(lapb);

            // Act - Send 10 frames (should wrap around)
            for (int i = 0; i < 10; i++)
            {
                // Simulate acknowledgment so window doesn't fill
                if (i > 0 && i % 3 == 0)
                {
                    // Receive RR acknowledging previous frames
                    byte nr = (byte)(i % 8);
                    byte[] rr = new byte[] { 0x01, (byte)((nr << 5) | 0x01) };
                    lapb.ReceiveFrame(rr, rr.Length);
                }
                lapb.SendData(new byte[] { (byte)i });
            }

            // Assert - V(S) should be in range 0-7
            Assert.That(lapb.VS, Is.InRange(0, 7));
        }

        #endregion

        #region Statistics Tests

        [Test]
        public void GetStatistics_ReturnsCorrectValues()
        {
            // Arrange
            using var lapb = new LapbInstance(_logger, "TestLink");
            EstablishConnection(lapb);

            // Send and receive some frames
            lapb.SendData(new byte[] { 0x01 });
            lapb.SendData(new byte[] { 0x02 });

            // Act
            var stats = lapb.GetStatistics();

            // Assert
            Assert.That(stats.VS, Is.EqualTo(2));
            Assert.That(stats.IFramesSent, Is.EqualTo(2));
            Assert.That(stats.UFramesSent, Is.GreaterThan(0)); // At least SABM during connect
        }

        #endregion

        #region Helper Methods

        private void EstablishConnection(LapbInstance lapb)
        {
            lapb.Connect();
            // Simulate UA response to establish connection
            lapb.ReceiveFrame(new byte[] { 0x01, 0x73 }, 2); // UA with F=1
            Assert.That(lapb.State, Is.EqualTo(LapbState.DataTransfer));
        }

        #endregion
    }
}
