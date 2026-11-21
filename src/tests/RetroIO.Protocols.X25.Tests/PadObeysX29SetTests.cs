//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using RetroIO.Protocols.X25.Modules;
using RetroIO.Protocols.X25.Services;

namespace RetroIO.Protocols.X25.Tests
{
    /// <summary>
    /// The PAD does what the host tells it with X.29 (step 3 of RetroX25 docs/altos-chat-port-plan.md). The Altos chat
    /// sends, with the Q bit, the set PAD message 02 02 01 03 02 0D 04 0F 01 10 7F 11 18 12 12 13 02: echo on, forward on CR, LF after CR in the
    /// echo, editing on, character delete DEL, line delete CAN, line display DC2, editing PAD service signals for display
    /// terminals (BS SP BS for a deleted character). Every C# PAD (the Desktop PAD window, the
    /// console and telnet PAD of RetroX25.Pad, the runtime's telnet PAD) is a <see cref="PadClient"/>, so these tests
    /// drive that class directly, with a transport that keeps what the PAD sends.
    /// </summary>
    /// <remarks>
    /// Spec clauses quoted are from the copies in the Specs\X25 folder: X.29 (12/97), X.28 (12/97), X.3 (03/00).
    /// </remarks>
    [TestFixture]
    public class PadObeysX29SetTests
    {
        private const ushort Lcn = 5;
        private const char Del = (char)127;
        private const char Can = (char)24;
        private const char Dc2 = (char)18;

        /// <summary>
        /// Keeps every packet the PAD sends: PAD messages and SendText data through <see cref="SendData"/>, typed data
        /// through <see cref="ForwardData"/>.
        /// </summary>
        private sealed class RecordingTransport : IPadTransport
        {
            public readonly List<(bool Q, byte[] Data)> Sent = new List<(bool, byte[])>();
            public readonly List<string> Forwarded = new List<string>();

            public bool PlaceCall(string calledAddress, string callingAddress, byte[]? callUserData) { return true; }

            public bool SendData(ushort lcn, byte[] data, bool mBit, bool qBit, bool dBit)
            {
                Sent.Add((qBit, data));
                return true;
            }

            public void ForwardData(ushort lcn, byte[] data, int length)
            {
                Forwarded.Add(Encoding.ASCII.GetString(data, 0, length));
            }

            public readonly List<ushort> Cleared = new List<ushort>();

            public void ClearCall(ushort lcn, byte cause, byte diagnostic) { Cleared.Add(lcn); }
        }

        /// <summary>One PAD in a call, with everything its terminal is shown in one string.</summary>
        private sealed class Rig : IDisposable
        {
            public readonly RecordingTransport Transport = new RecordingTransport();
            public readonly PadClient Pad;
            private readonly StringBuilder _screen = new StringBuilder();

            public Rig()
            {
                Pad = new PadClient(NullLogger.Instance, Transport);
                Pad.DataReceived += (s, e) => _screen.Append(e.Data);
                Pad.CharacterEcho += (s, e) => _screen.Append((char)e.Character);
                Pad.NotifyCallAccepted(Lcn);
                _screen.Clear(); // the COM service signal
            }

            public string Screen => _screen.ToString();

            public void ClearScreen() { _screen.Clear(); }

            public void Host(params byte[] padMessage) { Pad.DeliverData(Lcn, padMessage, true); }

            public void Type(string keys)
            {
                for (int i = 0; i < keys.Length; i++)
                {
                    Pad.OnKey(keys[i]);
                }
            }

            public void Dispose() { Pad.Dispose(); }
        }

        private static string Show(string s)
        {
            StringBuilder b = new StringBuilder();
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c < 32 || c == 127)
                {
                    b.Append("<").Append(((int)c).ToString("X2")).Append(">");
                }
                else
                {
                    b.Append(c);
                }
            }

            return b.ToString();
        }

        // ------------------------------------------------------------------ the set arrives

        /// <summary>
        /// X.29 Table 1, "Set / List of selected parameters with the given values / Set the selected parameters to the
        /// desired values / a) if no error is encountered: None", and 3.1.4 "The PAD will not return a parameter
        /// indication PAD message in response to a valid set PAD message received."
        /// </summary>
        [Test]
        public void TheChatSet_ChangesItsEightParameters_AndIsNotAnswered()
        {
            using Rig rig = new Rig();
            rig.Host(ChatProtocolService.SetPadMessage);

            Assert.That(rig.Pad.GetParameter(2), Is.EqualTo((byte)1), "echo");
            Assert.That(rig.Pad.GetParameter(3), Is.EqualTo((byte)2), "forward on CR");
            Assert.That(rig.Pad.GetParameter(13), Is.EqualTo((byte)4), "LF after CR in the echo");
            Assert.That(rig.Pad.GetParameter(15), Is.EqualTo((byte)1), "editing");
            Assert.That(rig.Pad.GetParameter(16), Is.EqualTo((byte)127), "character delete");
            Assert.That(rig.Pad.GetParameter(17), Is.EqualTo((byte)24), "line delete");
            Assert.That(rig.Pad.GetParameter(18), Is.EqualTo((byte)18), "line display");
            Assert.That(rig.Pad.GetParameter(19), Is.EqualTo((byte)2), "editing PAD service signals for display terminals");
            Assert.That(rig.Transport.Sent.Count, Is.EqualTo(0), "a valid set must not be answered");
        }

        /// <summary>
        /// X.29 3.1.2: "The PAD will also consider the arrival of such a PAD message as a data forwarding condition."
        /// Characters typed before the set go to the host when it arrives.
        /// </summary>
        [Test]
        public void TheArrivalOfASet_ForwardsWhatWasTyped()
        {
            using Rig rig = new Rig();
            rig.Host(ChatProtocolService.SetPadMessage);
            rig.Type("ab");
            Assert.That(rig.Transport.Forwarded.Count, Is.EqualTo(0), "forwarded before CR");

            rig.Host(0x02, 2, 1);
            Assert.That(rig.Transport.Forwarded, Is.EqualTo(new[] { "ab" }));
        }

        // ------------------------------------------------------------------ echo, forwarding, editing

        /// <summary>
        /// helo DEL lo CR after the chat's set: the host gets "hello" CR in one packet (X.3 3.3 value 2 forwards on CR),
        /// and the terminal is shown each typed character (X.28 4.10, parameter 2 = 1, echo mask 0 echoes all), and after
        /// the DEL the character deleted service signal of parameter 19. The chat's set makes parameter 19 = 2, editing PAD
        /// service signals for display terminals (X.3 3.19, "editing PAD service signals for display terminals -
        /// represented by decimal 2"), so the signal is BS SP BS (X.28 3.5.24, "Value 2: the characters 0/8 (BS) 2/0 (SP)
        /// 0/8 (BS) will be sent"): the deleted character is rubbed out on the screen.
        /// </summary>
        [Test]
        public void HeloDelLo_IsEchoedLocally_AndTheHostGetsHelloOnly()
        {
            using Rig rig = new Rig();
            rig.Host(ChatProtocolService.SetPadMessage);
            rig.Type("helo" + Del + "lo\r");

            Assert.That(rig.Transport.Forwarded, Is.EqualTo(new[] { "hello\r" }), "the host must get the edited line once");
            Assert.That(Show(rig.Screen), Is.EqualTo(Show("helo" + Del + "\b \blo\r\n")));
        }

        /// <summary>
        /// The Backspace key of a Windows console sends BS, not the DEL the chat sets as the delete character. BS deletes
        /// too (our choice, not X.3): ".n du", Backspace, "ude" names you "dude", and the screen rubs the character out.
        /// </summary>
        [Test]
        public void Backspace_DeletesLikeTheDeleteCharacter()
        {
            using Rig rig = new Rig();
            rig.Host(ChatProtocolService.SetPadMessage);
            rig.Type(".n du\bude\r");

            Assert.That(rig.Transport.Forwarded, Is.EqualTo(new[] { ".n dude\r" }));
            Assert.That(Show(rig.Screen), Is.EqualTo(Show(".n du\b\b \bude\r\n")));
        }

        /// <summary>
        /// EXIT at the * prompt during a call: the PAD clears the call first, then asks its program to end.
        /// </summary>
        [Test]
        public void Exit_ClearsTheCall_ThenAsksToEnd()
        {
            using Rig rig = new Rig();
            int exits = 0;
            int clearsWhenExitCame = -1;
            rig.Pad.ExitRequested += (s, e) =>
            {
                exits++;
                clearsWhenExitCame = rig.Transport.Cleared.Count;
            };

            rig.Type("\x10" + "EXIT\r"); // Ctrl+P, the PAD escape (X.3 parameter 1), then the command

            Assert.That(rig.Transport.Cleared, Is.EqualTo(new ushort[] { Lcn }), "the call must be cleared");
            Assert.That(exits, Is.EqualTo(1));
            Assert.That(clearsWhenExitCame, Is.EqualTo(1), "the clear must go out before the program is asked to end");
        }

        /// <summary>
        /// The user can still ask the PAD for the printing terminal signal: parameter 19 = 1 after the chat's set gives
        /// the backslash (X.28 3.5.24, "Value 1: the character 5/12 (\) will be sent").
        /// </summary>
        [Test]
        public void CharacterDelete_OnAPrintingTerminal_IsTheBackslash()
        {
            using Rig rig = new Rig();
            rig.Host(ChatProtocolService.SetPadMessage);
            rig.Host(0x02, 19, 1);
            rig.Type("ab" + Del + "c\r");

            Assert.That(rig.Transport.Forwarded, Is.EqualTo(new[] { "ac\r" }));
            Assert.That(Show(rig.Screen), Is.EqualTo(Show("ab" + Del + "\\c\r\n")));
        }

        /// <summary>
        /// A delete with nothing in the editing buffer deletes nothing, so no service signal (X.28 3.6.2.1: the signal is
        /// sent if "a character is removed from the editing buffer").
        /// </summary>
        [Test]
        public void CharacterDelete_OnAnEmptyLine_SendsNoSignal()
        {
            using Rig rig = new Rig();
            rig.Host(ChatProtocolService.SetPadMessage);
            rig.Type(Del + "x\r");

            Assert.That(rig.Transport.Forwarded, Is.EqualTo(new[] { "x\r" }));
            Assert.That(Show(rig.Screen), Is.EqualTo(Show(Del + "x\r\n")));
        }

        /// <summary>
        /// Line delete (CAN): the whole editing buffer goes (X.28 3.6.2.2, "causes the deletion of the current contents of
        /// the editing buffer"), the signal for parameter 19 = 1 is XXX and the format effector CR LF (X.28 3.5.25, "Value
        /// 1, 8, 32-126: the characters 5/8 (X), 5/8 (X), 5/8 (X) will be sent followed by the format effector"; 3.5.2).
        /// </summary>
        [Test]
        public void LineDelete_DropsTheLine_AndShowsXXX()
        {
            using Rig rig = new Rig();
            rig.Host(ChatProtocolService.SetPadMessage);
            rig.Host(0x02, 19, 1);
            rig.Type("abc" + Can);
            Assert.That(rig.Transport.Forwarded.Count, Is.EqualTo(0), "a deleted line reached the host");

            rig.Type("d\r");
            Assert.That(rig.Transport.Forwarded, Is.EqualTo(new[] { "d\r" }));
            Assert.That(Show(rig.Screen), Is.EqualTo(Show("abc" + Can + "XXX\r\nd\r\n")));
        }

        /// <summary>
        /// With parameter 9 = 3 the format effector after XXX is CR LF and three NULs (X.28 3.5.2: "If parameter 9 is
        /// not set to 0, then the number of padding characters transmitted after the character 0/10 (LF) will be equal
        /// to the current value of that parameter"; 3.5.20, the padding character is NUL). Added 2026-11-21.
        /// </summary>
        [Test]
        public void AnEchoedCarriageReturn_GetsParameter9Padding()
        {
            // X.28 4.12: parameter 9 padding "after each character 0/13 (CR) transmitted or echoed". Added 2026-11-21.
            using Rig rig = new Rig();
            rig.Host(ChatProtocolService.SetPadMessage);
            rig.Host(0x02, 9, 2);
            rig.ClearScreen();
            rig.Type("a\r");

            Assert.That(Show(rig.Screen), Is.EqualTo(Show("a\r\0\0\n")), "the set message turns on the LF after an echoed CR (13 = 4); parameter 14 is 0");
        }

        /// <summary>
        /// X.3 3.13 value 4 puts an LF after the echoed CR; X.28 4.16 pads that LF with parameter 14, after the CR's own
        /// parameter 9 padding. Added 2026-11-21.
        /// </summary>
        [Test]
        public void TheLinefeedInsertedInTheEcho_GetsParameter14Padding_AfterTheCarriageReturnsPadding()
        {
            using Rig rig = new Rig();
            rig.Host(ChatProtocolService.SetPadMessage);
            rig.Host(0x02, 9, 1, 13, 4, 14, 3);
            rig.ClearScreen();
            rig.Type("a\r");

            Assert.That(Show(rig.Screen), Is.EqualTo(Show("a\r\0\n\0\0\0")));
        }

        [Test]
        public void LineDelete_WithParameter9_PadsTheFormatEffector()
        {
            using Rig rig = new Rig();
            rig.Host(ChatProtocolService.SetPadMessage);
            rig.Host(0x02, 19, 1, 9, 3);
            rig.Type("abc" + Can);

            Assert.That(Show(rig.Screen), Is.EqualTo(Show("abc" + Can + "XXX\r\n\0\0\0")));
        }

        /// <summary>
        /// Line delete with parameter 19 = 2: BS SP BS once per graphic character deleted (X.28 3.5.25, "Value 2: the
        /// characters 0/8 (BS), 2/0 (SP), 0/8 (BS) will be sent the number of times equal to the number of graphic
        /// characters being deleted from the buffer").
        /// </summary>
        [Test]
        public void LineDelete_OnADisplayTerminal_RubsOutEveryCharacter()
        {
            using Rig rig = new Rig();
            rig.Host(ChatProtocolService.SetPadMessage);
            rig.Type("abc" + Can + "\r");

            Assert.That(rig.Transport.Forwarded, Is.EqualTo(new[] { "\r" }));
            Assert.That(Show(rig.Screen), Is.EqualTo(Show("abc" + Can + "\b \b\b \b\b \b\r\n")));
        }

        /// <summary>
        /// Line delete on an empty buffer sends no signal (X.28 3.6.2.2: "if parameter 6 and parameter 19 are not set to
        /// 0 and the editing buffer is not empty").
        /// </summary>
        [Test]
        public void LineDelete_OnAnEmptyLine_SendsNoSignal()
        {
            using Rig rig = new Rig();
            rig.Host(ChatProtocolService.SetPadMessage);
            rig.Type(Can.ToString());

            Assert.That(Show(rig.Screen), Is.EqualTo(Show(Can.ToString())));
        }

        /// <summary>
        /// Line display (DC2): a format effector, then the characters in the editing buffer (X.28 3.6.2.3, "causes the
        /// PAD to transmit to the start-stop mode DTE a format effector followed by the characters currently stored in
        /// the editing buffer"). The line is not sent, and stays editable.
        /// </summary>
        [Test]
        public void LineDisplay_ShowsTheLineAgain_AfterAFormatEffector()
        {
            using Rig rig = new Rig();
            rig.Host(ChatProtocolService.SetPadMessage);
            rig.Type("ab" + Del + "c" + Dc2);
            Assert.That(rig.Transport.Forwarded.Count, Is.EqualTo(0));

            rig.Type("d\r");
            Assert.That(rig.Transport.Forwarded, Is.EqualTo(new[] { "acd\r" }));
            Assert.That(Show(rig.Screen), Is.EqualTo(Show("ab" + Del + "\b \bc" + Dc2 + "\r\nacd\r\n")));
        }

        /// <summary>
        /// Parameter 15 = 0: no editing in the data transfer state (X.3 3.15; X.28 3.6, "When the value of parameter 15
        /// is set to 0, no editing is available during the data transfer state"). DEL, CAN and DC2 are then data.
        /// </summary>
        [Test]
        public void EditingOff_DelCanAndDc2_AreDataForTheHost()
        {
            using Rig rig = new Rig();
            rig.Host(ChatProtocolService.SetPadMessage);
            rig.Host(0x02, 15, 0);
            rig.Type("ab" + Del + Can + Dc2 + "\r");

            Assert.That(rig.Transport.Forwarded, Is.EqualTo(new[] { "ab" + Del + Can + Dc2 + "\r" }));
        }

        /// <summary>
        /// Echo off (parameter 2 = 0): nothing typed is shown (X.28 4.10 applies only "If parameter 2 is set to 1").
        /// </summary>
        [Test]
        public void EchoOff_ShowsNothing()
        {
            using Rig rig = new Rig();
            rig.Host(ChatProtocolService.SetPadMessage);
            rig.Host(0x02, 2, 0);
            rig.Type("hi\r");

            Assert.That(rig.Transport.Forwarded, Is.EqualTo(new[] { "hi\r" }));
            Assert.That(rig.Screen, Is.EqualTo(string.Empty));
        }

        /// <summary>
        /// Echo mask, X.3 3.20: value 1 "no echo of character CR"; value 64 "no echo of editing characters as designated
        /// by parameters 16, 17 and 18". The masked characters still do their work.
        /// </summary>
        [Test]
        public void EchoMask_HidesCr_AndTheEditingCharacters()
        {
            using Rig rig = new Rig();
            rig.Host(ChatProtocolService.SetPadMessage);
            rig.Host(0x02, 20, 65);
            rig.Type("ab" + Del + "\r");

            Assert.That(rig.Transport.Forwarded, Is.EqualTo(new[] { "a\r" }));
            Assert.That(Show(rig.Screen), Is.EqualTo(Show("ab\b \b")));
        }

        /// <summary>
        /// Parameter 2 = 32 to 126, "scrambled echo character": "The decimal value represents the character to be
        /// transmitted back to the start-stop mode DTE" (X.3 3.2, Note 2).
        /// </summary>
        [Test]
        public void ScrambledEcho_ShowsTheGivenCharacterInstead()
        {
            using Rig rig = new Rig();
            rig.Host(ChatProtocolService.SetPadMessage);
            rig.Host(0x02, 2, (byte)'*');
            rig.Type("pw\r");

            Assert.That(rig.Transport.Forwarded, Is.EqualTo(new[] { "pw\r" }));
            Assert.That(rig.Screen, Is.EqualTo("***"));
        }

        /// <summary>
        /// Parameter 13 value 4: "insert linefeed after each carriage return in the echo stream to the start-stop mode
        /// DTE" (X.3 3.13). The LF is the PAD's, it does not go to the host.
        /// </summary>
        [Test]
        public void LinefeedInsertionInTheEcho_AddsLfAfterTheEchoedCr()
        {
            using Rig rig = new Rig();
            rig.Host(ChatProtocolService.SetPadMessage);
            rig.Host(0x02, 13, 4);
            rig.Type("x\r");

            Assert.That(rig.Transport.Forwarded, Is.EqualTo(new[] { "x\r" }));
            Assert.That(Show(rig.Screen), Is.EqualTo(Show("x\r\n")));
        }

        /// <summary>
        /// X.3 3.15: editing "suspends ... data forwarding on full packet until the editing buffer is full"; X.28 3.6.1.3:
        /// "Whenever more than enough data has been received to fill the editing buffer, one full packet will be
        /// forwarded. The remaining characters will be placed in the beginning of the editing buffer". The buffer here is
        /// 128 characters (X.28 3.6.1.2 allows "a maximum of 128 octets"), the packet 128 octets.
        /// </summary>
        [Test]
        public void WithEditing_AFullBufferForwardsOnePacket_AndKeepsTheRestEditable()
        {
            using Rig rig = new Rig();
            rig.Host(ChatProtocolService.SetPadMessage);
            rig.Type(new string('a', 128));
            Assert.That(rig.Transport.Forwarded.Count, Is.EqualTo(0), "128 characters fill the buffer but are not more than enough");

            rig.Type("bc" + Del + "\r");
            Assert.That(rig.Transport.Forwarded, Is.EqualTo(new[] { new string('a', 128), "b\r" }));
        }

        /// <summary>
        /// The echo mask is about the echo of what is typed (X.3 3.20, X.28 4.10); the host's own text reaches the
        /// terminal whole, CR and LF included.
        /// </summary>
        [Test]
        public void TheEchoMask_DoesNotTouchTheHostsText()
        {
            using Rig rig = new Rig();
            rig.Host(0x02, 20, 255);
            rig.Pad.DeliverData(Lcn, Encoding.ASCII.GetBytes("a\r\nb"), false);

            Assert.That(Show(rig.Screen), Is.EqualTo(Show("a\r\nb")));
        }

        /// <summary>
        /// Parameter 13 value 1, "insert linefeed after each carriage return in the data stream to the start-stop DTE"
        /// (X.3 3.13): on the host's text, not on the echo and not on what goes to the host.
        /// </summary>
        [Test]
        public void LinefeedInsertionValueOne_IsOnTheHostsTextOnly()
        {
            using Rig rig = new Rig();
            rig.Host(ChatProtocolService.SetPadMessage);
            rig.Host(0x02, 13, 1);
            rig.Pad.DeliverData(Lcn, Encoding.ASCII.GetBytes("a\r"), false);
            rig.Type("x\r");

            Assert.That(Show(rig.Screen), Is.EqualTo(Show("a\r\nx\r")));
            Assert.That(rig.Transport.Forwarded, Is.EqualTo(new[] { "x\r" }));
        }

        // ------------------------------------------------------------------ after the call

        /// <summary>
        /// A PAD in command mode, with nothing placed yet, and its screen.
        /// </summary>
        private static (PadClient Pad, RecordingTransport Transport, StringBuilder Screen) IdlePad()
        {
            RecordingTransport transport = new RecordingTransport();
            PadClient pad = new PadClient(NullLogger.Instance, transport);
            StringBuilder screen = new StringBuilder();
            pad.DataReceived += (s, e) => screen.Append(e.Data);
            pad.CharacterEcho += (s, e) => screen.Append((char)e.Character);
            return (pad, transport, screen);
        }

        private static void Type(PadClient pad, string keys)
        {
            for (int i = 0; i < keys.Length; i++)
            {
                pad.OnKey(keys[i]);
            }
        }

        /// <summary>
        /// Our choice (Ronny, 2026-11-18; no X.3/X.28/X.29 text covers it): when a call ends the PAD goes back to the
        /// parameters it had when the call was placed. The host set 15 = 1 and 13 = 4 during the call; after the clear
        /// both are back, and PAR shows it.
        /// </summary>
        [Test]
        public void AfterAClear_TheHostsSetIsGone_AndParShowsThePreCallValues()
        {
            (PadClient pad, RecordingTransport _, StringBuilder screen) = IdlePad();
            byte p13 = pad.GetParameter(13);
            byte p15 = pad.GetParameter(15);
            Assert.That(p13, Is.EqualTo((byte)0));
            Assert.That(p15, Is.EqualTo((byte)0));

            Assert.That(pad.Connect("26245890040004", "26240001"), Is.True);
            pad.NotifyCallAccepted(Lcn);
            pad.DeliverData(Lcn, new byte[] { 0x02, 15, 1, 13, 4 }, true);
            Assert.That(pad.GetParameter(15), Is.EqualTo((byte)1));
            Assert.That(pad.GetParameter(13), Is.EqualTo((byte)4));

            pad.DeliverClear(Lcn, 0, 0);
            Assert.That(pad.GetParameter(13), Is.EqualTo(p13));
            Assert.That(pad.GetParameter(15), Is.EqualTo(p15));

            screen.Clear();
            Type(pad, "PAR\r");
            Assert.That(screen.ToString(), Does.Contain("13:0").And.Contain("15:0"), screen.ToString());
            pad.Dispose();
        }

        /// <summary>
        /// A value the person SET at the prompt before the call survives the clear; a value the host changed does not.
        /// </summary>
        [Test]
        public void AValueSetBeforeTheCall_SurvivesTheClear()
        {
            (PadClient pad, RecordingTransport _, StringBuilder _) = IdlePad();
            Type(pad, "SET 13:4\r");
            Assert.That(pad.GetParameter(13), Is.EqualTo((byte)4), "SET at the prompt");

            Assert.That(pad.Connect("26245890040004", "26240001"), Is.True);
            pad.NotifyCallAccepted(Lcn);
            pad.DeliverData(Lcn, new byte[] { 0x02, 13, 0, 15, 1 }, true);
            Assert.That(pad.GetParameter(13), Is.EqualTo((byte)0));

            pad.Disconnect();
            Assert.That(pad.GetParameter(13), Is.EqualTo((byte)4), "the person's own value");
            Assert.That(pad.GetParameter(15), Is.EqualTo((byte)0), "the host's value is dropped");
            pad.Dispose();
        }

        // ------------------------------------------------------------------ read, set and read, errors

        /// <summary>
        /// Read: X.29 3.1.3, "The PAD will respond to a valid read or set and read PAD message by transmitting a parameter
        /// indication PAD message"; 4.4.5.3, "Value fields in read PAD messages are coded as all binary 0s" - a read
        /// carries reference and value pairs like a set.
        /// </summary>
        [Test]
        public void Read_IsAnsweredWithTheCurrentValues()
        {
            using Rig rig = new Rig();
            rig.Host(ChatProtocolService.SetPadMessage);
            rig.Host(0x04, 2, 0, 15, 0, 16, 0);

            Assert.That(rig.Transport.Sent.Count, Is.EqualTo(1));
            Assert.That(rig.Transport.Sent[0].Q, Is.True, "PAD messages go with Q = 1 (X.29 1.5.2)");
            Assert.That(rig.Transport.Sent[0].Data, Is.EqualTo(new byte[] { 0x00, 2, 1, 15, 1, 16, 127 }));
        }

        /// <summary>
        /// Read with no parameter field: X.29 Table 1, "Read / None / None / List all implemented Recommendation X.3
        /// parameters with their current values". This PAD has parameters 1 to 22.
        /// </summary>
        [Test]
        public void ReadWithoutParameters_ListsAllTwentyTwo()
        {
            using Rig rig = new Rig();
            rig.Host(ChatProtocolService.SetPadMessage);
            rig.Host(0x04);

            Assert.That(rig.Transport.Sent.Count, Is.EqualTo(1));
            byte[] answer = rig.Transport.Sent[0].Data;
            Assert.That(answer.Length, Is.EqualTo(1 + 22 * 2));
            Assert.That(answer[0], Is.EqualTo((byte)0x00));
            for (int i = 0; i < 22; i++)
            {
                byte reference = answer[1 + i * 2];
                Assert.That(reference, Is.EqualTo((byte)(i + 1)));
                Assert.That(answer[2 + i * 2], Is.EqualTo(rig.Pad.GetParameter(reference)), "parameter " + reference);
            }
        }

        /// <summary>
        /// Set and read: X.29 Table 1, "Set the selected parameters to the given values / List of these parameters with
        /// their new current values".
        /// </summary>
        [Test]
        public void SetAndRead_SetsAndAnswersWithTheNewValues()
        {
            using Rig rig = new Rig();
            rig.Host(0x06, 2, 0, 15, 1);

            Assert.That(rig.Pad.GetParameter(2), Is.EqualTo((byte)0));
            Assert.That(rig.Pad.GetParameter(15), Is.EqualTo((byte)1));
            Assert.That(rig.Transport.Sent.Count, Is.EqualTo(1));
            Assert.That(rig.Transport.Sent[0].Data, Is.EqualTo(new byte[] { 0x00, 2, 0, 15, 1 }));
        }

        /// <summary>
        /// A set with a value the PAD cannot take: X.29 Table 1, "b) if the PAD fails to modify the values of some
        /// parameters / b) List of these invalid parameters", with "the error bit ... set and the value field ... coded as
        /// described in Table 3" (bit 8 of the reference, 4.4.5.2; Table 3 code 2, "The parameter value is invalid or has
        /// not been implemented in the PAD"). The valid parameters of the same message are set (3.5.1).
        /// </summary>
        [Test]
        public void SetWithAnInvalidValue_IsAnsweredWithTheErrorBit_AndTheValidOnesAreSet()
        {
            using Rig rig = new Rig();
            rig.Host(0x02, 2, 0, 15, 5);

            Assert.That(rig.Pad.GetParameter(2), Is.EqualTo((byte)0), "the valid parameter of the message");
            Assert.That(rig.Pad.GetParameter(15), Is.EqualTo((byte)0), "15 = 5 does not exist");
            Assert.That(rig.Transport.Sent.Count, Is.EqualTo(1));
            Assert.That(rig.Transport.Sent[0].Data, Is.EqualTo(new byte[] { 0x00, 0x80 | 15, 2 }));
        }

        /// <summary>
        /// An unknown reference: Table 3 code 1, "The parameter reference does not exist or has not been implemented in the
        /// PAD". Parameter 11 is read only (X.3 3.11, "a read-only parameter and cannot be changed by either of the DTEs"):
        /// Table 3 code 4, "The parameter is read-only".
        /// </summary>
        [Test]
        public void SetOfAnUnknownOrReadOnlyParameter_IsAnsweredWithTheReason()
        {
            using Rig rig = new Rig();
            byte speed = rig.Pad.GetParameter(11);
            rig.Host(0x02, 80, 1, 11, 3);

            Assert.That(rig.Pad.GetParameter(11), Is.EqualTo(speed));
            Assert.That(rig.Transport.Sent.Count, Is.EqualTo(1));
            Assert.That(rig.Transport.Sent[0].Data, Is.EqualTo(new byte[] { 0x00, 0x80 | 80, 1, 0x80 | 11, 4 }));
        }

        /// <summary>
        /// "In PAD messages received by the PAD, bit 8 of each octet will be ignored" (X.29 4.4.5.2): reference 0x82 is
        /// parameter 2.
        /// </summary>
        [Test]
        public void BitEightOfAReceivedReference_IsIgnored()
        {
            using Rig rig = new Rig();
            rig.Host(0x02, 0x82, 0);

            Assert.That(rig.Pad.GetParameter(2), Is.EqualTo((byte)0));
            Assert.That(rig.Transport.Sent.Count, Is.EqualTo(0));
        }

        /// <summary>
        /// A set with no parameter field: X.29 Table 1, "Set / None / Reset all implemented Recommendation X.3 parameters
        /// to their initial values corresponding to the initial profile / None".
        /// </summary>
        [Test]
        public void SetWithoutParameters_GoesBackToTheInitialProfile()
        {
            using Rig rig = new Rig();
            byte echo = rig.Pad.GetParameter(2);
            byte editing = rig.Pad.GetParameter(15);
            rig.Host(ChatProtocolService.SetPadMessage);
            rig.Host(0x02, 2, 0);
            rig.Host(0x02);

            Assert.That(rig.Pad.GetParameter(2), Is.EqualTo(echo));
            Assert.That(rig.Pad.GetParameter(15), Is.EqualTo(editing));
            Assert.That(rig.Transport.Sent.Count, Is.EqualTo(0));
        }

        /// <summary>
        /// A parameter field that is not whole pairs: error PAD message (X.29 3.5.2 c, "if the parameter field following a
        /// recognizable message code has an invalid format"), octet 2 = 00000100 (Table 4 case c), octet 3 the message
        /// code of the received message (4.4.6.2). Nothing is set.
        /// </summary>
        [Test]
        public void ASetWithAHalfPair_IsAnsweredWithAnErrorMessage()
        {
            using Rig rig = new Rig();
            byte echo = rig.Pad.GetParameter(2);
            rig.Host(0x02, 2);

            Assert.That(rig.Pad.GetParameter(2), Is.EqualTo(echo));
            Assert.That(rig.Transport.Sent.Count, Is.EqualTo(1));
            Assert.That(rig.Transport.Sent[0].Data, Is.EqualTo(new byte[] { 0x05, 0x04, 0x02 }));
        }

        /// <summary>
        /// A parameter indication sent to a PAD was never asked for: X.29 3.5.2 d, "if the PAD receives an unsolicited
        /// parameter indication PAD message", Table 4 case e (00001000). It changes nothing.
        /// </summary>
        [Test]
        public void AnUnsolicitedParameterIndication_IsAnError_AndChangesNothing()
        {
            using Rig rig = new Rig();
            byte echo = rig.Pad.GetParameter(2);
            rig.Host(0x00, 2, (byte)(echo == 0 ? 1 : 0));

            Assert.That(rig.Pad.GetParameter(2), Is.EqualTo(echo));
            Assert.That(rig.Transport.Sent.Count, Is.EqualTo(1));
            Assert.That(rig.Transport.Sent[0].Data, Is.EqualTo(new byte[] { 0x05, 0x08, 0x00 }));
        }

        /// <summary>
        /// An unknown message code: X.29 3.5.2 a, "if the PAD receives an unrecognizable message code", Table 4 case b
        /// (00000010), octet 3 the code received.
        /// </summary>
        [Test]
        public void AnUnknownMessageCode_IsAnsweredWithAnErrorMessage()
        {
            using Rig rig = new Rig();
            rig.Host(0x0C);

            Assert.That(rig.Transport.Sent.Count, Is.EqualTo(1));
            Assert.That(rig.Transport.Sent[0].Data, Is.EqualTo(new byte[] { 0x05, 0x02, 0x0C }));
        }

        /// <summary>
        /// A reselection request (code 0x07) or a reselection with TOA/NPI (0x08) is answered as an invalid message,
        /// because this PAD does not do reselection.
        /// </summary>
        /// <remarks>
        /// X.29 (12/97) 3.6 Note 2: reselection "is an optional feature of the PAD. PADs which do not implement this
        /// feature will consider reselection and reselection with TOA/NPI PAD messages as invalid"; 3.5.2 a) answers an
        /// invalid message code with an error PAD message, Table 4 case b (00000010), octet 3 the code received. Table 4
        /// case g (unauthorized reselection) is only for a PAD that does reselect (3.6 a to c). Until 2026-11-21 the
        /// PAD sent nothing and only logged it (RetroIO BUGS 46, RetroX25 PLAN H2).
        /// </remarks>
        /// <param name="code">
        /// The message code: 0x07 reselection, 0x08 reselection with TOA/NPI.
        /// </param>
        [TestCase((byte)0x07)]
        [TestCase((byte)0x08)]
        public void AReselectionRequest_IsAnsweredAsAnInvalidMessage(byte code)
        {
            using Rig rig = new Rig();
            rig.Host(code);

            Assert.That(rig.Transport.Sent.Count, Is.EqualTo(1));
            Assert.That(rig.Transport.Sent[0].Data, Is.EqualTo(new byte[] { 0x05, 0x02, code }));
        }
    }
}
