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
    /// <summary>How typed words are compared with the words in the file.</summary>
    public enum HostCase
    {
        /// <summary>As typed.</summary>
        Exact,

        /// <summary>Upper case: typed words are made upper case first (VMS).</summary>
        Upper,

        /// <summary>Lower case: typed words are made lower case first.</summary>
        Lower,
    }

    /// <summary>How <c>{date}</c>, <c>{time}</c> and <c>do = time</c> are written.</summary>
    public enum HostTimeFormat
    {
        /// <summary>2026-11-18 15:04:05.</summary>
        Iso,

        /// <summary>18-NOV-2026 15:04:05.</summary>
        Vms,

        /// <summary>Wed Nov 18 15:04:05 2026.</summary>
        Unix,
    }

    /// <summary>
    /// A host folder read and checked: what a <see cref="DescribedHost"/> pretends to be. The format is
    /// docs/x25host-format.md in RetroX25. Nothing in it runs: it only says what to print and which typed words lead
    /// where.
    /// </summary>
    /// <remarks>
    /// Everything wrong stops loading with a <see cref="FormatException"/> naming the file, the line and the name: an
    /// unknown section, key, placeholder or built-in action, a menu or text file that is not there, a text file name
    /// that would leave the folder. A typo is never a silently missing feature.
    /// </remarks>
    public sealed class HostDescription
    {
        /// <summary>X.25 (10/96) 5.2.1: at most 15 digits in an address.</summary>
        private const int MaxAddressDigits = 15;

        /// <summary>The placeholders of the format's table. The table is the whole list.</summary>
        private static readonly HashSet<string> Placeholders = new HashSet<string>(StringComparer.Ordinal)
        {
            "date", "time", "hostname", "node", "address", "calling", "sessions", "uptime", "nl",
            "user", "cwd", "word", "args", "from", "text", "since", "idle",
        };

        /// <summary>The closed list of built-in actions (besides <c>menu.&lt;name&gt;</c> and <c>room.&lt;name&gt;</c>).</summary>
        private static readonly HashSet<string> Actions = new HashSet<string>(StringComparer.Ordinal)
        {
            "logout", "echo", "time", "who", "ls", "cat", "cd", "clear", "finger", "wall", "send", "mesg",
        };

        private HostDescription()
        {
        }

        // ------------------------------------------------------------------ [host]

        /// <summary>The machine's name: <c>{hostname}</c> and <c>{node}</c>.</summary>
        public string Hostname { get; private set; } = string.Empty;

        /// <summary>The X.121 address the host answers.</summary>
        public string Address { get; private set; } = string.Empty;

        /// <summary>Calls served at once.</summary>
        public int Sessions { get; private set; } = 8;

        /// <summary>Sent for every "\n" of a text.</summary>
        public string LineEnd { get; private set; } = "\r\n";

        /// <summary>Minutes with no input before a call is cleared; 0 = never.</summary>
        public int IdleMinutes { get; private set; } = 15;

        /// <summary>Answer only calls whose call user data starts 01 00 00 00 or is empty.</summary>
        public bool PadCallsOnly { get; private set; } = true;

        /// <summary>The banner text, or null.</summary>
        public string? Banner { get; private set; }

        /// <summary>Where a call starts: <c>login</c>, <c>shell</c> or <c>menu.&lt;name&gt;</c>.</summary>
        public string Start { get; private set; } = string.Empty;

        // ------------------------------------------------------------------ [login], [user.N]

        /// <summary>The login, or null when the host has none.</summary>
        public HostLogin? Login { get; private set; }

        /// <summary>The accounts, in the order of their numbers.</summary>
        public List<HostUser> Users { get; } = new List<HostUser>();

        // ------------------------------------------------------------------ [shell], [command.N], [menu.x], [file.N]

        /// <summary>The command line.</summary>
        public HostShell Shell { get; } = new HostShell();

        /// <summary>The commands, in the order of their numbers.</summary>
        public List<HostCommand> Commands { get; } = new List<HostCommand>();

        /// <summary>The menus by name.</summary>
        public Dictionary<string, HostMenu> Menus { get; } = new Dictionary<string, HostMenu>(StringComparer.Ordinal);

        /// <summary>The fake file tree, in the order of the numbers.</summary>
        public List<HostFile> Files { get; } = new List<HostFile>();

        /// <summary>True when any path starts with "/": a Unix-style tree whose first directory is "/".</summary>
        public bool RootedFiles { get; private set; }

        /// <summary>Reads a host folder.</summary>
        /// <param name="folder">The folder that holds host.ini.</param>
        /// <param name="hostname">The name of this one machine (the machine list's <c>hostname</c>, or <c>-n</c>); null
        /// takes <c>[host] hostname</c>, then the folder name.</param>
        /// <param name="address">The address of this one machine; null takes <c>[host] address</c>.</param>
        /// <returns>The description.</returns>
        /// <exception cref="FormatException">Anything wrong; the message names the file, line and name.</exception>
        public static HostDescription Load(string folder, string? hostname, string? address)
        {
            string full = Path.GetFullPath(folder);
            string ini = Path.Combine(full, "host.ini");
            if (!File.Exists(ini))
            {
                throw new FormatException(ini + ": no such file");
            }

            return Parse(File.ReadAllText(ini), ini, Path.GetFileName(full.TrimEnd('\\', '/')), name =>
            {
                string path = Path.Combine(full, name);
                return File.Exists(path) ? File.ReadAllText(path) : null;
            }, hostname, address);
        }

        /// <summary>Reads a host.ini given as text, with its text files given by <paramref name="readFile"/>.</summary>
        /// <param name="iniText">The text of host.ini.</param>
        /// <param name="iniName">The file's name, for messages.</param>
        /// <param name="folderName">The folder's name: the hostname when nothing else gives one.</param>
        /// <param name="readFile">Reads a text file of the folder by its name; null when it is not there. Only called
        /// with a name that has no <c>/</c>, <c>\</c> or <c>..</c>.</param>
        /// <param name="hostname">See <see cref="Load"/>.</param>
        /// <param name="address">See <see cref="Load"/>.</param>
        /// <returns>The description.</returns>
        /// <exception cref="FormatException">Anything wrong; the message names the file, line and name.</exception>
        public static HostDescription Parse(string iniText, string iniName, string folderName, Func<string, string?> readFile,
            string? hostname, string? address)
        {
            ArgumentNullException.ThrowIfNull(iniText);
            ArgumentNullException.ThrowIfNull(readFile);
            var d = new HostDescription();
            var reader = new Reader(d, iniName, readFile);
            reader.Read(iniText);
            reader.Finish(folderName, hostname, address);
            return d;
        }

        // ------------------------------------------------------------------ the reader

        /// <summary>Reads the file once, section by section, and checks what refers to what at the end.</summary>
        private sealed class Reader
        {
            private readonly HostDescription _d;
            private readonly string _file;
            private readonly Func<string, string?> _readFile;
            private readonly Dictionary<string, string> _texts = new Dictionary<string, string>(StringComparer.Ordinal);

            // Section numbers seen, so [user.3] [user.1] keep the order of their numbers.
            private readonly SortedDictionary<int, HostUser> _users = new SortedDictionary<int, HostUser>();
            private readonly SortedDictionary<int, HostCommand> _commands = new SortedDictionary<int, HostCommand>();
            private readonly SortedDictionary<int, HostFile> _files = new SortedDictionary<int, HostFile>();
            private readonly HashSet<string> _sectionsSeen = new HashSet<string>(StringComparer.Ordinal);

            // Things to check once everything is read: (where, target).
            private readonly List<(string Where, string Target)> _gotos = new List<(string, string)>();

            private string _section = string.Empty;
            private int _sectionLine;
            private object? _current;
            private string? _hostnameKey;
            private string? _startKey;
            private string _startWhere = string.Empty;

            public Reader(HostDescription d, string file, Func<string, string?> readFile)
            {
                _d = d;
                _file = file;
                _readFile = readFile;
            }

            public void Read(string text)
            {
                string[] lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
                for (int n = 0; n < lines.Length; n++)
                {
                    string where = _file + ":" + (n + 1).ToString(CultureInfo.InvariantCulture);
                    string line = StripComment(lines[n]).Trim();
                    if (line.Length == 0)
                    {
                        continue;
                    }

                    if (line[0] == '[')
                    {
                        if (line[line.Length - 1] != ']')
                        {
                            throw new FormatException(where + ": a section line must end with ]");
                        }

                        OpenSection(line.Substring(1, line.Length - 2).Trim(), where, n + 1);
                        continue;
                    }

                    int eq = line.IndexOf('=');
                    if (eq <= 0)
                    {
                        throw new FormatException(where + ": expected key = value");
                    }

                    if (_section.Length == 0)
                    {
                        throw new FormatException(where + ": a key before the first section");
                    }

                    string key = line.Substring(0, eq).Trim();
                    string value = Unquote(line.Substring(eq + 1).Trim());
                    SetKey(key, value, where + ": [" + _section + "] " + key);
                }
            }

            private void OpenSection(string name, string where, int line)
            {
                if (!_sectionsSeen.Add(name))
                {
                    throw new FormatException(where + ": [" + name + "] is given twice");
                }

                _section = name;
                _sectionLine = line;
                if (name == "host" || name == "shell")
                {
                    _current = null;
                }
                else if (name == "login")
                {
                    _d.Login = new HostLogin();
                    _current = _d.Login;
                }
                else if (TryNumber(name, "user.", out int u))
                {
                    var user = new HostUser { Line = line };
                    _users.Add(u, user);
                    _current = user;
                }
                else if (TryNumber(name, "command.", out int c))
                {
                    var command = new HostCommand { Where = where + ": [" + name + "]" };
                    _commands.Add(c, command);
                    _current = command;
                }
                else if (TryNumber(name, "file.", out int f))
                {
                    var file = new HostFile { Where = where + ": [" + name + "]" };
                    _files.Add(f, file);
                    _current = file;
                }
                else if (name.StartsWith("menu.", StringComparison.Ordinal) && name.Length > 5)
                {
                    var menu = new HostMenu { Name = name.Substring(5) };
                    _d.Menus.Add(menu.Name, menu);
                    _current = menu;
                }
                else
                {
                    throw new FormatException(where + ": unknown section [" + name + "]");
                }
            }

            private void SetKey(string key, string value, string where)
            {
                switch (_current)
                {
                    case null when _section == "host":
                        SetHost(key, value, where);
                        return;
                    case null when _section == "shell":
                        SetShell(key, value, where);
                        return;
                    case HostLogin login:
                        SetLogin(login, key, value, where);
                        return;
                    case HostUser user:
                        SetUser(user, key, value, where);
                        return;
                    case HostCommand command:
                        SetCommand(command, key, value, where);
                        return;
                    case HostFile file:
                        SetFile(file, key, value, where);
                        return;
                    case HostMenu menu:
                        SetMenu(menu, key, value, where);
                        return;
                    default:
                        throw new FormatException(where + ": unknown key");
                }
            }

            private void SetHost(string key, string value, string where)
            {
                switch (key)
                {
                    case "hostname":
                        _hostnameKey = value;
                        break;
                    case "address":
                        _d.Address = Address(value, where);
                        break;
                    case "sessions":
                        _d.Sessions = Number(value, 1, 10000, where);
                        break;
                    case "line_end":
                        _d.LineEnd = value switch
                        {
                            "crlf" => "\r\n",
                            "cr" => "\r",
                            "lf" => "\n",
                            _ => throw new FormatException(where + " = " + value + " is not one of: crlf, cr, lf"),
                        };
                        break;
                    case "idle_minutes":
                        _d.IdleMinutes = Number(value, 0, 100000, where);
                        break;
                    case "pad_calls_only":
                        _d.PadCallsOnly = YesNo(value, where);
                        break;
                    case "banner":
                        _d.Banner = TextFile(value, where);
                        break;
                    case "start":
                        _startKey = value;
                        _startWhere = where;
                        break;
                    default:
                        throw new FormatException(where + ": unknown key");
                }
            }

            private void SetLogin(HostLogin l, string key, string value, string where)
            {
                switch (key)
                {
                    case "user_prompt":
                        l.UserPrompt = Text(value, where);
                        break;
                    case "password_prompt":
                        l.PasswordPrompt = Text(value, where);
                        break;
                    case "hide_password":
                        l.HidePassword = YesNo(value, where);
                        break;
                    case "case":
                        l.Case = Case(value, where);
                        break;
                    case "attempts":
                        l.Attempts = Number(value, 1, 100, where);
                        break;
                    case "fail_text":
                        l.FailText = Text(value, where);
                        break;
                    case "motd":
                        l.Motd = TextFile(value, where);
                        break;
                    case "then":
                        l.Then = value;
                        _gotos.Add((where, value));
                        break;
                    case "command":
                        // LOGIN, or several names: LOGIN, LOG. Compared in any case, as a command word.
                        string[] names = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                        if (names.Length == 0)
                        {
                            throw new FormatException(where + ": command needs a name");
                        }

                        l.Commands = names;
                        break;
                    case "command_prompt":
                        l.CommandPrompt = Text(value, where);
                        break;
                    case "command_refused":
                        l.CommandRefused = Text(value, where);
                        break;
                    default:
                        throw new FormatException(where + ": unknown key");
                }
            }

            private void SetUser(HostUser u, string key, string value, string where)
            {
                switch (key)
                {
                    case "name":
                        u.Name = value;
                        break;
                    case "password":
                        u.Password = value;
                        break;
                    case "greeting":
                        u.Greeting = TextFile(value, where);
                        break;
                    default:
                        throw new FormatException(where + ": unknown key");
                }
            }

            private void SetShell(string key, string value, string where)
            {
                HostShell s = _d.Shell;
                switch (key)
                {
                    case "prompt": s.Prompt = Text(value, where); break;
                    case "case": s.Case = Case(value, where); break;
                    case "unknown": s.Unknown = Text(value, where); break;
                    case "logout_text": s.LogoutText = Text(value, where); break;
                    case "unknown_file": s.UnknownFile = Text(value, where); break;
                    case "time_format":
                        s.TimeFormat = value switch
                        {
                            "iso" => HostTimeFormat.Iso,
                            "vms" => HostTimeFormat.Vms,
                            "unix" => HostTimeFormat.Unix,
                            _ => throw new FormatException(where + " = " + value + " is not one of: vms, unix, iso"),
                        };
                        break;
                    case "wall_text": s.WallText = Text(value, where); break;
                    case "send_text": s.SendText = Text(value, where); break;
                    case "user_not_on": s.UserNotOn = Text(value, where); break;
                    case "user_unknown": s.UserUnknown = Text(value, where); break;
                    case "who_format": s.WhoFormat = Text(value, where); break;
                    case "room_text": s.RoomText = Text(value, where); break;
                    case "room_join_text": s.RoomJoinText = Text(value, where); break;
                    case "room_leave_text": s.RoomLeaveText = Text(value, where); break;
                    case "room_leave":
                        if (value.Length == 0)
                        {
                            throw new FormatException(where + " is empty");
                        }

                        s.RoomLeave = value;
                        break;
                    default:
                        throw new FormatException(where + ": unknown key");
                }
            }

            private void SetCommand(HostCommand c, string key, string value, string where)
            {
                switch (key)
                {
                    case "name":
                        if (value.Length == 0 || value.Contains(' ', StringComparison.Ordinal))
                        {
                            throw new FormatException(where + " = " + value + " must be one word");
                        }

                        c.Name = value;
                        break;
                    case "alias":
                        string[] parts = value.Split(',');
                        for (int i = 0; i < parts.Length; i++)
                        {
                            string a = parts[i].Trim();
                            if (a.Length > 0)
                            {
                                c.Aliases.Add(a);
                            }
                        }

                        break;
                    case "text":
                        c.Text = Text(value, where);
                        break;
                    case "file":
                        c.File = TextFile(value, where);
                        break;
                    case "do":
                        c.Do = value;
                        CheckAction(value, where);
                        break;
                    default:
                        throw new FormatException(where + ": unknown key");
                }
            }

            private void SetFile(HostFile f, string key, string value, string where)
            {
                switch (key)
                {
                    case "path":
                        if (value.Length == 0)
                        {
                            throw new FormatException(where + " is empty");
                        }

                        f.Path = value.Length > 1 ? value.TrimEnd('/') : value;
                        break;
                    case "content":
                        f.Content = value.Length == 0 ? null : TextFile(value, where);
                        break;
                    default:
                        throw new FormatException(where + ": unknown key");
                }
            }

            private void SetMenu(HostMenu m, string key, string value, string where)
            {
                if (key.StartsWith("item.", StringComparison.Ordinal))
                {
                    if (!int.TryParse(key.AsSpan(5), NumberStyles.None, CultureInfo.InvariantCulture, out int n))
                    {
                        throw new FormatException(where + ": unknown key");
                    }

                    m.ItemNumbers.Add(n, Item(value, where));
                    return;
                }

                switch (key)
                {
                    case "title":
                        m.Title = TextFile(value, where);
                        break;
                    case "prompt":
                        m.Prompt = Text(value, where);
                        break;
                    case "unknown":
                        m.Unknown = Text(value, where);
                        break;
                    default:
                        throw new FormatException(where + ": unknown key");
                }
            }

            /// <summary><c>&lt;key&gt; | &lt;label&gt; | &lt;action&gt;</c>.</summary>
            private HostMenuItem Item(string value, string where)
            {
                string[] parts = value.Split('|');
                if (parts.Length != 3)
                {
                    throw new FormatException(where + " = " + value + " must be <key> | <label> | <action>");
                }

                var item = new HostMenuItem { Key = parts[0].Trim(), Label = parts[1].Trim() };
                string action = parts[2].Trim();
                if (item.Key.Length == 0)
                {
                    throw new FormatException(where + ": the key is empty");
                }

                if (action.StartsWith("text:", StringComparison.Ordinal))
                {
                    item.Text = Text(action.Substring(5), where);
                }
                else if (action.StartsWith("file:", StringComparison.Ordinal))
                {
                    item.File = TextFile(action.Substring(5).Trim(), where);
                }
                else if (action == "shell" || action == "logout" || action.StartsWith("menu.", StringComparison.Ordinal))
                {
                    item.Goto = action;
                    _gotos.Add((where, action));
                }
                else
                {
                    throw new FormatException(where + ": action " + action + " is not text:, file:, menu.<name>, shell or logout");
                }

                return item;
            }

            private void CheckAction(string value, string where)
            {
                if (value.StartsWith("menu.", StringComparison.Ordinal))
                {
                    _gotos.Add((where, value));
                    return;
                }

                if (value.StartsWith("room.", StringComparison.Ordinal) && value.Length > 5)
                {
                    return;
                }

                if (!Actions.Contains(value))
                {
                    throw new FormatException(where + " = " + value + " is not a built-in action (logout, echo, time, who, ls, cat, cd, "
                        + "menu.<name>, clear, finger, wall, send, room.<name>, mesg)");
                }
            }

            public void Finish(string folderName, string? hostname, string? address)
            {
                _d.Hostname = !string.IsNullOrEmpty(hostname) ? hostname : !string.IsNullOrEmpty(_hostnameKey) ? _hostnameKey : folderName;
                if (!string.IsNullOrEmpty(address))
                {
                    _d.Address = Address(address, "the address given for " + _d.Hostname);
                }

                if (_d.Address.Length == 0)
                {
                    throw new FormatException(_file + ": [host] has no address, and none was given");
                }

                foreach (KeyValuePair<int, HostUser> u in _users)
                {
                    if (u.Value.Name.Length == 0)
                    {
                        throw new FormatException(_file + ":" + u.Value.Line.ToString(CultureInfo.InvariantCulture) + ": [user." + u.Key.ToString(CultureInfo.InvariantCulture) + "] has no name");
                    }

                    _d.Users.Add(u.Value);
                }

                if (_d.Login != null && _d.Users.Count == 0)
                {
                    throw new FormatException(_file + ": there is a [login] but no [user.N]: nobody could log in");
                }

                foreach (KeyValuePair<int, HostCommand> c in _commands)
                {
                    HostCommand cmd = c.Value;
                    if (cmd.Name.Length == 0)
                    {
                        throw new FormatException(cmd.Where + " has no name");
                    }

                    int kinds = (cmd.Text != null ? 1 : 0) + (cmd.File != null ? 1 : 0) + (cmd.Do != null ? 1 : 0);
                    if (kinds != 1)
                    {
                        throw new FormatException(cmd.Where + " must have exactly one of text, file or do");
                    }

                    _d.Commands.Add(cmd);
                }

                foreach (KeyValuePair<int, HostFile> f in _files)
                {
                    if (f.Value.Path.Length == 0)
                    {
                        throw new FormatException(f.Value.Where + " has no path");
                    }

                    if (f.Value.Path[0] == '/')
                    {
                        _d.RootedFiles = true;
                    }

                    _d.Files.Add(f.Value);
                }

                foreach (KeyValuePair<string, HostMenu> m in _d.Menus)
                {
                    foreach (KeyValuePair<int, HostMenuItem> item in m.Value.ItemNumbers)
                    {
                        m.Value.Items.Add(item.Value);
                    }

                    if (m.Value.Items.Count == 0)
                    {
                        throw new FormatException(_file + ": [menu." + m.Key + "] has no item");
                    }
                }

                if (_startKey != null)
                {
                    _gotos.Add((_startWhere, _startKey));
                    _d.Start = _startKey;
                }
                else
                {
                    _d.Start = _d.Login != null ? "login" : "shell";
                }

                if (_d.Login != null && _d.Login.Then.Length == 0)
                {
                    _d.Login.Then = "shell";
                }

                for (int i = 0; i < _gotos.Count; i++)
                {
                    string target = _gotos[i].Target;
                    if (target == "shell" || target == "logout")
                    {
                        continue;
                    }

                    if (target == "login")
                    {
                        if (_d.Login == null)
                        {
                            throw new FormatException(_gotos[i].Where + " = login, but there is no [login]");
                        }

                        continue;
                    }

                    if (!target.StartsWith("menu.", StringComparison.Ordinal) || !_d.Menus.ContainsKey(target.Substring(5)))
                    {
                        throw new FormatException(_gotos[i].Where + ": " + target + " is not a menu of this host (no [" + target + "])");
                    }
                }
            }

            // ------------------------------------------------------------------ values

            /// <summary>A text value: its placeholders checked against the table.</summary>
            private static string Text(string value, string where)
            {
                CheckPlaceholders(value, where);
                return value;
            }

            /// <summary>A text file of the folder, read and checked once. Its line ends become "\n".</summary>
            private string TextFile(string name, string where)
            {
                if (name.Length == 0 || name.Contains('/', StringComparison.Ordinal) || name.Contains('\\', StringComparison.Ordinal)
                    || name.Contains("..", StringComparison.Ordinal) || name.Contains(':', StringComparison.Ordinal))
                {
                    throw new FormatException(where + " = " + name + ": a text file is named by its name alone, in the host's folder "
                        + "(no /, \\, : or ..)");
                }

                if (_texts.TryGetValue(name, out string? known))
                {
                    return known;
                }

                string? text = _readFile(name);
                if (text == null)
                {
                    throw new FormatException(where + " = " + name + ": no such file in the host's folder");
                }

                text = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
                CheckPlaceholders(text, name);
                _texts.Add(name, text);
                return text;
            }

            private static void CheckPlaceholders(string text, string where)
            {
                int i = 0;
                while (i < text.Length)
                {
                    char c = text[i];
                    if (c == '{')
                    {
                        if (i + 1 < text.Length && text[i + 1] == '{')
                        {
                            i += 2;
                            continue;
                        }

                        int close = text.IndexOf('}', i + 1);
                        if (close < 0)
                        {
                            throw new FormatException(where + ": a { with no } (write {{ for a brace)");
                        }

                        string name = text.Substring(i + 1, close - i - 1);
                        if (!Placeholders.Contains(name))
                        {
                            throw new FormatException(where + ": unknown placeholder {" + name + "}");
                        }

                        i = close + 1;
                        continue;
                    }

                    if (c == '}')
                    {
                        if (i + 1 < text.Length && text[i + 1] == '}')
                        {
                            i += 2;
                            continue;
                        }

                        throw new FormatException(where + ": a } with no { (write }} for a brace)");
                    }

                    i++;
                }
            }

            private static string Address(string value, string where)
            {
                if (value.Length == 0 || value.Length > MaxAddressDigits)
                {
                    throw new FormatException(where + " = " + value + " must be 1 to 15 digits (X.25 5.2.1)");
                }

                for (int i = 0; i < value.Length; i++)
                {
                    if (value[i] < '0' || value[i] > '9')
                    {
                        throw new FormatException(where + " = " + value + " must be 1 to 15 digits (X.25 5.2.1)");
                    }
                }

                return value;
            }

            private static int Number(string value, int min, int max, string where)
            {
                if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int n) || n < min || n > max)
                {
                    throw new FormatException(where + " = " + value + " must be a number from " + min.ToString(CultureInfo.InvariantCulture)
                        + " to " + max.ToString(CultureInfo.InvariantCulture));
                }

                return n;
            }

            private static bool YesNo(string value, string where)
            {
                return value switch
                {
                    "yes" => true,
                    "no" => false,
                    _ => throw new FormatException(where + " = " + value + " must be yes or no"),
                };
            }

            private static HostCase Case(string value, string where)
            {
                return value switch
                {
                    "exact" => HostCase.Exact,
                    "upper" => HostCase.Upper,
                    "lower" => HostCase.Lower,
                    _ => throw new FormatException(where + " = " + value + " is not one of: upper, lower, exact"),
                };
            }

            private static bool TryNumber(string name, string prefix, out int number)
            {
                number = 0;
                return name.StartsWith(prefix, StringComparison.Ordinal) && name.Length > prefix.Length
                    && int.TryParse(name.AsSpan(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out number);
            }

            /// <summary>Drops a <c>#</c> comment, but not a <c>#</c> inside double quotes.</summary>
            private static string StripComment(string line)
            {
                bool quoted = false;
                for (int i = 0; i < line.Length; i++)
                {
                    if (line[i] == '"')
                    {
                        quoted = !quoted;
                    }
                    else if (line[i] == '#' && !quoted)
                    {
                        return line.Substring(0, i);
                    }
                }

                return line;
            }

            private static string Unquote(string value)
            {
                if (value.Length >= 2 && value[0] == '"' && value[value.Length - 1] == '"')
                {
                    return value.Substring(1, value.Length - 2);
                }

                return value;
            }
        }
    }

    /// <summary><c>[login]</c>.</summary>
    public sealed class HostLogin
    {
        /// <summary>Text before the user name.</summary>
        public string UserPrompt { get; set; } = "Username: ";

        /// <summary>Text before the password.</summary>
        public string PasswordPrompt { get; set; } = "Password: ";

        /// <summary>Tell the PAD to stop echoing while the password is typed.</summary>
        public bool HidePassword { get; set; } = true;

        /// <summary>How the user name is compared.</summary>
        public HostCase Case { get; set; } = HostCase.Exact;

        /// <summary>Wrong logins before the call is cleared.</summary>
        public int Attempts { get; set; } = 3;

        /// <summary>Text after a wrong login.</summary>
        public string FailText { get; set; } = "Login incorrect";

        /// <summary>Text sent after a good login, or null.</summary>
        public string? Motd { get; set; }

        /// <summary>Where a good login goes: <c>shell</c> or <c>menu.&lt;name&gt;</c>.</summary>
        public string Then { get; set; } = string.Empty;

        /// <summary>
        /// The login command words (TOPS-20 <c>LOGIN</c>, RSX-11 <c>HELLO</c>), or empty for the two-prompt login.
        /// When set, the caller sees <see cref="CommandPrompt"/> and types <c>LOGIN user [password [more]]</c>.
        /// </summary>
        public string[] Commands { get; set; } = Array.Empty<string>();

        /// <summary>The prompt before login when <see cref="Commands"/> is set.</summary>
        public string CommandPrompt { get; set; } = "@";

        /// <summary>The answer to any other line before login; <c>{word}</c> is the word typed.</summary>
        public string CommandRefused { get; set; } = "?Please log in";
    }

    /// <summary><c>[user.N]</c>.</summary>
    public sealed class HostUser
    {
        /// <summary>The user name.</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>The password; empty takes any password.</summary>
        public string Password { get; set; } = string.Empty;

        /// <summary>Text sent after the motd for this user, or null.</summary>
        public string? Greeting { get; set; }

        internal int Line { get; set; }
    }

    /// <summary><c>[shell]</c>.</summary>
    public sealed class HostShell
    {
        /// <summary>The prompt.</summary>
        public string Prompt { get; set; } = "$ ";

        /// <summary>How command words are compared.</summary>
        public HostCase Case { get; set; } = HostCase.Exact;

        /// <summary>Text for a command not in the file.</summary>
        public string Unknown { get; set; } = "{word}: command not found";

        /// <summary>Text before a logout clears the call, or null.</summary>
        public string? LogoutText { get; set; }

        /// <summary>Text for <c>cat</c> of a name not in the tree.</summary>
        public string UnknownFile { get; set; } = "{args}: no such file";

        /// <summary>How dates and times are written.</summary>
        public HostTimeFormat TimeFormat { get; set; } = HostTimeFormat.Iso;

        /// <summary>The frame of a <c>wall</c> message.</summary>
        public string WallText { get; set; } = "{nl}Broadcast message from {from} at {time}...{nl}{text}{nl}";

        /// <summary>The frame of a <c>send</c> message.</summary>
        public string SendText { get; set; } = "{nl}Message from {from}: {text}{nl}";

        /// <summary>Text when the named user is not logged in.</summary>
        public string UserNotOn { get; set; } = "{args}: not logged in";

        /// <summary>Text when the named user has no account.</summary>
        public string UserUnknown { get; set; } = "{args}: no such user";

        /// <summary>One line of <c>who</c>.</summary>
        public string WhoFormat { get; set; } = "{user} {calling} {since} {idle}";

        /// <summary>A line said in a room, as the others see it.</summary>
        public string RoomText { get; set; } = "{from} says: {text}";

        /// <summary>Shown to the others when someone joins a room.</summary>
        public string RoomJoinText { get; set; } = "+++{from}+++";

        /// <summary>Shown to the others when someone leaves a room.</summary>
        public string RoomLeaveText { get; set; } = "---{from}---";

        /// <summary>What a user types to leave a room.</summary>
        public string RoomLeave { get; set; } = ".x";
    }

    /// <summary><c>[command.N]</c>.</summary>
    public sealed class HostCommand
    {
        /// <summary>The command word.</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>Other words for it.</summary>
        public List<string> Aliases { get; } = new List<string>();

        /// <summary>One line of output, or null.</summary>
        public string? Text { get; set; }

        /// <summary>A text file as the output (its text), or null.</summary>
        public string? File { get; set; }

        /// <summary>A built-in action, or null.</summary>
        public string? Do { get; set; }

        internal string Where { get; set; } = string.Empty;
    }

    /// <summary><c>[menu.&lt;name&gt;]</c>.</summary>
    public sealed class HostMenu
    {
        /// <summary>The menu's name (after <c>menu.</c>).</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>Text sent before the choices each time the menu is shown, or null.</summary>
        public string? Title { get; set; }

        /// <summary>The prompt.</summary>
        public string Prompt { get; set; } = "Choice: ";

        /// <summary>Text for a key not in the menu.</summary>
        public string Unknown { get; set; } = "No such choice.";

        /// <summary>The choices, in the order of their numbers.</summary>
        public List<HostMenuItem> Items { get; } = new List<HostMenuItem>();

        internal SortedDictionary<int, HostMenuItem> ItemNumbers { get; } = new SortedDictionary<int, HostMenuItem>();
    }

    /// <summary>One <c>item.N</c> of a menu.</summary>
    public sealed class HostMenuItem
    {
        /// <summary>What to type.</summary>
        public string Key { get; set; } = string.Empty;

        /// <summary>What is shown.</summary>
        public string Label { get; set; } = string.Empty;

        /// <summary><c>text:</c> line, or null.</summary>
        public string? Text { get; set; }

        /// <summary><c>file:</c> text, or null.</summary>
        public string? File { get; set; }

        /// <summary><c>menu.&lt;name&gt;</c>, <c>shell</c> or <c>logout</c>, or null.</summary>
        public string? Goto { get; set; }
    }

    /// <summary><c>[file.N]</c>: one name of the fake file tree.</summary>
    public sealed class HostFile
    {
        /// <summary>The name the user sees.</summary>
        public string Path { get; set; } = string.Empty;

        /// <summary>The text <c>cat</c> sends; null for a directory.</summary>
        public string? Content { get; set; }

        internal string Where { get; set; } = string.Empty;
    }
}
