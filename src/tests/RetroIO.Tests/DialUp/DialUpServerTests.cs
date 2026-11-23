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
