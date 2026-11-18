//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using NUnit.Framework;
using RetroIO.Protocols.X25.Switch;
using RetroIO.Protocols.X25.Switch.Management;
using RetroIO.Protocols.X25.Switch.Management.Telnet;
using RetroIO.Protocols.X25.Switch.Model;

namespace RetroIO.Protocols.X25.Tests
{
    /// <summary>
    /// Tests for the telnet management console: the line-editor state machine (deterministic) and a
    /// server round-trip over a real localhost socket.
    /// </summary>
    [TestFixture]
    public class TelnetCliTests
    {
        private static string FeedAll(TelnetLineEditor ed, string bytes, out List<string> lines)
        {
            lines = new List<string>();
            StringBuilder echo = new StringBuilder();
            for (int i = 0; i < bytes.Length; i++)
            {
                TelnetInput r = ed.Feed((byte)bytes[i]);
                echo.Append(r.Echo);
                if (r.LineComplete)
                {
                    lines.Add(r.Line);
                }
            }

            return echo.ToString();
        }

        [Test]
        public void Editor_PrintableEchoes_CrCompletesLine()
        {
            TelnetLineEditor ed = new TelnetLineEditor();
            string echo = FeedAll(ed, "abc\r", out List<string> lines);

            Assert.That(echo, Is.EqualTo("abc\r\n"));
            Assert.That(lines.Count, Is.EqualTo(1));
            Assert.That(lines[0], Is.EqualTo("abc"));
        }

        [Test]
        public void Editor_Backspace_ErasesLastChar()
        {
            TelnetLineEditor ed = new TelnetLineEditor();
            // 'a','b', backspace(0x08), 'c', CR -> line "ac"
            List<byte> input = new List<byte> { (byte)'a', (byte)'b', 0x08, (byte)'c', (byte)'\r' };
            StringBuilder echo = new StringBuilder();
            string? line = null;
            for (int i = 0; i < input.Count; i++)
            {
                TelnetInput r = ed.Feed(input[i]);
                echo.Append(r.Echo);
                if (r.LineComplete)
                {
                    line = r.Line;
                }
            }

            Assert.That(echo.ToString(), Is.EqualTo("ab\b \bc\r\n"));
            Assert.That(line, Is.EqualTo("ac"));
        }

        [Test]
        public void Editor_BackspaceOnEmpty_IsNoOp()
        {
            TelnetLineEditor ed = new TelnetLineEditor();
            TelnetInput r = ed.Feed(0x7F);
            Assert.That(r.Echo, Is.EqualTo(string.Empty));
            Assert.That(r.LineComplete, Is.False);
        }

        [Test]
        public void Editor_IacDiscardsNextTwoBytes()
        {
            TelnetLineEditor ed = new TelnetLineEditor();
            // IAC(0xFF) + two option bytes are discarded, then 'x' + CR -> line "x".
            List<byte> input = new List<byte> { 0xFF, 0xFB, 0x01, (byte)'x', (byte)'\r' };
            StringBuilder echo = new StringBuilder();
            string? line = null;
            for (int i = 0; i < input.Count; i++)
            {
                TelnetInput r = ed.Feed(input[i]);
                echo.Append(r.Echo);
                if (r.LineComplete)
                {
                    line = r.Line;
                }
            }

            Assert.That(echo.ToString(), Is.EqualTo("x\r\n"), "IAC + 2 bytes produce no echo");
            Assert.That(line, Is.EqualTo("x"));
        }

        [Test]
        public void Editor_CrLfAndCrNul_AreOneLineEnd_AsRfc854Says()
        {
            TelnetLineEditor ed = new TelnetLineEditor();
            string echo = FeedAll(ed, "admin\r\npw\r\0x\ny\r", out List<string> lines);

            Assert.That(lines, Is.EqualTo(new List<string> { "admin", "pw", "x", "y" }));
            Assert.That(echo, Is.EqualTo("admin\r\npw\r\nx\r\ny\r\n"));
        }

        [Test]
        public void Editor_TwoEntersAreTwoLines()
        {
            TelnetLineEditor ed = new TelnetLineEditor();
            FeedAll(ed, "a\r\n\r\n", out List<string> lines);

            Assert.That(lines, Is.EqualTo(new List<string> { "a", string.Empty }));
        }

        [Test]
        public void Editor_SkipsEveryKindOfTelnetCommand()
        {
            TelnetLineEditor ed = new TelnetLineEditor();
            // DO ECHO, DO SGA (the client's answer), a two-byte command (IAC NOP), a window-size subnegotiation that
            // contains 255 doubled, and IAC IAC (data 255, not printable): only "ok" is left.
            byte[] input =
            {
                0xFF, 0xFD, 0x01, 0xFF, 0xFD, 0x03, 0xFF, 0xF1,
                0xFF, 0xFA, 0x1F, 0x00, 0x50, 0xFF, 0xFF, 0x18, 0xFF, 0xF0,
                (byte)'o', 0xFF, 0xFF, (byte)'k', (byte)'\r',
            };
            StringBuilder echo = new StringBuilder();
            List<string> lines = new List<string>();
            for (int i = 0; i < input.Length; i++)
            {
                TelnetInput r = ed.Feed(input[i]);
                echo.Append(r.Echo);
                if (r.LineComplete)
                {
                    lines.Add(r.Line);
                }
            }

            Assert.That(lines, Is.EqualTo(new List<string> { "ok" }));
            Assert.That(echo.ToString(), Is.EqualTo("ok\r\n"));
        }

        [Test]
        public void Server_OffersEchoAndSuppressGoAhead_BeforeTheBanner()
        {
            SwitchConfig cfg = new SwitchConfig { Dnic = "2340", Name = "Switch Alpha" };
            SwitchCli cli = new SwitchCli(new SwitchNode(cfg) { UptimeStartMs = 0 }, () => 1000L);
            TelnetCliServer server = new TelnetCliServer(cli, "127.0.0.1", 0, new List<UserConfig>(), open: true);
            Assert.That(server.Start(), Is.True);
            try
            {
                using TcpClient client = new TcpClient();
                client.Connect("127.0.0.1", server.Port);
                using NetworkStream s = client.GetStream();
                s.ReadTimeout = 2000;
                byte[] first = new byte[6];
                int got = 0;
                while (got < 6)
                {
                    got += s.Read(first, got, 6 - got);
                }

                Assert.That(first, Is.EqualTo(new byte[] { 0xFF, 0xFB, 0x01, 0xFF, 0xFB, 0x03 }));
            }
            finally
            {
                server.Stop();
            }
        }

        [Test]
        public void Server_Banner_And_CommandRoundTrip()
        {
            SwitchConfig cfg = new SwitchConfig { Dnic = "2340", Name = "Switch Alpha" };
            SwitchNode node = new SwitchNode(cfg) { UptimeStartMs = 0 };
            SwitchCli cli = new SwitchCli(node, () => 1000L);
            TelnetCliServer server = new TelnetCliServer(cli, "127.0.0.1", 0, new List<UserConfig>(), open: true);

            Assert.That(server.Start(), Is.True);
            try
            {
                using TcpClient client = new TcpClient();
                client.Connect("127.0.0.1", server.Port);
                using NetworkStream s = client.GetStream();
                s.ReadTimeout = 2000;

                string banner = ReadAvailable(s, 500);
                Assert.That(banner, Does.Contain("X.25 Switch Management Console"));
                Assert.That(banner, Does.Contain("> "));

                byte[] cmd = Encoding.ASCII.GetBytes("show info\r");
                s.Write(cmd, 0, cmd.Length);

                string response = ReadAvailable(s, 500);
                Assert.That(response, Does.Contain("Name:        Switch Alpha"));
                Assert.That(response, Does.Contain("DNIC:        2340"));
            }
            finally
            {
                server.Stop();
            }
        }

        [Test]
        public void Server_AsksForALogin_AndALevel0UserMayOnlyLook()
        {
            SwitchConfig cfg = new SwitchConfig { Dnic = "2340", Name = "Switch Alpha" };
            SwitchNode node = new SwitchNode(cfg) { UptimeStartMs = 0 };
            SwitchCli cli = new SwitchCli(node, () => 1000L);
            List<UserConfig> users = new List<UserConfig> { new UserConfig { Name = "look", Password = "pw", Level = 0 } };
            TelnetCliServer server = new TelnetCliServer(cli, "127.0.0.1", 0, users, open: false);

            Assert.That(server.Start(), Is.True);
            try
            {
                using TcpClient client = new TcpClient();
                client.Connect("127.0.0.1", server.Port);
                using NetworkStream s = client.GetStream();

                Assert.That(ReadAvailable(s, 500), Does.EndWith("Username: "));

                // A command before the login is taken as a username, never run.
                Send(s, "show info\r");
                Assert.That(ReadAvailable(s, 500), Does.Contain("Password: "));
                Send(s, "x\r");
                Assert.That(ReadAvailable(s, 500), Does.Contain("Login incorrect."));

                Send(s, "look\r");
                ReadAvailable(s, 300);
                Send(s, "pw\r");
                string afterPassword = ReadAvailable(s, 500);
                Assert.That(afterPassword, Does.EndWith("> "));
                Assert.That(afterPassword, Does.Not.Contain("pw"), "the password is not echoed");

                Send(s, "show info\r");
                Assert.That(ReadAvailable(s, 500), Does.Contain("Name:        Switch Alpha"));

                Send(s, "x25 route add exact 23499999 1\r");
                Assert.That(ReadAvailable(s, 500), Does.Contain("Error: your user is read-only (level 0)"));
                Assert.That(cfg.Routing.Routes.Count, Is.EqualTo(0));
            }
            finally
            {
                server.Stop();
            }
        }

        /// <summary>
        /// A login as the Windows telnet client does it: it answers the server's offer with DO ECHO and DO
        /// SUPPRESS-GO-AHEAD, sends each key on its own, and sends CR LF for Enter. Every key comes back once, the
        /// password not at all, and admin/admin logs in. Before 2026-11-18 every key showed twice and every login
        /// from that client failed (the LF of Enter was taken as an empty password).
        /// </summary>
        [Test]
        public void Server_LoginAsWindowsTelnetDoesIt_EchoesOnce_AndLogsIn()
        {
            SwitchConfig cfg = new SwitchConfig { Dnic = "2340", Name = "Switch Alpha" };
            SwitchCli cli = new SwitchCli(new SwitchNode(cfg) { UptimeStartMs = 0 }, () => 1000L);
            List<UserConfig> users = new List<UserConfig> { new UserConfig { Name = "admin", Password = "admin", Level = 1 } };
            TelnetCliServer server = new TelnetCliServer(cli, "127.0.0.1", 0, users, open: false);
            Assert.That(server.Start(), Is.True);
            try
            {
                using TcpClient client = new TcpClient();
                client.Connect("127.0.0.1", server.Port);
                using NetworkStream s = client.GetStream();
                Assert.That(ReadAvailable(s, 500), Does.EndWith("Username: "));

                byte[] answer = { 0xFF, 0xFD, 0x01, 0xFF, 0xFD, 0x03 };
                s.Write(answer, 0, answer.Length);
                string typed = "admin\r\n";
                for (int i = 0; i < typed.Length; i++)
                {
                    s.WriteByte((byte)typed[i]);
                }

                Assert.That(ReadAvailable(s, 500), Is.EqualTo("admin\r\nPassword: "));

                for (int i = 0; i < typed.Length; i++)
                {
                    s.WriteByte((byte)typed[i]);
                }

                Assert.That(ReadAvailable(s, 500), Is.EqualTo("\r\n> "));

                Send(s, "show info\r\n");
                string info = ReadAvailable(s, 500);
                Assert.That(info, Does.StartWith("show info\r\nName:        Switch Alpha"));
                Assert.That(info, Does.EndWith("> "));
            }
            finally
            {
                server.Stop();
            }
        }

        private static void Send(NetworkStream s, string text)
        {
            byte[] b = Encoding.ASCII.GetBytes(text);
            s.Write(b, 0, b.Length);
        }

        private static string ReadAvailable(NetworkStream s, int quietMs)
        {
            StringBuilder sb = new StringBuilder();
            byte[] buf = new byte[1024];
            long deadline = Environment.TickCount64 + quietMs;
            while (Environment.TickCount64 < deadline)
            {
                if (s.DataAvailable)
                {
                    int n = s.Read(buf, 0, buf.Length);
                    if (n > 0)
                    {
                        sb.Append(Encoding.ASCII.GetString(buf, 0, n));
                        deadline = Environment.TickCount64 + quietMs;
                        continue;
                    }
                }

                Thread.Sleep(10);
            }

            return sb.ToString();
        }
    }
}
