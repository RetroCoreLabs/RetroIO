//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

// The Altos chat written as a script (RetroIO.Protocols.X25.Host/scripts/altos-chat.js) against the C# engine
// (AltosChatEngine), on the same calls, input, time and drain reports. Both must do exactly the same: the same
// bytes to the same session in the same order, the same calls cleared, the same log lines.
//
// The C# engine is the reference: it is checked against the original 1987 source by AltosChatEngineTests. This
// test is what proves the scripted-host API is enough to write a real multi-user service, the job of PLAN G6 in
// RetroX25.

using System.Text;
using NUnit.Framework;
using RetroIO.Protocols.X25.Host;
using RetroIO.Protocols.X25.Protocols;

namespace RetroIO.Protocols.X25.Tests
{
    /// <summary>The scripted Altos chat against the C# Altos chat, step by step.</summary>
    [TestFixture]
    public class ScriptedAltosChatParityTests
    {
        // 29 May 1987, the date of CHAT 3.1; a Friday.
        private static readonly DateTime T0 = new DateTime(1987, 5, 29, 12, 0, 0);

        // The copy built into the DLL (HostScripts), which is the one x25host runs.
        private static string Script() => HostScripts.Get("altos-chat") ?? throw new InvalidOperationException("altos-chat is not built in");

        [Test]
        public void TheBuiltInCopy_IsTheScriptsFolder()
        {
            string path = Path.Combine(TestContext.CurrentContext.TestDirectory, "scripts", "altos-chat.js");
            Assert.That(Script(), Is.EqualTo(File.ReadAllText(path)));
            Assert.That(HostScripts.Names(), Does.Contain("altos-chat"));
            Assert.That(HostScripts.Get("nothere"), Is.Null);
        }

        /// <summary>A step given to both engines.</summary>
        private enum Op
        {
            Connect,
            Input,
            Disconnect,
            Drained,
            Tick,
        }

        private readonly struct Step
        {
            public Step(Op op, int session, string text, int number, string? calling)
            {
                Kind = op;
                Session = session;
                Text = text;
                Number = number;
                Calling = calling;
            }

            public Op Kind { get; }

            public int Session { get; }

            public string Text { get; }

            public int Number { get; }

            public string? Calling { get; }

            public override string ToString()
            {
                switch (Kind)
                {
                    case Op.Connect: return "Connect " + Session + " calling " + (Calling ?? "(null)");
                    case Op.Input: return "Input " + Session + " \"" + Text.Replace("\r", "\\r").Replace("\n", "\\n") + "\"";
                    case Op.Disconnect: return "Disconnect " + Session;
                    case Op.Drained: return "Drained " + Session + " " + Number;
                    default: return "Tick +" + Number + " ms";
                }
            }
        }

        /// <summary>Copies the host's output into an action list, so both engines are compared the same way.</summary>
        private sealed class HostToList : IHostOutput
        {
            public HostToList(AltosChatActionList list)
            {
                List = list;
            }

            public AltosChatActionList List { get; }

            public void Send(int session, ReadOnlySpan<byte> data) => List.Send(session, data);

            public void Clear(int session) => List.Clear(session);

            public void Log(string text) => List.Line(text);
        }

        /// <summary>Both engines, made with the same settings.</summary>
        private sealed class Pair
        {
            public Pair(AltosChatSettings settings)
            {
                CSharp = new AltosChatActionList();
                Script = new AltosChatActionList();
                Engine = new AltosChatEngine(settings, CSharp, CSharp);

                var hs = new ScriptedHostSettings { LineEnd = settings.LineEnd };
                hs.Values["MaxUsers"] = settings.MaxUsers.ToString(System.Globalization.CultureInfo.InvariantCulture);
                hs.Values["OutputLimitBytes"] = settings.OutputLimitBytes.ToString(System.Globalization.CultureInfo.InvariantCulture);
                hs.Values["StartingNickname"] = settings.StartingNickname;
                hs.Values["WaitPauseMs"] = ((long)settings.WaitPause.TotalMilliseconds).ToString(System.Globalization.CultureInfo.InvariantCulture);
                Host = new ScriptedHost(ScriptedAltosChatParityTests.Script(), hs, new HostToList(Script));

                Now = T0;
                Engine.Start(Now);
                Host.Start(Now);
            }

            public AltosChatActionList CSharp { get; }

            public AltosChatActionList Script { get; }

            public AltosChatEngine Engine { get; }

            public ScriptedHost Host { get; }

            public DateTime Now { get; private set; }

            public void Apply(Step s)
            {
                switch (s.Kind)
                {
                    case Op.Connect:
                        Engine.Connect(s.Session, s.Text, s.Calling);
                        Host.Connect(s.Session, s.Text, s.Calling);
                        break;
                    case Op.Input:
                        byte[] bytes = Latin1(s.Text);
                        Engine.Input(s.Session, bytes);
                        Host.Input(s.Session, bytes);
                        break;
                    case Op.Disconnect:
                        Engine.Disconnect(s.Session);
                        Host.Disconnect(s.Session);
                        break;
                    case Op.Drained:
                        Engine.OutputDrained(s.Session, s.Number);
                        Host.OutputDrained(s.Session, s.Number);
                        break;
                    default:
                        Now = Now.AddMilliseconds(s.Number);
                        Engine.Tick(Now);
                        Host.Tick(Now);
                        break;
                }
            }
        }

        /// <summary>Run every step on both, and compare after each one, so a failure names the first step that differs.</summary>
        private static void RunAndCompare(AltosChatSettings settings, List<Step> steps)
        {
            var pair = new Pair(settings);
            AssertSame(pair, -1, steps);
            for (int i = 0; i < steps.Count; i++)
            {
                pair.Apply(steps[i]);
                AssertSame(pair, i, steps);
            }
        }

        private static void AssertSame(Pair pair, int stepIndex, List<Step> steps)
        {
            IReadOnlyList<AltosChatAction> a = pair.CSharp.Items;
            IReadOnlyList<AltosChatAction> b = pair.Script.Items;
            int n = Math.Min(a.Count, b.Count);
            for (int i = 0; i < n; i++)
            {
                if (!SameAction(a[i], b[i]))
                {
                    Assert.Fail("After step " + stepIndex + " (" + StepName(steps, stepIndex) + "), action " + i +
                                " differs.\n  C#:     " + Show(a[i]) + "\n  script: " + Show(b[i]) + Recent(steps, stepIndex));
                }
            }

            if (a.Count != b.Count)
            {
                AltosChatAction extra = a.Count > b.Count ? a[n] : b[n];
                Assert.Fail("After step " + stepIndex + " (" + StepName(steps, stepIndex) + "), C# did " + a.Count +
                            " actions and the script " + b.Count + ". First extra (" + (a.Count > b.Count ? "C#" : "script") +
                            "): " + Show(extra) + Recent(steps, stepIndex));
            }
        }

        private static string StepName(List<Step> steps, int i) => i < 0 ? "start" : steps[i].ToString();

        private static string Recent(List<Step> steps, int i)
        {
            var sb = new StringBuilder("\n  Steps before:");
            for (int k = Math.Max(0, i - 8); k <= i; k++)
            {
                sb.Append("\n    ").Append(k).Append(": ").Append(steps[k].ToString());
            }

            return sb.ToString();
        }

        private static bool SameAction(AltosChatAction x, AltosChatAction y)
        {
            if (x.Kind != y.Kind || x.Session != y.Session || x.Text != y.Text)
            {
                return false;
            }

            if (x.Data == null || y.Data == null)
            {
                return x.Data == y.Data;
            }

            return x.Data.AsSpan().SequenceEqual(y.Data);
        }

        private static string Show(AltosChatAction a)
        {
            switch (a.Kind)
            {
                case AltosChatActionKind.Send:
                    return "send " + a.Session + " \"" + Encoding.ASCII.GetString(a.Data!).Replace("\r", "\\r").Replace("\n", "\\n") + "\"";
                case AltosChatActionKind.Clear:
                    return "clear " + a.Session;
                default:
                    return "log \"" + a.Text + "\"";
            }
        }

        private static byte[] Latin1(string text)
        {
            var b = new byte[text.Length];
            for (int i = 0; i < text.Length; i++)
            {
                b[i] = (byte)text[i];
            }

            return b;
        }

        // ------------------------------------------------------------------ tests

        [Test]
        public void TheScriptLoadsAndLogsTheStartLine()
        {
            var pair = new Pair(new AltosChatSettings());
            Assert.That(pair.Script.LogLines(), Is.EqualTo(new List<string> { "Chat-Server Starting At: Fri May 29 12:00:00 1987" }));
            Assert.That(pair.CSharp.LogLines(), Is.EqualTo(pair.Script.LogLines()));
        }

        [Test]
        public void EveryCommand_TwoAndThreeUsers_SameOutput()
        {
            var s = new List<Step>
            {
                C(1, "guest", "20200001"), T(3000),
                C(2, "guest", "26245890040001"), C(3, "guest", null),
                I(1, "early typing\r"), T(1000), T(2000),
                I(1, ".h\r"), I(2, ".n alice\r"), I(3, ".n bob\r"), I(1, "hello all\r"),
                I(2, ".s\r"), I(3, "!0 psst\r"), I(3, "!7 nobody\r"), I(3, "!-1 neg\r"),
                I(1, ".c 5\r"), I(1, ".p secret\r"), I(2, ".c 5\r"), I(2, ".c 5,wrong\r"), I(2, ".c 5,secret\r"),
                I(1, ".p again\r"), I(2, "only channel five\r"), I(3, "channel zero\r"),
                I(2, ".w\r"), I(3, ".b\r"), I(1, ".m\r"), I(1, "--!sh\r"), I(1, ".so file\r"),
                I(1, ".c 0\r"), I(1, ".p x\r"), I(1, ".c -7\r"), I(1, ".c -2147483648\r"), I(1, ".s\r"),
                I(2, "   \r"), I(2, "\r"), I(2, "\u0007bell\u007Fdel\r"), I(2, "split "), I(2, "line\r"),
                I(3, ".x\r"), I(3, "after leaving\r"),
                D(2), I(1, ".s\r"), I(1, ".q\r"), D(99),
            };
            RunAndCompare(new AltosChatSettings(), s);
        }

        [Test]
        public void LongLines_AreCutAt118_AndTheRemainderIsALine()
        {
            string full = new string('x', 117) + " ";
            var s = new List<Step>
            {
                C(1, "guest", "1"), C(2, "guest", "2"), T(3000),
                I(1, new string('a', 300) + "\r"),
                I(1, full + "\r"),
                I(1, new string('b', 118) + ".x\r"),
                I(2, ".n " + new string('n', 60) + "\r"), I(2, "nick cut\r"), I(2, ".s\r"),
            };
            RunAndCompare(new AltosChatSettings(), s);
        }

        [Test]
        public void ATableThatIsFull_PutsTheNextCallerOut()
        {
            var s = new List<Step>
            {
                C(1, "guest", "1"), C(2, "guest", "2"), C(3, "guest", "3"), T(3000),
                I(3, "never joined\r"), I(1, ".x\r"), C(4, "guest", "4"), T(3000), I(4, "now there is room\r"),
            };
            RunAndCompare(new AltosChatSettings { MaxUsers = 2 }, s);
        }

        [Test]
        public void ASlowUser_IsThrownOut_AndStaysDeaf()
        {
            var s = new List<Step>
            {
                C(1, "guest", "1"), C(2, "guest", "2"), T(3000),
                Dr(1, 10_000),
                I(1, "one\r"), I(1, "two\r"), I(1, "three\r"), I(1, "four\r"),
                I(2, "can you hear me\r"), I(2, ".s\r"), I(2, ".x\r"),
            };
            RunAndCompare(new AltosChatSettings { OutputLimitBytes = 260 }, s);
        }

        [Test]
        public void OddCallingAddressesAndLogins()
        {
            var s = new List<Step>
            {
                C(1, "", ""), C(2, "averyverylongloginname", ":tty01"), C(3, "g\u00e5st\u0001", "12345678901234567890"),
                C(4, "guest", null), T(3000), I(1, ".w\r"), I(1, ".s\r"),
            };
            RunAndCompare(new AltosChatSettings { StartingNickname = "  Gast \u0007 " }, s);
        }

        [Test]
        public void JoinAtOnce_WhenThePauseIsZero()
        {
            var s = new List<Step>
            {
                C(1, "guest", "1"), I(1, "hi\r"), C(2, "guest", "2"), I(2, "hi back\r"), I(1, ".x\r"),
            };
            RunAndCompare(new AltosChatSettings { WaitPause = TimeSpan.Zero }, s);
        }

        /// <summary>
        /// Random calls, lines, clears, drain reports and time, from fixed seeds so a failure can be run again. Each
        /// run is compared after every step.
        /// </summary>
        [TestCase(1)]
        [TestCase(2)]
        [TestCase(3)]
        [TestCase(4)]
        [TestCase(5)]
        [TestCase(6)]
        [TestCase(7)]
        [TestCase(8)]
        public void RandomSessions_SameOutput(int seed)
        {
            var rnd = new Random(seed);
            string[] lines =
            {
                ".h", ".s", ".n bob", ".n " + new string('q', 40), ".c 3", ".c 3,pw", ".c 3,nope", ".p pw", ".c 0",
                ".c -1", "!0 hi", "!1 there", "!2 x", "!-1 z", "!99 far", ".w", ".b", ".m", "--!", ".so x", ".x", ".q",
                "hello", "", "   ", new string('w', 120), "\u0007ring", ".n", ".p", ".cx", "!", "!abc",
            };
            string?[] callings = { null, "", ":tty", "20200001", "26245890040004", "a very long calling address 123" };
            var steps = new List<Step>();
            var live = new List<int>();
            int nextId = 1;
            for (int i = 0; i < 500; i++)
            {
                int r = rnd.Next(100);
                if (r < 12 || live.Count == 0)
                {
                    int id = nextId++;
                    live.Add(id);
                    steps.Add(C(id, rnd.Next(4) == 0 ? "user" + id : "guest", callings[rnd.Next(callings.Length)]));
                }
                else if (r < 70)
                {
                    int id = live[rnd.Next(live.Count)];
                    string text = lines[rnd.Next(lines.Length)];
                    string end = rnd.Next(3) == 0 ? "\n" : "\r";
                    if (rnd.Next(5) == 0)
                    {
                        // Two lines in one packet, or half a line.
                        text = text + end + lines[rnd.Next(lines.Length)];
                    }

                    steps.Add(I(id, rnd.Next(6) == 0 ? text : text + end));
                }
                else if (r < 76)
                {
                    int at = rnd.Next(live.Count);
                    steps.Add(D(live[at]));
                    live.RemoveAt(at);
                }
                else if (r < 88)
                {
                    steps.Add(Dr(live[rnd.Next(live.Count)], rnd.Next(1, 400)));
                }
                else
                {
                    steps.Add(T(rnd.Next(0, 2500)));
                }
            }

            RunAndCompare(new AltosChatSettings { MaxUsers = 5, OutputLimitBytes = 700 }, steps);
        }

        private static Step C(int session, string login, string? calling) => new Step(Op.Connect, session, login, 0, calling);

        private static Step I(int session, string text) => new Step(Op.Input, session, text, 0, null);

        private static Step D(int session) => new Step(Op.Disconnect, session, "", 0, null);

        private static Step Dr(int session, int bytes) => new Step(Op.Drained, session, "", bytes, null);

        private static Step T(int ms) => new Step(Op.Tick, 0, "", ms, null);
    }
}
