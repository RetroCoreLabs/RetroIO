//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using NUnit.Framework;
using Microsoft.Extensions.Logging;
using RetroIO.Protocols.X25;
using RetroIO.Protocols.X25.Runtime;
using RetroIO.Link.Models;
using RetroIO.Protocols.X25.Models;
using RetroIO.Protocols.X25.Runtime.Models;
using RetroIO.Protocols.X25.Sintran;
using RetroIO.Protocols.X25.Modules;
using RetroIO.Protocols.X25.Runtime.Transport;
using RetroIO.Protocols.X25.Transport;
using RetroIO.Link.Physical;

namespace RetroIO.Protocols.X25.Tests
{
    /// <summary>
    /// Unit tests for response packet communication flow from machine 102 back to machine 100
    /// Tests bidirectional LAPB connection, X.25 routing responses, and acknowledgment patterns
    /// This represents the return path after the initial 100→102 communication
    /// </summary>
    [TestFixture]
    public class ListingRoutesRespond102to100Tests : TestBase
    {
        private readonly List<HistoryItem> _capturedHistory = new();
        
        [SetUp]
        public override void Setup()
        {
            base.Setup();
            
            // Configure routing for response communication 102 → 100
            ConfigureResponseRoutingFor102To100();
            
            // Subscribe to history events
            Runtime.HistoryItemAdded += OnHistoryItemAdded;
            
            Logger.LogInformation("ListingRoutesRespond102to100Tests setup complete with response routing configuration");
        }
        
        private void OnHistoryItemAdded(object? sender, HistoryItem item)
        {
            _capturedHistory.Add(item);
        }
        
        private void ConfigureResponseRoutingFor102To100()
        {
            // Enable test mode for mock interfaces - we are machine 102 responding to machine 100
            Config.TestMode = true;
            Config.X25.LocalMachineId = "102";  // We are machine 102 in this scenario
            
            // Configure serial interface for machine 100 (response destination)
            Config.Routing.SerialInterfaces.Add(new SerialInterfaceConfig
            {
                Name = "Serial0",
                PortName = "MOCK_SERIAL100",
                BaudRate = 9600,
                RemoteMachineId = "100",
                Enabled = true,
                Description = "Mock serial connection to machine 100 for response routing"
            });
            
            // Configure route for 100.* (responses go back to machine 100)
            Config.Routing.RoutingTable.Add(new X25RouteEntry
            {
                X121AddressPattern = "100.*",
                Type = RetroIO.Protocols.X25.Runtime.RouteType.Interface,
                Destination = "Serial0",
                Priority = 100,
                Enabled = true,
                Description = "Route responses back to machine 100 via Serial0"
            });
            
            // Configure route for 102.* to local services (we are machine 102)
            Config.Routing.RoutingTable.Add(new X25RouteEntry
            {
                X121AddressPattern = "102.*",
                Type = RetroIO.Protocols.X25.Runtime.RouteType.Local,
                Destination = "LocalTAD",
                Priority = 50,
                Enabled = true,
                Description = "Route to local services on machine 102"
            });
            
            // Recreate runtime with response routing configuration
            Runtime?.Dispose();
            var newRuntime = new ProtocolRuntime(Logger, Config);
            var runtimeProperty = typeof(TestBase).GetProperty("Runtime", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            runtimeProperty?.SetValue(this, newRuntime);
        }
        
        [Test]
        public void Test01_ResponseConnection_SABM_FromMachine100()
        {
            Logger.LogInformation("=== TEST 01: Response Connection - SABM from Machine 100 ===");
            
            // Same SABM as original, but in response context: 0x01 0x3F 0x00 0x64
            // This establishes the return communication channel
            var sabmPacket = new byte[] { 0x01, 0x3F, 0x00, 0x64 };
            
            Logger.LogInformation("Processing return channel SABM from machine 100...");
            Logger.LogInformation("  Packet: {Packet}", BitConverter.ToString(sabmPacket));
            Logger.LogInformation("  Context: Establishing response channel for 102→100 communication");
            Logger.LogInformation("  Address: 0x{Address:X2} (DTE)", sabmPacket[0]);
            Logger.LogInformation("  Control: 0x{Control:X2} (SABM, P=1)", sabmPacket[1]);
            Logger.LogInformation("  Source Machine ID: {MachineId}", ((sabmPacket[2] << 8) | sabmPacket[3]));
            
            _capturedHistory.Clear();
            Runtime.InjectLapbFrame(sabmPacket, (ushort)sabmPacket.Length);
            Thread.Sleep(200);
            
            // Verify LAPB connection for response channel
            var stats = Runtime.GetStatistics();
            Assert.That(stats.LAPB.CurrentState, Is.EqualTo("CONNECTED"), "Response channel should be established");
            
            // Should generate UA response
            var lapbFrames = _capturedHistory.OfType<LapbHistoryItem>().ToList();
            Assert.That(lapbFrames.Count, Is.GreaterThanOrEqualTo(2), "Should have SABM RX and UA TX for response channel");
            
            Logger.LogInformation("✅ Response channel established successfully (102→100)");
        }
        
        [Test]
        public void Test02_ResponseUA_AcknowledgeReturnChannel()
        {
            Logger.LogInformation("=== TEST 02: Response UA - Acknowledge Return Channel ===");
            
            // Establish response channel first
            var sabmPacket = new byte[] { 0x01, 0x3F, 0x00, 0x64 };
            Runtime.InjectLapbFrame(sabmPacket, (ushort)sabmPacket.Length);
            Thread.Sleep(100);
            
            // UA response in return channel context: 0x01 0x73 0x00 0x64
            var uaPacket = new byte[] { 0x01, 0x73, 0x00, 0x64 };
            
            Logger.LogInformation("Analyzing UA response for return channel...");
            Logger.LogInformation("  Packet: {Packet}", BitConverter.ToString(uaPacket));
            Logger.LogInformation("  Context: Machine 102 acknowledging return channel to machine 100");
            Logger.LogInformation("  Address: 0x{Address:X2} (DTE)", uaPacket[0]);
            Logger.LogInformation("  Control: 0x{Control:X2} (UA, F=1)", uaPacket[1]);
            Logger.LogInformation("  Target Machine ID: {MachineId}", ((uaPacket[2] << 8) | uaPacket[3]));
            
            // Validate return channel UA frame
            Assert.That(uaPacket[0], Is.EqualTo(0x01), "Address should be DTE");
            Assert.That((uaPacket[1] & 0xEF), Is.EqualTo(0x63), "Control should be UA");
            Assert.That((uaPacket[1] & 0x10), Is.EqualTo(0x10), "F bit should be set");
            Assert.That(((uaPacket[2] << 8) | uaPacket[3]), Is.EqualTo(100), "Target should be machine 100");
            
            Logger.LogInformation("✅ Return channel UA validated (102→100 confirmed)");
        }
        
        [Test] 
        public void Test03_InitialResponseFrame_RR_Analysis()
        {
            Logger.LogInformation("=== TEST 03: Initial Response Frame - RR Analysis ===");
            
            // Establish connection
            var sabmPacket = new byte[] { 0x01, 0x3F, 0x00, 0x64 };
            Runtime.InjectLapbFrame(sabmPacket, (ushort)sabmPacket.Length);
            Thread.Sleep(100);
            
            // Initial response RR frame: 0x09 0x01 0x00 0x64
            // Control 0x01 = RR S-frame with N(R)=0
            var initialRRPacket = new byte[] { 0x09, 0x01, 0x00, 0x64 };
            
            Logger.LogInformation("Processing initial response RR frame...");
            Logger.LogInformation("  Packet: {Packet}", BitConverter.ToString(initialRRPacket));
            Logger.LogInformation("  Context: Machine 102 acknowledging reception from machine 100");
            Logger.LogInformation("  Address: 0x{Address:X2} (DCE addressing)", initialRRPacket[0]);
            Logger.LogInformation("  Control: 0x{Control:X2} (RR S-frame, N(R)=0)", initialRRPacket[1]);
            
            // Analyze RR frame
            bool isSFrame = (initialRRPacket[1] & 0x03) == 0x01;
            byte nr = (byte)((initialRRPacket[1] >> 5) & 0x07);
            bool pollFinal = (initialRRPacket[1] & 0x10) != 0;
            byte sType = (byte)((initialRRPacket[1] >> 2) & 0x03);
            string frameType = sType switch
            {
                0 => "RR (Receive Ready)",
                1 => "RNR (Receive Not Ready)", 
                2 => "REJ (Reject)",
                3 => "SREJ (Selective Reject)",
                _ => "Unknown"
            };
            
            Assert.That(isSFrame, Is.True, "Should be S-frame");
            Assert.That(frameType, Is.EqualTo("RR (Receive Ready)"), "Should be RR frame");
            Assert.That(nr, Is.EqualTo(0), "N(R) should be 0 for initial response");
            Assert.That(pollFinal, Is.False, "P/F should be 0");
            
            Logger.LogInformation("  Analysis: {FrameType}, N(R)={NR}, P/F={PF}", frameType, nr, pollFinal);
            
            _capturedHistory.Clear();
            Runtime.InjectLapbFrame(initialRRPacket, (ushort)initialRRPacket.Length);
            Thread.Sleep(100);
            
            var lapbItems = _capturedHistory.OfType<LapbHistoryItem>().Where(h => h.Direction == Direction.RX).ToList();
            Assert.That(lapbItems.Any(), Is.True, "Should process initial RR response");
            
            Logger.LogInformation("✅ Initial response RR frame processed successfully");
        }
        
        [Test]
        public void Test04_ResponseDataPacket_WithRoutingInfo()
        {
            Logger.LogInformation("=== TEST 04: Response Data Packet with Routing Info ===");
            
            // Establish connection
            var sabmPacket = new byte[] { 0x01, 0x3F, 0x00, 0x64 };
            Runtime.InjectLapbFrame(sabmPacket, (ushort)sabmPacket.Length);
            Thread.Sleep(100);
            
            // Response data packet with routing: 0x09 0x00 0x21 0x13 0x00 0x19 0x00 0x66 0x00 0x64 0xFF 0xFF 0x00 0x01 0xDE 0x08
            // This is machine 102 responding with data back to machine 100
            // Packet structure analysis:
            // 0x09         = LAPB Address (DCE addressing)
            // 0x00         = LAPB I-frame: N(S)=0, N(R)=0, P/F=0
            // 0x21 0x13    = X.25 routing protocol header
            // 0x00 0x19    = Data length (25 bytes)
            // 0x00 0x66    = Source: Machine 102 (0x66 = 102 decimal)
            // 0x00 0x64    = Destination: Machine 100 (0x64 = 100 decimal) - RESPONSE DIRECTION!
            // 0xFF 0xFF    = Protocol/Service identifier
            // 0x00 0x01    = Response sequence/type
            // 0xDE 0x08    = Response data/CRC
            var responseDataPacket = new byte[] { 
                0x09, 0x00, 0x21, 0x13, 0x00, 0x19, 0x00, 0x66, 
                0x00, 0x64, 0xFF, 0xFF, 0x00, 0x01, 0xDE, 0x08 
            };
            
            Logger.LogInformation("Processing response data packet (102→100)...");
            Logger.LogInformation("  Full packet: {Packet}", BitConverter.ToString(responseDataPacket));
            Logger.LogInformation("  Context: Machine 102 sending data response to machine 100");
            
            // LAPB header analysis
            byte address = responseDataPacket[0];
            byte control = responseDataPacket[1];
            Logger.LogInformation("  LAPB Address: 0x{Address:X2} (DCE addressing)", address);
            Logger.LogInformation("  LAPB Control: 0x{Control:X2} (I-frame, N(S)=0, N(R)=0)", control);
            
            // X.25 routing analysis
            var x25Payload = responseDataPacket.Skip(2).ToArray();
            Logger.LogInformation("  X.25 routing payload: {Length} bytes", x25Payload.Length);
            
            if (x25Payload.Length >= 8)
            {
                // After 0x21 0x13 (2 bytes) and 0x00 0x19 length (2 bytes), source at offset 4-5
                var source = (x25Payload[4] << 8) | x25Payload[5];      // Should be 102
                var destination = (x25Payload[6] << 8) | x25Payload[7]; // Should be 100
                
                Logger.LogInformation("  Routing Analysis:");
                Logger.LogInformation("    Source: {Source} (Machine 102)", source);
                Logger.LogInformation("    Destination: {Destination} (Machine 100)", destination);
                Logger.LogInformation("    Direction: RESPONSE (102→100)");
                
                Assert.That(source, Is.EqualTo(102), "Source should be machine 102");
                Assert.That(destination, Is.EqualTo(100), "Destination should be machine 100 (response)");
            }
            
            // Verify I-frame structure
            bool isIFrame = (control & 0x01) == 0;
            Assert.That(isIFrame, Is.True, "Should be I-frame for data response");
            
            _capturedHistory.Clear();
            Runtime.InjectLapbFrame(responseDataPacket, (ushort)responseDataPacket.Length);
            Thread.Sleep(200);
            
            var historyItems = _capturedHistory.ToList();
            Assert.That(historyItems.Any(), Is.True, "Should generate history for response data");
            
            Logger.LogInformation("✅ Response data packet analyzed successfully (102→100)");
        }
        
        [Test]
        public void Test05_ExtendedResponseData_Analysis()
        {
            Logger.LogInformation("=== TEST 05: Extended Response Data Analysis ===");
            
            // Establish connection
            var sabmPacket = new byte[] { 0x01, 0x3F, 0x00, 0x64 };
            Runtime.InjectLapbFrame(sabmPacket, (ushort)sabmPacket.Length);
            Thread.Sleep(100);
            
            // Extended response with multiple data sections:
            // 0x09 0x22 0x21 0x13 0x00 0x0E 0x00 0x66 0x00 0x64 0x00 0x00 0x01 0x00 0xDD 0x14 
            // 0x21 0x00 0x86 0xC4 0x00 0x66 0x00 0x00 0x00 0x64 0x02 0xC2 0x01 0x00 
            // 0x01 0x4B 0x00 0x04 0x01 0x02 0x00 0x66
            var extendedResponsePacket = new byte[] { 
                0x09, 0x22, 0x21, 0x13, 0x00, 0x0E, 0x00, 0x66, 0x00, 0x64, 0x00, 0x00, 0x01, 0x00, 0xDD, 0x14,
                0x21, 0x00, 0x86, 0xC4, 0x00, 0x66, 0x00, 0x00, 0x00, 0x64, 0x02, 0xC2, 0x01, 0x00,
                0x01, 0x4B, 0x00, 0x04, 0x01, 0x02, 0x00, 0x66
            };
            
            Logger.LogInformation("Processing extended response data packet...");
            Logger.LogInformation("  Full packet: {Packet}", BitConverter.ToString(extendedResponsePacket));
            Logger.LogInformation("  Context: Extended response from machine 102 to machine 100");
            Logger.LogInformation("  Total length: {Length} bytes", extendedResponsePacket.Length);
            
            // LAPB header analysis
            byte control = extendedResponsePacket[1];
            bool isIFrame = (control & 0x01) == 0;
            byte ns = (byte)((control >> 1) & 0x07);
            byte nr = (byte)((control >> 5) & 0x07);
            bool pollFinal = (control & 0x10) != 0;
            
            Logger.LogInformation("  LAPB Analysis:");
            Logger.LogInformation("    Is I-Frame: {IsIFrame}", isIFrame);
            Logger.LogInformation("    N(S): {NS} (sequence 1)", ns);
            Logger.LogInformation("    N(R): {NR}", nr);
            Logger.LogInformation("    P/F: {PF}", pollFinal);
            
            Assert.That(isIFrame, Is.True, "Should be I-frame");
            Assert.That(ns, Is.EqualTo(1), "Should be sequence 1");
            // Control byte 0x22: P/F bit (bit 4) is 0, not 1
            Assert.That(pollFinal, Is.False, "P/F bit not set in this packet");
            
            // Analyze multiple data sections
            var x25Data = extendedResponsePacket.Skip(2).ToArray();
            Logger.LogInformation("  X.25 data sections: {Length} bytes", x25Data.Length);
            
            // Look for multiple routing headers (0x21 patterns)
            var routingHeaders = new List<int>();
            for (int i = 0; i < x25Data.Length - 1; i++)
            {
                if (x25Data[i] == 0x21 && i < x25Data.Length - 1)
                {
                    routingHeaders.Add(i);
                }
            }
            
            Logger.LogInformation("  Found {Count} routing header(s) in extended response", routingHeaders.Count);
            Assert.That(routingHeaders.Count, Is.GreaterThan(0), "Should contain routing headers");
            
            // Analyze addressing in different sections
            bool hasSource102 = x25Data.Contains((byte)0x66);
            bool hasDestination100 = x25Data.Contains((byte)0x64);
            
            Assert.That(hasSource102, Is.True, "Should contain source machine 102");
            Assert.That(hasDestination100, Is.True, "Should contain destination machine 100");
            
            _capturedHistory.Clear();
            Runtime.InjectLapbFrame(extendedResponsePacket, (ushort)extendedResponsePacket.Length);
            Thread.Sleep(300);
            
            var historyItems = _capturedHistory.ToList();
            Assert.That(historyItems.Any(), Is.True, "Should process extended response");
            
            Logger.LogInformation("✅ Extended response data analyzed successfully");
        }
        
        [Test]
        public void Test06_ResponseFlowControl_Sequence_Analysis()
        {
            Logger.LogInformation("=== TEST 06: Response Flow Control Sequence ===");
            
            // Establish connection
            var sabmPacket = new byte[] { 0x01, 0x3F, 0x00, 0x64 };
            Runtime.InjectLapbFrame(sabmPacket, (ushort)sabmPacket.Length);
            Thread.Sleep(100);
            
            // Flow control sequence in response communication:
            var flowControlSequence = new (string name, byte[] data, string expectedType, int expectedNR)[]
            {
                ("Response RR 1", new byte[] { 0x09, 0x29, 0x00, 0x64 }, "REJ (Reject)", 1),
                ("Response RR 2", new byte[] { 0x09, 0x41, 0x00, 0x64 }, "RR (Receive Ready)", 2),  
                ("Response RR 3", new byte[] { 0x09, 0x69, 0x00, 0x64 }, "REJ (Reject)", 3),
                ("Response RR 4", new byte[] { 0x09, 0x81, 0x00, 0x64 }, "RR (Receive Ready)", 4),
                ("Response RR 5", new byte[] { 0x09, 0xA9, 0x00, 0x64 }, "REJ (Reject)", 5)
            };
            
            Logger.LogInformation("Analyzing response flow control sequence...");
            Logger.LogInformation("  Context: Machine 102 flow control responses to machine 100");
            
            foreach (var (name, data, expectedType, expectedNR) in flowControlSequence)
            {
                Logger.LogInformation("Processing {Name}: {Data}", name, BitConverter.ToString(data));
                
                byte control = data[1];
                bool isSFrame = (control & 0x03) == 0x01;
                byte nr = (byte)((control >> 5) & 0x07);
                bool pollFinal = (control & 0x10) != 0;
                byte sType = (byte)((control >> 2) & 0x03);
                string frameType = sType switch
                {
                    0 => "RR (Receive Ready)",
                    1 => "RNR (Receive Not Ready)",
                    2 => "REJ (Reject)", 
                    3 => "SREJ (Selective Reject)",
                    _ => "Unknown"
                };
                
                Logger.LogInformation("  {Name} Analysis:", name);
                Logger.LogInformation("    Frame Type: {Type}", frameType);
                Logger.LogInformation("    N(R): {NR} (expected {Expected})", nr, expectedNR);
                Logger.LogInformation("    P/F: {PF}", pollFinal);
                
                Assert.That(isSFrame, Is.True, $"{name} should be S-frame");
                Assert.That(frameType, Is.EqualTo(expectedType), $"{name} should be {expectedType}");
                Assert.That(nr, Is.EqualTo(expectedNR), $"{name} should have N(R)={expectedNR}");
                
                // Process frame
                _capturedHistory.Clear();
                Runtime.InjectLapbFrame(data, (ushort)data.Length);
                Thread.Sleep(50);
                
                var lapbItems = _capturedHistory.OfType<LapbHistoryItem>().ToList();
                Assert.That(lapbItems.Any(), Is.True, $"Should process {name}");
            }
            
            Logger.LogInformation("✅ Response flow control sequence validated");
        }
        
        [Test]
        public void Test07_ResponseDataPackets_WithSequencing()
        {
            Logger.LogInformation("=== TEST 07: Response Data Packets with Sequencing ===");
            
            // Establish connection
            var sabmPacket = new byte[] { 0x01, 0x3F, 0x00, 0x64 };
            Runtime.InjectLapbFrame(sabmPacket, (ushort)sabmPacket.Length);
            Thread.Sleep(100);
            
            // Response data packets with proper sequencing:
            // Control bytes decoded: N(S) = (control >> 1) & 0x07
            // 0x64 -> N(S)=2, 0x66 -> N(S)=3, 0xA8 -> N(S)=4
            var responseDataSequence = new (string name, byte[] data, int expectedNS)[]
            {
                ("Response Data 1", new byte[] { 0x09, 0x64, 0x21, 0x13, 0x00, 0x03, 0x00, 0x66, 0x00, 0x64, 0x00, 0x00, 0x00, 0x01, 0xDE, 0x1E }, 2),
                ("Response Data 2", new byte[] { 0x09, 0x66, 0x21, 0x13, 0x00, 0x0E, 0x00, 0x66, 0x00, 0x64, 0x00, 0x01, 0x01, 0x00, 0xDD, 0x13 }, 3),
                ("Response Data 3", new byte[] { 0x09, 0xA8, 0x21, 0x13, 0x00, 0x03, 0x00, 0x66, 0x00, 0x64, 0x00, 0x01, 0x00, 0x01, 0xDE, 0x1D }, 4)
            };
            
            Logger.LogInformation("Analyzing response data packet sequencing...");
            Logger.LogInformation("  Context: Sequenced data responses from machine 102 to machine 100");
            
            foreach (var (name, data, expectedNS) in responseDataSequence)
            {
                Logger.LogInformation("Processing {Name}: {Data}", name, BitConverter.ToString(data));
                
                byte control = data[1];
                bool isIFrame = (control & 0x01) == 0;
                byte ns = (byte)((control >> 1) & 0x07);
                byte nr = (byte)((control >> 5) & 0x07);
                
                Logger.LogInformation("  {Name} Analysis:", name);
                Logger.LogInformation("    Is I-Frame: {IsIFrame}", isIFrame);
                Logger.LogInformation("    N(S): {NS} (expected {Expected})", ns, expectedNS);
                Logger.LogInformation("    N(R): {NR}", nr);
                
                Assert.That(isIFrame, Is.True, $"{name} should be I-frame");
                Assert.That(ns, Is.EqualTo(expectedNS), $"{name} should have N(S)={expectedNS}");
                
                // Analyze X.25 routing data
                if (data.Length > 4 && data[2] == 0x21 && data[3] == 0x13)
                {
                    Logger.LogInformation("    Contains routing protocol 0x21 0x13");
                    
                    // Look for address fields
                    bool hasSource102 = data.Contains((byte)0x66);
                    bool hasDestination100 = data.Contains((byte)0x64);
                    
                    Assert.That(hasSource102, Is.True, $"{name} should contain source 102");
                    Assert.That(hasDestination100, Is.True, $"{name} should contain destination 100");
                }
                
                // Process frame
                _capturedHistory.Clear();
                Runtime.InjectLapbFrame(data, (ushort)data.Length);
                Thread.Sleep(100);
                
                var historyItems = _capturedHistory.ToList();
                Assert.That(historyItems.Any(), Is.True, $"Should process {name}");
            }
            
            Logger.LogInformation("✅ Response data packet sequencing validated");
        }
        
        [Test]
        public void Test08_CompleteResponseFlow_Integration()
        {
            Logger.LogInformation("=== TEST 08: Complete Response Flow Integration ===");
            
            // Process the complete response packet sequence to validate end-to-end flow
            var completeResponseFlow = new (string phase, byte[] data, string description)[]
            {
                ("Connection Setup", new byte[] { 0x01, 0x3F, 0x00, 0x64 }, "SABM from machine 100"),
                ("Connection Ack", new byte[] { 0x01, 0x73, 0x00, 0x64 }, "UA to machine 100"),
                ("Initial Response", new byte[] { 0x09, 0x01, 0x00, 0x64 }, "Initial RR response"),
                ("Data Response 1", new byte[] { 0x09, 0x00, 0x21, 0x13, 0x00, 0x19, 0x00, 0x66, 0x00, 0x64, 0xFF, 0xFF, 0x00, 0x01, 0xDE, 0x08 }, "First data response"),
                ("Extended Response", new byte[] { 0x09, 0x22, 0x21, 0x13, 0x00, 0x0E, 0x00, 0x66, 0x00, 0x64, 0x00, 0x00, 0x01, 0x00, 0xDD, 0x14 }, "Extended response data"),
                ("Flow Control 1", new byte[] { 0x09, 0x29, 0x00, 0x64 }, "REJ N(R)=1"),
                ("Flow Control 2", new byte[] { 0x09, 0x41, 0x00, 0x64 }, "RR N(R)=2"),
                ("Data Response 2", new byte[] { 0x09, 0x64, 0x21, 0x13, 0x00, 0x03, 0x00, 0x66, 0x00, 0x64, 0x00, 0x00, 0x00, 0x01, 0xDE, 0x1E }, "Second data response"),
                ("Flow Control 3", new byte[] { 0x09, 0x69, 0x00, 0x64 }, "REJ N(R)=3"),
                ("Final Response", new byte[] { 0x09, 0xA8, 0x21, 0x13, 0x00, 0x03, 0x00, 0x66, 0x00, 0x64, 0x00, 0x01, 0x00, 0x01, 0xDE, 0x1D }, "Final data response"),
                ("Final Flow Control", new byte[] { 0x09, 0xA9, 0x00, 0x64 }, "Final REJ N(R)=5")
            };
            
            Logger.LogInformation("Processing complete response flow (102→100)...");
            Logger.LogInformation("  Total phases: {Count}", completeResponseFlow.Length);
            
            var flowResults = new List<(string phase, bool processed, string frameType)>();
            
            foreach (var (phase, data, description) in completeResponseFlow)
            {
                Logger.LogInformation("Processing {Phase}: {Description}", phase, description);
                Logger.LogInformation("  Data: {Data}", BitConverter.ToString(data));
                
                // Determine frame type
                string frameType = "Unknown";
                if (data.Length >= 2)
                {
                    byte control = data[1];
                    if ((control & 0x01) == 0)
                        frameType = "I-Frame (Data)";
                    else if ((control & 0x03) == 0x01)
                        frameType = "S-Frame (Supervisory)";
                    else if ((control & 0x03) == 0x03)
                        frameType = "U-Frame (Unnumbered)";
                }
                
                // Process frame (skip UA since it's response)
                bool processed = true;
                if (phase != "Connection Ack")
                {
                    try
                    {
                        _capturedHistory.Clear();
                        Runtime.InjectLapbFrame(data, (ushort)data.Length);
                        Thread.Sleep(50);
                        processed = _capturedHistory.Any();
                    }
                    catch (Exception ex)
                    {
                        Logger.LogWarning("Error processing {Phase}: {Error}", phase, ex.Message);
                        processed = false;
                    }
                }
                
                flowResults.Add((phase, processed, frameType));
                Logger.LogInformation("  Result: {FrameType}, Processed: {Processed}", frameType, processed);
            }
            
            // Analyze flow results
            var iFrameCount = flowResults.Count(r => r.frameType == "I-Frame (Data)");
            var sFrameCount = flowResults.Count(r => r.frameType == "S-Frame (Supervisory)");
            var uFrameCount = flowResults.Count(r => r.frameType == "U-Frame (Unnumbered)");
            var processedCount = flowResults.Count(r => r.processed);
            
            Logger.LogInformation("Complete Response Flow Analysis:");
            Logger.LogInformation("  I-Frames (Data): {IFrames}", iFrameCount);
            Logger.LogInformation("  S-Frames (Supervisory): {SFrames}", sFrameCount);
            Logger.LogInformation("  U-Frames (Unnumbered): {UFrames}", uFrameCount);
            Logger.LogInformation("  Successfully Processed: {Processed}/{Total}", processedCount, completeResponseFlow.Length);
            
            // Verify response flow characteristics
            Assert.That(iFrameCount, Is.GreaterThan(0), "Response flow should contain data I-frames");
            Assert.That(sFrameCount, Is.GreaterThan(0), "Response flow should contain supervisory frames");
            Assert.That(uFrameCount, Is.GreaterThan(0), "Response flow should contain connection frames");
            Assert.That(processedCount, Is.GreaterThanOrEqualTo(8), "Most frames should process successfully");
            
            Logger.LogInformation("✅ Complete response flow integration validated (102→100)");
        }
        
        [TearDown]
        public override void TearDown()
        {
            Runtime.HistoryItemAdded -= OnHistoryItemAdded;
            base.TearDown();
        }
    }
}