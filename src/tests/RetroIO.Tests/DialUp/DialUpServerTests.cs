//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using NUnit.Framework;
using RetroIO.Identity;
using RetroIO.Server.DialUp;
using RetroIO.Server.Hosting.Configuration;

namespace RetroIO.Tests.DialUp;

/// <summary>
/// RetroServer's dial-up ISP over real TCP connections: the login against RetroServer's users with
/// the dial-up right, the fixed address, and the call limit.
/// </summary>
[TestFixture]
public sealed class DialUpServerTests
{
    private const string Password = "secret123";
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private string _folder = string.Empty;
    private JsonRetroIDP _idp = null!;

    [SetUp]
    public void SetUp()
    {
        _folder = Path.Combine(Path.GetTempPath(), "dialup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_folder);
        _idp = new JsonRetroIDP(Path.Combine(_folder, "users.json"), Path.Combine(_folder, "groups.json"));
        AddUser("caller", allowDialUp: true, address: null);
        AddUser("fixed", allowDialUp: true, address: "10.42.0.20");
        AddUser("nodial", allowDialUp: false, address: null);
    }

    [TearDown]
    public void TearDown()
    {
        _idp.Dispose();
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, true);
        }
    }

    [Test]
    public void TheServiceIsOffByDefault()
    {
        DialUpServerConfiguration config = DialUpServerConfiguration.GetDefault();
        Assert.That(config.Enabled, Is.False);
        Assert.That(config.BindAddress, Is.EqualTo("127.0.0.1"));
        Assert.That(config.Network, Is.EqualTo("10.42.0.0"));
        Assert.That(config.PrefixLength, Is.EqualTo(24));
    }

    [Test]
    public void AUserWithTheRight_LogsIn_AndGetsAnAddressFromThePool()
    {
        using DialUpServer server = StartServer(maxCalls: 4);
        using TcpClient client = Dial(server);

        string text = LogIn(client, "caller", Password);

        Assert.That(text, Does.Contain("SLIP mode. Your address is 10.42.0.15"), Lines(text));
        Assert.That(server.CallsAnswered, Is.EqualTo(1));
        Assert.That(server.LoginsRefused, Is.EqualTo(0));
    }

    [Test]
    public void AUserWithAFixedAddress_GetsIt()
    {
        using DialUpServer server = StartServer(maxCalls: 4);
        using TcpClient client = Dial(server);

        string text = LogIn(client, "fixed", Password);

        Assert.That(text, Does.Contain("Your address is 10.42.0.20"), Lines(text));
    }

    [Test]
    public void AFixedAddressAlreadyInUse_EndsTheSecondCall()
    {
        using DialUpServer server = StartServer(maxCalls: 4);
        using TcpClient first = Dial(server);
        Assert.That(LogIn(first, "fixed", Password), Does.Contain("Your address is 10.42.0.20"));

        using TcpClient second = Dial(server);
        string text = LogIn(second, "fixed", Password);

        Assert.That(text, Does.Contain("No line free"), Lines(text));
        Assert.That(ReadsEndOfLine(second), Is.True, "the second call was not hung up");
    }

    [Test]
    public void AUserWithoutTheRight_IsRefused_AndThreeTriesHangUp()
    {
        using DialUpServer server = StartServer(maxCalls: 4);
        using TcpClient client = Dial(server);

        string text = LogIn(client, "nodial", Password);
        Assert.That(text, Does.Contain("Login incorrect"), Lines(text));
        Assert.That(text, Does.Not.Contain("SLIP mode"));

        Send(client, "nodial\r");
        ReadUntil(client, "Password:");
        Send(client, Password + "\r");
        ReadUntil(client, "login:");
        Send(client, "nodial\r");
        ReadUntil(client, "Password:");
        Send(client, Password + "\r");

        Assert.That(ReadsEndOfLine(client), Is.True, "three refused logins did not hang up");
        Assert.That(server.LoginsRefused, Is.EqualTo(3));
    }

    [Test]
    public void AWrongPassword_IsRefused()
    {
        using DialUpServer server = StartServer(maxCalls: 4);
        using TcpClient client = Dial(server);

        string text = LogIn(client, "caller", "wrong-password");

        Assert.That(text, Does.Contain("Login incorrect"), Lines(text));
        Assert.That(server.LoginsRefused, Is.EqualTo(1));
    }

    [Test]
    public void ACallOverTheLimit_GetsNoLineFree_AndIsHungUp()
    {
        using DialUpServer server = StartServer(maxCalls: 1);
        using TcpClient first = Dial(server);
        ReadUntil(first, "login:");

        using TcpClient second = Dial(server);
        string text = ReadToEnd(second);

        Assert.That(text, Does.Contain("No line free"), Lines(text));
        Assert.That(server.CallsRefusedFull, Is.EqualTo(1));
    }

    [Test]
    public void ACallerHangingUp_FreesItsLine()
    {
        using DialUpServer server = StartServer(maxCalls: 1);
        using (TcpClient first = Dial(server))
        {
            ReadUntil(first, "login:");
        }

        DateTime deadline = DateTime.UtcNow + Wait;
        while (server.CallsActive != 0 && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(10);
        }

        Assert.That(server.CallsActive, Is.EqualTo(0));
        using TcpClient second = Dial(server);
        Assert.That(ReadUntil(second, "login:"), Does.Contain("login:"));
    }

    [Test]
    public void TheManagementApi_ShowsTheMetricsAndTheLog_NextToEachOther()
    {
        using DialUpServer server = StartServer(maxCalls: 4);
        using TcpClient client = Dial(server);
        LogIn(client, "caller", "wrong-password");

        RetroIO.Management.Api.Controllers.TelemetryController api =
            new RetroIO.Management.Api.Controllers.TelemetryController(new[] { (RetroIO.Telemetry.ITelemetrySource)server.Telemetry });

        Microsoft.AspNetCore.Mvc.OkObjectResult okMetrics = (Microsoft.AspNetCore.Mvc.OkObjectResult)api.GetMetrics("RetroIO.DialUp").Result!;
        List<RetroIO.Management.Contracts.MetricDto> metrics =
            ((RetroIO.Management.Contracts.ApiResponse<RetroIO.Management.Contracts.MetricsResponse>)okMetrics.Value!).Data!.Metrics;
        Assert.That(ValueOf(metrics, "retroio.dialup.calls.answered", null), Is.EqualTo(1));
        Assert.That(ValueOf(metrics, "retroio.dialup.calls.active", null), Is.EqualTo(1));
        Assert.That(ValueOf(metrics, "retroio.dialup.logins.refused", null), Is.EqualTo(1));
        Assert.That(ValueOf(metrics, "retroio.dialup.calls.limit", null), Is.EqualTo(4));
        Assert.That(ValueOf(metrics, "retroio.dialup.io", "in"), Is.GreaterThan(0));
        Assert.That(ValueOf(metrics, "retroio.dialup.io", "out"), Is.GreaterThan(0));

        Microsoft.AspNetCore.Mvc.OkObjectResult okLog = (Microsoft.AspNetCore.Mvc.OkObjectResult)api.GetLog("RetroIO.DialUp").Result!;
        List<RetroIO.Management.Contracts.TelemetryLogRecordDto> log =
            ((RetroIO.Management.Contracts.ApiResponse<RetroIO.Management.Contracts.TelemetryLogResponse>)okLog.Value!).Data!.Records;
        bool refusedLogged = false;
        for (int i = 0; i < log.Count; i++)
        {
            if (log[i].Level == "Warning" && log[i].Message.Contains("refused", StringComparison.Ordinal))
            {
                refusedLogged = true;
            }
        }

        Assert.That(log[0].Message, Does.StartWith("listening on"));
        Assert.That(refusedLogged, Is.True, "the refused login is a warning in the log");
    }

    private static long ValueOf(List<RetroIO.Management.Contracts.MetricDto> metrics, string name, string? direction)
    {
        for (int i = 0; i < metrics.Count; i++)
        {
            if (metrics[i].Name == name
                && (direction == null || (metrics[i].Tags.TryGetValue("direction", out string? d) && d == direction)))
            {
                return metrics[i].Value;
            }
        }

        throw new AssertionException("no metric " + name + " " + direction);
    }

    private void AddUser(string name, bool allowDialUp, string? address)
    {
        RetroUser user = new RetroUser
        {
            UserId = Guid.NewGuid(),
            Username = name,
            AllowDialUp = allowDialUp,
            DialUpAddress = address,
        };
        _idp.CreateUserAsync(user).AsTask().GetAwaiter().GetResult();
        _idp.SetPasswordAsync(user.UserId, Password).AsTask().GetAwaiter().GetResult();
    }

    private DialUpServer StartServer(int maxCalls)
    {
        DialUpServerConfiguration config = new DialUpServerConfiguration
        {
            Enabled = true,
            Port = 0,
            MaxCalls = maxCalls,
        };
        DialUpServer server = new DialUpServer(config, _idp);
        server.Start();
        return server;
    }

    /// <summary>
    /// Load (plan Phase 14): 16 callers dial and log in at the same time, ten times over. In each
    /// wave every caller gets an address of its own; after each wave every line is free again and
    /// the addresses are given out again; at the end no call is left and nothing was refused.
    /// </summary>
    [Test]
    [Category("Load")]
    [Explicit("Load test; about 15 s, most of it the password check on every login")]
    public void Load_SixteenCallersAtOnce_TenWaves_EachGetsItsOwnAddress_AndNoCallIsLeft()
    {
        const int Callers = 16;
        const int Waves = 10;
        using DialUpServer server = StartServer(maxCalls: Callers);
        System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();

        for (int wave = 0; wave < Waves; wave++)
        {
            TcpClient[] lines = new TcpClient[Callers];
            string[] addresses = new string[Callers];
            System.Threading.Tasks.Parallel.For(0, Callers, i =>
            {
                lines[i] = Dial(server);
                string text = LogIn(lines[i], "caller", Password);
                int at = text.IndexOf("Your address is ", StringComparison.Ordinal);
                addresses[i] = at < 0 ? "none: " + text : text.Substring(at + 16).Split('\r', '\n', ' ')[0];
            });

            HashSet<string> distinct = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < Callers; i++)
            {
                Assert.That(addresses[i], Does.StartWith("10.42.0."), "wave " + wave + ", caller " + i);
                Assert.That(distinct.Add(addresses[i]), Is.True, "wave " + wave + ": " + addresses[i] + " given to two callers at once");
            }

            Assert.That(server.CallsActive, Is.EqualTo(Callers), "wave " + wave);
            for (int i = 0; i < Callers; i++)
            {
                lines[i].Dispose();
            }

            DateTime deadline = DateTime.UtcNow + Wait;
            while (server.CallsActive != 0 && DateTime.UtcNow < deadline)
            {
                System.Threading.Thread.Sleep(20);
            }

            Assert.That(server.CallsActive, Is.EqualTo(0), "wave " + wave + ": every caller hung up");
        }

        clock.Stop();
        TestContext.Out.WriteLine($"{Callers * Waves} calls in {Waves} waves of {Callers} in {clock.ElapsedMilliseconds} ms");
        Assert.That(server.CallsAnswered, Is.EqualTo(Callers * Waves));
        Assert.That(server.CallsRefusedFull, Is.EqualTo(0));
        Assert.That(server.LoginsRefused, Is.EqualTo(0));
    }

    [Test]
    public void OneCaller_SendsAnIpPacketToAnother_OverSlip_AndItArrives()
    {
        // Two callers on the dial-up subnet reach each other through the router (Ronny, 2026-11-22:
        // every caller has its own reachable address).
        using DialUpServer server = StartServer(maxCalls: 4);
        using TcpClient a = Dial(server);
        using TcpClient b = Dial(server);
        Assert.That(LogIn(a, "caller", Password), Does.Contain("Your address is 10.42.0.15"));
        Assert.That(LogIn(b, "fixed", Password), Does.Contain("Your address is 10.42.0.20"));

        byte[] payload = Encoding.ASCII.GetBytes("hello from caller A");
        byte[] packet = UdpPacket(new byte[] { 10, 42, 0, 15 }, new byte[] { 10, 42, 0, 20 }, 40000, 9, payload);

        RetroIO.Protocols.Slip.SlipLink slipA = new RetroIO.Protocols.Slip.SlipLink();
        slipA.OnLineOutput += bytes => a.GetStream().Write(bytes);
        slipA.SendPacket(packet);

        byte[]? arrived = null;
        RetroIO.Protocols.Slip.SlipLink slipB = new RetroIO.Protocols.Slip.SlipLink();
        slipB.OnPacket += p =>
        {
            // The line can carry other packets too; take the one from A.
            if (p.Length >= 20 && p[12] == 10 && p[15] == 15)
            {
                arrived = p.ToArray();
            }
        };

        byte[] buffer = new byte[2048];
        DateTime deadline = DateTime.UtcNow + Wait;
        while (arrived == null && DateTime.UtcNow < deadline)
        {
            int read = b.GetStream().Read(buffer, 0, buffer.Length);
            if (read == 0)
            {
                break;
            }

            slipB.ReceiveFromLine(buffer.AsSpan(0, read));
        }

        Assert.That(arrived, Is.Not.Null, "B got no packet from A");
        Assert.That(arrived!.Length, Is.EqualTo(packet.Length));
        Assert.That(arrived[16], Is.EqualTo(10));
        Assert.That(arrived[19], Is.EqualTo(20), "to B's address");
        Assert.That(arrived[8], Is.EqualTo(packet[8] - 1), "the router took one off the time-to-live");
        Assert.That(arrived.AsSpan(28).ToArray(), Is.EqualTo(payload));
    }

    [Test]
    public void ACaller_ReachesAUdpServiceOnThePc_ThroughTheGateway_AndGetsTheAnswer()
    {
        // Step 1 of the plan: a caller reaches RetroServer's own services. The gateway 10.42.0.2 is
        // the PC's own 127.0.0.1 through the NAT. A UDP echo on the PC stands for the service.
        using System.Net.Sockets.UdpClient service = new System.Net.Sockets.UdpClient(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 0));
        int servicePort = ((System.Net.IPEndPoint)service.Client.LocalEndPoint!).Port;
        byte[]? heard = null;
        Thread echo = new Thread(() =>
        {
            try
            {
                System.Net.IPEndPoint? from = null;
                service.Client.ReceiveTimeout = (int)Wait.TotalMilliseconds;
                heard = service.Receive(ref from);
                service.Send(Encoding.ASCII.GetBytes("answer from the PC"), from);
            }
            catch (SocketException)
            {
                // Nothing came: the asserts below say so.
            }
        }) { IsBackground = true };
        echo.Start();

        using DialUpServer server = StartServer(maxCalls: 4);
        using TcpClient caller = Dial(server);
        Assert.That(LogIn(caller, "caller", Password), Does.Contain("Your address is 10.42.0.15"));

        byte[] question = Encoding.ASCII.GetBytes("question from the caller");
        RetroIO.Protocols.Slip.SlipLink slip = new RetroIO.Protocols.Slip.SlipLink();
        slip.OnLineOutput += bytes => caller.GetStream().Write(bytes);
        byte[]? answer = null;
        slip.OnPacket += p =>
        {
            // UDP from the gateway to the caller.
            if (p.Length > 28 && p[9] == 17 && p[15] == 2 && p[19] == 15)
            {
                answer = p.ToArray();
            }
        };
        slip.SendPacket(UdpPacket(new byte[] { 10, 42, 0, 15 }, new byte[] { 10, 42, 0, 2 }, 40001, servicePort, question));

        byte[] buffer = new byte[2048];
        DateTime deadline = DateTime.UtcNow + Wait;
        while (answer == null && DateTime.UtcNow < deadline)
        {
            int read = caller.GetStream().Read(buffer, 0, buffer.Length);
            if (read == 0)
            {
                break;
            }

            slip.ReceiveFromLine(buffer.AsSpan(0, read));
        }

        echo.Join(Wait);
        Assert.That(heard, Is.EqualTo(question), "the service on the PC got the caller's packet");
        Assert.That(answer, Is.Not.Null, "the caller got no answer");
        Assert.That(answer!.AsSpan(28).ToArray(), Is.EqualTo(Encoding.ASCII.GetBytes("answer from the PC")));
        Assert.That((answer[20] << 8) | answer[21], Is.EqualTo(servicePort), "from the service's port");
        Assert.That((answer[22] << 8) | answer[23], Is.EqualTo(40001), "to the caller's port");
    }

    [Test]
    public void ACaller_OpensATcpConnectionToThePc_ThroughTheGateway_AndTalks()
    {
        // TCP from a caller to a listener on the PC (gateway 10.42.0.2 = the PC's 127.0.0.1). The
        // caller's side is done by hand: SYN, the PC's SYN-ACK, ACK with data, the PC's data.
        using TcpListener listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        int servicePort = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        string? serviceHeard = null;
        Thread serve = new Thread(() =>
        {
            try
            {
                using TcpClient accepted = listener.AcceptTcpClient();
                accepted.ReceiveTimeout = (int)Wait.TotalMilliseconds;
                byte[] got = new byte[256];
                int n = accepted.GetStream().Read(got, 0, got.Length);
                serviceHeard = Encoding.ASCII.GetString(got, 0, n);
                byte[] reply = Encoding.ASCII.GetBytes("hello caller");
                accepted.GetStream().Write(reply, 0, reply.Length);
                Thread.Sleep(500);
            }
            catch (Exception ex) when (ex is IOException || ex is SocketException)
            {
                // Nothing came: the asserts below say so.
            }
        }) { IsBackground = true };
        serve.Start();

        using DialUpServer server = StartServer(maxCalls: 4);
        using TcpClient caller = Dial(server);
        Assert.That(LogIn(caller, "caller", Password), Does.Contain("Your address is 10.42.0.15"));

        byte[] me = { 10, 42, 0, 15 };
        byte[] pc = { 10, 42, 0, 2 };
        const int MyPort = 40002;
        const uint MySeq = 1000;

        RetroIO.Protocols.Slip.SlipLink slip = new RetroIO.Protocols.Slip.SlipLink();
        slip.OnLineOutput += bytes => caller.GetStream().Write(bytes);
        List<byte[]> fromPc = new List<byte[]>();
        slip.OnPacket += p =>
        {
            if (p.Length >= 40 && p[9] == 6 && p[15] == 2)
            {
                fromPc.Add(p.ToArray());
            }
        };

        slip.SendPacket(TcpPacket(me, pc, MyPort, servicePort, MySeq, 0, 0x02, Array.Empty<byte>()));
        byte[]? synAck = WaitFor(caller, slip, fromPc, p => (p[33] & 0x12) == 0x12);
        Assert.That(synAck, Is.Not.Null, "no SYN-ACK from the PC");
        uint theirSeq = ReadUInt(synAck!, 24);
        Assert.That(ReadUInt(synAck!, 28), Is.EqualTo(MySeq + 1), "the SYN-ACK acknowledges our SYN");

        byte[] data = Encoding.ASCII.GetBytes("hello PC");
        slip.SendPacket(TcpPacket(me, pc, MyPort, servicePort, MySeq + 1, theirSeq + 1, 0x18, data));
        byte[]? withData = WaitFor(caller, slip, fromPc, p => p.Length > 40 + ((p[32] >> 4) * 4) - 20);

        serve.Join(Wait);
        Assert.That(serviceHeard, Is.EqualTo("hello PC"), "the listener on the PC got the caller's data");
        Assert.That(withData, Is.Not.Null, "no data back from the PC");
        int headerLength = 20 + ((withData![32] >> 4) * 4);
        Assert.That(Encoding.ASCII.GetString(withData, headerLength, withData.Length - headerLength), Is.EqualTo("hello caller"));
        listener.Stop();
    }

    [Test]
    public async System.Threading.Tasks.Task ACaller_MountsRetroServersTnfsServer_ThroughTheGateway()
    {
        // Step 1 with a real RetroServer service: TNFS over UDP, reached at the gateway 10.42.0.2.
        int tnfsPort;
        using (Socket probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
        {
            probe.Bind(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 0));
            tnfsPort = ((System.Net.IPEndPoint)probe.LocalEndPoint!).Port;
        }

        // The TNFS root the server shares, as the other TNFS tests set it up.
        RetroIO.Tests.TNFS.MockFileSystemBackend files = new RetroIO.Tests.TNFS.MockFileSystemBackend();
        files.AddDirectory("/tmp/tnfs");

        Microsoft.Extensions.Hosting.HostApplicationBuilder builder =
            Microsoft.Extensions.Hosting.Host.CreateEmptyApplicationBuilder(new Microsoft.Extensions.Hosting.HostApplicationBuilderSettings());
        Microsoft.Extensions.DependencyInjection.LoggingServiceCollectionExtensions.AddLogging(builder.Services);
        Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton<RetroIO.Server.Core.FileSystem.IFileSystemBackend>(
            builder.Services, files);
        RetroIO.Protocols.Tnfs.Server.TnfsServerExtensions.AddTnfsServer(builder.Services, o =>
        {
            o.EnableUdp = true;
            o.EnableTcp = false;
            o.UdpPort = tnfsPort;
            o.RootDirectory = "/tmp/tnfs";
        });
        using Microsoft.Extensions.Hosting.IHost tnfs = builder.Build();
        await tnfs.StartAsync();

        try
        {
            using DialUpServer server = StartServer(maxCalls: 4);
            using TcpClient caller = Dial(server);
            Assert.That(LogIn(caller, "caller", Password), Does.Contain("Your address is 10.42.0.15"));

            // MOUNT: session 0, retry n, command 0, version 1.2, mount point "/", no user, no password.
            List<byte> mount = new List<byte> { 0x00, 0x00, 0x00, RetroIO.Protocols.Tnfs.Protocol.TnfsConstants.TNFS_MOUNT,
                RetroIO.Protocols.Tnfs.Protocol.TnfsConstants.PROTOVERSION_LSB, RetroIO.Protocols.Tnfs.Protocol.TnfsConstants.PROTOVERSION_MSB,
                (byte)'/', 0x00, 0x00, 0x00 };

            RetroIO.Protocols.Slip.SlipLink slip = new RetroIO.Protocols.Slip.SlipLink();
            slip.OnLineOutput += bytes => caller.GetStream().Write(bytes);
            byte[]? reply = null;
            slip.OnPacket += p =>
            {
                if (p.Length > 32 && p[9] == 17 && p[15] == 2 && p[19] == 15)
                {
                    reply = p.ToArray();
                }
            };

            // A TNFS client resends a request that got no answer, counting up the retry byte; the
            // server may not be listening yet when the first one goes.
            byte[] buffer = new byte[2048];
            DateTime deadline = DateTime.UtcNow + Wait;
            byte retry = 0;
            caller.ReceiveTimeout = 500;
            while (reply == null && DateTime.UtcNow < deadline)
            {
                mount[2] = retry++;
                slip.SendPacket(UdpPacket(new byte[] { 10, 42, 0, 15 }, new byte[] { 10, 42, 0, 2 }, 40003, tnfsPort, mount.ToArray()));
                try
                {
                    int read = caller.GetStream().Read(buffer, 0, buffer.Length);
                    if (read == 0)
                    {
                        break;
                    }

                    slip.ReceiveFromLine(buffer.AsSpan(0, read));
                }
                catch (IOException)
                {
                    // No answer within half a second: send again.
                }
            }

            Assert.That(reply, Is.Not.Null, "no TNFS answer reached the caller");
            Assert.That(reply![28 + 3], Is.EqualTo(RetroIO.Protocols.Tnfs.Protocol.TnfsConstants.TNFS_MOUNT), "an answer to MOUNT");
            Assert.That(reply[28 + 4], Is.EqualTo(0), "status OK; the answer was " + Convert.ToHexString(reply, 28, reply.Length - 28));
            Assert.That(reply[28] | (reply[29] << 8), Is.Not.EqualTo(0), "a session number was given");
        }
        finally
        {
            await tnfs.StopAsync();
        }
    }

    /// <summary>
    /// Reads the caller's line until a packet from the PC matches, or the wait runs out.
    /// </summary>
    private static byte[]? WaitFor(TcpClient caller, RetroIO.Protocols.Slip.SlipLink slip, List<byte[]> fromPc, Func<byte[], bool> match)
    {
        byte[] buffer = new byte[2048];
        DateTime deadline = DateTime.UtcNow + Wait;
        while (DateTime.UtcNow < deadline)
        {
            for (int i = 0; i < fromPc.Count; i++)
            {
                if (match(fromPc[i]))
                {
                    byte[] found = fromPc[i];
                    fromPc.RemoveAt(i);
                    return found;
                }
            }

            int read = caller.GetStream().Read(buffer, 0, buffer.Length);
            if (read == 0)
            {
                return null;
            }

            slip.ReceiveFromLine(buffer.AsSpan(0, read));
        }

        return null;
    }

    private static uint ReadUInt(byte[] p, int at)
    {
        return ((uint)p[at] << 24) | ((uint)p[at + 1] << 16) | ((uint)p[at + 2] << 8) | p[at + 3];
    }

    /// <summary>
    /// Builds an IPv4 TCP packet with correct IP and TCP checksums (TCP over the RFC 793 pseudo-header).
    /// </summary>
    private static byte[] TcpPacket(byte[] from, byte[] to, int fromPort, int toPort, uint seq, uint ack, byte flags, byte[] payload)
    {
        int length = 40 + payload.Length;
        byte[] p = new byte[length];
        p[0] = 0x45;
        p[2] = (byte)(length >> 8);
        p[3] = (byte)length;
        p[8] = 64;
        p[9] = 6;
        from.CopyTo(p, 12);
        to.CopyTo(p, 16);
        WriteChecksum(p, 10, Sum(p, 0, 20, 0));
        p[20] = (byte)(fromPort >> 8);
        p[21] = (byte)fromPort;
        p[22] = (byte)(toPort >> 8);
        p[23] = (byte)toPort;
        p[24] = (byte)(seq >> 24); p[25] = (byte)(seq >> 16); p[26] = (byte)(seq >> 8); p[27] = (byte)seq;
        p[28] = (byte)(ack >> 24); p[29] = (byte)(ack >> 16); p[30] = (byte)(ack >> 8); p[31] = (byte)ack;
        p[32] = 0x50;
        p[33] = flags;
        p[34] = 0xFF;
        p[35] = 0xFF;
        payload.CopyTo(p, 40);

        // Pseudo-header: source, destination, zero, protocol 6, TCP length.
        int tcpLength = 20 + payload.Length;
        int pseudo = Sum(p, 12, 8, 0) + 6 + tcpLength;
        WriteChecksum(p, 36, Sum(p, 20, tcpLength, pseudo));
        return p;
    }

    private static int Sum(byte[] p, int start, int count, int sum)
    {
        for (int i = 0; i < count; i += 2)
        {
            int low = i + 1 < count ? p[start + i + 1] : 0;
            sum += (p[start + i] << 8) | low;
        }

        return sum;
    }

    private static void WriteChecksum(byte[] p, int at, int sum)
    {
        while ((sum >> 16) != 0)
        {
            sum = (sum & 0xFFFF) + (sum >> 16);
        }

        sum = ~sum & 0xFFFF;
        p[at] = (byte)(sum >> 8);
        p[at + 1] = (byte)sum;
    }

    /// <summary>
    /// Builds an IPv4 UDP packet with a correct header checksum and no UDP checksum (0, allowed by RFC 768).
    /// </summary>
    private static byte[] UdpPacket(byte[] from, byte[] to, int fromPort, int toPort, byte[] payload)
    {
        int length = 28 + payload.Length;
        byte[] p = new byte[length];
        p[0] = 0x45;
        p[2] = (byte)(length >> 8);
        p[3] = (byte)length;
        p[8] = 64;
        p[9] = 17;
        from.CopyTo(p, 12);
        to.CopyTo(p, 16);
        int sum = 0;
        for (int i = 0; i < 20; i += 2)
        {
            sum += (p[i] << 8) | p[i + 1];
        }

        while ((sum >> 16) != 0)
        {
            sum = (sum & 0xFFFF) + (sum >> 16);
        }

        sum = ~sum & 0xFFFF;
        p[10] = (byte)(sum >> 8);
        p[11] = (byte)sum;
        p[20] = (byte)(fromPort >> 8);
        p[21] = (byte)fromPort;
        p[22] = (byte)(toPort >> 8);
        p[23] = (byte)toPort;
        int udpLength = 8 + payload.Length;
        p[24] = (byte)(udpLength >> 8);
        p[25] = (byte)udpLength;
        payload.CopyTo(p, 28);
        return p;
    }

    private static TcpClient Dial(DialUpServer server)
    {
        TcpClient client = new TcpClient();
        client.Connect("127.0.0.1", server.ListeningPort);
        client.ReceiveTimeout = (int)Wait.TotalMilliseconds;
        return client;
    }

    private static string LogIn(TcpClient client, string name, string password)
    {
        StringBuilder all = new StringBuilder();
        all.Append(ReadUntil(client, "login:"));
        Send(client, name + "\r");
        all.Append(ReadUntil(client, "Password:"));
        Send(client, password + "\r");
        all.Append(ReadUntilAny(client, "MTU", "Login incorrect", "No line free"));
        return all.ToString();
    }

    private static void Send(TcpClient client, string text)
    {
        byte[] bytes = Encoding.ASCII.GetBytes(text);
        client.GetStream().Write(bytes, 0, bytes.Length);
    }

    private static string ReadUntil(TcpClient client, string marker)
    {
        return ReadUntilAny(client, marker, marker, marker);
    }

    private static string ReadUntilAny(TcpClient client, string a, string b, string c)
    {
        StringBuilder text = new StringBuilder();
        byte[] buffer = new byte[512];
        DateTime deadline = DateTime.UtcNow + Wait;
        while (DateTime.UtcNow < deadline)
        {
            string now = text.ToString();
            if (now.Contains(a, StringComparison.Ordinal) || now.Contains(b, StringComparison.Ordinal) || now.Contains(c, StringComparison.Ordinal))
            {
                // Let the rest of the line arrive.
                Thread.Sleep(50);
                if (client.Available > 0)
                {
                    int more = client.GetStream().Read(buffer, 0, Math.Min(buffer.Length, client.Available));
                    text.Append(Encoding.ASCII.GetString(buffer, 0, more));
                }

                return text.ToString();
            }

            int read;
            try
            {
                read = client.GetStream().Read(buffer, 0, buffer.Length);
            }
            catch (IOException)
            {
                break;
            }

            if (read == 0)
            {
                break;
            }

            text.Append(Encoding.ASCII.GetString(buffer, 0, read));
        }

        return text.ToString();
    }

    private static string ReadToEnd(TcpClient client)
    {
        StringBuilder text = new StringBuilder();
        byte[] buffer = new byte[512];
        try
        {
            int read;
            while ((read = client.GetStream().Read(buffer, 0, buffer.Length)) > 0)
            {
                text.Append(Encoding.ASCII.GetString(buffer, 0, read));
            }
        }
        catch (IOException)
        {
            // The server reset the line; what came before it is the answer.
        }

        return text.ToString();
    }

    private static bool ReadsEndOfLine(TcpClient client)
    {
        byte[] buffer = new byte[512];
        DateTime deadline = DateTime.UtcNow + Wait;
        try
        {
            while (DateTime.UtcNow < deadline)
            {
                if (client.GetStream().Read(buffer, 0, buffer.Length) == 0)
                {
                    return true;
                }
            }
        }
        catch (IOException)
        {
            return true;
        }

        return false;
    }

    private static string Lines(string text)
    {
        return "received: " + text.Replace("\r", "\\r").Replace("\n", "\\n");
    }
}
