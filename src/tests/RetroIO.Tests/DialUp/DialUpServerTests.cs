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
