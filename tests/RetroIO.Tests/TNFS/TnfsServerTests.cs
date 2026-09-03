//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen 
// HackerCorp Labs — https://github.com/HackerCorpLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using NUnit.Framework;

namespace RetroIO.Tests.TNFS;

/// <summary>
/// Tests that require an external TNFSD server running on localhost.
/// Must be started manually before running these tests.
/// </summary>
[TestFixture]
[Category("Manual")]
[Category("ExternalTNFSD")]
[Explicit("Requires external TNFSD server running on localhost:16385")]
public class TnfsServerTests
{
    private const int TEST_PORT = 16385; // Use different port for testing
    private const string TEST_ROOT = "/tmp/tnfs-test";

    [SetUp]
    public void Setup()
    {
        // Create test directory structure
        if (Directory.Exists(TEST_ROOT))
            Directory.Delete(TEST_ROOT, true);

        Directory.CreateDirectory(TEST_ROOT);
        File.WriteAllText(Path.Combine(TEST_ROOT, "test.txt"), "Hello TNFS!");
    }

    [TearDown]
    public void Cleanup()
    {
        if (Directory.Exists(TEST_ROOT))
            Directory.Delete(TEST_ROOT, true);
    }

    [Test]
    public async Task TestMountCommand()
    {
        // Build MOUNT packet
        // Header: SessionID(0x0000) + Seq(0x00) + Cmd(0x00)
        // Data: Version(0x0100) + "/" + 0x00
        byte[] mountRequest = new byte[] {
            0x00, 0x00,  // Session ID = 0 (no session yet)
            0x00,        // Sequence = 0
            0x00,        // Command = MOUNT
            0x01, 0x00,  // Protocol version = 0x0001
            0x2F, 0x00   // "/" + null terminator
        };

        using var client = new UdpClient();
        client.Connect(IPAddress.Loopback, TEST_PORT);

        // Send MOUNT request
        await client.SendAsync(mountRequest, mountRequest.Length);

        // Receive response with timeout
        client.Client.ReceiveTimeout = 5000;
        var remoteEP = new IPEndPoint(IPAddress.Any, 0);
        byte[] response = client.Receive(ref remoteEP);

        // Verify response
        Assert.That(response.Length, Is.GreaterThanOrEqualTo(5), "Response too short");

        ushort sessionId = BitConverter.ToUInt16(response, 0);
        byte seq = response[2];
        byte cmd = response[3];
        byte status = response[4];

        Assert.That(sessionId, Is.Not.EqualTo(0), "Session ID should be non-zero");
        Assert.That(seq, Is.EqualTo(0), "Sequence should match request");
        Assert.That(cmd, Is.EqualTo(0x00), "Command should be MOUNT");
        Assert.That(status, Is.EqualTo(0x00), "Status should be SUCCESS");

        if (response.Length >= 9)
        {
            byte verLSB = response[5];
            byte verMSB = response[6];

            Assert.That(verLSB, Is.EqualTo(0x01), "Protocol version LSB should be 1");
            Assert.That(verMSB, Is.EqualTo(0x00), "Protocol version MSB should be 0");
        }

        Console.WriteLine($"✅ MOUNT SUCCESSFUL! Session ID: 0x{sessionId:X4}");
    }

    [Test]
    public async Task TestUnmountCommand()
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

        // Now unmount
        byte[] unmountRequest = new byte[] {
            (byte)(sessionId & 0xFF),
            (byte)((sessionId >> 8) & 0xFF),
            0x01,  // Sequence
            0x01   // Command = UNMOUNT
        };

        await client.SendAsync(unmountRequest, unmountRequest.Length);
        byte[] unmountResponse = client.Receive(ref remoteEP);

        Assert.That(unmountResponse.Length, Is.GreaterThanOrEqualTo(5));
        byte status = unmountResponse[4];
        Assert.That(status, Is.EqualTo(0x00), "UNMOUNT should succeed");

        Console.WriteLine($"✅ UNMOUNT SUCCESSFUL for session 0x{sessionId:X4}");
    }

    [Test]
    public async Task TestInvalidSessionId()
    {
        // Try to unmount with invalid session ID
        byte[] unmountRequest = new byte[] {
            0xFF, 0xFF,  // Invalid session ID
            0x00,        // Sequence
            0x01         // Command = UNMOUNT
        };

        using var client = new UdpClient();
        client.Connect(IPAddress.Loopback, TEST_PORT);

        await client.SendAsync(unmountRequest, unmountRequest.Length);
        client.Client.ReceiveTimeout = 5000;
        var remoteEP = new IPEndPoint(IPAddress.Any, 0);
        byte[] response = client.Receive(ref remoteEP);

        Assert.That(response.Length, Is.GreaterThanOrEqualTo(5));
        byte status = response[4];
        Assert.That(status, Is.EqualTo(0xFF), "Should return EBADSESSION (0xFF)");

        Console.WriteLine("✅ Invalid session correctly rejected");
    }

    [Test]
    public async Task TestMountInvalidDirectory()
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

        await client.SendAsync(mountRequest, mountRequest.Length);
        client.Client.ReceiveTimeout = 5000;
        var remoteEP = new IPEndPoint(IPAddress.Any, 0);
        byte[] response = client.Receive(ref remoteEP);

        Assert.That(response.Length, Is.GreaterThanOrEqualTo(5));
        byte status = response[4];
        Assert.That(status, Is.EqualTo(0x02), "Should return ENOENT (0x02)");

        Console.WriteLine("✅ Invalid directory correctly rejected");
    }
}
