//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

// The host description format (docs/x25host-format.md in RetroX25): reading and checking a host folder, and a
// machine that runs from one - login, shell, menus, the fake file tree, and the users of one machine talking to each
// other. The two descriptions below are the RetroX25 examples demo/hosts/vax and demo/hosts/bbs, cut down.

using System.Text;
using NUnit.Framework;
using RetroIO.Protocols.X25.Host;

namespace RetroIO.Protocols.X25.Tests
{
    /// <summary>HostDescription and DescribedHost.</summary>
    [TestFixture]
    public class DescribedHostTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 11, 18, 15, 4, 5);

        private const string Vax = """
            [host]
            hostname = TOMVAX
            address = 23429002
            sessions = 2
            banner = banner.txt

            [login]
            user_prompt = "Username: "
            password_prompt = "Password: "
            case = upper
            attempts = 2
            fail_text = User authorization failure
            motd = motd.txt

            [user.0]
            name = GUEST
            password = GUEST

            [user.1]
            name = SYSTEM
            password = MANAGER
            greeting = greeting.txt

            [shell]
            prompt = "$ "
            case = upper
            unknown = %DCL-W-IVVERB, unrecognized command verb{nl} \{word}\
            unknown_file = %TYPE-W-SEARCHFAIL, error searching for {args}
            time_format = vms
            logout_text = {user} logged out at {date} {time}

            [command.0]
            name = LOGOUT
            alias = LO,LOGOFF
            do = logout

            [command.1]
            name = DIRECTORY
            alias = DIR
            do = ls

            [command.2]
            name = TYPE
            do = cat

            [command.3]
            name = SET
            text = %SET-W-NOPRIV, insufficient privilege

            [command.4]
            name = SHOW
            do = who

            [command.5]
            name = REPLY
            do = send

            [command.6]
            name = TIME
            do = time

            [file.0]
            path = NOTES.TXT
            content = notes.txt
            """;

        private static readonly Dictionary<string, string> VaxFiles = new Dictionary<string, string>
        {
            ["banner.txt"] = "Welcome to {node}\n",
            ["motd.txt"] = "Hello {user}\n",
            ["greeting.txt"] = "You are the system manager.\n",
            ["notes.txt"] = "Line one\nLine two\n",
        };

        private const string Bbs = """
            [host]
            address = 24229002
            line_end = lf

            [login]
            hide_password = no
            case = upper
            then = menu.main

            [user.0]
            name = GUEST
            password =

            [user.1]
            name = ANN
            password =

            [user.2]
            name = BOB
            password =

            [menu.main]
            title = main.txt
            prompt = "Choice: "
            item.0 = N | News | file:news.txt
            item.1 = W | Who | text:{user} from {calling}
            item.2 = S | Shell | shell
            item.3 = G | Goodbye | logout
            unknown = Please type N, W, S or G.

            [shell]
            logout_text = Thank you for calling.

            [command.0]
            name = chat
            do = room.lobby

            [command.1]
            name = back
            do = menu.main

            [command.2]
            name = wall
            do = wall
            """;

        private static readonly Dictionary<string, string> BbsFiles = new Dictionary<string, string>
        {
            ["main.txt"] = "N News\nW Who\n",
            ["news.txt"] = "No news.\n",
        };

        private sealed class Output : IHostOutput, IHostPadControl
        {
            public Dictionary<int, StringBuilder> Text { get; } = new Dictionary<int, StringBuilder>();

            public List<int> Cleared { get; } = new List<int>();

            public List<string> Echo { get; } = new List<string>();

            public List<string> Log { get; } = new List<string>();

            public void Send(int session, ReadOnlySpan<byte> data)
            {
                if (!Text.TryGetValue(session, out StringBuilder? sb))
                {
                    sb = new StringBuilder();
                    Text.Add(session, sb);
                }

                for (int i = 0; i < data.Length; i++)
                {
                    sb.Append((char)data[i]);
                }
            }

            public void Clear(int session) => Cleared.Add(session);

            void IHostOutput.Log(string text) => Log.Add(text);

            public void SetEcho(int session, bool echo) => Echo.Add(session + (echo ? " on" : " off"));

            /// <summary>What was sent to the session since the last call, then forgotten.</summary>
            public string Take(int session)
            {
                if (!Text.TryGetValue(session, out StringBuilder? sb))
                {
                    return string.Empty;
                }

                string s = sb.ToString();
                sb.Clear();
                return s;
            }
        }

        private static HostDescription Describe(string ini, Dictionary<string, string> files, string? hostname = null, string? address = null)
        {
            return HostDescription.Parse(ini, "host.ini", "folder", n => files.TryGetValue(n, out string? t) ? t : null, hostname, address);
        }

        private static (DescribedHost Host, Output Out) Make(string ini, Dictionary<string, string> files)
        {
            var o = new Output();
            var h = new DescribedHost(Describe(ini, files), o);
            h.Start(T0);
            return (h, o);
        }

        private static void Type(DescribedHost h, int session, string text) => h.Input(session, Encoding.ASCII.GetBytes(text));

        private static (DescribedHost Host, Output Out) LoggedInVax(int session = 1, string user = "guest", string password = "GUEST")
        {
            (DescribedHost h, Output o) = Make(Vax, VaxFiles);
            h.Connect(session, string.Empty, "24227001");
            Type(h, session, user + "\r");
            Type(h, session, password + "\r");
            o.Take(session);
            return (h, o);
        }

        // ------------------------------------------------------------------ the operator (IHostOperator)

        [Test]
        public void TheOperator_SeesTheUserAndState_SendsALine_AndLogsTheUserOut()
        {
            (DescribedHost h, Output o) = LoggedInVax();
            Assert.That(h.TryDescribe(1, out HostSessionInfo info), Is.True);
            Assert.That(info.User, Is.EqualTo("guest"));
            Assert.That(info.State, Is.EqualTo("shell"));
            Assert.That(info.LoggedInAt, Is.EqualTo(T0));
            Assert.That(h.TryDescribe(2, out _), Is.False);

            Type(h, 1, "DIR");
            h.OperatorSend(1, "System going down in 5 minutes");
            Assert.That(o.Take(1), Is.EqualTo("\r\nSystem going down in 5 minutes\r\n$ DIR"), "the prompt and the half-typed line again");

            h.OperatorLogout(1);
            Assert.That(o.Take(1), Does.StartWith("guest logged out at"));
            Assert.That(o.Cleared, Is.EqualTo(new List<int> { 1 }));
        }

        [Test]
        public void TheOperator_SeesACallerNotYetLoggedIn_AsLogin()
        {
            (DescribedHost h, Output o) = Make(Vax, VaxFiles);
            h.Connect(1, string.Empty, "24227001");
            Assert.That(h.TryDescribe(1, out HostSessionInfo info), Is.True);
            Assert.That(info.User, Is.Empty);
            Assert.That(info.State, Is.EqualTo("login"));
            Assert.That(info.LoggedInAt, Is.Null);
        }

        // ------------------------------------------------------------------ reading the folder

        [Test]
        public void TheVaxExample_IsRead()
        {
            HostDescription d = Describe(Vax, VaxFiles);
            Assert.That(d.Hostname, Is.EqualTo("TOMVAX"));
            Assert.That(d.Address, Is.EqualTo("23429002"));
            Assert.That(d.Users.Count, Is.EqualTo(2));
            Assert.That(d.Commands[0].Aliases, Is.EqualTo(new List<string> { "LO", "LOGOFF" }));
            Assert.That(d.Start, Is.EqualTo("login"));
            Assert.That(d.Shell.Prompt, Is.EqualTo("$ "), "the quotes keep the space");
        }

        [Test]
        public void TheMachineListNameAndAddress_WinOverTheFolder()
        {
            HostDescription d = Describe(Vax, VaxFiles, "OSLOVAX", "24229005");
            Assert.That(d.Hostname, Is.EqualTo("OSLOVAX"));
            Assert.That(d.Address, Is.EqualTo("24229005"));
        }

        [TestCase("[host]\naddress = 1\n[hots]\n", "host.ini:3: unknown section [hots]")]
        [TestCase("[host]\naddress = 1\nbaner = x.txt\n", "host.ini:3: [host] baner: unknown key")]
        [TestCase("[host]\naddress = 1\nbanner = nothere.txt\n", "no such file in the host's folder")]
        [TestCase("[host]\naddress = 1\nbanner = ../etc/passwd\n", "by its name alone")]
        [TestCase("[host]\naddress = 1\n[shell]\nprompt = {usr}$\n", "unknown placeholder {usr}")]
        [TestCase("[host]\naddress = 1\n[command.0]\nname = X\ndo = format\n", "is not a built-in action")]
        [TestCase("[host]\naddress = 1\n[command.0]\nname = X\ntext = a\ndo = echo\n", "exactly one of text, file or do")]
        [TestCase("[host]\naddress = 1\n[command.0]\nname = X\ndo = menu.nope\n", "menu.nope is not a menu of this host")]
        [TestCase("[host]\naddress = 1\n[login]\n", "no [user.N]")]
        [TestCase("[host]\nsessions = 2\n", "has no address")]
        [TestCase("[host]\naddress = 12a\n", "must be 1 to 15 digits")]
        [TestCase("[host]\naddress = 1\nstart = menu.x\n", "menu.x is not a menu of this host")]
        public void AMistake_StopsLoading_NamingTheLine(string ini, string expected)
        {
            FormatException? ex = Assert.Throws<FormatException>(() => Describe(ini, VaxFiles));
            Assert.That(ex!.Message, Does.Contain(expected));
        }

        [Test]
        public void AMisspelledPlaceholderInATextFile_StopsLoading_NamingTheFile()
        {
            var files = new Dictionary<string, string>(VaxFiles) { ["banner.txt"] = "Hello {hostnme}\n" };
            FormatException? ex = Assert.Throws<FormatException>(() => Describe(Vax, files));
            Assert.That(ex!.Message, Does.Contain("banner.txt: unknown placeholder {hostnme}"));
        }

        [Test]
        public void HidingThePassword_NeedsAnOutputThatReachesThePad()
        {
            HostDescription d = Describe("[host]\naddress = 1\n[login]\n[user.0]\nname = a\n", VaxFiles);
            Assert.Throws<ArgumentException>(() => new DescribedHost(d, new TextOnly()));
        }

        private sealed class TextOnly : IHostOutput
        {
            public void Send(int session, ReadOnlySpan<byte> data)
            {
            }

            public void Clear(int session)
            {
            }

            public void Log(string text)
            {
            }
        }

        // ------------------------------------------------------------------ calls

        [Test]
        public void ACall_GetsTheBanner_ThenTheUserPrompt()
        {
            (DescribedHost h, Output o) = Make(Vax, VaxFiles);
            h.Connect(1, string.Empty, "24227001");
            Assert.That(o.Take(1), Is.EqualTo("Welcome to TOMVAX\r\nUsername: "));
        }

        [Test]
        public void AGoodLogin_HidesThePassword_ThenSendsTheMotdAndThePrompt()
        {
            (DescribedHost h, Output o) = Make(Vax, VaxFiles);
            h.Connect(1, string.Empty, "24227001");
            o.Take(1);
            Type(h, 1, "guest\r");
            Assert.That(o.Take(1), Is.EqualTo("Password: "));
            Assert.That(o.Echo, Is.EqualTo(new List<string> { "1 off" }));
            Type(h, 1, "GUEST\r");
            Assert.That(o.Echo, Is.EqualTo(new List<string> { "1 off", "1 on" }));
            Assert.That(o.Take(1), Is.EqualTo("\r\nHello guest\r\n$ "), "the host ends the hidden line; {user} is the name as typed");
        }

        private const string Tops20 = "[host]\naddress = 1\nhostname = AURORA\n[login]\ncommand = LOGIN, LOG\n"
            + "command_prompt = \"@\"\ncommand_refused = ?Unrecognized command\ncase = upper\nfail_text = ?Incorrect password\n"
            + "[user.0]\nname = SARTINI\npassword = XYZ\n[shell]\nprompt = \"@\"\n";

        [Test]
        public void ALoginCommand_TakesTheNameAndPasswordOnOneLine_AndIgnoresTheAccount()
        {
            (DescribedHost h, Output o) = Make(Tops20, VaxFiles);
            h.Connect(1, string.Empty, null);
            Assert.That(o.Take(1), Is.EqualTo("@"), "no user prompt: the command prompt");
            Type(h, 1, "DAYTIME\r");
            Assert.That(o.Take(1), Is.EqualTo("?Unrecognized command\r\n@"));
            Type(h, 1, "login sartini XYZ 341\r");
            Assert.That(o.Take(1), Is.EqualTo("@"), "logged in: the shell prompt");
            Assert.That(o.Echo, Is.Empty, "the password was typed on the line: echo was never turned off");
            Assert.That(h.TryDescribe(1, out HostSessionInfo info) && info.State == "shell", Is.True);
        }

        [Test]
        public void ALoginCommand_WithoutThePassword_AsksForIt_WithEchoOff()
        {
            (DescribedHost h, Output o) = Make(Tops20, VaxFiles);
            h.Connect(1, string.Empty, null);
            o.Take(1);
            Type(h, 1, "LOG SARTINI\r");
            Assert.That(o.Take(1), Is.EqualTo("Password: "));
            Type(h, 1, "nope\r");
            Assert.That(o.Echo, Is.EqualTo(new List<string> { "1 off", "1 on" }));
            Assert.That(o.Take(1), Is.EqualTo("\r\n?Incorrect password\r\n@"), "refused: the command prompt again");
            Type(h, 1, "LOGIN\r");
            Assert.That(o.Take(1), Is.EqualTo("?Incorrect password\r\n@"), "LOGIN with no name");
        }

        [Test]
        public void TheGreeting_IsForThatUserOnly()
        {
            (DescribedHost h, Output o) = Make(Vax, VaxFiles);
            h.Connect(1, string.Empty, null);
            Type(h, 1, "SYSTEM\rMANAGER\r");
            Assert.That(o.Take(1), Does.Contain("You are the system manager."));
        }

        [Test]
        public void WrongLogins_AreRefused_AndTheLastOneClearsTheCall()
        {
            (DescribedHost h, Output o) = Make(Vax, VaxFiles);
            h.Connect(1, string.Empty, "24227001");
            o.Take(1);
            Type(h, 1, "GUEST\rwrong\r");
            Assert.That(o.Take(1), Is.EqualTo("Password: \r\nUser authorization failure\r\nUsername: "));
            Assert.That(o.Cleared, Is.Empty);
            Type(h, 1, "NOBODY\rx\r");
            Assert.That(o.Cleared, Is.EqualTo(new List<int> { 1 }), "attempts = 2");
            Assert.That(h.LoginsRefused, Is.EqualTo(2));
            Assert.That(h.SessionCount, Is.EqualTo(0));
        }

        [Test]
        public void Commands_Aliases_AndAnUnknownWord()
        {
            (DescribedHost h, Output o) = LoggedInVax();
            Type(h, 1, "set default\r");
            Assert.That(o.Take(1), Is.EqualTo("%SET-W-NOPRIV, insufficient privilege\r\n$ "));
            Type(h, 1, "dir\r");
            Assert.That(o.Take(1), Is.EqualTo("NOTES.TXT\r\n$ "));
            Type(h, 1, "frob it\r");
            Assert.That(o.Take(1), Is.EqualTo("%DCL-W-IVVERB, unrecognized command verb\r\n \\FROB\\\r\n$ "));
        }

        [Test]
        public void Type_SendsAFile_OrTheUnknownFileText()
        {
            (DescribedHost h, Output o) = LoggedInVax();
            Type(h, 1, "type notes.txt\r");
            Assert.That(o.Take(1), Is.EqualTo("Line one\r\nLine two\r\n$ "));
            Type(h, 1, "type secret.txt\r");
            Assert.That(o.Take(1), Is.EqualTo("%TYPE-W-SEARCHFAIL, error searching for secret.txt\r\n$ "));
        }

        [Test]
        public void Time_IsWrittenTheVmsWay()
        {
            (DescribedHost h, Output o) = LoggedInVax();
            Type(h, 1, "time\r");
            Assert.That(o.Take(1), Is.EqualTo("18-NOV-2026 15:04:05\r\n$ "));
        }

        [Test]
        public void Logout_SendsTheLogoutText_ThenClears()
        {
            (DescribedHost h, Output o) = LoggedInVax();
            Type(h, 1, "lo\r");
            Assert.That(o.Take(1), Is.EqualTo("guest logged out at 18-NOV-2026 15:04:05\r\n"));
            Assert.That(o.Cleared, Is.EqualTo(new List<int> { 1 }));
        }

        [Test]
        public void BackspaceAndDelete_TakeBackACharacter()
        {
            (DescribedHost h, Output o) = LoggedInVax();
            Type(h, 1, "dix\bx\x7Fr\r");
            Assert.That(o.Take(1), Is.EqualTo("NOTES.TXT\r\n$ "));
        }

        [Test]
        public void ThePadsCrLf_IsOneLineEnd()
        {
            (DescribedHost h, Output o) = LoggedInVax();
            Type(h, 1, "dir\r\n");
            Assert.That(o.Take(1), Is.EqualTo("NOTES.TXT\r\n$ "));
        }

        [Test]
        public void Admit_RefusesWhenFull_AndANonPadCall()
        {
            (DescribedHost h, Output _) = Make(Vax, VaxFiles);
            Assert.That(h.Admit(new byte[] { 1, 0, 0, 0 }), Is.EqualTo(HostAdmission.Accept));
            Assert.That(h.Admit(ReadOnlySpan<byte>.Empty), Is.EqualTo(HostAdmission.Accept));
            Assert.That(h.Admit(new byte[] { 0xCC, 0, 0, 0 }), Is.EqualTo(HostAdmission.NotPadCall));
            h.Connect(1, string.Empty, null);
            h.Connect(2, string.Empty, null);
            Assert.That(h.Admit(new byte[] { 1, 0, 0, 0 }), Is.EqualTo(HostAdmission.Busy), "sessions = 2");
        }

        [Test]
        public void AnIdleCall_IsCleared()
        {
            (DescribedHost h, Output o) = LoggedInVax();
            h.Tick(T0.AddMinutes(14));
            Assert.That(o.Cleared, Is.Empty);
            h.Tick(T0.AddMinutes(15));
            Assert.That(o.Cleared, Is.EqualTo(new List<int> { 1 }), "idle_minutes defaults to 15");
        }

        // ------------------------------------------------------------------ users of one machine

        [Test]
        public void Who_ListsEveryLoggedInSession()
        {
            (DescribedHost h, Output o) = LoggedInVax(1, "guest", "GUEST");
            h.Connect(2, string.Empty, "24227002");
            Type(h, 2, "SYSTEM\rMANAGER\r");
            o.Take(2);
            Type(h, 1, "show\r");
            Assert.That(o.Take(1), Is.EqualTo("guest 24227001 15:04:05 00:00\r\nSYSTEM 24227002 15:04:05 00:00\r\n$ "));
        }

        [Test]
        public void Send_ReachesTheOtherUser_AndTheirPromptIsShownAgain()
        {
            (DescribedHost h, Output o) = LoggedInVax(1, "guest", "GUEST");
            h.Connect(2, string.Empty, "24227002");
            Type(h, 2, "SYSTEM\rMANAGER\r");
            Type(h, 2, "ty");
            o.Take(2);
            Type(h, 1, "reply system hello there\r");
            Assert.That(o.Take(2), Is.EqualTo("\r\nMessage from guest: hello there\r\n$ ty"));
            Assert.That(o.Take(1), Is.EqualTo("$ "));
            Type(h, 1, "reply nobody hi\r");
            Assert.That(o.Take(1), Is.EqualTo("nobody: no such user\r\n$ "));
        }

        [Test]
        public void Send_ToAUserWhoIsNotOn_SaysSo()
        {
            (DescribedHost h, Output o) = LoggedInVax();
            Type(h, 1, "reply system hi\r");
            Assert.That(o.Take(1), Is.EqualTo("system: not logged in\r\n$ "));
        }

        [Test]
        public void MoreThan20MessagesASecond_AreDroppedAndCounted()
        {
            (DescribedHost h, Output o) = LoggedInVax(1, "guest", "GUEST");
            h.Connect(2, string.Empty, null);
            Type(h, 2, "SYSTEM\rMANAGER\r");
            for (int i = 0; i < 25; i++)
            {
                Type(h, 1, "reply system x\r");
            }

            Assert.That(h.MessagesDropped, Is.EqualTo(5));
            h.Tick(T0.AddSeconds(1));
            Type(h, 1, "reply system y\r");
            Assert.That(h.MessagesDropped, Is.EqualTo(5), "a new second takes messages again");
        }

        // ------------------------------------------------------------------ menus, rooms, the shell after a menu

        [Test]
        public void TheBbs_LogsInToTheMenu_AndAnswersItsKeys()
        {
            (DescribedHost h, Output o) = Make(Bbs, BbsFiles);
            h.Connect(1, string.Empty, "24227001");
            Assert.That(o.Take(1), Is.EqualTo("Username: "));
            Type(h, 1, "guest\ranything\r");
            Assert.That(o.Echo, Is.Empty, "hide_password = no");
            Assert.That(o.Take(1), Is.EqualTo("Password: N News\nW Who\nChoice: "), "an empty password takes any; line_end = lf");
            Type(h, 1, "w\r");
            Assert.That(o.Take(1), Is.EqualTo("guest from 24227001\nChoice: "));
            Type(h, 1, "n\r");
            Assert.That(o.Take(1), Is.EqualTo("No news.\nChoice: "));
            Type(h, 1, "x\r");
            Assert.That(o.Take(1), Is.EqualTo("Please type N, W, S or G.\nChoice: "));
            Type(h, 1, "g\r");
            Assert.That(o.Take(1), Is.EqualTo("Thank you for calling.\n"));
            Assert.That(o.Cleared, Is.EqualTo(new List<int> { 1 }));
        }

        [Test]
        public void TheHostnameComesFromTheFolder_WhenNothingElseGivesOne()
        {
            Assert.That(Describe(Bbs, BbsFiles).Hostname, Is.EqualTo("folder"));
        }

        [Test]
        public void ARoom_CarriesLinesToTheOthers_AndAnnouncesJoinsAndLeaves()
        {
            (DescribedHost h, Output o) = Make(Bbs, BbsFiles);
            h.Connect(1, string.Empty, null);
            h.Connect(2, string.Empty, null);
            Type(h, 1, "ann\rx\rs\rchat\r");
            Type(h, 2, "bob\rx\rs\rchat\r");
            o.Take(1);
            o.Take(2);
            Type(h, 2, "hi all\r");
            Assert.That(o.Take(1), Is.EqualTo("bob says: hi all\n"));
            Assert.That(o.Take(2), Is.Empty, "nothing comes back to the one who said it");
            Type(h, 2, ".x\r");
            Assert.That(o.Take(1), Is.EqualTo("---bob---\n"));
            Assert.That(o.Take(2), Is.EqualTo("$ "), "back to the shell");
        }

        [Test]
        public void ARoomJoin_IsAnnouncedToThoseAlreadyThere()
        {
            (DescribedHost h, Output o) = Make(Bbs, BbsFiles);
            h.Connect(1, string.Empty, null);
            h.Connect(2, string.Empty, null);
            Type(h, 1, "ann\rx\rs\rchat\r");
            o.Take(1);
            Type(h, 2, "bob\rx\rs\rchat\r");
            Assert.That(o.Take(1), Is.EqualTo("+++bob+++\n"));
        }

        [Test]
        public void Wall_ReachesEverySession_AndMesgNStopsIt()
        {
            string ini = Bbs + "\n[command.3]\nname = mesg\ndo = mesg\n";
            (DescribedHost h, Output o) = Make(ini, BbsFiles);
            h.Connect(1, string.Empty, null);
            h.Connect(2, string.Empty, null);
            Type(h, 1, "ann\rx\rs\r");
            Type(h, 2, "bob\rx\rs\r");
            o.Take(1);
            o.Take(2);
            Type(h, 1, "wall lunch\r");
            Assert.That(o.Take(2), Is.EqualTo("\nBroadcast message from ann at 15:04:05...\nlunch\n$ "));
            Type(h, 2, "mesg n\r");
            o.Take(2);
            Type(h, 1, "wall again\r");
            Assert.That(o.Take(2), Is.Empty);
        }

        [Test]
        public void AUnixTree_ListsAndChangesDirectory()
        {
            const string ini = """
                [host]
                address = 1
                pad_calls_only = no
                [shell]
                unknown_file = {args}: no such file
                time_format = unix
                prompt = "{cwd} $ "
                [command.0]
                name = ls
                do = ls
                [command.1]
                name = cd
                do = cd
                [command.2]
                name = cat
                do = cat
                [command.3]
                name = date
                do = time
                [file.0]
                path = /usr
                [file.1]
                path = /usr/notes
                content = notes.txt
                [file.2]
                path = /etc
                """;
            (DescribedHost h, Output o) = Make(ini, VaxFiles);
            h.Connect(1, string.Empty, null);
            Assert.That(o.Take(1), Is.EqualTo("/ $ "), "no login: straight to the shell");
            Type(h, 1, "ls\r");
            Assert.That(o.Take(1), Is.EqualTo("usr\r\netc\r\n/ $ "));
            Type(h, 1, "cd usr\r");
            Type(h, 1, "cat notes\r");
            Assert.That(o.Take(1), Is.EqualTo("/usr $ Line one\r\nLine two\r\n/usr $ "));
            Type(h, 1, "cd notes\r");
            Assert.That(o.Take(1), Is.EqualTo("notes: no such file\r\n/usr $ "), "a file is not a directory");
            Type(h, 1, "cd ..\r");
            Assert.That(o.Take(1), Is.EqualTo("/ $ "));
            Type(h, 1, "date\r");
            Assert.That(o.Take(1), Is.EqualTo("Wed Nov 18 15:04:05 2026\r\n/ $ "));
        }

        [Test]
        public void ABraceIsWrittenTwice()
        {
            (DescribedHost h, Output o) = Make("[host]\naddress = 1\n[shell]\nprompt = \"{{x}} \"\n", VaxFiles);
            h.Connect(1, string.Empty, null);
            Assert.That(o.Take(1), Is.EqualTo("{x} "));
        }
    }
}
