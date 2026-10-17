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
        public void Editor_CrLf_CompletesTypedLineThenEmptyLine()
        {
            TelnetLineEditor ed = new TelnetLineEditor();
            FeedAll(ed, "hi\r\n", out List<string> lines);

            Assert.That(lines.Count, Is.EqualTo(2));
            Assert.That(lines[0], Is.EqualTo("hi"));
            Assert.That(lines[1], Is.EqualTo(string.Empty));
        }

        [Test]
        public void Server_Banner_And_CommandRoundTrip()
        {
            SwitchConfig cfg = new SwitchConfig { Dnic = "2340", Name = "Switch Alpha" };
            SwitchNode node = new SwitchNode(cfg) { UptimeStartMs = 0 };
            SwitchCli cli = new SwitchCli(node, () => 1000L);
            TelnetCliServer server = new TelnetCliServer(cli, "127.0.0.1", 0);

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
