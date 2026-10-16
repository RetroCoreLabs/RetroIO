using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;
using RetroIO.Protocols.X25;
using RetroIO.Protocols.X25.Runtime;
using RetroIO.Link.Models;
using RetroIO.Protocols.X25.Models;
using RetroIO.Protocols.X25.Runtime.Models;
using RetroIO.Protocols.X25.Sintran;
using RetroIO.Protocols.X25.Modules;
using RetroIO.Protocols.X25.Services;
using RetroIO.Protocols.X25.Sintran.Services;

namespace RetroIO.Protocols.X25.Tests
{
    /// <summary>
    /// Tests for PAD to TAD connection scenarios to validate the routing fix
    /// </summary>
    public class PadToTadConnectionTests
    {
        private readonly ILogger _logger;
        private readonly ITestOutputHelper _output;

        public PadToTadConnectionTests(ITestOutputHelper output)
        {
            _output = output;
            var loggerFactory = LoggerFactory.Create(builder => 
                builder.AddProvider(new TestLoggerProvider(output)));
            _logger = loggerFactory.CreateLogger<PadToTadConnectionTests>();
        }

        [Fact]
        public void PAD_to_TAD_Connection_Should_Route_Correctly()
        {
            // Arrange
            var config = RuntimeConfig.CreateDefault();
            config.X25.LocalMachineId = "200";  // Local TAD server ID
            config.Routing.Enabled = true;

            var runtime = new ProtocolRuntime(_logger, config);
            
            // Note: ProtocolRuntime doesn't have Start() method, it starts automatically

            bool callAcceptedReceived = false;
            bool tadResponseReceived = false;

            // Create PAD client
            var padClient = runtime.CreatePadClient("TestPAD");
            
            // Subscribe to connection events
            padClient.ConnectionStateChanged += (sender, args) =>
            {
                _output.WriteLine($"PAD Connection State: {args.State} - {args.Reason}");
                if (args.State == PadConnectionState.Connected)
                {
                    callAcceptedReceived = true;
                }
            };

            padClient.DataReceived += (sender, args) =>
            {
                _output.WriteLine($"PAD Received: {args.Data}");
                if (args.Data.Contains("TAD"))
                {
                    tadResponseReceived = true;
                }
            };

            // Act - Connect PAD to local TAD (ID 200)
            var connected = padClient.Connect("200", "100");

            // Wait for connection establishment and TAD response
            System.Threading.Thread.Sleep(1000);

            // Assert
            Assert.True(connected, "PAD should successfully initiate connection to TAD");
            Assert.True(callAcceptedReceived, "Call Accepted should be received from TAD");
            Assert.True(tadResponseReceived, "Welcome message should be received from TAD server");

            // Verify protocol detection worked correctly
            var protocolDetection = runtime.GetProtocolDetectionService();
            var padCud = new byte[] { 0x01, 0x00, 0x00, 0x00 };
            var protocolInfo = protocolDetection.GetProtocolInfo(padCud, "200");
            
            Assert.Equal(RetroIO.Protocols.X25.Services.X25ProtocolType.PAD, protocolInfo.Protocol);

            // Verify TAD session was created
            var tadProtocol = runtime.GetLocalTadProtocol();
            var activeSessions = tadProtocol.GetActiveSessions();
            
            Assert.True(activeSessions.Length > 0, "TAD should have active sessions");
            Assert.Equal("200", activeSessions[0].CalledAddress);
            Assert.Equal("100", activeSessions[0].CallingAddress);

            // Cleanup
            runtime.Dispose();
        }

        [Fact]
        public void PAD_CUD_Should_Be_Correct_Format()
        {
            // Arrange
            var config = RuntimeConfig.CreateDefault();
            config.X25.LocalMachineId = "200";
            var runtime = new ProtocolRuntime(_logger, config);

            // Act - Create PAD and check CUD format
            var padClient = runtime.CreatePadClient("TestPAD");
            
            // Use reflection to access the private CreateInitialX29Message method
            var method = typeof(PadClient).GetMethod("CreateInitialX29Message", 
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var cud = (byte[])method!.Invoke(padClient, null)!;

            // Assert - CUD should be PAD protocol identifier according to X.25 spec
            Assert.Equal(4, cud.Length);
            Assert.Equal(0x01, cud[0]);
            Assert.Equal(0x00, cud[1]);
            Assert.Equal(0x00, cud[2]);
            Assert.Equal(0x00, cud[3]);

            // Verify protocol detection recognizes it as PAD
            var protocolDetection = runtime.GetProtocolDetectionService();
            var protocolInfo = protocolDetection.GetProtocolInfo(cud, "200");
            
            Assert.Equal(X25ProtocolType.PAD, protocolInfo.Protocol);
        }

        [Fact]
        public void X25_Call_Request_Packet_Structure_Should_Be_Valid()
        {
            // Test the packet structure from the hex dump: 00 01 0B 33 20 00 10 00 00 01 00 00 00
            var packetBytes = new byte[] { 0x00, 0x01, 0x0B, 0x33, 0x20, 0x00, 0x10, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00 };
            
            // Parse the packet
            var packet = new X25Packet(packetBytes);
            
            // Assert packet structure
            Assert.Equal(0x00, packet.GFI);
            Assert.Equal((ushort)1, packet.LCN);  // LCN = 1
            Assert.Equal(0x0B, packet.PTI);  // Call Request
            Assert.Equal(X25PacketType.CallRequest, packet.PacketType);
            
            // Data should be the remaining bytes (Call User Data and addresses)
            Assert.Equal(10, packet.Data.Length);
            Assert.Equal(0x33, packet.Data[0]);  // First byte of call data
            Assert.Equal(0x20, packet.Data[1]);  // This was mentioned as problematic in the original issue
        }

        [Fact]
        public void Protocol_Detection_Should_Handle_Multiple_Protocols()
        {
            // Arrange
            var config = RuntimeConfig.CreateDefault();
            config.X25.LocalMachineId = "200";
            var runtime = new ProtocolRuntime(_logger, config);
            var protocolDetection = runtime.GetProtocolDetectionService();

            // Test PAD protocol
            var padCud = new byte[] { 0x01, 0x00, 0x00, 0x00 };
            var padInfo = protocolDetection.GetProtocolInfo(padCud, "200");
            Assert.Equal(X25ProtocolType.PAD, padInfo.Protocol);

            // Test XMSG protocol (test with version 0x4C03)
            var xmsgCud = new byte[] { 0x4C, 0x03, 0x00, 0x01, 0x10, 0x00, 0x00, 0x08, 0x12, 0x34, 0x56, 0x78 };
            var xmsgInfo = protocolDetection.GetProtocolInfo(xmsgCud, "200");
            Assert.Equal(X25ProtocolType.XMSG, xmsgInfo.Protocol);

            // Test unknown protocol - should default to TAD
            var unknownCud = new byte[] { 0xFF, 0xFF, 0xFF, 0xFF };
            var unknownInfo = protocolDetection.GetProtocolInfo(unknownCud, "200");
            Assert.Equal(X25ProtocolType.Unknown, unknownInfo.Protocol);
        }
    }

    /// <summary>
    /// Test logger provider for capturing test output
    /// </summary>
    public class TestLoggerProvider : ILoggerProvider
    {
        private readonly ITestOutputHelper _output;

        public TestLoggerProvider(ITestOutputHelper output)
        {
            _output = output;
        }

        public ILogger CreateLogger(string categoryName)
        {
            return new TestLogger(_output, categoryName);
        }

        public void Dispose() { }
    }

    /// <summary>
    /// Test logger that outputs to xUnit test output
    /// </summary>
    public class TestLogger : ILogger
    {
        private readonly ITestOutputHelper _output;
        private readonly string _categoryName;

        public TestLogger(ITestOutputHelper output, string categoryName)
        {
            _output = output;
            _categoryName = categoryName;
        }

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => null!;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            try
            {
                var message = formatter(state, exception);
                _output.WriteLine($"[{logLevel}] {_categoryName}: {message}");
                if (exception != null)
                {
                    _output.WriteLine($"Exception: {exception}");
                }
            }
            catch
            {
                // Ignore test output errors
            }
        }
    }
}