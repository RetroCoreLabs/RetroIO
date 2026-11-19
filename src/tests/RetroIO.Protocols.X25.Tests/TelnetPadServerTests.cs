//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using RetroIO.Protocols.X25.Runtime;

namespace RetroIO.Protocols.X25.Tests
{
    /// <summary>
    /// A telnet terminal as the tests use it: a real TCP connection that collects everything the server
    /// sends, and can wait for a piece of text to show up.
    /// </summary>
    internal sealed class TelnetTestTerminal : IDisposable
    {
        private readonly TcpClient _client;
        private readonly NetworkStream _stream;
        private readonly List<byte> _received = new List<byte>();
        private readonly object _lock = new object();
        private readonly Thread _reader;

        public TelnetTestTerminal(int port)
        {
            _client = new TcpClient();
            _client.Connect(IPAddress.Loopback, port);
            _stream = _client.GetStream();
            _reader = new Thread(ReadLoop) { IsBackground = true };
            _reader.Start();
        }

        private void ReadLoop()
        {
            byte[] buffer = new byte[1024];
            try
            {
                while (true)
                {
                    int n = _stream.Read(buffer, 0, buffer.Length);
                    if (n <= 0)
                    {
                        break;
                    }

                    lock (_lock)
                    {
                        for (int i = 0; i < n; i++)
                        {
                            _received.Add(buffer[i]);
                        }
                    }
                }
            }
            catch (Exception)
            {
                // closed
            }

            lock (_lock)
            {
                Closed = true;
            }
        }

        /// <summary>
        /// True once the server has closed the connection.
        /// </summary>
        public bool Closed { get; private set; }

        /// <summary>
        /// Everything received so far, as Latin-1 text.
        /// </summary>
        public string Text
        {
            get
            {
                lock (_lock)
                {
                    StringBuilder sb = new StringBuilder(_received.Count);
                    for (int i = 0; i < _received.Count; i++)
                    {
                        sb.Append((char)_received[i]);
                    }

                    return sb.ToString();
                }
            }
        }

        /// <summary>
        /// Everything received so far, as bytes.
        /// </summary>
        public byte[] Bytes
        {
            get
            {
                lock (_lock)
                {
                    return _received.ToArray();
                }
            }
        }

        /// <summary>
        /// The text with the telnet negotiation (IAC x y) taken out, as a terminal would show it.
        /// </summary>
        public string Visible
        {
            get
            {
                byte[] b = Bytes;
                StringBuilder sb = new StringBuilder();
                int i = 0;
                while (i < b.Length)
                {
                    if (b[i] == 255 && i + 2 < b.Length + 0 && b[i + 1] >= 251 && b[i + 1] <= 254)
                    {
                        i += 3;
                    }
                    else if (b[i] == 255 && i + 1 < b.Length && b[i + 1] == 255)
                    {
                        sb.Append((char)255);
                        i += 2;
                    }
                    else
                    {
                        sb.Append((char)b[i]);
                        i++;
                    }
                }

                return sb.ToString();
            }
        }

        public void Send(string text)
        {
            byte[] bytes = Encoding.Latin1.GetBytes(text);
            _stream.Write(bytes, 0, bytes.Length);
        }

        public void Send(params byte[] bytes)
        {
            _stream.Write(bytes, 0, bytes.Length);
        }

        /// <summary>
        /// Types a line the way a telnet client does: the text, then CR NUL.
        /// </summary>
        public void TypeLine(string line)
        {
            Send(line);
            Send(13, 0);
        }

        /// <summary>
        /// Waits until the visible text contains the piece, counted from <paramref name="fromIndex"/>.
        /// </summary>
        public bool WaitFor(string piece, int timeoutMs = 5000, int fromIndex = 0)
        {
            DateTime end = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < end)
            {
                string v = Visible;
                if (fromIndex <= v.Length && v.IndexOf(piece, fromIndex, StringComparison.Ordinal) >= 0)
                {
                    return true;
                }

                Thread.Sleep(15);
            }

            string last = Visible;
            return fromIndex <= last.Length && last.IndexOf(piece, fromIndex, StringComparison.Ordinal) >= 0;
        }

        public bool WaitForClose(int timeoutMs = 5000)
        {
            DateTime end = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < end)
            {
                if (Closed)
                {
                    return true;
                }

                Thread.Sleep(15);
            }

            return Closed;
        }

        public void Dispose()
        {
            try
            {
                _client.Close();
            }
            catch (Exception)
            {
                // best effort
            }
        }
    }

    /// <summary>
    /// The telnet PAD, tested over real sockets in front of a real runtime whose built-in TAD server
    /// answers calls to its own address.
    /// </summary>
    [TestFixture]
    public class TelnetPadServerTests
    {
        private const string LocalAddress = "23430200";

        private ProtocolRuntime? _runtime;
        private readonly List<TelnetTestTerminal> _terminals = new List<TelnetTestTerminal>();

        [TearDown]
        public void TearDown()
        {
            for (int i = 0; i < _terminals.Count; i++)
            {
                _terminals[i].Dispose();
            }

            _terminals.Clear();
            _runtime?.Dispose();
            _runtime = null;
        }

        private static int FreePort()
        {
            TcpListener l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            int port = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return port;
        }

        private ProtocolRuntime StartRuntime(bool enabled = true, int maxSessions = 8, int port = 0)
        {
            RuntimeConfig config = new RuntimeConfig();
            config.X25.LocalMachineId = LocalAddress;
            config.TelnetPad.Enabled = enabled;
            config.TelnetPad.Port = port == 0 ? FreePort() : port;
            config.TelnetPad.MaxSessions = maxSessions;
            _runtime = new ProtocolRuntime(NullLogger.Instance, config);
            return _runtime;
        }

        private TelnetTestTerminal Connect(ProtocolRuntime runtime)
        {
            TelnetTestTerminal t = new TelnetTestTerminal(runtime.Config.TelnetPad.Port);
            _terminals.Add(t);
            return t;
        }

        private static bool WaitUntil(Func<bool> condition, int timeoutMs = 5000)
        {
            DateTime end = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < end)
            {
                if (condition())
                {
                    return true;
                }

                Thread.Sleep(20);
            }

            return condition();
        }

        [Test]
        public void Connect_ShowsBannerAndPrompt_AndOffersEchoAndSuppressGoAhead()
        {
            ProtocolRuntime rt = StartRuntime();
            TelnetTestTerminal t = Connect(rt);

            Assert.That(t.WaitFor("PAD> "), Is.True, "no prompt; got: " + t.Visible);
            Assert.That(t.Visible, Does.Contain("X.28 PAD over telnet"));

            // IAC WILL ECHO, IAC WILL SGA, IAC DO SGA are the first bytes on the wire.
            byte[] b = t.Bytes;
            Assert.That(b.Length, Is.GreaterThan(9));
            Assert.That(b[0], Is.EqualTo((byte)255));
            Assert.That(b[1], Is.EqualTo((byte)251));
            Assert.That(b[2], Is.EqualTo((byte)1));
            Assert.That(b[3], Is.EqualTo((byte)255));
            Assert.That(b[4], Is.EqualTo((byte)251));
            Assert.That(b[5], Is.EqualTo((byte)3));
            Assert.That(b[6], Is.EqualTo((byte)255));
            Assert.That(b[7], Is.EqualTo((byte)253));
            Assert.That(b[8], Is.EqualTo((byte)3));
        }

        [Test]
        public void TypedCommand_IsEchoedByThePad_ExactlyOnce()
        {
            ProtocolRuntime rt = StartRuntime();
            TelnetTestTerminal t = Connect(rt);
            Assert.That(t.WaitFor("PAD> "), Is.True);

            t.TypeLine("HELP");
            Assert.That(t.WaitFor("PAD commands:"), Is.True, "HELP gave nothing: " + t.Visible);

            string v = t.Visible;
            Assert.That(v, Does.Contain("PAD> HELP\r\n"), "the PAD should echo the typed command once: " + v);
            Assert.That(v, Does.Not.Contain("HELPHELP"), "echo doubled: " + v);
        }

        [Test]
        public void Call_ConnectsToTheTadServer_AndTypedTextGetsAnAnswer()
        {
            ProtocolRuntime rt = StartRuntime();
            TelnetTestTerminal t = Connect(rt);
            Assert.That(t.WaitFor("PAD> "), Is.True);

            t.TypeLine("CALL " + LocalAddress);
            Assert.That(t.WaitFor("COM"), Is.True, "no COM after the call: " + t.Visible);
            Assert.That(WaitUntil(() => rt.GetLocalTadProtocol().GetActiveSessions().Length > 0), Is.True, "the TAD never saw the call");

            // Whatever the TAD says on its own has been shown by now. Mark the place and type text.
            int mark = t.Visible.Length;
            t.TypeLine("hello tad");

            // The characters go to the host and the TAD answers. What really comes back (measured):
            //   hello tad            the PAD's own echo of the typed characters
            //   hello tad CR LF       the TAD's echo of the line (the TAD echoes too)
            //   Echo: hello tad CR LF  the TAD's reply to the line
            //   Analysis: 9 chars, alphabetic CR LF
            //   TAD> 
            Assert.That(t.WaitFor("TAD> ", 5000, mark), Is.True,
                "no TAD prompt after typing in the call. Whole transcript: " + t.Visible);
            string answer = t.Visible.Substring(mark);
            Assert.That(answer, Does.Contain("Echo: hello tad\r\n"), "the TAD's reply to the line is missing: " + answer);
            Assert.That(answer, Does.Contain("Analysis: 9 chars, alphabetic\r\n"), answer);
        }

        [Test]
        public void SetOfAnX3Parameter_ReachesTheTadSession()
        {
            ProtocolRuntime rt = StartRuntime();
            TelnetTestTerminal t = Connect(rt);
            Assert.That(t.WaitFor("PAD> "), Is.True);
            t.TypeLine("CALL " + LocalAddress);
            Assert.That(t.WaitFor("COM"), Is.True);
            Assert.That(WaitUntil(() => rt.GetLocalTadProtocol().GetActiveSessions().Length > 0), Is.True);

            ushort lcn = rt.GetLocalTadProtocol().GetActiveSessions()[0].LCN;
            byte before = rt.GetLocalTadProtocol().GetSessionX3ParametersByLCN(lcn)?.GetValueOrDefault((byte)12) ?? 0;
            byte wanted = (byte)(before == 0 ? 1 : 0);

            // In a call the PAD escape (Ctrl+P, DLE) returns to command mode (X.28).
            t.Send(0x10);
            t.TypeLine("SET 12:" + wanted);

            Assert.That(WaitUntil(() =>
                    (rt.GetLocalTadProtocol().GetSessionX3ParametersByLCN(lcn)?.GetValueOrDefault((byte)12) ?? 255) == wanted),
                Is.True, "the TAD session never got parameter 12 = " + wanted + ". Transcript: " + t.Visible);
        }

        [Test]
        public void IacFF_IsOneFFCharacter_AndOptionRequestsAreAnswered()
        {
            TelnetInputFilter f = new TelnetInputFilter();
            TelnetFilterResult r = f.Feed(255);
            Assert.That(r.HasData, Is.False);
            r = f.Feed(255);
            Assert.That(r.HasData, Is.True);
            Assert.That(r.Data, Is.EqualTo((byte)255));

            // DO of an option we do not have is refused with WONT.
            f.Feed(255);
            f.Feed(253);
            r = f.Feed(24); // TERMINAL-TYPE
            Assert.That(r.HasReply, Is.True);
            Assert.That(r.ReplyVerb, Is.EqualTo((byte)252));
            Assert.That(r.ReplyOption, Is.EqualTo((byte)24));

            // WILL of an option we did not ask for is refused with DONT.
            f.Feed(255);
            f.Feed(251);
            r = f.Feed(31); // NAWS
            Assert.That(r.HasReply, Is.True);
            Assert.That(r.ReplyVerb, Is.EqualTo((byte)254));

            // The client's answers to our offer need no reply (no loop).
            f.Feed(255);
            f.Feed(253);
            Assert.That(f.Feed(1).HasReply, Is.False);
            f.Feed(255);
            f.Feed(251);
            Assert.That(f.Feed(3).HasReply, Is.False);
            // A refusal is never answered.
            f.Feed(255);
            f.Feed(254);
            Assert.That(f.Feed(24).HasReply, Is.False);

            // CR NUL and CR LF are one CR.
            Assert.That(f.Feed(13).Data, Is.EqualTo((byte)13));
            Assert.That(f.Feed(0).HasData, Is.False);
            Assert.That(f.Feed(13).HasData, Is.True);
            Assert.That(f.Feed(10).HasData, Is.False);

            // A sub-negotiation is skipped whole.
            f.Feed(255);
            f.Feed(250);
            Assert.That(f.Feed(31).HasData, Is.False);
            Assert.That(f.Feed(0).HasData, Is.False);
            f.Feed(255);
            f.Feed(240);
            Assert.That(f.Feed((byte)'A').Data, Is.EqualTo((byte)'A'));
        }

        [Test]
        public void IacOnTheWire_IsAnsweredAndDoesNotReachThePad()
        {
            ProtocolRuntime rt = StartRuntime();
            TelnetTestTerminal t = Connect(rt);
            Assert.That(t.WaitFor("PAD> "), Is.True);
            int mark = t.Bytes.Length;

            // DO TERMINAL-TYPE (24), then a command with a doubled 0xFF inside it that the PAD must see as one character.
            t.Send(255, 253, 24);
            Assert.That(WaitUntil(() => t.Bytes.Length >= mark + 3), Is.True, "no answer to the option request");
            byte[] b = t.Bytes;
            Assert.That(b[mark], Is.EqualTo((byte)255));
            Assert.That(b[mark + 1], Is.EqualTo((byte)252));
            Assert.That(b[mark + 2], Is.EqualTo((byte)24));

            // A command with a 0xFF in it comes back echoed as the single character, doubled on the wire.
            mark = t.Bytes.Length;
            t.Send((byte)'A', 255, 255, (byte)'B');
            Assert.That(WaitUntil(() => t.Bytes.Length >= mark + 4), Is.True, "no echo");
            byte[] e = t.Bytes;
            Assert.That(e[mark], Is.EqualTo((byte)'A'));
            Assert.That(e[mark + 1], Is.EqualTo((byte)255));
            Assert.That(e[mark + 2], Is.EqualTo((byte)255));
            Assert.That(e[mark + 3], Is.EqualTo((byte)'B'));
        }

        [Test]
        public void Clear_ReturnsToPrompt_AndTheTadSessionEnds()
        {
            ProtocolRuntime rt = StartRuntime();
            TelnetTestTerminal t = Connect(rt);
            Assert.That(t.WaitFor("PAD> "), Is.True);
            t.TypeLine("CALL " + LocalAddress);
            Assert.That(t.WaitFor("COM"), Is.True);
            Assert.That(WaitUntil(() => rt.GetLocalTadProtocol().GetActiveSessions().Length > 0), Is.True);

            int mark = t.Visible.Length;
            t.Send(0x10);
            t.TypeLine("CLR");

            Assert.That(t.WaitFor("CLR CONF", 5000, mark), Is.True, "no CLR CONF: " + t.Visible.Substring(mark));
            Assert.That(t.WaitFor("PAD> ", 5000, t.Visible.IndexOf("CLR CONF", mark, StringComparison.Ordinal)), Is.True,
                "no prompt after the clear: " + t.Visible.Substring(mark));
            Assert.That(WaitUntil(() => rt.GetLocalTadProtocol().GetActiveSessions().Length == 0), Is.True, "the TAD session is still open");

            // The same terminal can place another call.
            mark = t.Visible.Length;
            t.TypeLine("CALL " + LocalAddress);
            Assert.That(t.WaitFor("COM", 5000, mark), Is.True, "second call failed: " + t.Visible.Substring(mark));
        }

        /// <summary>
        /// EXIT at the prompt during a call clears the call, says "Disconnected." and closes the telnet connection.
        /// </summary>
        [Test]
        public void Exit_ClearsTheCall_AndClosesTheTelnetConnection()
        {
            ProtocolRuntime rt = StartRuntime();
            TelnetTestTerminal t = Connect(rt);
            Assert.That(t.WaitFor("PAD> "), Is.True);
            t.TypeLine("CALL " + LocalAddress);
            Assert.That(t.WaitFor("COM"), Is.True);
            Assert.That(WaitUntil(() => rt.GetLocalTadProtocol().GetActiveSessions().Length > 0), Is.True);

            int mark = t.Visible.Length;
            t.Send(0x10);
            t.TypeLine("EXIT");

            Assert.That(t.WaitFor("Disconnected.", 5000, mark), Is.True, "no goodbye: " + t.Visible.Substring(mark));
            Assert.That(t.WaitForClose(), Is.True, "the telnet connection was not closed");
            Assert.That(WaitUntil(() => rt.GetLocalTadProtocol().GetActiveSessions().Length == 0), Is.True, "the call was not cleared");
            Assert.That(WaitUntil(() => rt.TelnetPadStatus.Sessions == 0), Is.True, "the session was not freed");
        }

        [Test]
        public void ClosingTheSocket_ClearsTheCall_AndFreesTheSession()
        {
            ProtocolRuntime rt = StartRuntime();
            TelnetTestTerminal t = Connect(rt);
            Assert.That(t.WaitFor("PAD> "), Is.True);
            t.TypeLine("CALL " + LocalAddress);
            Assert.That(t.WaitFor("COM"), Is.True);
            Assert.That(WaitUntil(() => rt.GetLocalTadProtocol().GetActiveSessions().Length > 0), Is.True);
            Assert.That(rt.TelnetPadStatus.Sessions, Is.EqualTo(1));

            t.Dispose();

            Assert.That(WaitUntil(() => rt.GetLocalTadProtocol().GetActiveSessions().Length == 0), Is.True, "the call was not cleared");
            Assert.That(WaitUntil(() => rt.TelnetPadStatus.Sessions == 0), Is.True, "the session was not freed");
            // The PAD module of that session is gone from the runtime.
            Assert.That(rt.GetRegisteredModules().Keys, Has.None.StartsWith("TelnetPad_"));
        }

        [Test]
        public void RemoteClear_ReturnsToPrompt()
        {
            ProtocolRuntime rt = StartRuntime();
            TelnetTestTerminal t = Connect(rt);
            Assert.That(t.WaitFor("PAD> "), Is.True);
            t.TypeLine("CALL " + LocalAddress);
            Assert.That(t.WaitFor("COM"), Is.True);
            Assert.That(WaitUntil(() => rt.GetLocalTadProtocol().GetActiveSessions().Length > 0), Is.True);

            // The host clears the call.
            ushort lcn = rt.GetLocalTadProtocol().GetActiveSessions()[0].LCN;
            int mark = t.Visible.Length;
            rt.GetLocalTadProtocol().DisconnectSession(lcn);

            Assert.That(t.WaitFor("CLR", 5000, mark), Is.True, "no clear signal: " + t.Visible.Substring(mark));
            Assert.That(t.WaitFor("PAD> ", 5000, mark), Is.True, "no prompt after the remote clear: " + t.Visible.Substring(mark));
        }

        [Test]
        public void TooManySessions_AreRefusedPolitely_AndASlotCanBeReused()
        {
            ProtocolRuntime rt = StartRuntime(maxSessions: 2);
            TelnetTestTerminal a = Connect(rt);
            TelnetTestTerminal b = Connect(rt);
            Assert.That(a.WaitFor("PAD> "), Is.True);
            Assert.That(b.WaitFor("PAD> "), Is.True);

            TelnetTestTerminal c = Connect(rt);
            Assert.That(c.WaitFor("telnet sessions are in use"), Is.True, "no polite refusal: " + c.Visible);
            Assert.That(c.WaitForClose(), Is.True, "the refused connection was not closed");
            Assert.That(c.Visible, Does.Not.Contain("PAD> "));

            a.Dispose();
            Assert.That(WaitUntil(() => rt.TelnetPadStatus.Sessions == 1), Is.True);
            TelnetTestTerminal d = Connect(rt);
            Assert.That(d.WaitFor("PAD> "), Is.True, "the freed slot was not reusable: " + d.Visible);
        }

        [Test]
        public void TwoSessions_AreIndependent()
        {
            ProtocolRuntime rt = StartRuntime();
            TelnetTestTerminal a = Connect(rt);
            TelnetTestTerminal b = Connect(rt);
            Assert.That(a.WaitFor("PAD> "), Is.True);
            Assert.That(b.WaitFor("PAD> "), Is.True);

            a.TypeLine("CALL " + LocalAddress);
            Assert.That(a.WaitFor("COM"), Is.True);
            Assert.That(WaitUntil(() => rt.GetLocalTadProtocol().GetActiveSessions().Length == 1), Is.True);

            // B is still at the prompt, not in A's call: its own command gets its own answer.
            b.TypeLine("HELP");
            Assert.That(b.WaitFor("PAD commands:"), Is.True);
            Assert.That(a.Visible, Does.Not.Contain("PAD commands:"));
            // The connected signal is COM on its own line (X.28 3.5.21); the help text has the word COMMAND in it.
            Assert.That(System.Text.RegularExpressions.Regex.IsMatch(b.Visible, @"(^|\n)COM\b"), Is.False, b.Visible);

            b.TypeLine("CALL " + LocalAddress);
            Assert.That(b.WaitFor("COM"), Is.True);
            Assert.That(WaitUntil(() => rt.GetLocalTadProtocol().GetActiveSessions().Length == 2), Is.True);

            // Closing A ends only A's call.
            a.Dispose();
            Assert.That(WaitUntil(() => rt.GetLocalTadProtocol().GetActiveSessions().Length == 1), Is.True);
            // A session ends in this order: the call is cleared, then the session is counted as gone
            // (TelnetPadServer End). So the count is waited for too; reading it at once failed now and then.
            Assert.That(WaitUntil(() => rt.TelnetPadStatus.Sessions == 1), Is.True, "A's session was not freed");
        }

        [Test]
        public void Disabled_NothingListens()
        {
            int port = FreePort();
            ProtocolRuntime rt = StartRuntime(enabled: false, port: port);
            Assert.That(rt.TelnetPadStatus.State, Is.EqualTo(TelnetPadState.Off));
            Assert.That(rt.TelnetPadStatus.Text, Is.EqualTo("Telnet PAD: off"));
            Assert.Throws<SocketException>(() =>
            {
                using TcpClient c = new TcpClient();
                c.Connect(IPAddress.Loopback, port);
            });
        }

        [Test]
        public void PortInUse_IsReported_AndTheRuntimeStillWorks()
        {
            TcpListener blocker = new TcpListener(IPAddress.Loopback, 0);
            blocker.Start();
            try
            {
                int port = ((IPEndPoint)blocker.LocalEndpoint).Port;
                ProtocolRuntime rt = StartRuntime(port: port);

                Assert.That(rt.TelnetPadStatus.State, Is.EqualTo(TelnetPadState.Failed));
                Assert.That(rt.TelnetPadStatus.Text, Does.StartWith("Telnet PAD: failed: "));
                Assert.That(rt.TelnetPadStatus.Text, Does.Contain("in use"));
                Assert.That(rt.IsRunning, Is.True);

                // The runtime still carries calls.
                RetroIO.Protocols.X25.Modules.PadClient pad = rt.CreatePadClient("StillWorks");
                Assert.That(pad.Connect(LocalAddress, "100"), Is.True);
                Assert.That(WaitUntil(() => rt.GetLocalTadProtocol().GetActiveSessions().Length > 0), Is.True);
                pad.Disconnect();
            }
            finally
            {
                blocker.Stop();
            }
        }

        [Test]
        public void ChangingTheConfig_RestartsOnTheNewPort_AndDroppingItStopsListening()
        {
            ProtocolRuntime rt = StartRuntime();
            int oldPort = rt.Config.TelnetPad.Port;
            TelnetTestTerminal t = Connect(rt);
            Assert.That(t.WaitFor("PAD> "), Is.True);

            int newPort = FreePort();
            rt.Config.TelnetPad.Port = newPort;
            rt.ApplyTelnetPadConfig();

            // The old session was closed, the old port is closed, the new one answers.
            Assert.That(t.WaitForClose(), Is.True, "the old session was not closed by the restart");
            Assert.Throws<SocketException>(() =>
            {
                using TcpClient c = new TcpClient();
                c.Connect(IPAddress.Loopback, oldPort);
            });
            Assert.That(rt.TelnetPadStatus.Port, Is.EqualTo(newPort));
            TelnetTestTerminal t2 = Connect(rt);
            Assert.That(t2.WaitFor("PAD> "), Is.True);

            rt.Config.TelnetPad.Enabled = false;
            rt.ApplyTelnetPadConfig();
            Assert.That(rt.TelnetPadStatus.State, Is.EqualTo(TelnetPadState.Off));
            Assert.Throws<SocketException>(() =>
            {
                using TcpClient c = new TcpClient();
                c.Connect(IPAddress.Loopback, newPort);
            });
        }

        [Test]
        public void StatusLine_ShowsListeningAddressPortAndSessions()
        {
            ProtocolRuntime rt = StartRuntime();
            int port = rt.Config.TelnetPad.Port;
            Assert.That(rt.TelnetPadStatus.Text, Is.EqualTo("Telnet PAD: listening on 127.0.0.1:" + port + ", 0 sessions"));
            TelnetTestTerminal a = Connect(rt);
            Assert.That(a.WaitFor("PAD> "), Is.True);
            Assert.That(rt.TelnetPadStatus.Text, Is.EqualTo("Telnet PAD: listening on 127.0.0.1:" + port + ", 1 session"));
        }

        [Test]
        public void Config_ValidatesPortAddressAndLimit()
        {
            TelnetPadConfig c = new TelnetPadConfig();
            Assert.That(c.Enabled, Is.False);
            Assert.That(c.Port, Is.EqualTo(2323));
            Assert.That(c.BindAddress, Is.EqualTo("127.0.0.1"));
            Assert.That(c.MaxSessions, Is.EqualTo(8));
            Assert.That(c.Validate(), Is.Null);

            c.Port = 0;
            Assert.That(c.Validate(), Is.Not.Null);
            c.Port = 65536;
            Assert.That(c.Validate(), Is.Not.Null);
            c.Port = 65535;
            Assert.That(c.Validate(), Is.Null);
            c.BindAddress = "not an address";
            Assert.That(c.Validate(), Is.Not.Null);
            c.BindAddress = "0.0.0.0";
            Assert.That(c.Validate(), Is.Null);
            c.MaxSessions = 0;
            Assert.That(c.Validate(), Is.Not.Null);
        }

        [Test]
        public void BadConfig_IsReportedAsFailed_NotThrown()
        {
            RuntimeConfig config = new RuntimeConfig();
            config.X25.LocalMachineId = LocalAddress;
            config.TelnetPad.Enabled = true;
            config.TelnetPad.Port = 70000;
            _runtime = new ProtocolRuntime(NullLogger.Instance, config);

            Assert.That(_runtime.TelnetPadStatus.State, Is.EqualTo(TelnetPadState.Failed));
            Assert.That(_runtime.TelnetPadStatus.Error, Does.Contain("port"));
        }

        [Test]
        public void ConfigFileWithoutTheSection_StillLoads_WithTelnetPadOff()
        {
            // The section is optional: a JSON file written before it existed deserialises with the defaults.
            RuntimeConfig? config = System.Text.Json.JsonSerializer.Deserialize<RuntimeConfig>("{ \"X25\": { \"LocalMachineId\": \"" + LocalAddress + "\" } }");
            Assert.That(config, Is.Not.Null);
            Assert.That(config!.TelnetPad.Enabled, Is.False);
            Assert.That(config.TelnetPad.Port, Is.EqualTo(2323));
        }
    }
}
