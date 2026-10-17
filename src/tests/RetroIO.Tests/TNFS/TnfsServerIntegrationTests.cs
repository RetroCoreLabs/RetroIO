//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using RetroIO.Storage.Utilities;

namespace RetroIO.Tests.TNFS;

/// <summary>
/// Integration tests that start a real TNFS server process and test client connections.
/// These tests spawn our RetroIO.Server in a separate process.
/// </summary>
[TestFixture]
[Category("Integration")]
[Category("ProcessSpawn")]
[Explicit("Spawns RetroServer.Service, which lives in the RetroServer repo since 2026-09; run by hand with the server built")]
public class TnfsServerIntegrationTests
{
    private const int TEST_PORT = 16385;
    private static readonly string TEST_ROOT = Path.Combine(Path.GetTempPath(), "tnfs-test");

    /// <summary>
    /// Throwaway RetroFS user data directory for the spawned server, handed to it through
    /// <see cref="PathHelper.DataPathEnvironmentVariable"/>.
    /// </summary>
    /// <remarks>
    /// The server writes its configuration back out on every start (ProtocolConfigurationLoader
    /// calls SaveConfiguration), so without this it overwrites the developer's real
    /// Documents\RetroFS	nfs-config.json with the test port and the test root every time the
    /// suite runs. The variable is set on the CHILD process only -- the test host's own
    /// environment is left alone, so parallel fixtures are unaffected.
    /// </remarks>
    private static readonly string TEST_DATA_PATH = Path.Combine(Path.GetTempPath(), "tnfs-test-data");

    private Process? _serverProcess;

    [OneTimeSetUp]
    public async Task StartServer()
    {
        // Create test directory
        if (Directory.Exists(TEST_ROOT))
            Directory.Delete(TEST_ROOT, true);

        Directory.CreateDirectory(TEST_ROOT);
        File.WriteAllText(Path.Combine(TEST_ROOT, "test.txt"), "Hello TNFS!");

        if (Directory.Exists(TEST_DATA_PATH))
            Directory.Delete(TEST_DATA_PATH, true);

        Directory.CreateDirectory(TEST_DATA_PATH);

        // Find project path by walking up from test directory
        var testDir = TestContext.CurrentContext.TestDirectory;
        var solutionDir = testDir;

        // Walk up to find the solution root (contains src/ directory)
        while (!Directory.Exists(Path.Combine(solutionDir, "src")) && Directory.GetParent(solutionDir) != null)
        {
            solutionDir = Directory.GetParent(solutionDir)!.FullName;
        }

        var projectPath = Path.Combine(solutionDir, "src", "RetroServer", "RetroServer.csproj");

        if (!File.Exists(projectPath))
        {
            throw new FileNotFoundException($"RetroServer.Service project not found at: {projectPath}");
        }

        TestContext.Out.WriteLine($"Using server project: {projectPath}");

        // Build the server first to avoid startup delays
        var buildProcess = Process.Start(new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"build {projectPath} --configuration Debug --no-incremental",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        });

        buildProcess?.WaitForExit();
        if (buildProcess?.ExitCode != 0)
        {
            throw new Exception($"Failed to build server project. Exit code: {buildProcess?.ExitCode}");
        }

        TestContext.Out.WriteLine("Server built successfully");

        _serverProcess = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = $"run --project {projectPath} --no-build -- --Tnfs:RootDirectory={TEST_ROOT} --Tnfs:UdpPort={TEST_PORT} --Tnfs:TcpPort={TEST_PORT}",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };

        // Redirect the server's user data directory -- see TEST_DATA_PATH for why.
        _serverProcess.StartInfo.Environment[PathHelper.DataPathEnvironmentVariable] = TEST_DATA_PATH;

        // Completed the moment the server's own log says the UDP socket is bound. Waiting on the
        // server's output is exact: it is the server telling us it is listening, not us guessing.
        //
        // What used to be here was `new UdpClient().Connect(loopback, port)` in a retry loop. UDP
        // is connectionless, so Connect only records the peer address -- it touches no network and
        // can never throw. The loop therefore broke on its first pass and printed "Server is
        // ready!" whether or not the server had started, and the only real wait left was the
        // Task.Delay(1000) below it. The server takes about 2.6 s to bind, so every test fired at
        // ~1 s into an unbound port, Windows replied with ICMP port-unreachable, and all five
        // tests died on "An existing connection was forcibly closed by the remote host".
        string readyMarker = $"UDP transport listening on port {TEST_PORT}";
        TaskCompletionSource serverReady =
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        _serverProcess.OutputDataReceived += (sender, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
            {
                TestContext.Out.WriteLine($"[SERVER] {e.Data}");
                if (e.Data.Contains(readyMarker, StringComparison.Ordinal))
                    serverReady.TrySetResult();
            }
        };

        _serverProcess.ErrorDataReceived += (sender, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
            {
                TestContext.Error.WriteLine($"[SERVER ERROR] {e.Data}");
                // The console logger can be pointed at stderr, so watch both streams for the marker.
                if (e.Data.Contains(readyMarker, StringComparison.Ordinal))
                    serverReady.TrySetResult();
            }
        };

        _serverProcess.Start();
        _serverProcess.BeginOutputReadLine();
        _serverProcess.BeginErrorReadLine();

        // 30 s, not 10: the marker is only reached after the host has built the identity provider
        // (Argon2id) and both protocol stacks, which is slow on a cold run.
        Task finished = await Task.WhenAny(serverReady.Task, Task.Delay(TimeSpan.FromSeconds(30)));

        if (finished != serverReady.Task)
        {
            throw new TimeoutException(
                $"TNFS server did not report \"{readyMarker}\" within 30 seconds. " +
                "See the [SERVER] / [SERVER ERROR] lines above for why it did not start.");
        }

        TestContext.Out.WriteLine("Server is listening.");
    }

    [OneTimeTearDown]
    public void StopServer()
    {
        if (_serverProcess != null && !_serverProcess.HasExited)
        {
            _serverProcess.Kill(true);
            _serverProcess.WaitForExit(5000);
            _serverProcess.Dispose();
        }

        if (Directory.Exists(TEST_ROOT))
            Directory.Delete(TEST_ROOT, true);

        // Best effort: the server keeps a JSON log file open in here, and on Windows the handle
        // can outlive Kill() by a moment. A temp directory left behind is harmless -- SetUp
        // deletes it on the next run -- but a throwing teardown would mask the real test results.
        try
        {
            if (Directory.Exists(TEST_DATA_PATH))
                Directory.Delete(TEST_DATA_PATH, true);
        }
        catch (IOException ex)
        {
            TestContext.Out.WriteLine($"Could not remove {TEST_DATA_PATH}: {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            TestContext.Out.WriteLine($"Could not remove {TEST_DATA_PATH}: {ex.Message}");
        }
    }

    [Test, Order(1)]
    public async Task Test_01_MountCommand_Success()
    {
        // Build MOUNT packet
        byte[] mountRequest = new byte[] {
            0x00, 0x00,  // Session ID = 0 (no session yet)
            0x00,        // Sequence = 0
            0x00,        // Command = MOUNT
            0x01, 0x00,  // Protocol version = 0x0001
            0x2F, 0x00   // "/" + null terminator
        };

        using var client = new UdpClient();
        client.Connect(IPAddress.Loopback, TEST_PORT);

        TestContext.Out.WriteLine($"Sending MOUNT request to {IPAddress.Loopback}:{TEST_PORT}");
        TestContext.Out.WriteLine($"Request: {BitConverter.ToString(mountRequest)}");

        await client.SendAsync(mountRequest, mountRequest.Length);

        client.Client.ReceiveTimeout = 5000;
        var remoteEP = new IPEndPoint(IPAddress.Any, 0);
        byte[] response = client.Receive(ref remoteEP);

        TestContext.Out.WriteLine($"Response: {BitConverter.ToString(response)}");

        Assert.That(response.Length, Is.GreaterThanOrEqualTo(5), "Response too short");

        ushort sessionId = BitConverter.ToUInt16(response, 0);
        byte seq = response[2];
        byte cmd = response[3];
        byte status = response[4];

        TestContext.Out.WriteLine($"Session ID: 0x{sessionId:X4}");
        TestContext.Out.WriteLine($"Sequence: {seq}");
        TestContext.Out.WriteLine($"Command: 0x{cmd:X2}");
        TestContext.Out.WriteLine($"Status: 0x{status:X2}");

        Assert.That(sessionId, Is.Not.EqualTo(0), "Session ID should be non-zero");
        Assert.That(seq, Is.EqualTo(0), "Sequence should match request");
        Assert.That(cmd, Is.EqualTo(0x00), "Command should be MOUNT");
        Assert.That(status, Is.EqualTo(0x00), "Status should be SUCCESS");

        if (response.Length >= 9)
        {
            byte verLSB = response[5];
            byte verMSB = response[6];
            byte timeoutLSB = response[7];
            byte timeoutMSB = response[8];

            TestContext.Out.WriteLine($"Protocol Version: {verMSB}.{verLSB}");
            TestContext.Out.WriteLine($"Timeout: {timeoutMSB}{timeoutLSB} seconds");

            Assert.That(verLSB, Is.EqualTo(0x02), "Protocol version LSB should be 2");
            Assert.That(verMSB, Is.EqualTo(0x01), "Protocol version MSB should be 1");
        }

        TestContext.Out.WriteLine($"✅ MOUNT SUCCESSFUL! Session ID: 0x{sessionId:X4}");
    }

    [Test, Order(2)]
    public async Task Test_02_MountAndUnmount()
    {
        // First mount
        byte[] mountRequest = new byte[] {
            0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x2F, 0x00
        };

        using var client = new UdpClient();
        client.Connect(IPAddress.Loopback, TEST_PORT);

        await client.SendAsync(mountRequest, mountRequest.Length);
        client.Client.ReceiveTimeout = 5000;
        var remoteEP = new IPEndPoint(IPAddress.Any, 0);
        byte[] mountResponse = client.Receive(ref remoteEP);

        ushort sessionId = BitConverter.ToUInt16(mountResponse, 0);
        Assert.That(sessionId, Is.Not.EqualTo(0));
        TestContext.Out.WriteLine($"Created session: 0x{sessionId:X4}");

        // Now unmount
        byte[] unmountRequest = new byte[] {
            (byte)(sessionId & 0xFF),
            (byte)((sessionId >> 8) & 0xFF),
            0x01,  // Sequence
            0x01   // Command = UNMOUNT
        };

        TestContext.Out.WriteLine($"Sending UNMOUNT for session 0x{sessionId:X4}");
        await client.SendAsync(unmountRequest, unmountRequest.Length);
        byte[] unmountResponse = client.Receive(ref remoteEP);

        Assert.That(unmountResponse.Length, Is.GreaterThanOrEqualTo(5));
        byte status = unmountResponse[4];
        TestContext.Out.WriteLine($"UNMOUNT status: 0x{status:X2}");
        Assert.That(status, Is.EqualTo(0x00), "UNMOUNT should succeed");

        TestContext.Out.WriteLine($"✅ UNMOUNT SUCCESSFUL for session 0x{sessionId:X4}");
    }

    [Test, Order(3)]
    public async Task Test_03_InvalidSessionId()
    {
        byte[] unmountRequest = new byte[] {
            0xFF, 0xFF,  // Invalid session ID
            0x00,        // Sequence
            0x01         // Command = UNMOUNT
        };

        using var client = new UdpClient();
        client.Connect(IPAddress.Loopback, TEST_PORT);

        TestContext.Out.WriteLine("Sending UNMOUNT with invalid session ID 0xFFFF");
        await client.SendAsync(unmountRequest, unmountRequest.Length);
        client.Client.ReceiveTimeout = 5000;
        var remoteEP = new IPEndPoint(IPAddress.Any, 0);
        byte[] response = client.Receive(ref remoteEP);

        Assert.That(response.Length, Is.GreaterThanOrEqualTo(5));
        byte status = response[4];
        TestContext.Out.WriteLine($"Response status: 0x{status:X2}");
        Assert.That(status, Is.EqualTo(0xFF), "Should return EBADSESSION (0xFF)");

        TestContext.Out.WriteLine("✅ Invalid session correctly rejected");
    }

    [Test, Order(4)]
    public async Task Test_04_MountInvalidDirectory()
    {
        // Try to mount non-existent directory
        byte[] mountRequest = new byte[] {
            0x00, 0x00,  // Session ID = 0
            0x00,        // Sequence = 0
            0x00,        // Command = MOUNT
            0x01, 0x00,  // Protocol version
            // Mount point: "/nonexistent" + null
            0x2F, 0x6E, 0x6F, 0x6E, 0x65, 0x78, 0x69, 0x73, 0x74, 0x65, 0x6E, 0x74, 0x00
        };

        using var client = new UdpClient();
        client.Connect(IPAddress.Loopback, TEST_PORT);

        TestContext.Out.WriteLine("Attempting to mount non-existent directory");
        await client.SendAsync(mountRequest, mountRequest.Length);
        client.Client.ReceiveTimeout = 5000;
        var remoteEP = new IPEndPoint(IPAddress.Any, 0);
        byte[] response = client.Receive(ref remoteEP);

        Assert.That(response.Length, Is.GreaterThanOrEqualTo(5));
        byte status = response[4];
        TestContext.Out.WriteLine($"Response status: 0x{status:X2}");
        Assert.That(status, Is.EqualTo(0x02), "Should return ENOENT (0x02)");

        TestContext.Out.WriteLine("✅ Invalid directory correctly rejected");
    }

    [Test, Order(5)]
    public async Task Test_05_MultipleClients()
    {
        // Test that multiple clients can mount simultaneously
        using var client1 = new UdpClient();
        using var client2 = new UdpClient();

        client1.Connect(IPAddress.Loopback, TEST_PORT);
        client2.Connect(IPAddress.Loopback, TEST_PORT);

        byte[] mountRequest = new byte[] {
            0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x2F, 0x00
        };

        // Mount with client 1
        await client1.SendAsync(mountRequest, mountRequest.Length);
        client1.Client.ReceiveTimeout = 5000;
        var ep1 = new IPEndPoint(IPAddress.Any, 0);
        byte[] response1 = client1.Receive(ref ep1);
        ushort sessionId1 = BitConverter.ToUInt16(response1, 0);

        // Mount with client 2
        await client2.SendAsync(mountRequest, mountRequest.Length);
        client2.Client.ReceiveTimeout = 5000;
        var ep2 = new IPEndPoint(IPAddress.Any, 0);
        byte[] response2 = client2.Receive(ref ep2);
        ushort sessionId2 = BitConverter.ToUInt16(response2, 0);

        TestContext.Out.WriteLine($"Client 1 session: 0x{sessionId1:X4}");
        TestContext.Out.WriteLine($"Client 2 session: 0x{sessionId2:X4}");

        Assert.That(sessionId1, Is.Not.EqualTo(0), "Client 1 should get session ID");
        Assert.That(sessionId2, Is.Not.EqualTo(0), "Client 2 should get session ID");
        Assert.That(sessionId1, Is.Not.EqualTo(sessionId2), "Session IDs should be different");

        TestContext.Out.WriteLine("✅ Multiple clients can mount simultaneously");
    }
}
