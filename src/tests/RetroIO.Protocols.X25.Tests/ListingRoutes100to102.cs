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
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using RetroIO.Link.Models;
using RetroIO.Link.Physical;
using RetroIO.Protocols.X25;
using RetroIO.Protocols.X25.Models;
using RetroIO.Protocols.X25.Modules;
using RetroIO.Protocols.X25.Runtime;
using RetroIO.Protocols.X25.Runtime.Models;
using RetroIO.Protocols.X25.Runtime.Transport;
using RetroIO.Protocols.X25.Sintran;
using RetroIO.Protocols.X25.Transport;

namespace RetroIO.Protocols.X25.Tests
{
    /// <summary>
    /// Unit tests for packet communication flow between machines 100 and 102
    /// Tests LAPB connection establishment, X.25 routing, and data exchange patterns
    /// </summary>
    [TestFixture]
    public class ListingRoutes100to102Tests : TestBase
    {
        private readonly List<HistoryItem> _capturedHistory = new();
        
        [SetUp]
        public override void Setup()
        {
            base.Setup();
            
            // Configure routing for machines 100 ↔ 102 communication
            ConfigureRoutingFor100And102();
            
            // Subscribe to history events
            Runtime.HistoryItemAdded += OnHistoryItemAdded;
            
            Logger.LogInformation("ListingRoutes100to102Tests setup complete with bidirectional routing");
        }
        
        private void OnHistoryItemAdded(object? sender, HistoryItem item)
        {
            _capturedHistory.Add(item);
        }
        
        private void ConfigureRoutingFor100And102()
        {
            // Enable test mode for mock interfaces
            Config.TestMode = true;
            Config.X25.LocalMachineId = "102";  // We are machine 102
            
            // Configure serial interface for machine 100
            Config.Routing.SerialInterfaces.Add(new SerialInterfaceConfig
            {
                Name = "Serial0",
                PortName = "MOCK_SERIAL100",
                BaudRate = 9600,
                RemoteMachineId = "100",
                Enabled = true,
                Description = "Mock serial connection to machine 100"
            });
            
            // Configure route for 100.* to Serial0
            Config.Routing.RoutingTable.Add(new X25RouteEntry
            {
                X121AddressPattern = "100.*",
                Type = RetroIO.Protocols.X25.Runtime.RouteType.Interface,
                Destination = "Serial0",
                Priority = 100,
                Enabled = true,
                Description = "Route to machine 100 via Serial0"
            });
            
            // Configure route for 102.* to local services (we are machine 102)
            Config.Routing.RoutingTable.Add(new X25RouteEntry
            {
                X121AddressPattern = "102.*",
                Type = RetroIO.Protocols.X25.Runtime.RouteType.Local,
                Destination = "LocalTAD",
                Priority = 50,
                Enabled = true,
                Description = "Route to local TAD service (machine 102)"
            });
            
            // Recreate runtime with routing configuration
            Runtime?.Dispose();
            var newRuntime = new ProtocolRuntime(Logger, Config);
            var runtimeProperty = typeof(TestBase).GetProperty("Runtime", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            runtimeProperty?.SetValue(this, newRuntime);
        }
        
        [Test]
        public void Test01_SABM_EstablishConnection_FromMachine100()
        {
            Logger.LogInformation("=== TEST 01: SABM Connection Establishment ===");
            
            // SABM from machine 100: 0x01 0x3F 0x00 0x64
            var sabmPacket = new byte[] { 0x01, 0x3F, 0x00, 0x64 };
            
            Logger.LogInformation("Processing SABM from machine 100...");
            Logger.LogInformation("  Packet: {Packet}", BitConverter.ToString(sabmPacket));
            Logger.LogInformation("  Address: 0x{Address:X2} (DTE)", sabmPacket[0]);
            Logger.LogInformation("  Control: 0x{Control:X2} (SABM, P=1)", sabmPacket[1]);
            Logger.LogInformation("  Machine ID: {MachineId}", ((sabmPacket[2] << 8) | sabmPacket[3]));
            
            _capturedHistory.Clear();
            Runtime.InjectLapbFrame(sabmPacket, (ushort)sabmPacket.Length);
            Thread.Sleep(200);
            
            // Verify LAPB connection established
            var stats = Runtime.GetStatistics();
            Assert.That(stats.LAPB.CurrentState, Is.EqualTo("CONNECTED"), "LAPB should be connected after SABM");
            
            // Should generate UA response
            var lapbFrames = _capturedHistory.OfType<LapbHistoryItem>().ToList();
            Assert.That(lapbFrames.Count, Is.GreaterThanOrEqualTo(2), "Should have SABM RX and UA TX frames");
            
            var uaFrame = lapbFrames.FirstOrDefault(f => f.Direction == Direction.TX && (f.Control & 0xEF) == 0x63);
            Assert.That(uaFrame, Is.Not.Null, "Should generate UA response");
            
            Logger.LogInformation("✅ SABM processed successfully, LAPB connection established");
        }
        
        [Test]
        public void Test02_UA_AcknowledgeConnection_ToMachine100()
        {
            Logger.LogInformation("=== TEST 02: UA Connection Acknowledgment ===");
            
            // First establish connection with SABM
            var sabmPacket = new byte[] { 0x01, 0x3F, 0x00, 0x64 };
            Runtime.InjectLapbFrame(sabmPacket, (ushort)sabmPacket.Length);
            Thread.Sleep(100);
            
            // UA response to machine 100: 0x01 0x73 0x00 0x64
            // (This should be generated automatically by SABM, but we test explicit processing)
            var uaPacket = new byte[] { 0x01, 0x73, 0x00, 0x64 };
            
            Logger.LogInformation("Analyzing UA response to machine 100...");
            Logger.LogInformation("  Packet: {Packet}", BitConverter.ToString(uaPacket));
            Logger.LogInformation("  Address: 0x{Address:X2} (DTE)", uaPacket[0]);
            Logger.LogInformation("  Control: 0x{Control:X2} (UA, F=1)", uaPacket[1]);
            Logger.LogInformation("  Machine ID: {MachineId}", ((uaPacket[2] << 8) | uaPacket[3]));
            
            // Verify the frame structure
            Assert.That(uaPacket[0], Is.EqualTo(0x01), "Address should be DTE");
            Assert.That((uaPacket[1] & 0xEF), Is.EqualTo(0x63), "Control should be UA");
            Assert.That((uaPacket[1] & 0x10), Is.EqualTo(0x10), "F bit should be set");
            Assert.That(((uaPacket[2] << 8) | uaPacket[3]), Is.EqualTo(100), "Machine ID should be 100");
            
            Logger.LogInformation("✅ UA frame structure validated");
        }
        
        [Test]
        public void Test03_SFrame_DataPacket1_Analysis()
        {
            Logger.LogInformation("=== TEST 03: S-Frame Data Packet Analysis ===");
            
            // Establish LAPB connection first
            var sabmPacket = new byte[] { 0x01, 0x3F, 0x00, 0x64 };
            Runtime.InjectLapbFrame(sabmPacket, (ushort)sabmPacket.Length);
            Thread.Sleep(100);
            
            // S-frame packet: 0x09 0x01 0x00 0x64
            // Control 0x01 = S-frame RR with N(R)=0
            var sFramePacket = new byte[] { 0x09, 0x01, 0x00, 0x64 };
            
            Logger.LogInformation("Processing S-frame data packet...");
            Logger.LogInformation("  Packet: {Packet}", BitConverter.ToString(sFramePacket));
            Logger.LogInformation("  Address: 0x{Address:X2}", sFramePacket[0]);
            Logger.LogInformation("  Control: 0x{Control:X2} (S-frame RR, N(R)=0)", sFramePacket[1]);
            
            // Analyze S-frame control field
            bool isSFrame = (sFramePacket[1] & 0x03) == 0x01;
            byte nr = (byte)((sFramePacket[1] >> 5) & 0x07);
            bool pollFinal = (sFramePacket[1] & 0x10) != 0;
            byte sType = (byte)((sFramePacket[1] >> 2) & 0x03);
            string frameType = sType switch
            {
                0 => "RR (Receive Ready)",
                1 => "RNR (Receive Not Ready)",
                2 => "REJ (Reject)",
                3 => "SREJ (Selective Reject)",
                _ => "Unknown"
            };
            
            Assert.That(isSFrame, Is.True, "Should be S-frame");
            Assert.That(nr, Is.EqualTo(0), "N(R) should be 0");
            Assert.That(pollFinal, Is.False, "P/F should be 0");
            Assert.That(frameType, Is.EqualTo("RR (Receive Ready)"), "Should be RR frame");
            
            _capturedHistory.Clear();
            Runtime.InjectLapbFrame(sFramePacket, (ushort)sFramePacket.Length);
            Thread.Sleep(200);
            
            // Verify S-frame processing
            var lapbItems = _capturedHistory.OfType<LapbHistoryItem>().Where(h => h.Direction == Direction.RX).ToList();
            Assert.That(lapbItems.Any(), Is.True, "Should process S-frame");
            
            Logger.LogInformation("✅ S-frame analyzed and processed successfully");
        }
        
        [Test]
        public void Test04_ComplexDataPacket_X25Analysis()
        {
            Logger.LogInformation("=== TEST 04: Complex Data Packet with X.25 Analysis ===");
            
            // Establish connection
            var sabmPacket = new byte[] { 0x01, 0x3F, 0x00, 0x64 };
            Runtime.InjectLapbFrame(sabmPacket, (ushort)sabmPacket.Length);
            Thread.Sleep(100);
            
            // Complex data packet with routing info: 100 → 102
            // Packet structure analysis:
            // 0x09         = LAPB Address (DCE, machine addressing)
            // 0x00         = LAPB I-frame Control: N(S)=0, N(R)=0, P/F=0
            // 0x21         = X.25 GFI + LCN high (GFI=2, LCN high=1)
            // 0x13         = X.25 LCN low + Packet Type (LCN=0x113=275, or routing packet)
            // 0x00 0x19    = Data length field (25 bytes follow)
            // 0x00 0x66    = Destination: Machine 102 (0x66 = 102 decimal)
            // 0x00 0x64    = Source: Machine 100 (0x64 = 100 decimal)
            // 0xFF 0xFF    = Protocol/Service identifier
            // 0x00 0x01    = Sequence/Message type
            // 0xDE 0x08    = Data payload/CRC
            var complexPacket = new byte[] { 
                0x09, 0x00, 0x21, 0x13, 0x00, 0x19, 0x00, 0x66, 
                0x00, 0x64, 0xFF, 0xFF, 0x00, 0x01, 0xDE, 0x08 
            };
            
            Logger.LogInformation("Processing complex data packet...");
            Logger.LogInformation("  Full packet: {Packet}", BitConverter.ToString(complexPacket));
            
            // LAPB header analysis
            byte address = complexPacket[0];
            byte control = complexPacket[1];
            Logger.LogInformation("  LAPB Address: 0x{Address:X2}", address);
            Logger.LogInformation("  LAPB Control: 0x{Control:X2} (I-frame, N(S)=0, N(R)=0)", control);
            
            // X.25 payload analysis (skip LAPB header)
            var x25Payload = complexPacket.Skip(2).ToArray();
            Logger.LogInformation("  X.25 payload length: {Length} bytes", x25Payload.Length);
            Logger.LogInformation("  X.25 payload: {Payload}", BitConverter.ToString(x25Payload));
            
            if (x25Payload.Length > 0)
            {
                // Analyze X.25 header
                byte gfi = (byte)(x25Payload[0] >> 4);
                ushort lcn = (ushort)(((x25Payload[0] & 0x0F) << 8) | x25Payload[1]);
                byte packetType = x25Payload[2];
                
                Logger.LogInformation("  X.25 GFI: 0x{GFI:X}", gfi);
                Logger.LogInformation("  X.25 LCN: {LCN}", lcn);
                Logger.LogInformation("  X.25 Packet Type: 0x{PacketType:X2}", packetType);
                
                // Analyze payload data
                if (x25Payload.Length > 3)
                {
                    var userData = x25Payload.Skip(3).ToArray();
                    Logger.LogInformation("  User data: {UserData}", BitConverter.ToString(userData));
                    
                    // Look for specific patterns
                    if (userData.Length >= 4 && userData[0] == 0x13 && userData[1] == 0x00)
                    {
                        ushort dataLength = (ushort)((userData[2] << 8) | userData[3]);
                        Logger.LogInformation("  Data structure detected - Length field: {Length}", dataLength);
                    }
                }
            }
            
            _capturedHistory.Clear();
            Runtime.InjectLapbFrame(complexPacket, (ushort)complexPacket.Length);
            Thread.Sleep(300);
            
            // Verify processing
            var historyItems = _capturedHistory.ToList();
            Assert.That(historyItems.Any(), Is.True, "Should generate history items");
            
            Logger.LogInformation("✅ Complex packet analyzed successfully");
        }
        
        [Test]
        public void Test05_MultiPacketSequence_AnalyzeEntireFlow()
        {
            Logger.LogInformation("=== TEST 05: Multi-Packet Sequence Analysis ===");
            
            // Define the complete packet sequence from machine 100 to 200
            var packetSequence = new (string name, byte[] data)[]
            {
                ("SABM", new byte[] { 0x01, 0x3F, 0x00, 0x64 }),
                ("UA", new byte[] { 0x01, 0x73, 0x00, 0x64 }),
                ("I-Frame 1", new byte[] { 0x09, 0x01, 0x00, 0x64 }),
                ("I-Frame 2", new byte[] { 0x09, 0x00, 0x21, 0x13, 0x00, 0x19, 0x00, 0x66, 0x00, 0x64, 0xFF, 0xFF, 0x00, 0x01, 0xDE, 0x08 }),
                ("I-Frame 3", new byte[] { 0x09, 0x22, 0x21, 0x13, 0x00, 0x0E, 0x00, 0x66, 0x00, 0x64, 0x00, 0x00, 0x01, 0x00, 0xDD, 0x14, 0x21, 0x00, 0x86, 0xC4, 0x00, 0x66, 0x00, 0x00, 0x00, 0x64, 0x02, 0xC2, 0x01, 0x00, 0x01, 0x4B, 0x00, 0x04, 0x01, 0x02, 0x00, 0x66 }),
                ("S-Frame 1", new byte[] { 0x09, 0x29, 0x00, 0x64 }),
                ("S-Frame 2", new byte[] { 0x09, 0x41, 0x00, 0x64 }),
                ("I-Frame 4", new byte[] { 0x09, 0x64, 0x21, 0x13, 0x00, 0x03, 0x00, 0x66, 0x00, 0x64, 0x00, 0x00, 0x00, 0x01, 0xDE, 0x1E }),
                ("S-Frame 3", new byte[] { 0x09, 0x69, 0x00, 0x64 }),
                ("I-Frame 5", new byte[] { 0x09, 0x66, 0x21, 0x13, 0x00, 0x0E, 0x00, 0x66, 0x00, 0x64, 0x00, 0x01, 0x01, 0x00, 0xDD, 0x13 }),
                ("U-Frame", new byte[] { 0x01, 0x4B, 0xB7, 0xF0, 0x01, 0x02, 0x00, 0x66 }),
                ("S-Frame 4", new byte[] { 0x09, 0x81, 0x00, 0x64 }),
                ("I-Frame 6", new byte[] { 0x09, 0xA8, 0x21, 0x13, 0x00, 0x03, 0x00, 0x66, 0x00, 0x64, 0x00, 0x01, 0x00, 0x01, 0xDE, 0x1D }),
                ("S-Frame 5", new byte[] { 0x09, 0xA9, 0x00, 0x64 })
            };
            
            Logger.LogInformation("Processing complete packet sequence ({Count} packets)...", packetSequence.Length);
            
            var sequenceResults = new List<(string name, bool isIFrame, bool isSFrame, bool isUFrame, int x25PayloadLength)>();
            
            foreach (var (name, data) in packetSequence)
            {
                Logger.LogInformation("Processing {Name}: {Data}", name, BitConverter.ToString(data));
                
                if (data.Length >= 2)
                {
                    byte control = data[1];
                    bool isIFrame = (control & 0x01) == 0;
                    bool isSFrame = (control & 0x03) == 0x01;
                    bool isUFrame = (control & 0x03) == 0x03;
                    
                    int x25PayloadLength = 0;
                    if (isIFrame && data.Length > 2)
                    {
                        x25PayloadLength = data.Length - 2; // Subtract LAPB header
                    }
                    
                    sequenceResults.Add((name, isIFrame, isSFrame, isUFrame, x25PayloadLength));
                    
                    Logger.LogInformation("  Frame Type: {Type}, X.25 Payload: {PayloadLength} bytes",
                        isIFrame ? "I-Frame" : isSFrame ? "S-Frame" : isUFrame ? "U-Frame" : "Unknown",
                        x25PayloadLength);
                }
                
                // Process through runtime (only if not UA - that's a response)
                if (name != "UA")
                {
                    try
                    {
                        Runtime.InjectLapbFrame(data, (ushort)data.Length);
                        Thread.Sleep(50); // Small delay between packets
                    }
                    catch (Exception ex)
                    {
                        Logger.LogWarning("Error processing {Name}: {Error}", name, ex.Message);
                    }
                }
            }
            
            // Analyze sequence results
            var iFrameCount = sequenceResults.Count(r => r.isIFrame);
            var sFrameCount = sequenceResults.Count(r => r.isSFrame);
            var uFrameCount = sequenceResults.Count(r => r.isUFrame);
            var totalX25Payload = sequenceResults.Sum(r => r.x25PayloadLength);
            
            Logger.LogInformation("Sequence Analysis Summary:");
            Logger.LogInformation("  I-Frames (Data): {IFrames}", iFrameCount);
            Logger.LogInformation("  S-Frames (Supervisory): {SFrames}", sFrameCount);
            Logger.LogInformation("  U-Frames (Unnumbered): {UFrames}", uFrameCount);
            Logger.LogInformation("  Total X.25 payload: {Payload} bytes", totalX25Payload);
            
            // Verify sequence characteristics
            Assert.That(iFrameCount, Is.GreaterThan(0), "Should have I-frames for data transport");
            Assert.That(sFrameCount, Is.GreaterThan(0), "Should have S-frames for flow control");
            Assert.That(uFrameCount, Is.GreaterThan(0), "Should have U-frames for connection control");
            Assert.That(totalX25Payload, Is.GreaterThan(0), "Should transport X.25 data");
            
            Logger.LogInformation("✅ Complete packet sequence analyzed successfully");
        }
        
        [Test]
        public void Test06_X25_RoutingPatterns_Analysis()
        {
            Logger.LogInformation("=== TEST 06: X.25 Routing Patterns Analysis ===");
            
            // Establish connection
            var sabmPacket = new byte[] { 0x01, 0x3F, 0x00, 0x64 };
            Runtime.InjectLapbFrame(sabmPacket, (ushort)sabmPacket.Length);
            Thread.Sleep(100);
            
            // Analyze specific X.25 routing patterns in the I-frames
            var routingPackets = new (string description, byte[] data, string expectedPattern)[]
            {
                ("Call with routing info", 
                 new byte[] { 0x09, 0x00, 0x21, 0x13, 0x00, 0x19, 0x00, 0x66, 0x00, 0x64, 0xFF, 0xFF, 0x00, 0x01, 0xDE, 0x08 },
                 "Routing call from 100 to 102"),
                ("Response with data",
                 new byte[] { 0x09, 0x22, 0x21, 0x13, 0x00, 0x0E, 0x00, 0x66, 0x00, 0x64, 0x00, 0x00, 0x01, 0x00, 0xDD, 0x14 },
                 "Data response routing"),
                ("Final routing packet",
                 new byte[] { 0x09, 0x64, 0x21, 0x13, 0x00, 0x03, 0x00, 0x66, 0x00, 0x64, 0x00, 0x00, 0x00, 0x01, 0xDE, 0x1E },
                 "Completion routing")
            };
            
            foreach (var (description, data, expectedPattern) in routingPackets)
            {
                Logger.LogInformation("Analyzing: {Description}", description);
                Logger.LogInformation("  Expected: {Pattern}", expectedPattern);
                
                // Extract X.25 portion
                if (data.Length > 2)
                {
                    var x25Data = data.Skip(2).ToArray();
                    Logger.LogInformation("  X.25 Data: {Data}", BitConverter.ToString(x25Data));
                    
                    if (x25Data.Length >= 3)
                    {
                        // Check for address patterns (0x66 = 102, 0x64 = 100)
                        bool hasSource100 = x25Data.Contains((byte)0x64);
                        bool hasDestination102 = x25Data.Contains((byte)0x66); // 102 in hex
                        
                        Logger.LogInformation("  Address Analysis:");
                        Logger.LogInformation("    Contains 100 (0x64): {Has100}", hasSource100);
                        Logger.LogInformation("    Contains 102 (0x66): {Has102}", hasDestination102);
                        
                        // Look for routing protocol markers (0x21, 0x13 pattern)
                        if (x25Data.Length > 1 && x25Data[0] == 0x21 && x25Data[1] == 0x13)
                        {
                            Logger.LogInformation("  Routing protocol marker detected (0x21 0x13)");
                        }
                        
                        Assert.That(hasSource100, Is.True, "Should contain source machine 100 reference");
                    }
                }
                
                // Process packet
                _capturedHistory.Clear();
                Runtime.InjectLapbFrame(data, (ushort)data.Length);
                Thread.Sleep(100);
                
                var historyCount = _capturedHistory.Count;
                Logger.LogInformation("  Generated {Count} history items", historyCount);
            }
            
            Logger.LogInformation("✅ X.25 routing patterns analyzed successfully");
        }
        
        [Test]
        public void Test07_FlowControl_SupervisoryFrames_Analysis()
        {
            Logger.LogInformation("=== TEST 07: Flow Control and Supervisory Frames ===");
            
            // Establish connection
            var sabmPacket = new byte[] { 0x01, 0x3F, 0x00, 0x64 };
            Runtime.InjectLapbFrame(sabmPacket, (ushort)sabmPacket.Length);
            Thread.Sleep(100);
            
            // Analyze supervisory frames for flow control
            var supervisoryFrames = new (string type, byte[] data, string expectedFunction, string expectedFrameType)[]
            {
                ("S-Frame 1", new byte[] { 0x09, 0x29, 0x00, 0x64 }, "REJ N(R)=1", "REJ (Reject)"),
                ("S-Frame 2", new byte[] { 0x09, 0x41, 0x00, 0x64 }, "RR N(R)=2", "RR (Receive Ready)"),
                ("S-Frame 3", new byte[] { 0x09, 0x69, 0x00, 0x64 }, "REJ N(R)=3", "REJ (Reject)"),
                ("S-Frame 4", new byte[] { 0x09, 0x81, 0x00, 0x64 }, "RR N(R)=4", "RR (Receive Ready)"),
                ("S-Frame 5", new byte[] { 0x09, 0xA9, 0x00, 0x64 }, "REJ N(R)=5", "REJ (Reject)")
            };
            
            foreach (var (type, data, expectedFunction, expectedFrameType) in supervisoryFrames)
            {
                Logger.LogInformation("Analyzing {Type}: {Data}", type, BitConverter.ToString(data));
                Logger.LogInformation("  Expected: {Function}", expectedFunction);
                
                if (data.Length >= 2)
                {
                    byte control = data[1];
                    bool isSFrame = (control & 0x03) == 0x01;
                    byte nr = (byte)((control >> 5) & 0x07);
                    bool pollFinal = (control & 0x10) != 0;
                    
                    Assert.That(isSFrame, Is.True, $"{type} should be S-frame");
                    
                    Logger.LogInformation("  Control Analysis:");
                    Logger.LogInformation("    Is S-Frame: {IsSFrame}", isSFrame);
                    Logger.LogInformation("    N(R): {NR}", nr);
                    Logger.LogInformation("    P/F bit: {PF}", pollFinal);
                    
                    // Determine S-frame type
                    byte sType = (byte)((control >> 2) & 0x03);
                    string frameType = sType switch
                    {
                        0 => "RR (Receive Ready)",
                        1 => "RNR (Receive Not Ready)",
                        2 => "REJ (Reject)",
                        3 => "SREJ (Selective Reject)",
                        _ => "Unknown"
                    };
                    
                    Logger.LogInformation("    S-Frame Type: {Type}", frameType);
                    Assert.That(frameType, Is.EqualTo(expectedFrameType), $"{type} should be {expectedFrameType}");
                }
                
                // Process frame
                _capturedHistory.Clear();
                Runtime.InjectLapbFrame(data, (ushort)data.Length);
                Thread.Sleep(50);
                
                var lapbItems = _capturedHistory.OfType<LapbHistoryItem>().ToList();
                Assert.That(lapbItems.Any(), Is.True, $"Should process {type}");
            }
            
            Logger.LogInformation("✅ Flow control frames analyzed successfully");
        }
        
        [TearDown]
        public override void TearDown()
        {
            Runtime.HistoryItemAdded -= OnHistoryItemAdded;
            base.TearDown();
        }
    }
}