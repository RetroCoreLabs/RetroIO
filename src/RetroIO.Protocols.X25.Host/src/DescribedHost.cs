//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System.Globalization;
using System.Text;

namespace RetroIO.Protocols.X25.Host
{
    /// <summary>
    /// One machine described by a host folder (<see cref="HostDescription"/>): a banner, a login, a command line or a
    /// menu, a fake file tree, and the built-in actions that let the users of the machine see and talk to each other.
    /// Nothing typed is ever run: it is only compared with the words in the description.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The same contract as <see cref="ScriptedHost"/>: no I/O, no clock, no threads. The program that runs it gives it
    /// the time and the calls; what it sends comes out through the <see cref="IHostOutput"/>. Not thread safe.
    /// </para>
    /// <para>
    /// Input is taken a line at a time: CR (or LF) ends a line, BS and DEL take back the last character, other control
    /// characters are dropped, a line is cut at 256 characters. The caller's PAD echoes what is typed (X.3 parameter 2);
    /// for a hidden password the host tells the PAD to stop, through <see cref="IHostPadControl"/>, and sends the line
    /// end itself afterwards, since the PAD did not echo the CR either.
    /// </para>
    /// <para>
    /// Messages between users (<c>wall</c>, <c>send</c>, rooms) are cut at 256 characters; a session takes at most 20 a
    /// second from others, the rest are dropped and counted (<see cref="MessagesDropped"/>). One that arrives at a prompt
    /// is sent on its own lines and the prompt is shown again with what was typed so far.
    /// </para>
    /// </remarks>
    public sealed class DescribedHost : IHostMachine
    {
        /// <summary>The longest line taken from a caller.</summary>
        private const int MaxLine = 256;

        /// <summary>The longest message from one user to another.</summary>
        private const int MaxMessage = 256;

        /// <summary>Messages a session takes from others in one second.</summary>
        private const int MessagesPerSecond = 20;

        private static readonly string[] VmsMonths = { "JAN", "FEB", "MAR", "APR", "MAY", "JUN", "JUL", "AUG", "SEP", "OCT", "NOV", "DEC" };
        private static readonly string[] UnixMonths = { "Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec" };
        private static readonly string[] UnixDays = { "Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat" };

        private readonly HostDescription _d;
        private readonly IHostOutput _output;
        private readonly IHostPadControl? _pad;
        private readonly Dictionary<int, Session> _sessions = new Dictionary<int, Session>();
        private readonly List<Session> _order = new List<Session>();
        private DateTime _now;
        private DateTime _started;

        /// <summary>Makes the machine.</summary>
        /// <param name="description">What it is.</param>
        /// <param name="output">Where it sends; it must also be an <see cref="IHostPadControl"/> when the login hides the
        /// password, since that is the only way to tell the PAD to stop echoing.</param>
        /// <exception cref="ArgumentException">The login hides the password and the output cannot reach the PAD.</exception>
        public DescribedHost(HostDescription description, IHostOutput output)
        {
            _d = description ?? throw new ArgumentNullException(nameof(description));
            _output = output ?? throw new ArgumentNullException(nameof(output));
            _pad = output as IHostPadControl;
            if (_d.Login != null && _d.Login.HidePassword && _pad == null)
            {
                throw new ArgumentException("hide_password = yes needs an output that can send an X.29 Set to the PAD (IHostPadControl)", nameof(output));
            }
        }

        /// <inheritdoc/>
        public int SessionCount => _order.Count;

        /// <summary>Messages from other users dropped because a session took more than 20 in a second.</summary>
        public long MessagesDropped { get; private set; }

        /// <summary>Logins refused (a wrong name or password).</summary>
        public long LoginsRefused { get; private set; }

        /// <inheritdoc/>
        public void Start(DateTime now)
        {
            _now = now;
            _started = now;
        }

        /// <inheritdoc/>
        public void Tick(DateTime now)
        {
            _now = now;
            if (_d.IdleMinutes <= 0)
            {
                return;
            }

            TimeSpan limit = TimeSpan.FromMinutes(_d.IdleMinutes);
            Session[] all = _order.ToArray();
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i].Open && now - all[i].LastInput >= limit)
                {
                    _output.Log("call " + all[i].Id.ToString(CultureInfo.InvariantCulture) + " from " + (all[i].Calling ?? "?")
                        + " idle " + _d.IdleMinutes.ToString(CultureInfo.InvariantCulture) + " minutes, cleared");
                    Close(all[i]);
                }
            }
        }

        /// <inheritdoc/>
        public HostAdmission Admit(ReadOnlySpan<byte> callUserData)
        {
            if (_order.Count >= _d.Sessions)
            {
                return HostAdmission.Busy;
            }

            // X.29 (11/88) 2.2: a PAD's call carries the protocol identifier 01 00 00 00 in its first four bytes.
            if (_d.PadCallsOnly && callUserData.Length > 0
                && (callUserData.Length < 4 || callUserData[0] != 1 || callUserData[1] != 0 || callUserData[2] != 0 || callUserData[3] != 0))
            {
                return HostAdmission.NotPadCall;
            }

            return HostAdmission.Accept;
        }

        /// <inheritdoc/>
        public void Connect(int session, string login, string? calling)
        {
            if (_sessions.ContainsKey(session))
            {
                throw new InvalidOperationException("Session " + session.ToString(CultureInfo.InvariantCulture) + " is already connected.");
            }

            var s = new Session(session, calling, _now, Home());
            _sessions.Add(session, s);
            _order.Add(s);
            if (_d.Banner != null)
            {
                Send(s, Render(_d.Banner, s, null, null));
            }

            Goto(s, _d.Start);
        }

        /// <inheritdoc/>
        public void Input(int session, ReadOnlySpan<byte> data)
        {
            if (!_sessions.TryGetValue(session, out Session? s))
            {
                return;
            }

            s.LastInput = _now;
            for (int i = 0; i < data.Length && s.Open; i++)
            {
                byte b = data[i];
                if (b == '\r' || b == '\n')
                {
                    // CR LF is one line end: the LF after a CR is dropped.
                    bool lfAfterCr = b == '\n' && s.LastWasCr;
                    s.LastWasCr = b == '\r';
                    if (!lfAfterCr)
                    {
                        string line = s.Line.ToString();
                        s.Line.Clear();
                        OnLine(s, line);
                    }

                    continue;
                }

                s.LastWasCr = false;
                if (b == 0x08 || b == 0x7F)
                {
                    if (s.Line.Length > 0)
                    {
                        s.Line.Length--;
                    }
                }
                else if (b >= 0x20 && s.Line.Length < MaxLine)
                {
                    s.Line.Append((char)b);
                }
            }
        }

        /// <inheritdoc/>
        public void Disconnect(int session)
        {
            if (_sessions.TryGetValue(session, out Session? s))
            {
                s.Open = false;
                LeaveRoom(s);
                Forget(s);
            }
        }

        /// <inheritdoc/>
        public void OutputDrained(int session, int bytes)
        {
            // Nothing waits on delivery here: texts are short and sent whole.
        }

        // ------------------------------------------------------------------ where a call is

        private void Goto(Session s, string target)
        {
            if (target == "login")
            {
                s.State = State.User;
                Send(s, Render(_d.Login!.UserPrompt, s, null, null));
            }
            else if (target == "shell")
            {
                s.State = State.Shell;
                Prompt(s);
            }
            else if (target == "logout")
            {
                Logout(s);
            }
            else
            {
                // menu.<name>: checked at load, so it is there.
                s.State = State.Menu;
                s.Menu = _d.Menus[target.Substring(5)];
                if (s.Menu.Title != null)
                {
                    Send(s, Render(s.Menu.Title, s, null, null));
                }

                Prompt(s);
            }
        }

        private void Prompt(Session s)
        {
            if (!s.Open)
            {
                return;
            }

            switch (s.State)
            {
                case State.User:
                    Send(s, Render(_d.Login!.UserPrompt, s, null, null));
                    break;
                case State.Password:
                    Send(s, Render(_d.Login!.PasswordPrompt, s, null, null));
                    break;
                case State.Shell:
                    Send(s, Render(_d.Shell.Prompt, s, null, null));
                    break;
                case State.Menu:
                    Send(s, Render(s.Menu!.Prompt, s, null, null));
                    break;
                case State.Room:
                    break;
            }
        }

        private void OnLine(Session s, string line)
        {
            switch (s.State)
            {
                case State.User:
                    OnUser(s, line.Trim());
                    break;
                case State.Password:
                    OnPassword(s, line);
                    break;
                case State.Shell:
                    OnShell(s, line.Trim());
                    break;
                case State.Menu:
                    OnMenu(s, line.Trim());
                    break;
                case State.Room:
                    OnRoom(s, line);
                    break;
            }
        }

        // ------------------------------------------------------------------ login

        private void OnUser(Session s, string name)
        {
            if (name.Length == 0)
            {
                Prompt(s);
                return;
            }

            s.TypedUser = name;
            s.State = State.Password;
            if (_d.Login!.HidePassword)
            {
                _pad!.SetEcho(s.Id, false);
            }

            Prompt(s);
        }

        private void OnPassword(Session s, string password)
        {
            HostLogin login = _d.Login!;
            if (login.HidePassword)
            {
                // The PAD echoed nothing, the CR included: the host ends the line itself, then echo comes back.
                _pad!.SetEcho(s.Id, true);
                Send(s, "\n");
            }

            HostUser? user = FindUser(s.TypedUser, login.Case);
            if (user == null || (user.Password.Length > 0 && !string.Equals(user.Password, password, StringComparison.Ordinal)))
            {
                LoginsRefused++;
                s.Attempts++;
                _output.Log("login refused for " + s.TypedUser + " from " + (s.Calling ?? "?")
                    + " (" + s.Attempts.ToString(CultureInfo.InvariantCulture) + " of " + login.Attempts.ToString(CultureInfo.InvariantCulture) + ")");
                Send(s, Render(login.FailText, s, null, null) + "\n");
                if (s.Attempts >= login.Attempts)
                {
                    Close(s);
                    return;
                }

                s.TypedUser = string.Empty;
                s.State = State.User;
                Prompt(s);
                return;
            }

            s.User = s.TypedUser;
            s.Account = user;
            s.LoggedInAt = _now;
            _output.Log(s.User + " logged in from " + (s.Calling ?? "?"));
            if (login.Motd != null)
            {
                Send(s, Render(login.Motd, s, null, null));
            }

            if (user.Greeting != null)
            {
                Send(s, Render(user.Greeting, s, null, null));
            }

            Goto(s, login.Then);
        }

        private HostUser? FindUser(string name, HostCase how)
        {
            StringComparison cmp = how == HostCase.Exact ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            for (int i = 0; i < _d.Users.Count; i++)
            {
                if (string.Equals(_d.Users[i].Name, name, cmp))
                {
                    return _d.Users[i];
                }
            }

            return null;
        }

        // ------------------------------------------------------------------ shell

        private void OnShell(Session s, string line)
        {
            if (line.Length == 0)
            {
                Prompt(s);
                return;
            }

            int space = line.IndexOf(' ');
            string word = Cased(space < 0 ? line : line.Substring(0, space), _d.Shell.Case);
            string args = space < 0 ? string.Empty : line.Substring(space + 1).Trim();
            HostCommand? command = FindCommand(word);
            if (command == null)
            {
                Send(s, Render(_d.Shell.Unknown, s, word, args) + "\n");
                Prompt(s);
                return;
            }

            if (command.Text != null)
            {
                Send(s, Render(command.Text, s, word, args) + "\n");
            }
            else if (command.File != null)
            {
                Send(s, Render(command.File, s, word, args));
            }
            else if (!Do(s, command.Do!, word, args))
            {
                return; // The action moved the call on (a menu, a room) or ended it.
            }

            Prompt(s);
        }

        private HostCommand? FindCommand(string word)
        {
            StringComparison cmp = _d.Shell.Case == HostCase.Exact ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            for (int i = 0; i < _d.Commands.Count; i++)
            {
                HostCommand c = _d.Commands[i];
                if (string.Equals(c.Name, word, cmp))
                {
                    return c;
                }

                for (int j = 0; j < c.Aliases.Count; j++)
                {
                    if (string.Equals(c.Aliases[j], word, cmp))
                    {
                        return c;
                    }
                }
            }

            return null;
        }

        /// <summary>Runs one built-in action. False when the call left the shell (or ended), so no prompt follows.</summary>
        private bool Do(Session s, string action, string word, string args)
        {
            switch (action)
            {
                case "logout":
                    Logout(s);
                    return false;
                case "clear":
                    Close(s);
                    return false;
                case "echo":
                    Send(s, args + "\n");
                    return true;
                case "time":
                    Send(s, DateTimeText(_now) + "\n");
                    return true;
                case "who":
                    Who(s, null);
                    return true;
                case "finger":
                    Finger(s, args);
                    return true;
                case "ls":
                    List(s);
                    return true;
                case "cat":
                    Cat(s, args);
                    return true;
                case "cd":
                    ChangeDirectory(s, args);
                    return true;
                case "wall":
                    Wall(s, args);
                    return true;
                case "send":
                    SendTo(s, args);
                    return true;
                case "mesg":
                    Mesg(s, args);
                    return true;
            }

            if (action.StartsWith("menu.", StringComparison.Ordinal))
            {
                Goto(s, action);
                return false;
            }

            // room.<name>: the only kind left (checked at load).
            JoinRoom(s, action.Substring(5));
            return false;
        }

        private void Logout(Session s)
        {
            if (_d.Shell.LogoutText != null)
            {
                Send(s, Render(_d.Shell.LogoutText, s, null, null) + "\n");
            }

            Close(s);
        }

        private void Who(Session s, string? only)
        {
            for (int i = 0; i < _order.Count; i++)
            {
                Session o = _order[i];
                if (o.User.Length == 0 || (only != null && !SameUser(o.User, only)))
                {
                    continue;
                }

                Send(s, RenderWho(o, s) + "\n");
            }
        }

        private void Finger(Session s, string args)
        {
            string name = FirstWord(args);
            if (FindUser(name, LoginCase()) == null)
            {
                Send(s, Render(_d.Shell.UserUnknown, s, null, name) + "\n");
                return;
            }

            if (!AnyOn(name))
            {
                Send(s, Render(_d.Shell.UserNotOn, s, null, name) + "\n");
                return;
            }

            Who(s, name);
        }

        private void Wall(Session s, string text)
        {
            string message = Cut(text);
            for (int i = 0; i < _order.Count; i++)
            {
                Session o = _order[i];
                if (o.User.Length > 0 && o.Mesg)
                {
                    Deliver(o, Render(_d.Shell.WallText, o, null, null, s.User, message));
                }
            }
        }

        private void SendTo(Session s, string args)
        {
            string name = FirstWord(args);
            string text = args.Length > name.Length ? args.Substring(name.Length).Trim() : string.Empty;
            if (name.Length == 0 || FindUser(name, LoginCase()) == null)
            {
                Send(s, Render(_d.Shell.UserUnknown, s, null, name) + "\n");
                return;
            }

            bool any = false;
            string message = Cut(text);
            for (int i = 0; i < _order.Count; i++)
            {
                Session o = _order[i];
                if (o.User.Length > 0 && o.Mesg && SameUser(o.User, name))
                {
                    any = true;
                    Deliver(o, Render(_d.Shell.SendText, o, null, null, s.User, message));
                }
            }

            if (!any)
            {
                Send(s, Render(_d.Shell.UserNotOn, s, null, name) + "\n");
            }
        }

        private void Mesg(Session s, string args)
        {
            string a = args.Trim();
            if (a == "y" || a == "Y")
            {
                s.Mesg = true;
            }
            else if (a == "n" || a == "N")
            {
                s.Mesg = false;
            }
            else
            {
                Send(s, (s.Mesg ? "is y" : "is n") + "\n");
            }
        }

        // ------------------------------------------------------------------ rooms

        private void JoinRoom(Session s, string room)
        {
            s.State = State.Room;
            s.Room = room;
            for (int i = 0; i < _order.Count; i++)
            {
                Session o = _order[i];
                if (o != s && o.Room == room && o.State == State.Room)
                {
                    Deliver(o, Render(_d.Shell.RoomJoinText, o, null, null, s.User, string.Empty) + "\n");
                }
            }
        }

        private void OnRoom(Session s, string line)
        {
            if (string.Equals(line.Trim(), _d.Shell.RoomLeave, StringComparison.OrdinalIgnoreCase))
            {
                LeaveRoom(s);
                s.State = State.Shell;
                Prompt(s);
                return;
            }

            if (line.Length == 0)
            {
                return;
            }

            string message = Cut(line);
            for (int i = 0; i < _order.Count; i++)
            {
                Session o = _order[i];
                if (o != s && o.Room == s.Room && o.State == State.Room)
                {
                    Deliver(o, Render(_d.Shell.RoomText, o, null, null, s.User, message) + "\n");
                }
            }
        }

        private void LeaveRoom(Session s)
        {
            if (s.Room == null)
            {
                return;
            }

            string room = s.Room;
            s.Room = null;
            for (int i = 0; i < _order.Count; i++)
            {
                Session o = _order[i];
                if (o != s && o.Room == room && o.State == State.Room)
                {
                    Deliver(o, Render(_d.Shell.RoomLeaveText, o, null, null, s.User, string.Empty) + "\n");
                }
            }
        }

        /// <summary>
        /// A message from another user: at most 20 a second per session, the rest dropped and counted. At a prompt the
        /// prompt is shown again afterwards, with what was typed so far.
        /// </summary>
        private void Deliver(Session to, string text)
        {
            if (!to.Open)
            {
                return;
            }

            long second = _now.Ticks / TimeSpan.TicksPerSecond;
            if (to.MessageSecond != second)
            {
                to.MessageSecond = second;
                to.MessagesThisSecond = 0;
            }

            if (++to.MessagesThisSecond > MessagesPerSecond)
            {
                MessagesDropped++;
                return;
            }

            Send(to, text);
            if (to.State == State.Shell || to.State == State.Menu)
            {
                Prompt(to);
                if (to.Line.Length > 0)
                {
                    Send(to, to.Line.ToString());
                }
            }
        }

        // ------------------------------------------------------------------ menu

        private void OnMenu(Session s, string key)
        {
            HostMenu menu = s.Menu!;
            if (key.Length == 0)
            {
                Prompt(s);
                return;
            }

            for (int i = 0; i < menu.Items.Count; i++)
            {
                HostMenuItem item = menu.Items[i];
                if (!string.Equals(item.Key, key, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (item.Text != null)
                {
                    Send(s, Render(item.Text, s, null, null) + "\n");
                    Prompt(s);
                }
                else if (item.File != null)
                {
                    Send(s, Render(item.File, s, null, null));
                    Prompt(s);
                }
                else
                {
                    Goto(s, item.Goto!);
                }

                return;
            }

            Send(s, Render(menu.Unknown, s, key, null) + "\n");
            Prompt(s);
        }

        // ------------------------------------------------------------------ the fake file tree

        private string Home() => _d.RootedFiles ? "/" : string.Empty;

        private void List(Session s)
        {
            for (int i = 0; i < _d.Files.Count; i++)
            {
                HostFile f = _d.Files[i];
                if (string.Equals(Parent(f.Path), s.Cwd, FileCompare()))
                {
                    Send(s, Name(f.Path) + "\n");
                }
            }
        }

        private void Cat(Session s, string args)
        {
            HostFile? f = args.Length == 0 ? null : FindFile(Resolve(s.Cwd, args));
            if (f == null || f.Content == null)
            {
                Send(s, Render(_d.Shell.UnknownFile, s, null, args) + "\n");
                return;
            }

            Send(s, Render(f.Content, s, null, args));
        }

        private void ChangeDirectory(Session s, string args)
        {
            if (args.Length == 0)
            {
                s.Cwd = Home();
                return;
            }

            if (args == "..")
            {
                s.Cwd = s.Cwd.Length == 0 || s.Cwd == "/" ? s.Cwd : Parent(s.Cwd);
                return;
            }

            string target = Resolve(s.Cwd, args);
            HostFile? f = FindFile(target);
            if (f == null || f.Content != null)
            {
                Send(s, Render(_d.Shell.UnknownFile, s, null, args) + "\n");
                return;
            }

            s.Cwd = f.Path;
        }

        private HostFile? FindFile(string path)
        {
            for (int i = 0; i < _d.Files.Count; i++)
            {
                if (string.Equals(_d.Files[i].Path, path, FileCompare()))
                {
                    return _d.Files[i];
                }
            }

            return null;
        }

        private StringComparison FileCompare() => _d.Shell.Case == HostCase.Exact ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

        private static string Resolve(string cwd, string name)
        {
            if (name.Length > 1)
            {
                name = name.TrimEnd('/');
            }

            if (name.StartsWith('/') || cwd.Length == 0)
            {
                return name;
            }

            return cwd == "/" ? "/" + name : cwd + "/" + name;
        }

        private static string Parent(string path)
        {
            int slash = path.LastIndexOf('/');
            if (slash < 0)
            {
                return string.Empty;
            }

            return slash == 0 ? (path.Length == 1 ? string.Empty : "/") : path.Substring(0, slash);
        }

        private static string Name(string path)
        {
            int slash = path.LastIndexOf('/');
            return slash < 0 ? path : path.Substring(slash + 1);
        }

        // ------------------------------------------------------------------ texts

        /// <summary>Fills the placeholders of a text (the table in the format). One with no value here becomes nothing.</summary>
        private string Render(string text, Session s, string? word, string? args, string? from = null, string? message = null)
        {
            var sb = new StringBuilder(text.Length + 32);
            int i = 0;
            while (i < text.Length)
            {
                char c = text[i];
                if ((c == '{' || c == '}') && i + 1 < text.Length && text[i + 1] == c)
                {
                    sb.Append(c);
                    i += 2;
                    continue;
                }

                if (c == '{')
                {
                    int close = text.IndexOf('}', i + 1);
                    if (close > i)
                    {
                        sb.Append(Value(text.Substring(i + 1, close - i - 1), s, word, args, from, message));
                        i = close + 1;
                        continue;
                    }
                }

                sb.Append(c);
                i++;
            }

            return sb.ToString();
        }

        private string Value(string name, Session s, string? word, string? args, string? from, string? message)
        {
            switch (name)
            {
                case "date": return DateText(_now);
                case "time": return TimeText(_now);
                case "hostname":
                case "node": return _d.Hostname;
                case "address": return _d.Address;
                case "calling": return s.Calling ?? string.Empty;
                case "sessions": return _order.Count.ToString(CultureInfo.InvariantCulture);
                case "uptime": return Span(_now - _started, true);
                case "nl": return "\n";
                case "user": return s.User;
                case "cwd": return s.State == State.User || s.State == State.Password ? string.Empty : s.Cwd;
                case "word": return word ?? string.Empty;
                case "args": return args ?? string.Empty;
                case "from": return from ?? string.Empty;
                case "text": return message ?? string.Empty;
                default: return string.Empty; // {since} and {idle} have a value only in who_format.
            }
        }

        private string RenderWho(Session o, Session to)
        {
            // who_format: the user, calling, since and idle are the listed session's; the rest are the asker's.
            string text = _d.Shell.WhoFormat;
            var sb = new StringBuilder();
            int i = 0;
            while (i < text.Length)
            {
                char c = text[i];
                if ((c == '{' || c == '}') && i + 1 < text.Length && text[i + 1] == c)
                {
                    sb.Append(c);
                    i += 2;
                    continue;
                }

                if (c == '{')
                {
                    int close = text.IndexOf('}', i + 1);
                    if (close > i)
                    {
                        string name = text.Substring(i + 1, close - i - 1);
                        sb.Append(name switch
                        {
                            "user" => o.User,
                            "calling" => o.Calling ?? string.Empty,
                            "since" => TimeText(o.LoggedInAt),
                            "idle" => Span(_now - o.LastInput, false),
                            _ => Value(name, to, null, null, null, null),
                        });
                        i = close + 1;
                        continue;
                    }
                }

                sb.Append(c);
                i++;
            }

            return sb.ToString();
        }

        private string DateText(DateTime t)
        {
            return _d.Shell.TimeFormat switch
            {
                HostTimeFormat.Vms => t.Day.ToString("00", CultureInfo.InvariantCulture) + "-" + VmsMonths[t.Month - 1] + "-" + t.Year.ToString("0000", CultureInfo.InvariantCulture),
                HostTimeFormat.Unix => UnixDays[(int)t.DayOfWeek] + " " + UnixMonths[t.Month - 1] + " " + t.Day.ToString(CultureInfo.InvariantCulture).PadLeft(2) + " " + t.Year.ToString("0000", CultureInfo.InvariantCulture),
                _ => t.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            };
        }

        private static string TimeText(DateTime t) => t.ToString("HH:mm:ss", CultureInfo.InvariantCulture);

        /// <summary><c>do = time</c>: VMS 18-NOV-2026 15:04:05, Unix Wed Nov 18 15:04:05 2026 (C asctime), ISO 2026-11-18 15:04:05.</summary>
        private string DateTimeText(DateTime t)
        {
            if (_d.Shell.TimeFormat == HostTimeFormat.Unix)
            {
                return UnixDays[(int)t.DayOfWeek] + " " + UnixMonths[t.Month - 1] + " " + t.Day.ToString(CultureInfo.InvariantCulture).PadLeft(2)
                    + " " + TimeText(t) + " " + t.Year.ToString("0000", CultureInfo.InvariantCulture);
            }

            return DateText(t) + " " + TimeText(t);
        }

        /// <summary><c>d hh:mm:ss</c> for the uptime; <c>hh:mm</c> for an idle time.</summary>
        private static string Span(TimeSpan t, bool days)
        {
            if (t < TimeSpan.Zero)
            {
                t = TimeSpan.Zero;
            }

            if (days)
            {
                return t.Days.ToString(CultureInfo.InvariantCulture) + " " + t.Hours.ToString("00", CultureInfo.InvariantCulture) + ":"
                    + t.Minutes.ToString("00", CultureInfo.InvariantCulture) + ":" + t.Seconds.ToString("00", CultureInfo.InvariantCulture);
            }

            return ((int)t.TotalHours).ToString("00", CultureInfo.InvariantCulture) + ":" + t.Minutes.ToString("00", CultureInfo.InvariantCulture);
        }

        // ------------------------------------------------------------------ small things

        private HostCase LoginCase() => _d.Login?.Case ?? HostCase.Exact;

        private bool SameUser(string a, string b)
        {
            return string.Equals(a, b, LoginCase() == HostCase.Exact ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
        }

        private bool AnyOn(string name)
        {
            for (int i = 0; i < _order.Count; i++)
            {
                if (_order[i].User.Length > 0 && SameUser(_order[i].User, name))
                {
                    return true;
                }
            }

            return false;
        }

        private static string FirstWord(string args)
        {
            string a = args.Trim();
            int space = a.IndexOf(' ');
            return space < 0 ? a : a.Substring(0, space);
        }

        private static string Cut(string text) => text.Length <= MaxMessage ? text : text.Substring(0, MaxMessage);

        private static string Cased(string word, HostCase how)
        {
            return how switch
            {
                HostCase.Upper => word.ToUpperInvariant(),
                HostCase.Lower => word.ToLowerInvariant(),
                _ => word,
            };
        }

        /// <summary>Sends a text: "\n" becomes the line end, a character above 255 becomes '?'.</summary>
        private void Send(Session s, string text)
        {
            if (!s.Open || text.Length == 0)
            {
                return;
            }

            string end = _d.LineEnd;
            var buf = new List<byte>(text.Length + 16);
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '\n')
                {
                    for (int j = 0; j < end.Length; j++)
                    {
                        buf.Add((byte)end[j]);
                    }
                }
                else
                {
                    buf.Add(c <= 0xFF ? (byte)c : (byte)'?');
                }
            }

            _output.Send(s.Id, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(buf));
        }

        private void Close(Session s)
        {
            if (!s.Open)
            {
                return;
            }

            LeaveRoom(s);
            Forget(s);
            _output.Clear(s.Id);
        }

        private void Forget(Session s)
        {
            s.Open = false;
            if (_sessions.Remove(s.Id))
            {
                _order.Remove(s);
            }
        }

        // ------------------------------------------------------------------ types

        private enum State
        {
            User,
            Password,
            Shell,
            Menu,
            Room,
        }

        private sealed class Session
        {
            public Session(int id, string? calling, DateTime now, string cwd)
            {
                Id = id;
                Calling = calling;
                ConnectedAt = now;
                LastInput = now;
                Cwd = cwd;
            }

            public int Id { get; }

            public string? Calling { get; }

            public DateTime ConnectedAt { get; }

            public DateTime LastInput { get; set; }

            public DateTime LoggedInAt { get; set; }

            public bool Open { get; set; } = true;

            public State State { get; set; }

            public StringBuilder Line { get; } = new StringBuilder();

            public bool LastWasCr { get; set; }

            /// <summary>The name typed at the user prompt, before the password is checked.</summary>
            public string TypedUser { get; set; } = string.Empty;

            /// <summary>The name the caller logged in with, as typed; empty before a login (or with no login).</summary>
            public string User { get; set; } = string.Empty;

            public HostUser? Account { get; set; }

            public int Attempts { get; set; }

            public HostMenu? Menu { get; set; }

            public string? Room { get; set; }

            public string Cwd { get; set; }

            public bool Mesg { get; set; } = true;

            public long MessageSecond { get; set; } = -1;

            public int MessagesThisSecond { get; set; }
        }
    }
}
