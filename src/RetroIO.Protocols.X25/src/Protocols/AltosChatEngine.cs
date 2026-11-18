//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace RetroIO.Protocols.X25.Protocols
{
    /// <summary>
    /// The Altos CHAT 3.1 conference program (Hans Korneder, DataVision, Munich, 29 May 1987), the chat that ran on the
    /// Altos system reached over X.25 at NUA 0262 45 8900 40004. Rebuilt as one engine with no I/O, no clock and no
    /// threads: the host calls <see cref="Connect"/>, <see cref="Input"/>, <see cref="Disconnect"/>,
    /// <see cref="OutputDrained"/> and <see cref="Tick"/>, and the engine answers through an
    /// <see cref="IAltosChatSink"/> (send bytes, clear a call) and an <see cref="IAltosChatLog"/> (the server log).
    /// <para>
    /// The original C source is published at https://archive.org/details/altoschat and is the authority for every
    /// text, order and rule here. It is not kept in this repository; "CHAT 3.1 serv.c:123" in a comment means line 123
    /// of that file. The original was two programs: chat.c, one per user (a reader of the keyboard that answered some
    /// commands itself, plus a child process that copied the server's output to the terminal), and serv.c, one server
    /// that held the user table and passed text between users through named pipes. Here the per-user half is the
    /// <c>Session</c> and the server half is the user table; the texts, the order of the command checks and the rules
    /// are kept, and each "\n" of the original goes out as <see cref="AltosChatSettings.LineEnd"/>.
    /// </para>
    /// <para>
    /// Original bugs a, b, d are fixed and e is guarded (see the comments where each one was); bug c (a line of
    /// spaces is sent) is kept because it is what users saw. Not thread safe: the host calls it from one thread.
    /// </para>
    /// </summary>
    public sealed class AltosChatEngine
    {
        /// <summary>
        /// Most characters in one line, TEXTLEN (CHAT 3.1 chat.h:28). Chosen so one chat packet fits one 128-byte
        /// X.25 packet (chat.h:23-26). A longer line is cut into several.
        /// </summary>
        private const int TextLen = 118;

        /// <summary>
        /// The text of /usr/lib/chat_msg, written by the original INSTALL script, that .b sent with wall.
        /// </summary>
        private const string BellText = "+++ requesting for a chat +++\n";

        /// <summary>write_msg's "every channel" value: a channel below zero (CHAT 3.1 serv.c:198).</summary>
        private const long AllChannels = -1L;

        private readonly AltosChatSettings _settings;
        private readonly IAltosChatSink _sink;
        private readonly IAltosChatLog? _log;

        /// <summary>The users[] table of the server (CHAT 3.1 serv.c:18-28), one entry per place.</summary>
        private readonly UserSlot[] _users;

        /// <summary>
        /// The chans[] table of protected channels (CHAT 3.1 serv.c:29-33), as many entries as places.
        /// <see cref="ChanEntry.Chan"/> below zero is a free entry (serv.c:171, 116).
        /// </summary>
        private readonly ChanEntry[] _chans;

        /// <summary>Every call on the service, in the order they came, and the same by session number.</summary>
        private readonly List<Session> _sessions = new List<Session>();
        private readonly Dictionary<int, Session> _byId = new Dictionary<int, Session>();

        /// <summary>The last time the host gave us, through <see cref="Start"/> or <see cref="Tick"/>.</summary>
        private DateTime _now;

        /// <summary>Make the engine. Nothing is logged until <see cref="Start"/>.</summary>
        /// <param name="settings">The settings; read when needed, so later changes count.</param>
        /// <param name="sink">Where output and call clears go.</param>
        /// <param name="log">The server log, or null for none (the original logged only with -l).</param>
        public AltosChatEngine(AltosChatSettings settings, IAltosChatSink sink, IAltosChatLog? log)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _sink = sink ?? throw new ArgumentNullException(nameof(sink));
            _log = log;
            int n = settings.MaxUsers;
            if (n < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(settings), "MaxUsers must be at least 1.");
            }

            _users = new UserSlot[n];
            _chans = new ChanEntry[n];
            for (int i = 0; i < n; i++)
            {
                _users[i] = new UserSlot();

                // CHAT 3.1 serv.c:169-171: every place empty, every channel entry free.
                _chans[i] = new ChanEntry { Chan = -1L, Pass = "" };
            }
        }

        /// <summary>Number of users in the chat now (joined, not thrown out).</summary>
        public int UserCount
        {
            get
            {
                int n = 0;
                for (int i = 0; i < _users.Length; i++)
                {
                    if (_users[i].InUse)
                    {
                        n++;
                    }
                }

                return n;
            }
        }

        /// <summary>
        /// Copies the users in the chat now into <paramref name="into"/> (cleared first), lowest place first, as
        /// <c>.s</c> lists them. Read only: the chat is not changed and nobody is told.
        /// </summary>
        /// <param name="into">The list to fill.</param>
        /// <returns>The number of users copied.</returns>
        public int GetUsers(List<AltosChatUserInfo> into)
        {
            if (into == null)
            {
                throw new ArgumentNullException(nameof(into));
            }

            into.Clear();
            for (int i = 0; i < _users.Length; i++)
            {
                UserSlot u = _users[i];
                if (!u.InUse)
                {
                    continue;
                }

                DateTime connectedAt = u.Owner != null ? u.Owner.ConnectedAt : default;
                into.Add(new AltosChatUserInfo(i, u.Nickname, u.Login.TrimEnd(' '), u.From.TrimEnd(' '), u.Channel, connectedAt));
            }

            return into.Count;
        }

        /// <summary>
        /// Copies every channel that has at least one user on it into <paramref name="into"/> (cleared first), lowest
        /// channel number first, with its user count and whether it is protected (a <c>.p</c> password; the password is
        /// not copied). Read only.
        /// </summary>
        /// <param name="into">The list to fill.</param>
        /// <returns>The number of channels copied.</returns>
        public int GetChannels(List<AltosChatChannelInfo> into)
        {
            if (into == null)
            {
                throw new ArgumentNullException(nameof(into));
            }

            into.Clear();
            for (int i = 0; i < _users.Length; i++)
            {
                if (!_users[i].InUse)
                {
                    continue;
                }

                long chan = _users[i].Channel;

                // Already counted: a lower place is on the same channel.
                bool seen = false;
                for (int k = 0; k < into.Count; k++)
                {
                    if (into[k].Channel == chan)
                    {
                        into[k] = new AltosChatChannelInfo(chan, into[k].Users + 1, into[k].IsProtected);
                        seen = true;
                        break;
                    }
                }

                if (seen)
                {
                    continue;
                }

                bool isProtected = false;
                for (int e = 0; e < _chans.Length; e++)
                {
                    if (_chans[e].Chan >= 0 && _chans[e].Chan == chan)
                    {
                        isProtected = true;
                        break;
                    }
                }

                // Keep the list ordered by channel number (insertion; the list is at most MaxUsers long).
                int at = into.Count;
                while (at > 0 && into[at - 1].Channel > chan)
                {
                    at--;
                }

                into.Insert(at, new AltosChatChannelInfo(chan, 1, isProtected));
            }

            return into.Count;
        }

        /// <summary>
        /// The server starts: set the time and log "Chat-Server Starting At: ..." (CHAT 3.1 serv.c:166-167).
        /// </summary>
        /// <param name="now">The wall-clock time now.</param>
        public void Start(DateTime now)
        {
            _now = now;
            ToLog("Chat-Server Starting At: " + CTime(now));
        }

        /// <summary>
        /// Give the engine the time. Callers whose pause after "Waiting for connection." is over are joined now.
        /// </summary>
        /// <param name="now">The wall-clock time now. Also used in log lines and the .w list.</param>
        public void Tick(DateTime now)
        {
            _now = now;

            // Collect first: a join can clear its own call (table full) and change the list.
            List<Session>? due = null;
            for (int i = 0; i < _sessions.Count; i++)
            {
                Session s = _sessions[i];
                if (s.State == SessionState.Waiting && now >= s.JoinDue)
                {
                    due ??= new List<Session>();
                    due.Add(s);
                }
            }

            if (due == null)
            {
                return;
            }

            for (int i = 0; i < due.Count; i++)
            {
                if (due[i].State == SessionState.Waiting)
                {
                    JoinNow(due[i]);
                }
            }
        }

        /// <summary>
        /// A caller arrives. Sends the banner and "Waiting for connection." at once (CHAT 3.1 chat.c:248-250, 277); the
        /// join itself follows after <see cref="AltosChatSettings.WaitPause"/>, counted from the last time given.
        /// </summary>
        /// <param name="session">A number the host picks for this call, unique while the call lasts.</param>
        /// <param name="login">The login name; kept 8 characters, like the /etc/passwd name (chat.c:312).</param>
        /// <param name="from">Where the caller comes from (the calling X.121 address); kept 16 characters
        /// (chat.c:325). Null is "somewhere", empty is "localhost" (chat.c:326-328).</param>
        public void Connect(int session, string login, string? from)
        {
            if (_byId.ContainsKey(session))
            {
                throw new InvalidOperationException("Session " + session + " is already connected.");
            }

            var s = new Session(session)
            {
                // CHAT 3.1 chat.c:312-314 then chat.c:131: cut to 8, filter, then padded with spaces to 8. The server
                // copies the 8 bytes with no strip (serv.c:233), so the padding shows in .s (serv.c:293).
                Login = Pad(Filter(Cut(login ?? "", 8)), 8),
                From = Pad(FromRule(from), 16),
                ConnectedAt = _now,
                JoinDue = _now + _settings.WaitPause,
            };
            _sessions.Add(s);
            _byId.Add(session, s);

            // CHAT 3.1 chat.c:248-250, then the parent's chat.c:277.
            Msg(s, "Welcome to the original, evergreen\n");
            Msg(s, "Conference-utility \"CHAT\"  ver. 3.1\n");
            Msg(s, "Copyright (C) 1987 Hans Korneder, Muenchen\n");
            Msg(s, "Waiting for connection.\n");

            if (_settings.WaitPause <= TimeSpan.Zero)
            {
                JoinNow(s);
            }
        }

        /// <summary>
        /// Bytes from the caller, as the PAD forwarded them. Joined into lines; each line is handled as the original
        /// handled one line.
        /// </summary>
        /// <param name="session">The session.</param>
        /// <param name="data">The bytes. Unknown sessions are ignored.</param>
        public void Input(int session, ReadOnlySpan<byte> data)
        {
            if (!_byId.TryGetValue(session, out Session? s))
            {
                return;
            }

            if (s.State == SessionState.Waiting)
            {
                // The original's parent sat in pause() (chat.c:278); what the user typed waited in the terminal
                // buffer and was read after log_on. Keep it for the join.
                for (int i = 0; i < data.Length; i++)
                {
                    s.Early.Add(data[i]);
                }

                return;
            }

            for (int i = 0; i < data.Length; i++)
            {
                // get_line, CHAT 3.1 chat.c:161-170, one byte at a time.
                int c = data[i] & 0x7F;
                bool end = c == 10 || c == 13;

                // c == 10 ends the line in the original. CR ends it here too: the PAD forwards on CR, where on the
                // Altos the Unix terminal driver had turned CR into LF before chat read it.
                if (!end)
                {
                    if (c < ' ' || c > 126)
                    {
                        continue;
                    }

                    s.Line[s.LineLength++] = (byte)c;
                    if (s.LineLength < TextLen)
                    {
                        continue;
                    }
                }

                // A line is complete: at a line end, or full at 118 characters (the loop of chat.c:161 stops there
                // and the next characters start a new line, so a remainder ".x" leaves).
                int len = s.LineLength;
                s.LineLength = 0;
                bool alive = HandleLine(s, new ReadOnlySpan<byte>(s.Line, 0, len));
                if (!alive)
                {
                    // The user left (.x) or the call is gone: the rest of the input is never read.
                    return;
                }
            }
        }

        /// <summary>
        /// The call was cleared by the network or the caller. A user in the chat leaves as with .x: the others see
        /// "---nick---" (the original's SIGHUP ran log_off, CHAT 3.1 chat.c:246, 101-125). No clear is sent back.
        /// </summary>
        /// <param name="session">The session. Unknown sessions are ignored.</param>
        public void Disconnect(int session)
        {
            if (!_byId.TryGetValue(session, out Session? s))
            {
                return;
            }

            Leave(s, false);
        }

        /// <summary>
        /// The host has delivered <paramref name="bytes"/> of this session's output. Lowers the count that
        /// <see cref="AltosChatSettings.OutputLimitBytes"/> is checked against; never below zero.
        /// </summary>
        /// <param name="session">The session.</param>
        /// <param name="bytes">Bytes delivered.</param>
        public void OutputDrained(int session, int bytes)
        {
            if (!_byId.TryGetValue(session, out Session? s) || bytes <= 0)
            {
                return;
            }

            s.Pending -= bytes;
            if (s.Pending < 0)
            {
                s.Pending = 0;
            }
        }

        // ------------------------------------------------------------------ the per-user side (chat.c)

        /// <summary>
        /// The end of the wait: the server's log_on (CHAT 3.1 serv.c:221-256) and the child's
        /// "Connection established." (chat.c:270).
        /// </summary>
        private void JoinNow(Session s)
        {
            // Find the lowest free place (CHAT 3.1 serv.c:223-224).
            int i = 0;
            while (i < _users.Length && _users[i].InUse)
            {
                i++;
            }

            if (i == _users.Length)
            {
                // Table full: the server ignored the request (serv.c:225-229), the child's open of its answer pipe
                // timed out (chat.c:261-269) and the user was put out. The original timed out after 5 seconds from
                // the start rather than 3; the port uses the one pause for both.
                Msg(s, "open of answerpipe failed.\n");
                ClearCall(s);
                return;
            }

            s.State = SessionState.Joined;
            s.Slot = i;

            // The child prints this once the server has opened the pipe (chat.c:262-270), before it copies
            // anything the server writes, so it comes before "+++nick+++".
            Msg(s, "Connection established.\nGet help with:    .h\n");

            UserSlot u = _users[i];
            u.InUse = true;
            u.Owner = s;
            u.Login = s.Login;
            u.From = s.From;
            u.Channel = 0; // fill() with 0 (serv.c:231)

            // CHAT 3.1 chat.c:317 strncpy 32, filter; chat.c:131 pads to 32; serv.c:234, 237 cut and strip spaces.
            u.Nickname = Filter(Cut(_settings.StartingNickname ?? "", 32)).TrimEnd(' ');

            ToLog("Incoming Call From " + u.From + " At " + CTime(_now)); // serv.c:250-252
            string text = "+++" + u.Nickname + "+++\n";                   // serv.c:253
            ToLogText(text);
            for (int k = 0; k < _users.Length; k++)
            {
                WriteMsg(k, AllChannels, text);
            }

            if (s.Early.Count > 0 && s.State == SessionState.Joined)
            {
                byte[] early = s.Early.ToArray();
                s.Early.Clear();
                Input(s.Id, early);
            }
        }

        /// <summary>
        /// One line, as chat.c's process() (CHAT 3.1 chat.c:173-239) and then the server's process()
        /// (serv.c:266-383). The checks are in the original order and match on the start of the line, case kept.
        /// </summary>
        /// <returns>False when the session is gone (the user left).</returns>
        private bool HandleLine(Session s, ReadOnlySpan<byte> line)
        {
            // ---- the user's own program first (chat.c:179-235) ----
            if (StartsWith(line, ".w"))
            {
                // The original ran /usr/bin/who on the Unix host (chat.c:179-186). There is no host here: list the
                // calls on this service in a who-like layout.
                WhoList(s);
                Msg(s, "--- back in chat ---\n");
                return true;
            }

            if (StartsWith(line, ".m"))
            {
                Msg(s, "This is disabled by default- use SMTP!\n"); // chat.c:187-189
                return true;
            }

            if (StartsWith(line, ".b"))
            {
                // The original ran "wall < /usr/lib/chat_msg" (chat.c:199-203); wall reaches every terminal, the
                // sender's too. Here: every call on this service.
                for (int k = 0; k < _sessions.Count; k++)
                {
                    Msg(_sessions[k], BellText);
                }

                Msg(s, "--- back in chat ---\n");
                return true;
            }

            if (StartsWith(line, ".h"))
            {
                Help(s); // chat.c:205-209
                return true;
            }

            if (StartsWith(line, ".q") || StartsWith(line, ".x"))
            {
                // chat.c:210-213: leave the loop, then log_off (chat.c:283), and the program ends: the call is cleared.
                Leave(s, true);
                return false;
            }

            if (StartsWith(line, "--!"))
            {
                Msg(s, "Function is being disabled by default\n"); // chat.c:214-217, the shell escape
                return true;
            }

            if (StartsWith(line, ".so"))
            {
                Msg(s, "Function is being disabled by default\n"); // chat.c:224-228, read commands from a file
                return true;
            }

            // ---- else the line goes to the server (chat.c:237) ----
            if (s.State != SessionState.Joined)
            {
                // A user thrown out as slow: the server no longer knows them and ignores the packet (serv.c:267-272).
                return true;
            }

            ServerText(s.Slot, line);
            return true;
        }

        /// <summary>
        /// The user leaves: the server's log_off when still in the chat, then the call goes.
        /// </summary>
        private void Leave(Session s, bool clearCall)
        {
            if (s.State == SessionState.Joined)
            {
                LogOff(s.Slot);
            }

            if (clearCall)
            {
                ClearCall(s);
            }
            else
            {
                Forget(s);
            }
        }

        private void ClearCall(Session s)
        {
            Forget(s);
            _sink.Clear(s.Id);
        }

        private void Forget(Session s)
        {
            s.State = SessionState.Gone;
            _sessions.Remove(s);
            _byId.Remove(s.Id);
        }

        /// <summary>The help text, CHAT 3.1 chat.c:87-99.</summary>
        private void Help(Session s)
        {
            Msg(s, ".x           exit this program\n");
            Msg(s, ".s           get the list of users on the chat\n");
            Msg(s, ".n name      change your nickname\n");
            Msg(s, ".h           get this page of text\n");
            Msg(s, ".w           get a list of users on the system\n");
            Msg(s, ".b           bell all users\n");
            Msg(s, ".m           send mail to the sysop\n");
            Msg(s, ".c #         switch to channel no <#>\n");
            Msg(s, ".p passwd    set a passwd for a channel\n");
            Msg(s, "!# text      send a private message to user <#>\n");
        }

        /// <summary>
        /// .w: one row per call on this service, name, line and time. The layout "%-8.8s %-12.12s %.12s" is this
        /// port's choice in the manner of who(1); the original only ran /usr/bin/who. The line column is the
        /// caller's origin, cut to 12.
        /// </summary>
        private void WhoList(Session s)
        {
            for (int k = 0; k < _sessions.Count; k++)
            {
                Session o = _sessions[k];
                string from = o.From.Length > 12 ? o.From.Substring(0, 12) : o.From;
                string when = CTime(o.ConnectedAt).Substring(4, 12);
                Msg(s, o.Login + " " + Pad(from, 12) + " " + when + "\n");
            }
        }

        // ------------------------------------------------------------------ the server side (serv.c)

        /// <summary>
        /// Text from user <paramref name="i"/>: CHAT 3.1 serv.c:273-383 in the original order of checks.
        /// </summary>
        private void ServerText(int i, ReadOnlySpan<byte> line)
        {
            // serv.c:273 blankstrip(cp_text, TEXTLEN) starts at the 118th byte and stops at the first non-space. For
            // a shorter line that byte is past the line's own end marker, so the line keeps its spaces (bug c, kept).
            // A full 118-character line has no end marker (bug b, chat.c:169) and does lose its trailing spaces.
            if (line.Length == TextLen)
            {
                int n = line.Length;
                while (n > 0 && line[n - 1] == ' ')
                {
                    n--;
                }

                line = line.Slice(0, n);
            }

            if (line.Length == 0)
            {
                return; // serv.c:274: empty lines are ignored
            }

            UserSlot me = _users[i];
            string text = Encoding.ASCII.GetString(line);

            // Bug b, fixed: the original's strlen ran past a 118-character line into the next bytes in memory (the
            // server's buf, serv.c:34-35). Here the text is exactly the 118 characters.

            if (StartsWith(line, ".n "))
            {
                // serv.c:276-285. The announcement shows the whole new name; the name kept is cut to 32.
                string msg = ">>>" + me.Nickname + ">>>" + text.Substring(3) + ">>>\n";
                ToLogText(msg);
                long chan = me.Channel;
                for (int k = 0; k < _users.Length; k++)
                {
                    WriteMsg(k, chan, msg);
                }

                if (me.InUse)
                {
                    me.Nickname = Cut(text.Substring(3), 32);
                }

                return;
            }

            if (StartsWith(line, ".s"))
            {
                // serv.c:286-301. Every user on every channel, by place.
                WriteMsg(i, AllChannels, "No Chan       From             User     Called\n");
                for (int k = 0; k < _users.Length; k++)
                {
                    UserSlot u = _users[k];
                    if (u.InUse)
                    {
                        // "%2d %10ld %s %s %s\n" (serv.c:293); from and login keep their padding.
                        string row = k.ToString(CultureInfo.InvariantCulture).PadLeft(2) + " " +
                                     u.Channel.ToString(CultureInfo.InvariantCulture).PadLeft(10) + " " +
                                     u.From + " " + u.Login + " " + u.Nickname + "\n";
                        WriteMsg(i, AllChannels, row);
                    }
                }

                return;
            }

            if (StartsWith(line, ".p "))
            {
                Protect(i, text);
                return;
            }

            if (StartsWith(line, ".c"))
            {
                ChangeChannel(i, text, line);
                return;
            }

            if (StartsWith(line, "!"))
            {
                // serv.c:368-376: to place N on any channel, the whole line quoted, no confirmation. A bad number or an
                // empty place is silent (write_msg's checks, serv.c:194-195).
                long target = Atol(line.Slice(1));
                string msg = me.Nickname + " whispers: " + text + "\n";
                ToLog("[" + i.ToString(CultureInfo.InvariantCulture) + ":" +
                      me.Channel.ToString(CultureInfo.InvariantCulture) + "]" + msg.Substring(0, msg.Length - 1));
                if (target >= 0 && target < _users.Length)
                {
                    WriteMsg((int)target, AllChannels, msg);
                }

                return;
            }

            // serv.c:377-383: plain text to the others on the same channel, not to the sender.
            string says = me.Nickname + " says: " + text + "\n";
            ToLog("[" + i.ToString(CultureInfo.InvariantCulture) + ":" +
                  me.Channel.ToString(CultureInfo.InvariantCulture) + "]" + says.Substring(0, says.Length - 1));
            long ch = me.Channel;
            for (int k = 0; k < _users.Length; k++)
            {
                if (k != i)
                {
                    // Bug a, fixed: the original built this in the shared buf (serv.c:378), and a slow user thrown out
                    // during this loop overwrote it with "---nick---" (serv.c:127), so later users got that twice
                    // and never the line. Here the text is this local string and cannot change.
                    WriteMsg(k, ch, says);
                }
            }
        }

        /// <summary>.p passwd, CHAT 3.1 serv.c:302-332.</summary>
        private void Protect(int i, string text)
        {
            UserSlot me = _users[i];
            if (me.Channel == 0)
            {
                WriteMsg(i, AllChannels, "Channel 0 cannot be protected.\n");
                return;
            }

            int n = 0;
            for (int k = 0; k < _users.Length; k++)
            {
                if (_users[k].InUse && _users[k].Channel == me.Channel)
                {
                    n++;
                }
            }

            if (n > 1)
            {
                WriteMsg(i, AllChannels, "You are not alone on the channel.\n");
                return;
            }

            // The channel's own entry, else the first free one (serv.c:320-325).
            int e = 0;
            while (e < _chans.Length && _chans[e].Chan != me.Channel)
            {
                e++;
            }

            if (e == _chans.Length)
            {
                e = 0;
                while (e < _chans.Length && _chans[e].Chan >= 0)
                {
                    e++;
                }
            }

            if (e == _chans.Length)
            {
                // Bug e, guarded: the original went on to write chans[NUSERS], past the table (serv.c:323-326). It
                // cannot happen here: each protected channel has at least one user on it (check_free frees it when
                // empty) and there are as many entries as places, so a free entry always exists. Nothing is done.
                return;
            }

            _chans[e].Chan = me.Channel;
            _chans[e].Pass = Cut(text.Substring(3), 8); // strncpy 8 (serv.c:328)
        }

        /// <summary>.c N[,passwd], CHAT 3.1 serv.c:333-367.</summary>
        private void ChangeChannel(int i, string text, ReadOnlySpan<byte> line)
        {
            UserSlot me = _users[i];
            long old = me.Channel;
            long chan = Atol(line.Slice(2));

            // serv.c:338 "if (new<0) new = -new;". Bug d, fixed: in the original's 32-bit long the most negative
            // number stays negative, and a negative channel is "every channel" to write_msg (serv.c:198), so that
            // user's text reached everyone. Atol gives a 32-bit value; the sign is taken off in 64 bits, so
            // -2147483648 becomes channel 2147483648, a channel of its own.
            if (chan < 0)
            {
                chan = -chan;
            }

            if (old == chan)
            {
                return; // same channel: silent (serv.c:339)
            }

            int e = 0;
            while (e < _chans.Length && _chans[e].Chan != chan)
            {
                e++;
            }

            if (e < _chans.Length)
            {
                // Protected (serv.c:342-357). The password follows the first comma anywhere in the line.
                int comma = text.IndexOf(',');
                if (comma < 0)
                {
                    WriteMsg(i, AllChannels, "Channel is protected. Use a passwd\n");
                    return;
                }

                if (!StrNCmpEqual(_chans[e].Pass, text.Substring(comma + 1), 8))
                {
                    WriteMsg(i, AllChannels, "Wrong passwd.\n");
                    return;
                }
            }

            // serv.c:358-366: told to the old and the new channel, place by place, then logged.
            string msg = ">>>" + me.Nickname + ">>> chan " + chan.ToString(CultureInfo.InvariantCulture) + "\n";
            for (int k = 0; k < _users.Length; k++)
            {
                WriteMsg(k, old, msg);
                WriteMsg(k, chan, msg);
            }

            ToLogText(msg);
            if (me.InUse)
            {
                me.Channel = chan;
            }

            CheckFree(old);
        }

        /// <summary>
        /// A user leaves the chat: CHAT 3.1 serv.c:119-131. The place is freed first, so the leaver is not told.
        /// The session itself stays (a slow user thrown out keeps the call, deaf); the caller decides about the call.
        /// </summary>
        private void LogOff(int i)
        {
            UserSlot u = _users[i];
            u.InUse = false;
            Session? owner = u.Owner;
            u.Owner = null;
            if (owner != null && owner.State == SessionState.Joined)
            {
                owner.State = SessionState.Deaf;
                owner.Slot = -1;
            }

            string text = "---" + u.Nickname + "---\n";
            ToLogText(text);
            for (int k = 0; k < _users.Length; k++)
            {
                WriteMsg(k, AllChannels, text);
            }

            CheckFree(u.Channel);
        }

        /// <summary>A channel with nobody left loses its password (CHAT 3.1 serv.c:107-117).</summary>
        private void CheckFree(long ch)
        {
            for (int k = 0; k < _users.Length; k++)
            {
                if (_users[k].InUse && _users[k].Channel == ch)
                {
                    return;
                }
            }

            for (int k = 0; k < _chans.Length; k++)
            {
                if (_chans[k].Chan == ch)
                {
                    _chans[k].Chan = -1L;
                }
            }
        }

        /// <summary>
        /// The server writes to one user: CHAT 3.1 serv.c:188-205. Only to a place in use, and only when on
        /// <paramref name="channel"/> unless that is <see cref="AllChannels"/>. A write that does not fit under the
        /// output limit counts as the original's write that blocked more than 2 seconds: the user is thrown out
        /// (log_off) and their call stays, deaf. The original also accepted a part-written message; here it is the
        /// whole message or nothing.
        /// </summary>
        private void WriteMsg(int person, long channel, string text)
        {
            if (person < 0 || person >= _users.Length)
            {
                return;
            }

            UserSlot u = _users[person];
            if (!u.InUse)
            {
                return;
            }

            if (channel >= 0 && channel != u.Channel)
            {
                return;
            }

            Session s = u.Owner!;
            int bytes = WireLength(text);
            if (s.Pending + bytes > _settings.OutputLimitBytes)
            {
                LogOff(person);
                return;
            }

            Msg(s, text);
        }

        // ------------------------------------------------------------------ output, log, helpers

        /// <summary>
        /// Send text to a session, each "\n" as the line end. Counts toward the session's waiting output. The
        /// user program's own messages (chat.c's msg()) use this directly and never throw anyone out.
        /// </summary>
        private void Msg(Session s, string text)
        {
            string end = _settings.LineEnd ?? "\r\n";
            int size = WireLength(text);
            byte[] buf = ArrayPool<byte>.Shared.Rent(size);
            try
            {
                int o = 0;
                for (int i = 0; i < text.Length; i++)
                {
                    char c = text[i];
                    if (c == '\n')
                    {
                        for (int j = 0; j < end.Length; j++)
                        {
                            buf[o++] = (byte)end[j];
                        }
                    }
                    else
                    {
                        buf[o++] = (byte)c;
                    }
                }

                s.Pending += o;
                _sink.Send(s.Id, new ReadOnlySpan<byte>(buf, 0, o));
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buf);
            }
        }

        private int WireLength(string text)
        {
            int endLen = (_settings.LineEnd ?? "\r\n").Length;
            int n = 0;
            for (int i = 0; i < text.Length; i++)
            {
                n += text[i] == '\n' ? endLen : 1;
            }

            return n;
        }

        private void ToLog(string line)
        {
            _log?.Line(line);
        }

        /// <summary>Log a text that ends in "\n", without the "\n".</summary>
        private void ToLogText(string text)
        {
            ToLog(text.Substring(0, text.Length - 1));
        }

        /// <summary>
        /// ctime() without its "\n", the "%.24s" of CHAT 3.1 serv.c:167, 251: "Fri May 29 12:00:00 1987", the day of
        /// the month padded with a space.
        /// </summary>
        private static string CTime(DateTime t)
        {
            CultureInfo ci = CultureInfo.InvariantCulture;
            return t.ToString("ddd MMM ", ci) + t.Day.ToString(ci).PadLeft(2) + t.ToString(" HH:mm:ss yyyy", ci);
        }

        /// <summary>
        /// atol() as the 1987 C library had it: spaces skipped, one sign, digits in a 32-bit long that wraps.
        /// UNVERIFIED: the Altos library itself was not seen; this is the classic Unix atol, with '+' accepted as
        /// the C standard says. Only spaces can come before the number, the line has no other white space left.
        /// </summary>
        private static long Atol(ReadOnlySpan<byte> s)
        {
            int p = 0;
            while (p < s.Length && s[p] == ' ')
            {
                p++;
            }

            bool neg = false;
            if (p < s.Length && (s[p] == '-' || s[p] == '+'))
            {
                neg = s[p] == '-';
                p++;
            }

            int n = 0;
            unchecked
            {
                while (p < s.Length && s[p] >= '0' && s[p] <= '9')
                {
                    n = (n * 10) + (s[p] - '0');
                    p++;
                }

                return neg ? -n : n;
            }
        }

        /// <summary>
        /// strncmp(a, b, n) == 0 for two C strings: equal up to n characters or up to where both end.
        /// </summary>
        private static bool StrNCmpEqual(string a, string b, int n)
        {
            for (int i = 0; i < n; i++)
            {
                int ca = i < a.Length ? a[i] : 0;
                int cb = i < b.Length ? b[i] : 0;
                if (ca != cb)
                {
                    return false;
                }

                if (ca == 0)
                {
                    return true;
                }
            }

            return true;
        }

        private static bool StartsWith(ReadOnlySpan<byte> line, string prefix)
        {
            if (line.Length < prefix.Length)
            {
                return false;
            }

            for (int i = 0; i < prefix.Length; i++)
            {
                if (line[i] != prefix[i])
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>filter(), CHAT 3.1 chat.c:73-85: top bit off, control characters and DEL out.</summary>
        private static string Filter(string text)
        {
            var sb = new StringBuilder(text.Length);
            for (int i = 0; i < text.Length; i++)
            {
                int c = text[i] & 0x7F;
                if (c < ' ' || c > 126)
                {
                    continue;
                }

                sb.Append((char)c);
            }

            return sb.ToString();
        }

        /// <summary>
        /// Where the caller comes from: CHAT 3.1 chat.c:325-329. strncpy 16, the Linux patch's two special cases,
        /// then filter().
        /// </summary>
        private static string FromRule(string? from)
        {
            string f;
            if (from == null)
            {
                f = "somewhere";
            }
            else
            {
                f = Cut(from, 16);
                if (f.Length > 0 && f[0] == ':')
                {
                    f = "Xlocalhost";
                }

                if (f.Length == 0)
                {
                    f = "localhost";
                }
            }

            return Filter(f);
        }

        private static string Cut(string text, int max) => text.Length > max ? text.Substring(0, max) : text;

        private static string Pad(string text, int width) => Cut(text, width).PadRight(width);

        // ------------------------------------------------------------------ types

        private enum SessionState
        {
            /// <summary>Banner sent, the pause after "Waiting for connection." not over.</summary>
            Waiting,

            /// <summary>In the chat, holding a place.</summary>
            Joined,

            /// <summary>Thrown out as slow (CHAT 3.1 serv.c:203-204): no place, the call stays until .x.</summary>
            Deaf,

            /// <summary>Gone.</summary>
            Gone,
        }

        /// <summary>One call: the original's user program (chat.c) for one user.</summary>
        private sealed class Session
        {
            public Session(int id)
            {
                Id = id;
            }

            public int Id { get; }

            public SessionState State { get; set; } = SessionState.Waiting;

            /// <summary>The place in the user table while <see cref="SessionState.Joined"/>, else -1.</summary>
            public int Slot { get; set; } = -1;

            /// <summary>The login name padded to 8.</summary>
            public string Login { get; set; } = "";

            /// <summary>The origin padded to 16.</summary>
            public string From { get; set; } = "";

            public DateTime ConnectedAt { get; set; }

            public DateTime JoinDue { get; set; }

            /// <summary>Bytes sent and not yet reported delivered.</summary>
            public long Pending { get; set; }

            /// <summary>The line being read, cp_text (CHAT 3.1 chat.h:59).</summary>
            public byte[] Line { get; } = new byte[TextLen];

            public int LineLength { get; set; }

            /// <summary>Input that came while waiting.</summary>
            public List<byte> Early { get; } = new List<byte>();
        }

        /// <summary>struct users, CHAT 3.1 serv.c:18-28.</summary>
        private sealed class UserSlot
        {
            public bool InUse { get; set; }

            public Session? Owner { get; set; }

            public string Login { get; set; } = "";

            public string Nickname { get; set; } = "";

            public long Channel { get; set; }

            public string From { get; set; } = "";
        }

        /// <summary>struct chans, CHAT 3.1 serv.c:29-33.</summary>
        private struct ChanEntry
        {
            public long Chan;

            public string Pass;
        }
    }
}
