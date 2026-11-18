//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

// Tests of the Altos CHAT 3.1 engine, driven directly with no X.25 underneath.
//
// The original C source (chat.c, chat.h, serv.c, README, README.linux, INSTALL) is published at
// https://archive.org/details/altoschat and is the authority for every text, order and rule tested here.
// It is not kept in this repository; "CHAT 3.1 serv.c:123" means line 123 of that file.
// The test numbers are the "Tests" list of RetroX25 docs/altos-chat-port-plan.md.

using System.Diagnostics;
using System.Text;
using NUnit.Framework;
using RetroIO.Protocols.X25.Protocols;

namespace RetroIO.Protocols.X25.Tests
{
    /// <summary>
    /// The Altos CHAT 3.1 engine: what every user sees, byte for byte, and the log.
    /// </summary>
    [TestFixture]
    public class AltosChatEngineTests
    {
        private const string Banner =
            "Welcome to the original, evergreen\r\n" +
            "Conference-utility \"CHAT\"  ver. 3.1\r\n" +
            "Copyright (C) 1987 Hans Korneder, Muenchen\r\n";

        private const string Waiting = "Waiting for connection.\r\n";
        private const string Established = "Connection established.\r\nGet help with:    .h\r\n";

        private const string Help =
            ".x           exit this program\r\n" +
            ".s           get the list of users on the chat\r\n" +
            ".n name      change your nickname\r\n" +
            ".h           get this page of text\r\n" +
            ".w           get a list of users on the system\r\n" +
            ".b           bell all users\r\n" +
            ".m           send mail to the sysop\r\n" +
            ".c #         switch to channel no <#>\r\n" +
            ".p passwd    set a passwd for a channel\r\n" +
            "!# text      send a private message to user <#>\r\n";

        private const string SHeader = "No Chan       From             User     Called\r\n";

        // 29 May 1987, the date of CHAT 3.1 (README revision history); a Friday.
        private static readonly DateTime T0 = new DateTime(1987, 5, 29, 12, 0, 0);

        private AltosChatActionList _out = null!;
        private AltosChatEngine _chat = null!;

        [SetUp]
        public void Setup()
        {
            Make(null);
        }

        private void Make(Action<AltosChatSettings>? change)
        {
            var s = new AltosChatSettings
            {
                WaitPause = TimeSpan.Zero,
                OutputLimitBytes = int.MaxValue,
            };
            change?.Invoke(s);
            _out = new AltosChatActionList();
            _chat = new AltosChatEngine(s, _out, _out);
            _chat.Start(T0);
        }

        private void Join(int session, string from = "20200001")
        {
            _chat.Connect(session, "guest", from);
        }

        /// <summary>Join and take a nickname, then forget all output so far.</summary>
        private void JoinAs(int session, string nick, string from = "20200001")
        {
            Join(session, from);
            Type(session, ".n " + nick);
            _out.Reset();
        }

        private void Type(int session, string line)
        {
            _chat.Input(session, Encoding.ASCII.GetBytes(line + "\r"));
        }

        private string Seen(int session) => _out.TextFor(session);

        // ---- 1 ----

        [Test]
        public void T01_ConnectText_ExactBytesAndOrder_AfterThePause()
        {
            Make(s => s.WaitPause = TimeSpan.FromSeconds(3));
            _chat.Tick(T0);
            Join(1);
            Assert.That(Seen(1), Is.EqualTo(Banner + Waiting));

            _chat.Tick(T0.AddMilliseconds(2999));
            Assert.That(Seen(1), Is.EqualTo(Banner + Waiting));

            _chat.Tick(T0.AddSeconds(3));
            Assert.That(Seen(1), Is.EqualTo(Banner + Waiting + Established + "+++guest+++\r\n"));
        }

        [Test]
        public void T01b_InputTypedWhileWaiting_IsHandledAfterTheJoin()
        {
            Make(s => s.WaitPause = TimeSpan.FromSeconds(3));
            _chat.Tick(T0);
            Join(1);
            Type(1, ".h");
            Assert.That(Seen(1), Is.EqualTo(Banner + Waiting));
            _chat.Tick(T0.AddSeconds(3));
            Assert.That(Seen(1), Is.EqualTo(Banner + Waiting + Established + "+++guest+++\r\n" + Help));
        }

        [Test]
        public void T01c_ClearedWhileWaiting_NobodyIsTold()
        {
            Make(s => s.WaitPause = TimeSpan.FromSeconds(3));
            _chat.Tick(T0);
            Join(1);
            Join(2);
            _chat.Tick(T0.AddSeconds(1));
            _chat.Disconnect(2);
            _chat.Tick(T0.AddSeconds(5));
            Assert.That(Seen(1), Is.EqualTo(Banner + Waiting + Established + "+++guest+++\r\n"));
            Assert.That(Seen(2), Is.EqualTo(Banner + Waiting));
        }

        // ---- 2 ----

        [Test]
        public void T02_Help_ByteForByte_AndDotHello()
        {
            JoinAs(1, "alice");
            Type(1, ".h");
            Assert.That(Seen(1), Is.EqualTo(Help));
            _out.Reset();
            Type(1, ".hello");
            Assert.That(Seen(1), Is.EqualTo(Help));
        }

        // ---- 3 ----

        [TestCase(".x")]
        [TestCase(".q")]
        [TestCase(".xyz")]
        public void T03_Leave_NothingPrinted_OthersSeeTheNick(string cmd)
        {
            JoinAs(1, "alice");
            JoinAs(2, "bob");
            Type(1, cmd);
            Assert.That(Seen(1), Is.EqualTo(""));
            Assert.That(_out.ClearCount(1), Is.EqualTo(1));
            Assert.That(Seen(2), Is.EqualTo("---alice---\r\n"));
        }

        [Test]
        public void T03b_UpperCaseX_IsText()
        {
            JoinAs(1, "alice");
            JoinAs(2, "bob");
            Type(1, ".X");
            Assert.That(_out.ClearCount(1), Is.EqualTo(0));
            Assert.That(Seen(2), Is.EqualTo("alice says: .X\r\n"));
        }

        // ---- 4 ----

        [Test]
        public void T04_Text_ReachesOthersOnTheSameChannelOnly_NotTheSender()
        {
            JoinAs(1, "alice");
            JoinAs(2, "bob");
            JoinAs(3, "carol");
            Type(3, ".c 5");
            _out.Reset();
            Type(1, "hello there");
            Assert.That(Seen(1), Is.EqualTo(""));
            Assert.That(Seen(2), Is.EqualTo("alice says: hello there\r\n"));
            Assert.That(Seen(3), Is.EqualTo(""));
        }

        // ---- 5 ----

        [Test]
        public void T05_EmptyLineIgnored_LineOfSpacesSent()
        {
            JoinAs(1, "alice");
            JoinAs(2, "bob");
            Type(1, "");
            Assert.That(Seen(2), Is.EqualTo(""));
            Type(1, "   ");
            Assert.That(Seen(2), Is.EqualTo("alice says:    \r\n"));
        }

        // ---- 6 ----

        [Test]
        public void T06_118IsOneMessage_BugB_NothingAfterIt()
        {
            JoinAs(1, "alice");
            JoinAs(2, "bob");
            string a118 = new string('a', 118);
            Type(1, a118);
            // Bug b (CHAT 3.1 chat.c:169): the original left no end marker after 118 characters and the server read
            // on into whatever followed. Fixed: exactly the 118 characters, and the CR after them is an empty line.
            Assert.That(Seen(2), Is.EqualTo("alice says: " + a118 + "\r\n"));
        }

        [Test]
        public void T06b_200IsTwoMessages()
        {
            JoinAs(1, "alice");
            JoinAs(2, "bob");
            Type(1, new string('b', 200));
            Assert.That(Seen(2), Is.EqualTo(
                "alice says: " + new string('b', 118) + "\r\n" +
                "alice says: " + new string('b', 82) + "\r\n"));
        }

        [Test]
        public void T06c_RemainderStartingDotX_Leaves()
        {
            JoinAs(1, "alice");
            JoinAs(2, "bob");
            Type(1, new string('c', 118) + ".x and more");
            Assert.That(Seen(2), Is.EqualTo("alice says: " + new string('c', 118) + "\r\n---alice---\r\n"));
            Assert.That(_out.ClearCount(1), Is.EqualTo(1));
        }

        [Test]
        public void T06d_118EndingInSpaces_TrailingSpacesStripped()
        {
            // CHAT 3.1 serv.c:273 blankstrip starts at the 118th byte; only a full 118-character line reaches
            // its own text, so only then are trailing spaces removed (shorter lines keep theirs, test 5).
            JoinAs(1, "alice");
            JoinAs(2, "bob");
            Type(1, new string('d', 100) + new string(' ', 18));
            Assert.That(Seen(2), Is.EqualTo("alice says: " + new string('d', 100) + "\r\n"));
            _out.Reset();
            Type(1, new string(' ', 118));
            Assert.That(Seen(2), Is.EqualTo(""));
        }

        [Test]
        public void T06e_LfAlsoEndsALine_CrLfGivesNoExtraMessage()
        {
            JoinAs(1, "alice");
            JoinAs(2, "bob");
            _chat.Input(1, Encoding.ASCII.GetBytes("one\r\ntwo\nthr"));
            _chat.Input(1, Encoding.ASCII.GetBytes("ee\r"));
            Assert.That(Seen(2), Is.EqualTo("alice says: one\r\nalice says: two\r\nalice says: three\r\n"));
        }

        // ---- 7 ----

        [Test]
        public void T07_ControlCharactersAndTopBitRemoved()
        {
            JoinAs(1, "alice");
            JoinAs(2, "bob");
            // 0x01 and 0x7F dropped, 0xE9 is 'i' with the top bit, 0x8D is CR with the top bit and ends the line.
            _chat.Input(1, new byte[] { (byte)'h', 0x01, (byte)'i', 0x7F, 0xE9, 0x07, 0x8D });
            Assert.That(Seen(2), Is.EqualTo("alice says: hii\r\n"));
        }

        // ---- 8 ----

        [Test]
        public void T08_Nick_AnnouncedOnTheSendersChannel_SenderIncluded()
        {
            JoinAs(1, "alice");
            JoinAs(2, "bob");
            JoinAs(3, "carol");
            Type(3, ".c 2");
            _out.Reset();
            Type(1, ".n alex");
            Assert.That(Seen(1), Is.EqualTo(">>>alice>>>alex>>>\r\n"));
            Assert.That(Seen(2), Is.EqualTo(">>>alice>>>alex>>>\r\n"));
            Assert.That(Seen(3), Is.EqualTo(""));
        }

        [Test]
        public void T08b_Nick_CutTo32_AnnouncementShowsAll()
        {
            JoinAs(1, "alice");
            JoinAs(2, "bob");
            string longName = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789xyz";
            Type(1, ".n " + longName);
            Assert.That(Seen(2), Is.EqualTo(">>>alice>>>" + longName + ">>>\r\n"));
            _out.Reset();
            Type(1, "hi");
            Assert.That(Seen(2), Is.EqualTo(longName.Substring(0, 32) + " says: hi\r\n"));
        }

        [Test]
        public void T08c_EmptyAndDuplicateNick_Allowed_DotNfooIsText()
        {
            JoinAs(1, "alice");
            JoinAs(2, "bob");
            Type(1, ".n ");
            Assert.That(Seen(2), Is.EqualTo(">>>alice>>>>>>\r\n"));
            _out.Reset();
            Type(1, "hi");
            Assert.That(Seen(2), Is.EqualTo(" says: hi\r\n"));
            _out.Reset();
            Type(1, ".n bob");
            Type(1, ".nfoo");
            Assert.That(Seen(2), Is.EqualTo(">>>>>>bob>>>\r\nbob says: .nfoo\r\n"));
        }

        // ---- 9 ----

        [Test]
        public void T09_S_HeaderAndRowsExact_EveryChannel()
        {
            _chat.Connect(1, "guest", "26245890040004");
            Type(1, ".n alice");
            _chat.Connect(2, "verylonglogin", "12345678901234567890");
            Type(2, ".c 77");
            _chat.Connect(3, "x", null);
            _out.Reset();
            Type(1, ".s");
            Assert.That(Seen(1), Is.EqualTo(
                SHeader +
                " 0          0 26245890040004   guest    alice\r\n" +
                " 1         77 1234567890123456 verylong guest\r\n" +
                " 2          0 somewhere        x        guest\r\n"));
            _out.Reset();
            Type(1, ".sxyz");
            Assert.That(Seen(1), Does.StartWith(SHeader));
        }

        [Test]
        public void T09b_So_GivesTheDisabledText()
        {
            JoinAs(1, "alice");
            Type(1, ".so file");
            Assert.That(Seen(1), Is.EqualTo("Function is being disabled by default\r\n"));
        }

        [Test]
        public void T09c_EmptyFrom_IsLocalhost()
        {
            // CHAT 3.1 chat.c:326-328: no origin is "somewhere", an empty one "localhost", one starting ':' "Xlocalhost".
            _chat.Connect(1, "guest", "");
            _chat.Connect(2, "guest", ":0");
            _out.Reset();
            Type(1, ".s");
            Assert.That(Seen(1), Is.EqualTo(
                SHeader +
                " 0          0 localhost        guest    guest\r\n" +
                " 1          0 Xlocalhost       guest    guest\r\n"));
        }

        // ---- 10 ----

        [Test]
        public void T10_Channel_OldAndNewTold_SameSilent()
        {
            JoinAs(1, "alice");
            JoinAs(2, "bob");
            JoinAs(3, "carol");
            Type(3, ".c5");
            _out.Reset();
            Type(1, ".c5");
            Assert.That(Seen(1), Is.EqualTo(">>>alice>>> chan 5\r\n"));
            Assert.That(Seen(2), Is.EqualTo(">>>alice>>> chan 5\r\n"));
            Assert.That(Seen(3), Is.EqualTo(">>>alice>>> chan 5\r\n"));
            _out.Reset();
            Type(1, ".c 5");
            Assert.That(Seen(1) + Seen(2) + Seen(3), Is.EqualTo(""));
        }

        [TestCase(".c-7", 7)]
        [TestCase(".c 12", 12)]
        [TestCase(".c+3", 3)]
        [TestCase(".c9,pw", 9)]
        public void T10b_Channel_NumberParsing(string cmd, long chan)
        {
            JoinAs(1, "alice");
            Type(1, cmd);
            Assert.That(Seen(1), Is.EqualTo(">>>alice>>> chan " + chan + "\r\n"));
        }

        [TestCase(".c")]
        [TestCase(".cool")]
        public void T10c_Channel_NoNumberMeansZero(string cmd)
        {
            JoinAs(1, "alice");
            JoinAs(2, "bob");
            Type(1, ".c4");
            _out.Reset();
            Type(1, cmd);
            Assert.That(Seen(1), Is.EqualTo(">>>alice>>> chan 0\r\n"));
            Assert.That(Seen(2), Is.EqualTo(">>>alice>>> chan 0\r\n"));
        }

        [Test]
        public void T10d_BugD_MostNegativeChannel_StaysSeparate()
        {
            // CHAT 3.1 serv.c:338: -(-2147483648) stays negative in a 32-bit long, and a negative channel means
            // "every channel" to write_msg (serv.c:198), so that user's text reached everyone. Fixed.
            JoinAs(1, "alice");
            JoinAs(2, "bob");
            Type(1, ".c-2147483648");
            _out.Reset();
            Type(1, "hello");
            Assert.That(Seen(2), Is.EqualTo(""));
            Type(1, ".s");
            Assert.That(Seen(1), Does.Contain(" 0 2147483648 20200001"));
        }

        // ---- 11 ----

        [Test]
        public void T11_Protect_Channel0_NotAlone_Success()
        {
            JoinAs(1, "alice");
            JoinAs(2, "bob");
            Type(1, ".p secret");
            Assert.That(Seen(1), Is.EqualTo("Channel 0 cannot be protected.\r\n"));
            Type(1, ".c3");
            Type(2, ".c3");
            _out.Reset();
            Type(1, ".p secret");
            Assert.That(Seen(1), Is.EqualTo("You are not alone on the channel.\r\n"));
            Type(2, ".c0");
            _out.Reset();
            Type(1, ".p secret");
            Assert.That(Seen(1) + Seen(2), Is.EqualTo(""));
        }

        [Test]
        public void T11b_Protect_NoComma_Wrong_Right()
        {
            JoinAs(1, "alice");
            JoinAs(2, "bob");
            Type(1, ".c3");
            Type(1, ".p secret");
            _out.Reset();
            Type(2, ".c3");
            Assert.That(Seen(2), Is.EqualTo("Channel is protected. Use a passwd\r\n"));
            _out.Reset();
            Type(2, ".c3,bad");
            Assert.That(Seen(2), Is.EqualTo("Wrong passwd.\r\n"));
            _out.Reset();
            Type(2, ".c3,secretX");
            Assert.That(Seen(2), Is.EqualTo("Wrong passwd.\r\n"));
            _out.Reset();
            Type(2, ".c3,secret");
            Assert.That(Seen(2), Is.EqualTo(">>>bob>>> chan 3\r\n"));
            Assert.That(Seen(1), Is.EqualTo(">>>bob>>> chan 3\r\n"));
        }

        [Test]
        public void T11c_Protect_LongerThan8_Kept8()
        {
            JoinAs(1, "alice");
            JoinAs(2, "bob");
            Type(1, ".c4");
            Type(1, ".p abcdefghijk");
            _out.Reset();
            Type(2, ".c4,abcdefg");
            Assert.That(Seen(2), Is.EqualTo("Wrong passwd.\r\n"));
            _out.Reset();
            Type(2, ".c4,abcdefghXYZ");
            Assert.That(Seen(2), Is.EqualTo(">>>bob>>> chan 4\r\n"));
        }

        [Test]
        public void T11d_Protect_EmptyPassword()
        {
            JoinAs(1, "alice");
            JoinAs(2, "bob");
            Type(1, ".c4");
            Type(1, ".p ");
            _out.Reset();
            Type(2, ".c4");
            Assert.That(Seen(2), Is.EqualTo("Channel is protected. Use a passwd\r\n"));
            _out.Reset();
            Type(2, ".c4,");
            Assert.That(Seen(2), Is.EqualTo(">>>bob>>> chan 4\r\n"));
        }

        [Test]
        public void T11e_Protect_FreedWhenTheChannelEmpties()
        {
            JoinAs(1, "alice");
            JoinAs(2, "bob");
            Type(1, ".c4");
            Type(1, ".p pw");
            Type(1, ".c0");
            _out.Reset();
            Type(2, ".c4");
            Assert.That(Seen(2), Is.EqualTo(">>>bob>>> chan 4\r\n"));
        }

        [Test]
        public void T11f_Protect_FreedWhenTheProtectorLeaves()
        {
            JoinAs(1, "alice");
            JoinAs(2, "bob");
            Type(1, ".c4");
            Type(1, ".p pw");
            _chat.Disconnect(1);
            _out.Reset();
            Type(2, ".c4");
            Assert.That(Seen(2), Is.EqualTo(">>>bob>>> chan 4\r\n"));
        }

        [Test]
        public void T11g_GuardE_EveryUserProtectsAChannel_TableHolds()
        {
            // CHAT 3.1 serv.c:323-326 wrote past the channel table when no entry was free. The table has one entry per
            // user place, so a full chat with every channel protected must still fit and keep every password.
            Make(s => { s.MaxUsers = 5; s.WaitPause = TimeSpan.Zero; s.OutputLimitBytes = int.MaxValue; });
            for (int i = 0; i < 5; i++)
            {
                Join(i);
                Type(i, ".c" + (i + 1));
                Type(i, ".p pw" + i);
            }

            _out.Reset();
            Type(0, ".c2");
            Assert.That(Seen(0), Is.EqualTo("Channel is protected. Use a passwd\r\n"));
            _out.Reset();
            Type(0, ".c5,pw4");
            Assert.That(Seen(0), Is.EqualTo(">>>guest>>> chan 5\r\n"));
        }

        // ---- 12 ----

        [Test]
        public void T12_Whisper_ExactText_AcrossChannels_NoConfirmation()
        {
            JoinAs(1, "alice");
            JoinAs(2, "bob");
            Type(2, ".c9");
            _out.Reset();
            Type(1, "!1 psst");
            Assert.That(Seen(2), Is.EqualTo("alice whispers: !1 psst\r\n"));
            Assert.That(Seen(1), Is.EqualTo(""));
        }

        [TestCase("!9 x")]
        [TestCase("!99 x")]
        [TestCase("!-1 x")]
        [TestCase("!99999999999 x")]
        public void T12b_Whisper_BadOrEmptySlot_Silent(string cmd)
        {
            JoinAs(1, "alice");
            JoinAs(2, "bob");
            Type(1, cmd);
            Assert.That(Seen(1) + Seen(2), Is.EqualTo(""));
        }

        [Test]
        public void T12c_Whisper_NoNumberIsSlot0_AndToYourself()
        {
            JoinAs(1, "alice");
            JoinAs(2, "bob");
            Type(2, "!hi");
            Assert.That(Seen(1), Is.EqualTo("bob whispers: !hi\r\n"));
            _out.Reset();
            Type(2, "!1 me");
            Assert.That(Seen(2), Is.EqualTo("bob whispers: !1 me\r\n"));
            Assert.That(Seen(1), Is.EqualTo(""));
        }

        // ---- 13 ----

        [Test]
        public void T13_Mail_Shell_Who_Bell()
        {
            JoinAs(1, "alice");
            JoinAs(2, "bob");
            Type(1, ".m");
            Assert.That(Seen(1), Is.EqualTo("This is disabled by default- use SMTP!\r\n"));
            _out.Reset();
            Type(1, "--! ls");
            Assert.That(Seen(1), Is.EqualTo("Function is being disabled by default\r\n"));
            _out.Reset();
            Type(1, ".b");
            Assert.That(Seen(1), Is.EqualTo("+++ requesting for a chat +++\r\n--- back in chat ---\r\n"));
            Assert.That(Seen(2), Is.EqualTo("+++ requesting for a chat +++\r\n"));
            _out.Reset();
            Type(1, ".w");
            Assert.That(Seen(1), Is.EqualTo(
                "guest    20200001     May 29 12:00\r\n" +
                "guest    20200001     May 29 12:00\r\n" +
                "--- back in chat ---\r\n"));
            Assert.That(Seen(2), Is.EqualTo(""));
        }

        // ---- 14 ----

        [TestCase(".foo")]
        [TestCase(".H")]
        [TestCase(".S")]
        [TestCase(".N bob")]
        [TestCase(". x")]
        public void T14_UnknownAndUpperCaseCommands_AreText(string cmd)
        {
            JoinAs(1, "alice");
            JoinAs(2, "bob");
            Type(1, cmd);
            Assert.That(Seen(2), Is.EqualTo("alice says: " + cmd + "\r\n"));
            Assert.That(Seen(1), Is.EqualTo(""));
        }

        // ---- 15 ----

        [Test]
        public void T15_LowestFreeSlotReused()
        {
            JoinAs(10, "a");
            JoinAs(11, "b");
            JoinAs(12, "c");
            Type(11, ".x");
            JoinAs(13, "d");
            Type(10, ".s");
            Assert.That(Seen(10), Is.EqualTo(
                SHeader +
                " 0          0 20200001         guest    a\r\n" +
                " 1          0 20200001         guest    d\r\n" +
                " 2          0 20200001         guest    c\r\n"));
        }

        // ---- 16 ----

        [Test]
        public void T16_Limit30_User31Refused()
        {
            Make(s => { s.WaitPause = TimeSpan.Zero; s.OutputLimitBytes = int.MaxValue; });
            for (int i = 0; i < 30; i++)
            {
                Join(i);
            }

            _out.Reset();
            Join(30);
            Assert.That(Seen(30), Is.EqualTo(Banner + Waiting + "open of answerpipe failed.\r\n"));
            Assert.That(_out.ClearCount(30), Is.EqualTo(1));
            Assert.That(Seen(0), Is.EqualTo(""));
            _chat.Disconnect(30);
            Assert.That(Seen(0), Is.EqualTo(""));
        }

        // ---- 17 ----

        [Test]
        public void T17_SlowUser_ThrownOut_Deaf_BugA()
        {
            Make(s => { s.WaitPause = TimeSpan.Zero; s.OutputLimitBytes = 250; });
            // A join sends about 200 bytes in one go, so everyone's output is drained as it goes while joining.
            string[] nicks = { "", "alice", "bob", "carol" };
            for (int s = 1; s <= 3; s++)
            {
                Join(s);
                DrainAll(1, 2, 3);
                Type(s, ".n " + nicks[s]);
                DrainAll(1, 2, 3);
            }

            _out.Reset();

            // bob drains nothing from here on. Each line is 100 bytes; the third would make 300, over his 250.
            Type(1, L(1));
            Type(1, L(2));
            DrainAll(1, 3);
            Assert.That(Seen(2), Is.EqualTo("alice says: " + L(1) + "\r\nalice says: " + L(2) + "\r\n"));
            _out.Reset();
            Type(1, L(3));
            Assert.That(Seen(2), Is.EqualTo(""));
            // Bug a (CHAT 3.1 serv.c:127, 204): the shared buffer was overwritten by "---bob---" in the middle of
            // the broadcast, so carol got "---bob---" twice and never the line. Fixed: she gets both, once each.
            Assert.That(Seen(3), Is.EqualTo("---bob---\r\nalice says: " + L(3) + "\r\n"));
            Assert.That(Seen(1), Is.EqualTo("---bob---\r\n"));
            Assert.That(_out.ClearCount(2), Is.EqualTo(0));

            // bob is deaf: chat output no longer reaches him, the client-side commands still answer.
            DrainAll(1, 2, 3);
            _out.Reset();
            Type(1, L(4));
            Type(2, "can anyone hear me");
            Assert.That(Seen(2), Is.EqualTo(""));
            Assert.That(Seen(3), Is.EqualTo("alice says: " + L(4) + "\r\n"));
            Type(2, ".h");
            Assert.That(Seen(2), Is.EqualTo(Help));

            // .s does not list him; his .x clears him and nobody is told again.
            _out.Reset();
            Type(1, ".s");
            Assert.That(Seen(1), Does.Not.Contain("bob"));
            Type(2, ".x");
            Assert.That(_out.ClearCount(2), Is.EqualTo(1));
            Assert.That(Seen(3), Is.EqualTo(""));
        }

        private static string L(int n) => "line " + n + " " + new string('x', 80);

        private void DrainAll(params int[] sessions)
        {
            for (int i = 0; i < sessions.Length; i++)
            {
                _chat.OutputDrained(sessions[i], int.MaxValue);
            }
        }

        // ---- 18 ----

        [Test]
        public void T18_ClearedCall_GivesLeaveOnce()
        {
            JoinAs(1, "alice");
            JoinAs(2, "bob");
            _chat.Disconnect(1);
            _chat.Disconnect(1);
            Assert.That(Seen(2), Is.EqualTo("---alice---\r\n"));
            Assert.That(_out.ClearCount(1), Is.EqualTo(0));
        }

        // ---- 19 ----

        [Test]
        public void T19_LogLines_Exact()
        {
            _chat.Tick(T0.AddDays(-24)); // 5 May 1987: a one-digit day is padded with a space, as ctime does.
            _chat.Connect(1, "guest", "26245890040004");
            _chat.Connect(2, "guest", "1234");
            Type(1, ".n alice");
            Type(2, ".c 3");
            Type(2, "hi all");
            Type(2, "!0 psst");
            Type(1, ".x");
            Assert.That(_out.LogLines(), Is.EqualTo(new[]
            {
                "Chat-Server Starting At: Fri May 29 12:00:00 1987",
                "Incoming Call From 26245890040004   At Tue May  5 12:00:00 1987",
                "+++guest+++",
                "Incoming Call From 1234             At Tue May  5 12:00:00 1987",
                "+++guest+++",
                ">>>guest>>>alice>>>",
                ">>>guest>>> chan 3",
                "[1:3]guest says: hi all",
                "[1:3]guest whispers: !0 psst",
                "---alice---",
            }));
        }

        // ---- 20 ----

        [TestCase(60)]
        [TestCase(40)]
        public void T20_ManyUsers_ThreeChannels_EveryLineRight(int users)
        {
            var sw = Stopwatch.StartNew();
            Make(s => { s.MaxUsers = users; s.WaitPause = TimeSpan.Zero; s.OutputLimitBytes = int.MaxValue; });
            for (int u = 0; u < users; u++)
            {
                Join(u);
            }

            // Join counts: user u saw its own join and everyone after it.
            for (int u = 0; u < users; u++)
            {
                Assert.That(Count(Seen(u), "+++guest+++\r\n"), Is.EqualTo(users - u), "joins seen by " + u);
            }

            Join(users);
            Assert.That(_out.ClearCount(users), Is.EqualTo(1), "one over the limit is refused");

            for (int u = 0; u < users; u++)
            {
                Type(u, ".n u" + u.ToString("D2"));
                Type(u, ".c" + ((u % 3) + 1));
            }

            _out.Reset();
            Type(0, ".s");
            Assert.That(Count(Seen(0), "\r\n"), Is.EqualTo(users + 1));

            var present = new bool[users];
            var expected = new StringBuilder[users];
            for (int u = 0; u < users; u++)
            {
                present[u] = true;
                expected[u] = new StringBuilder();
            }

            _out.Reset();
            int left = 0;
            for (int r = 0; r < 20; r++)
            {
                if (r == 10)
                {
                    for (int u = 0; u < users; u += 6)
                    {
                        Type(u, ".x");
                        present[u] = false;
                        left++;
                        for (int v = 0; v < users; v++)
                        {
                            if (present[v])
                            {
                                expected[v].Append("---u" + u.ToString("D2") + "---\r\n");
                            }
                        }
                    }
                }

                for (int u = 0; u < users; u++)
                {
                    if (!present[u])
                    {
                        continue;
                    }

                    string text = "m" + u + "-" + r;
                    Type(u, text);
                    for (int v = 0; v < users; v++)
                    {
                        if (v != u && present[v] && v % 3 == u % 3)
                        {
                            expected[v].Append("u" + u.ToString("D2") + " says: " + text + "\r\n");
                        }
                    }
                }
            }

            for (int v = 0; v < users; v++)
            {
                if (present[v])
                {
                    Assert.That(Seen(v), Is.EqualTo(expected[v].ToString()), "user " + v);
                    Assert.That(Count(Seen(v), "---"), Is.EqualTo(left * 2), "leaves seen by " + v);
                }
            }

            _out.Reset();
            Type(1, ".s");
            Assert.That(Count(Seen(1), "\r\n"), Is.EqualTo(users - left + 1));
            sw.Stop();
            TestContext.Out.WriteLine(users + " users: " + sw.ElapsedMilliseconds + " ms");
        }

        private static int Count(string text, string part)
        {
            int n = 0;
            int at = 0;
            while ((at = text.IndexOf(part, at, StringComparison.Ordinal)) >= 0)
            {
                n++;
                at += part.Length;
            }

            return n;
        }
    }
}
