//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using NUnit.Framework;

namespace RetroIO.Tests.TNFS;

/// <summary>
/// Tests for TNFS directory operations (OPENDIR, READDIR, CLOSEDIR).
/// IMPORTANT: These tests require a manually started remote TNFS server.
/// See TnfsTestConfig for server address configuration.
/// </summary>
[TestFixture]
[Category("Manual")]
[Category("ExternalTNFSD")]
[Explicit("Requires manually started TNFS server (see TnfsTestConfig) with /test directory")]
public class TnfsDirectoryTests
{
    private const int SERVER_PORT = TnfsTestConfig.REMOTE_SERVER_PORT;
    private static readonly string SERVER_HOST = TnfsTestConfig.REMOTE_SERVER_ADDRESS;
    private ushort _sessionId;
    private UdpClient? _client;

    [OneTimeSetUp]
    public async Task SetupSession()
    {
        // Mount and get session ID
        _client = new UdpClient();
        _client.Connect(IPAddress.Parse(SERVER_HOST), SERVER_PORT);

        byte[] mountRequest = new byte[] {
            0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x2F, 0x00
        };

        await _client.SendAsync(mountRequest, mountRequest.Length);
        _client.Client.ReceiveTimeout = 5000;
        var remoteEP = new IPEndPoint(IPAddress.Any, 0);
        byte[] response = _client.Receive(ref remoteEP);

        _sessionId = BitConverter.ToUInt16(response, 0);
        TestContext.Out.WriteLine($"Created session: 0x{_sessionId:X4}");
    }

    [OneTimeTearDown]
    public void TearDown()
    {
        _client?.Dispose();
    }

    [Test, Order(1)]
    public async Task Test_01_OpenDir_Success()
    {
        TestContext.Out.WriteLine("Testing OPENDIR command...");

        // Build OPENDIR packet
        // Header(4) + path + 0x00
        List<byte> packet = new List<byte>();
        packet.AddRange(BitConverter.GetBytes(_sessionId));  // Session ID (little-endian)
        packet.Add(0x00);  // Sequence
        packet.Add(0x10);  // Command: OPENDIR
        packet.AddRange(Encoding.ASCII.GetBytes("/test"));  // Path
        packet.Add(0x00);  // Null terminator

        await _client!.SendAsync(packet.ToArray(), packet.Count);

        var remoteEP = new IPEndPoint(IPAddress.Any, 0);
        byte[] response = _client.Receive(ref remoteEP);

        TestContext.Out.WriteLine($"Response: {BitConverter.ToString(response)}");

        Assert.That(response.Length, Is.GreaterThanOrEqualTo(5));
        byte status = response[4];
        Assert.That(status, Is.EqualTo(0x00), "OPENDIR should succeed");

        if (response.Length >= 6)
        {
            byte handle = response[5];
            TestContext.Out.WriteLine($"Directory handle: {handle}");
            Assert.That(handle, Is.GreaterThanOrEqualTo(0).And.LessThan(16), "Handle should be valid (0-15)");
        }

        TestContext.Out.WriteLine("✅ OPENDIR successful");
    }

    [Test, Order(2)]
    public async Task Test_02_OpenDir_ReadDir_CloseDir_Cycle()
    {
        TestContext.Out.WriteLine("Testing full directory cycle...");

        // 1. OPENDIR
        List<byte> openPacket = new List<byte>();
        openPacket.AddRange(BitConverter.GetBytes(_sessionId));
        openPacket.Add(0x01);  // Sequence
        openPacket.Add(0x10);  // OPENDIR
        openPacket.AddRange(Encoding.ASCII.GetBytes("/test"));
        openPacket.Add(0x00);

        await _client!.SendAsync(openPacket.ToArray(), openPacket.Count);
        var remoteEP = new IPEndPoint(IPAddress.Any, 0);
        byte[] openResponse = _client.Receive(ref remoteEP);

        Assert.That(openResponse[4], Is.EqualTo(0x00), "OPENDIR should succeed");
        byte handle = openResponse[5];
        TestContext.Out.WriteLine($"Opened directory with handle: {handle}");

        // 2. READDIR loop
        List<string> entries = new List<string>();
        byte sequence = 0x02;

        while (true)
        {
            List<byte> readPacket = new List<byte>();
            readPacket.AddRange(BitConverter.GetBytes(_sessionId));
            readPacket.Add(sequence++);
            readPacket.Add(0x11);  // READDIR
            readPacket.Add(handle);  // Directory handle

            await _client.SendAsync(readPacket.ToArray(), readPacket.Count);
            byte[] readResponse = _client.Receive(ref remoteEP);

            byte status = readResponse[4];

            if (status == 0x21)  // EOF
            {
                TestContext.Out.WriteLine("Reached end of directory");
                break;
            }

            Assert.That(status, Is.EqualTo(0x00), "READDIR should succeed");

            // Parse entry: name + 0x00 (no flags byte per TNFS 1.2 spec)
            StringBuilder name = new StringBuilder();
            for (int i = 5; i < readResponse.Length && readResponse[i] != 0; i++)
            {
                name.Append((char)readResponse[i]);
            }

            string entry = name.ToString();
            entries.Add(entry);

            TestContext.Out.WriteLine($"  {entry}");
        }

        TestContext.Out.WriteLine($"Total entries read: {entries.Count}");
        Assert.That(entries.Count, Is.GreaterThan(0), "Should have read some entries");

        // Verify we got expected files
        Assert.That(entries, Does.Contain("file1.txt"), "Should find file1.txt");
        Assert.That(entries, Does.Contain("file2.txt"), "Should find file2.txt");
        Assert.That(entries, Does.Contain("subdir"), "Should find subdir");

        // 3. CLOSEDIR
        List<byte> closePacket = new List<byte>();
        closePacket.AddRange(BitConverter.GetBytes(_sessionId));
        closePacket.Add(sequence);
        closePacket.Add(0x12);  // CLOSEDIR
        closePacket.Add(handle);

        await _client.SendAsync(closePacket.ToArray(), closePacket.Count);
        byte[] closeResponse = _client.Receive(ref remoteEP);

        Assert.That(closeResponse[4], Is.EqualTo(0x00), "CLOSEDIR should succeed");
        TestContext.Out.WriteLine("✅ Full directory cycle successful");
    }

    [Test, Order(3)]
    public async Task Test_03_OpenDir_InvalidPath()
    {
        TestContext.Out.WriteLine("Testing OPENDIR with invalid path...");

        List<byte> packet = new List<byte>();
        packet.AddRange(BitConverter.GetBytes(_sessionId));
        packet.Add(0x10);  // Sequence
        packet.Add(0x10);  // OPENDIR
        packet.AddRange(Encoding.ASCII.GetBytes("/nonexistent"));
        packet.Add(0x00);

        await _client!.SendAsync(packet.ToArray(), packet.Count);
        var remoteEP = new IPEndPoint(IPAddress.Any, 0);
        byte[] response = _client.Receive(ref remoteEP);

        byte status = response[4];
        TestContext.Out.WriteLine($"Response status: 0x{status:X2}");
        Assert.That(status, Is.EqualTo(0x02), "Should return ENOENT (0x02)");

        TestContext.Out.WriteLine("✅ Invalid path correctly rejected");
    }

    [Test, Order(4)]
    public async Task Test_04_ReadDir_InvalidHandle()
    {
        TestContext.Out.WriteLine("Testing READDIR with invalid handle...");

        List<byte> packet = new List<byte>();
        packet.AddRange(BitConverter.GetBytes(_sessionId));
        packet.Add(0x11);  // Sequence
        packet.Add(0x11);  // READDIR
        packet.Add(0xFF);  // Invalid handle

        await _client!.SendAsync(packet.ToArray(), packet.Count);
        var remoteEP = new IPEndPoint(IPAddress.Any, 0);
        byte[] response = _client.Receive(ref remoteEP);

        byte status = response[4];
        TestContext.Out.WriteLine($"Response status: 0x{status:X2}");
        Assert.That(status, Is.EqualTo(0x09), "Should return EBADF (0x09)");

        TestContext.Out.WriteLine("✅ Invalid handle correctly rejected");
    }

    [Test, Order(5)]
    public async Task Test_05_OpenDir_RootDirectory()
    {
        TestContext.Out.WriteLine("Testing OPENDIR on root directory...");

        List<byte> packet = new List<byte>();
        packet.AddRange(BitConverter.GetBytes(_sessionId));
        packet.Add(0x12);  // Sequence
        packet.Add(0x10);  // OPENDIR
        packet.AddRange(Encoding.ASCII.GetBytes("/"));
        packet.Add(0x00);

        await _client!.SendAsync(packet.ToArray(), packet.Count);
        var remoteEP = new IPEndPoint(IPAddress.Any, 0);
        byte[] response = _client.Receive(ref remoteEP);

        byte status = response[4];
        TestContext.Out.WriteLine($"Response status: 0x{status:X2}");
        Assert.That(status, Is.EqualTo(0x00), "Should succeed opening root");

        if (response.Length >= 6)
        {
            byte handle = response[5];
            TestContext.Out.WriteLine($"Root directory handle: {handle}");

            // Read one entry to verify
            List<byte> readPacket = new List<byte>();
            readPacket.AddRange(BitConverter.GetBytes(_sessionId));
            readPacket.Add(0x13);  // Sequence
            readPacket.Add(0x11);  // READDIR
            readPacket.Add(handle);

            await _client.SendAsync(readPacket.ToArray(), readPacket.Count);
            byte[] readResponse = _client.Receive(ref remoteEP);

            byte readStatus = readResponse[4];
            if (readStatus == 0x00)
            {
                StringBuilder name = new StringBuilder();
                for (int i = 6; i < readResponse.Length && readResponse[i] != 0; i++)
                {
                    name.Append((char)readResponse[i]);
                }
                TestContext.Out.WriteLine($"First entry in root: {name}");
            }
        }

        TestContext.Out.WriteLine("✅ Root directory opened successfully");
    }
}
