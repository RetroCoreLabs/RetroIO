//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using RetroIO.Protocols.X25.Modules;
using RetroIO.Protocols.X25.Protocols;
using RetroIO.Protocols.X25.Runtime;
using RetroIO.Protocols.X25.Runtime.Models;

namespace RetroIO.Protocols.X25.Tests
{
    /// <summary>
    /// X.29 says the HOST may change the PAD's own X.3 parameters: a SET PARAMETERS or SET AND READ
    /// PARAMETERS message (an X.25 data packet with the Q bit set) makes the PAD change them, and a READ
    /// PARAMETERS or SET AND READ PARAMETERS message is answered with a PARAMETER INDICATION. The built-in
    /// TAD server sends SET with echo (X.3 parameter 2) = 0 at the start of every call so that it does the
    /// echoing; after that the PAD must not echo what the person types, or every typed line shows twice.
    /// </summary>
    [TestFixture]
    public class PadHostSetParametersTests
    {
        private ProtocolRuntime _runtime;

        [SetUp]
        public void Setup()
        {
            RuntimeConfig config = new RuntimeConfig();
            config.X25.LocalMachineId = "200";
            _runtime = new ProtocolRuntime(NullLogger.Instance, config);
        }

        [TearDown]
        public void TearDown()
        {
            _runtime?.Dispose();
        }

        private static bool WaitUntil(Func<bool> condition, int timeoutMs)
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

        /// <summary>
        /// Counts how often one character occurs in the text.
        /// </summary>
        private static int Count(string text, char c)
        {
            int n = 0;
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] == c)
                {
                    n++;
                }
            }

            return n;
        }

        /// <summary>
        /// Real runtime, real call to the built-in TAD (address 200 here, the same code path as 23430200).
        /// The TAD's SET (echo = 0) arrives with the Q bit, so the PAD must now have echo 0, and a typed
        /// line must be shown ONCE: the PAD echoes nothing, the TAD echoes it.
        /// </summary>
        [Test]
        public void TypedLine_AfterHostSetsEchoOff_ShowsOnce()
        {
            PadClient pad = _runtime.CreatePadClient("HostSetPad");
            StringBuilder shown = new StringBuilder();
            object gate = new object();
            pad.DataReceived += (s, e) => { lock (gate) { shown.Append(e.Data); } };
            pad.CharacterEcho += (s, e) => { lock (gate) { shown.Append((char)e.Character); } };
            Assert.That(pad.Connect("200", "100"), Is.True);
            Assert.That(WaitUntil(() => pad.ConnectionState == PadConnectionState.Connected, 3000), Is.True);
            Assert.That(WaitUntil(() => { lock (gate) { return shown.ToString().Contains("TAD"); } }, 3000), Is.True,
                "the welcome text never arrived");
            Thread.Sleep(300);

            lock (gate) { shown.Clear(); }
            string typed = "ZQZ\r";
            for (int i = 0; i < typed.Length; i++)
            {
                pad.OnKey(typed[i]);
            }

            Thread.Sleep(600);
            string seen;
            lock (gate) { seen = shown.ToString(); }
            Console.WriteLine("shown after typing ZQZ: " + seen.Replace("\r", "\\r").Replace("\n", "\\n"));

            Assert.That(pad.GetParameter(2), Is.EqualTo((byte)0),
                "the host sent SET with echo = 0 and the PAD did not apply it; the screen showed: " + seen);

            // The line is shown ONCE (typed text, CR LF), then the TAD echo service answers with its own
            // line "Echo: ...". Before the fix the screen began with the line twice: "ZQZZQZ".
            Assert.That(seen, Does.StartWith("ZQZ" + (char)13 + (char)10 + "Echo: ZQZ"),
                "the typed line was not shown exactly once before the service answer: " + seen);
            Assert.That(seen.IndexOf("ZQZZQZ", StringComparison.Ordinal), Is.EqualTo(-1), "typed line shown twice: " + seen);

            pad.Disconnect();
            pad.Dispose();
        }

        /// <summary>
        /// A host SET PARAMETERS message (Q bit) changes the PAD's parameters; the Q bit is what marks it as
        /// a control message, so the same bytes without the Q bit must be text and change nothing.
        /// </summary>
        [Test]
        public void HostSetParameters_WithQBit_ChangesThePad_WithoutQBitDoesNot()
        {
            PadClient pad = _runtime.CreatePadClient("HostSetPad2");
            Assert.That(pad.Connect("200", "100"), Is.True);
            Assert.That(WaitUntil(() => pad.ConnectionState == PadConnectionState.Connected, 3000), Is.True);
            Thread.Sleep(300);

            // Both messages go straight to the PAD's delivery method, the way the runtime delivers them.
            byte p12Before = pad.GetParameter(12);
            byte wanted = (byte)(p12Before == 0 ? 1 : 0);
            pad.DeliverData(1, new byte[] { 0x02, 12, wanted }, false);
            Assert.That(pad.GetParameter(12), Is.EqualTo(p12Before), "text without the Q bit changed a parameter");
            pad.DeliverData(1, new byte[] { 0x02, 12, wanted }, true);
            Assert.That(pad.GetParameter(12), Is.EqualTo(wanted), "SET PARAMETERS with the Q bit was not applied");

            pad.Disconnect();
            pad.Dispose();
        }

        /// <summary>
        /// A recording transport: keeps every packet the PAD sends, so the answer to a host READ can be decoded.
        /// </summary>
        private sealed class RecordingTransport : IPadTransport
        {
            public readonly List<(bool Q, byte[] Data)> Sent = new List<(bool, byte[])>();

            public bool PlaceCall(string calledAddress, string callingAddress, byte[]? callUserData) { return true; }

            public bool SendData(ushort lcn, byte[] data, bool mBit, bool qBit, bool dBit)
            {
                lock (Sent) { Sent.Add((qBit, data)); }
                return true;
            }

            public void ForwardData(ushort lcn, byte[] data, int length) { }

            public void ClearCall(ushort lcn, byte cause, byte diagnostic) { }
        }

        /// <summary>
        /// X.29: READ PARAMETERS is answered with a PARAMETER INDICATION (type 0, Q bit set) carrying the
        /// value of each requested parameter; SET AND READ PARAMETERS applies the values and answers the same way.
        /// </summary>
        [Test]
        public void HostRead_IsAnsweredWithParameterIndication()
        {
            RecordingTransport transport = new RecordingTransport();
            PadClient pad = new PadClient(NullLogger.Instance, transport);
            pad.NotifyCallAccepted(5);

            // READ PARAMETERS for 2 and 3 (X.29 type 4). A read carries reference and value pairs like a set, "Value
            // fields in read PAD messages are coded as all binary 0s" (X.29 (12/97) 4.4.5.3). Until 2026-11-18 this test
            // sent bare references (04 02 03), which X.29 reads as parameter 2 with the value 3.
            pad.DeliverData(5, new byte[] { 0x04, 2, 0, 3, 0 }, true);
            Assert.That(transport.Sent.Count, Is.EqualTo(1), "no answer to READ PARAMETERS");
            Assert.That(transport.Sent[0].Q, Is.True, "the answer must be a control message (Q bit)");
            Assert.That(transport.Sent[0].Data, Is.EqualTo(new byte[] { 0x00, 2, pad.GetParameter(2), 3, pad.GetParameter(3) }));

            // SET AND READ (type 6): set 2 = 0, answer is a PARAMETER INDICATION with the new value.
            transport.Sent.Clear();
            pad.DeliverData(5, new byte[] { 0x06, 2, 0 }, true);
            Assert.That(pad.GetParameter(2), Is.EqualTo((byte)0));
            Assert.That(transport.Sent.Count, Is.EqualTo(1), "no answer to SET AND READ PARAMETERS");
            Assert.That(transport.Sent[0].Q, Is.True);
            Assert.That(transport.Sent[0].Data[0], Is.EqualTo((byte)0x00));
            pad.Dispose();
        }
    }
}
