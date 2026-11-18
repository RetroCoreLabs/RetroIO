//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System.Text;
using Jint;
using Jint.Native;
using Jint.Runtime;
using Jint.Runtime.Interop;

namespace RetroIO.Protocols.X25.Host
{
    /// <summary>
    /// One scripted machine: a JavaScript program that answers the calls to one X.25 address. The program runs once
    /// per machine, not once per call, so everything it keeps in its own variables is shared by every caller on that
    /// machine: a user table, chat channels, a high-score list. Other machines never see it.
    /// <para>
    /// The host has no I/O, no clock and no threads, the same contract as <c>AltosChatEngine</c>: the caller gives it
    /// <see cref="Start"/>, <see cref="Connect"/>, <see cref="Input"/>, <see cref="Disconnect"/>,
    /// <see cref="OutputDrained"/> and <see cref="Tick"/>, and everything the script does comes out through an
    /// <see cref="IHostOutput"/>. Not thread safe: call it from one thread.
    /// </para>
    /// <para>
    /// What the script sees (docs/x25host-format.md in RetroX25, "Users on the same machine"):
    /// </para>
    /// <list type="bullet">
    ///   <item><description>Events it may define: <c>onStart()</c>, <c>onConnect(s)</c>, <c>onInput(s, text)</c> (the bytes
    ///   as they came, one character per byte), <c>onDisconnect(s)</c> (the caller's call is gone).</description></item>
    ///   <item><description>A session object <c>s</c> per call: <c>id</c>, <c>login</c>, <c>calling</c> (null when unknown),
    ///   <c>connectedAt</c>; <c>send(text)</c> ("\n" becomes the line end), <c>clear()</c>, <c>pending()</c> (bytes sent
    ///   and not yet reported delivered), <c>isOpen()</c>.</description></item>
    ///   <item><description>The <c>machine</c> object: <c>settings</c>, <c>log(text)</c>, <c>now()</c>, <c>after(ms, fn)</c>,
    ///   <c>cancel(id)</c>, <c>wireLength(text)</c>, <c>session(id)</c>, <c>sessions()</c>,
    ///   <c>broadcast(text, exceptId)</c>.</description></item>
    /// </list>
    /// <para>
    /// Safety: the script runs in Jint with CLR access off (the default, never switched on here), so it cannot name a
    /// .NET type, a file, a socket or a process; the only things it can reach are the functions above. Every call
    /// into it runs under the limits of <see cref="ScriptedHostSettings"/> (memory, time, statements, recursion). A
    /// script that loads with an error throws from the constructor: a broken machine never starts. A script that
    /// fails while serving a call (an exception, or a limit hit) has that one call cleared and the failure logged;
    /// the other calls go on. A failure in <c>onStart</c> or in a timer is logged only.
    /// </para>
    /// </summary>
    public sealed class ScriptedHost : IHostMachine
    {
        private readonly ScriptedHostSettings _settings;
        private readonly IHostOutput _output;
        private readonly Engine _engine;

        /// <summary>Open calls by session number, and the same in the order they came.</summary>
        private readonly Dictionary<int, HostSession> _sessions = new Dictionary<int, HostSession>();
        private readonly List<HostSession> _order = new List<HostSession>();

        /// <summary>Waiting timers, kept sorted by due time and then by the order they were made.</summary>
        private readonly List<HostTimer> _timers = new List<HostTimer>();
        private long _timerSequence;

        private readonly JsValue _onStart;
        private readonly JsValue _onConnect;
        private readonly JsValue _onInput;
        private readonly JsValue _onDisconnect;

        /// <summary>The last time given through <see cref="Start"/> or <see cref="Tick"/>.</summary>
        private DateTime _now;

        /// <summary>Load the script and run its top level once. Fails loudly: a script that does not load throws.</summary>
        /// <param name="script">The JavaScript program.</param>
        /// <param name="settings">The machine's settings and limits; <see cref="ScriptedHostSettings.Values"/> is copied.</param>
        /// <param name="output">Where sends, clears and log lines go.</param>
        /// <exception cref="JavaScriptException">The script has a syntax error or throws at its top level.</exception>
        public ScriptedHost(string script, ScriptedHostSettings settings, IHostOutput output)
        {
            ArgumentNullException.ThrowIfNull(script);
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _output = output ?? throw new ArgumentNullException(nameof(output));

            // CLR access is NOT allowed (no options.AllowClr): the script sees only the machine object below.
            _engine = new Engine(o =>
            {
                o.LimitMemory(settings.MemoryLimitBytes);
                o.TimeoutInterval(settings.Timeout);
                o.MaxStatements(settings.MaxStatements);
                o.LimitRecursion(settings.MaxRecursion);
                o.Strict();
            });

            _engine.SetValue("machine", BuildMachine());
            _engine.Execute(script);

            _onStart = _engine.GetValue("onStart");
            _onConnect = _engine.GetValue("onConnect");
            _onInput = _engine.GetValue("onInput");
            _onDisconnect = _engine.GetValue("onDisconnect");
        }

        /// <summary>Calls open now.</summary>
        public int SessionCount => _order.Count;

        /// <summary>Timers waiting now.</summary>
        public int TimerCount => _timers.Count;

        /// <summary>The machine starts: set the time and run the script's <c>onStart</c>.</summary>
        /// <param name="now">The wall-clock time now.</param>
        public void Start(DateTime now)
        {
            _now = now;
            CallScript(_onStart, null, "onStart");
        }

        /// <summary>
        /// Give the host the time. Every timer due at <paramref name="now"/> or earlier runs, earliest first, those due
        /// at the same time in the order they were made. A timer made while these run waits for the next tick.
        /// </summary>
        /// <param name="now">The wall-clock time now.</param>
        public void Tick(DateTime now)
        {
            _now = now;
            int due = 0;
            while (due < _timers.Count && _timers[due].Due <= now)
            {
                due++;
            }

            if (due == 0)
            {
                return;
            }

            HostTimer[] run = _timers.GetRange(0, due).ToArray();
            _timers.RemoveRange(0, due);
            for (int i = 0; i < run.Length; i++)
            {
                CallScript(run[i].Function, null, "timer");
            }
        }

        /// <summary>A script takes every call; it may clear one itself in <c>onConnect</c>.</summary>
        /// <param name="callUserData">The call user data (not looked at).</param>
        /// <returns><see cref="HostAdmission.Accept"/>.</returns>
        public HostAdmission Admit(ReadOnlySpan<byte> callUserData) => HostAdmission.Accept;

        /// <summary>A call arrives; the script's <c>onConnect</c> runs with the new session.</summary>
        /// <param name="session">A number the caller picks for this call, unique while the call lasts.</param>
        /// <param name="login">The name the caller is known by (empty when none).</param>
        /// <param name="calling">The caller's X.121 address, or null when unknown.</param>
        /// <exception cref="InvalidOperationException">The session number is already in use.</exception>
        public void Connect(int session, string login, string? calling)
        {
            if (_sessions.ContainsKey(session))
            {
                throw new InvalidOperationException("Session " + session + " is already connected.");
            }

            var s = new HostSession(session, login ?? string.Empty, calling, _now);
            s.Js = BuildSession(s);
            _sessions.Add(session, s);
            _order.Add(s);
            CallScript(_onConnect, s, "onConnect", s.Js);
        }

        /// <summary>Bytes from the caller, given to <c>onInput</c> as a string of one character per byte.</summary>
        /// <param name="session">The session; unknown sessions are ignored (the call is already gone).</param>
        /// <param name="data">The bytes.</param>
        public void Input(int session, ReadOnlySpan<byte> data)
        {
            if (!_sessions.TryGetValue(session, out HostSession? s))
            {
                return;
            }

            CallScript(_onInput, s, "onInput", s.Js!, new JsString(Latin1(data)));
        }

        /// <summary>
        /// The call was cleared by the caller or the network. The session is closed first (sends to it are dropped:
        /// there is nobody to receive them), then <c>onDisconnect</c> runs, then the session is forgotten.
        /// </summary>
        /// <param name="session">The session; unknown sessions are ignored.</param>
        public void Disconnect(int session)
        {
            if (!_sessions.TryGetValue(session, out HostSession? s))
            {
                return;
            }

            s.Open = false;
            CallScript(_onDisconnect, null, "onDisconnect", s.Js!);
            Forget(s);
        }

        /// <summary>The caller has delivered <paramref name="bytes"/> of this session's output; lowers <c>pending()</c>.</summary>
        /// <param name="session">The session.</param>
        /// <param name="bytes">Bytes delivered.</param>
        public void OutputDrained(int session, int bytes)
        {
            if (!_sessions.TryGetValue(session, out HostSession? s) || bytes <= 0)
            {
                return;
            }

            s.Pending -= bytes;
            if (s.Pending < 0)
            {
                s.Pending = 0;
            }
        }

        // ------------------------------------------------------------------ calling the script

        /// <summary>
        /// Run one script function under the limits. A failure is logged with the event name; when the event belongs
        /// to an open session, that session's call is cleared and nothing else is touched.
        /// </summary>
        private void CallScript(JsValue function, HostSession? session, string eventName, params JsValue[] args)
        {
            if (!function.IsObject())
            {
                return; // The script does not handle this event.
            }

            try
            {
                _engine.Call(function, args);
            }
            catch (Exception ex) when (ex is JintException || ex is TimeoutException)
            {
                _output.Log("script error in " + eventName + ": " + ex.Message);
                if (session != null && session.Open)
                {
                    Forget(session);
                    _output.Clear(session.Id);
                }
            }
        }

        private void Forget(HostSession s)
        {
            s.Open = false;
            if (_sessions.Remove(s.Id))
            {
                _order.Remove(s);
            }
        }

        // ------------------------------------------------------------------ the objects the script sees

        private JsValue BuildMachine()
        {
            var machine = new JsObject(_engine);

            var values = new JsObject(_engine);
            foreach (KeyValuePair<string, string> kv in _settings.Values)
            {
                values.Set(kv.Key, kv.Value);
            }

            machine.Set("settings", values);

            machine.Set("log", Fn("log", (_, a) =>
            {
                _output.Log(Str(a, 0));
                return JsValue.Undefined;
            }));

            machine.Set("now", Fn("now", (_, _) => Clock(_now)));

            machine.Set("after", Fn("after", (_, a) =>
            {
                JsValue fn = Arg(a, 1);
                if (!fn.IsObject())
                {
                    throw new JavaScriptException("machine.after: the second argument must be a function");
                }

                if (_timers.Count >= _settings.MaxTimers)
                {
                    throw new JavaScriptException("machine.after: too many timers waiting (" + _settings.MaxTimers + ")");
                }

                double ms = TypeConverter.ToNumber(Arg(a, 0));
                if (double.IsNaN(ms) || ms < 0)
                {
                    ms = 0;
                }

                var t = new HostTimer(_now.AddMilliseconds(ms), ++_timerSequence, fn);
                int at = _timers.Count;
                while (at > 0 && _timers[at - 1].Due > t.Due)
                {
                    at--;
                }

                _timers.Insert(at, t);
                return t.Sequence;
            }));

            machine.Set("cancel", Fn("cancel", (_, a) =>
            {
                double id = TypeConverter.ToNumber(Arg(a, 0));
                for (int i = 0; i < _timers.Count; i++)
                {
                    if (_timers[i].Sequence == id)
                    {
                        _timers.RemoveAt(i);
                        return JsBoolean.True;
                    }
                }

                return JsBoolean.False;
            }));

            machine.Set("wireLength", Fn("wireLength", (_, a) => WireLength(Str(a, 0))));

            machine.Set("session", Fn("session", (_, a) =>
            {
                int id = (int)TypeConverter.ToNumber(Arg(a, 0));
                return _sessions.TryGetValue(id, out HostSession? s) ? s.Js! : JsValue.Undefined;
            }));

            machine.Set("sessions", Fn("sessions", (_, _) =>
            {
                var items = new JsValue[_order.Count];
                for (int i = 0; i < _order.Count; i++)
                {
                    items[i] = _order[i].Js!;
                }

                return new JsArray(_engine, items);
            }));

            machine.Set("broadcast", Fn("broadcast", (_, a) =>
            {
                string text = Str(a, 0);
                JsValue except = Arg(a, 1);
                int exceptId = except.IsNumber() ? (int)except.AsNumber() : int.MinValue;
                HostSession[] now = _order.ToArray();
                for (int i = 0; i < now.Length; i++)
                {
                    if (now[i].Id != exceptId)
                    {
                        Send(now[i], text);
                    }
                }

                return JsValue.Undefined;
            }));

            return machine;
        }

        private JsValue BuildSession(HostSession s)
        {
            var o = new JsObject(_engine);
            o.Set("id", s.Id);
            o.Set("login", s.Login);
            o.Set("calling", s.Calling == null ? JsValue.Null : new JsString(s.Calling));
            o.Set("connectedAt", Clock(s.ConnectedAt));
            o.Set("send", Fn("send", (_, a) =>
            {
                Send(s, Str(a, 0));
                return JsValue.Undefined;
            }));
            o.Set("clear", Fn("clear", (_, _) =>
            {
                if (s.Open)
                {
                    Forget(s);
                    _output.Clear(s.Id);
                }

                return JsValue.Undefined;
            }));
            o.Set("pending", Fn("pending", (_, _) => (double)s.Pending));
            o.Set("isOpen", Fn("isOpen", (_, _) => s.Open ? JsBoolean.True : JsBoolean.False));
            return o;
        }

        /// <summary>
        /// The time as a plain object of numbers, with no time zone arithmetic: the wall clock the host was given.
        /// <c>weekday</c> is 0 for Sunday (as C's tm_wday), <c>month</c> 1 to 12.
        /// </summary>
        private JsValue Clock(DateTime t)
        {
            var o = new JsObject(_engine);
            o.Set("year", t.Year);
            o.Set("month", t.Month);
            o.Set("day", t.Day);
            o.Set("hour", t.Hour);
            o.Set("minute", t.Minute);
            o.Set("second", t.Second);
            o.Set("millisecond", t.Millisecond);
            o.Set("weekday", (int)t.DayOfWeek);
            o.Set("ticks", (double)(t.Ticks / TimeSpan.TicksPerMillisecond));
            return o;
        }

        /// <summary>Send text to a session: "\n" becomes the line end, a character above 255 becomes '?'.</summary>
        private void Send(HostSession s, string text)
        {
            if (!s.Open)
            {
                return; // The call is gone; there is nobody to send to.
            }

            string end = _settings.LineEnd;
            int size = WireLength(text);
            byte[] buf = size <= 512 ? new byte[size] : System.Buffers.ArrayPool<byte>.Shared.Rent(size);
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
                        buf[o++] = c <= 0xFF ? (byte)c : (byte)'?';
                    }
                }

                s.Pending += o;
                _output.Send(s.Id, new ReadOnlySpan<byte>(buf, 0, o));
            }
            finally
            {
                if (size > 512)
                {
                    System.Buffers.ArrayPool<byte>.Shared.Return(buf);
                }
            }
        }

        private int WireLength(string text)
        {
            int endLength = _settings.LineEnd.Length;
            int n = 0;
            for (int i = 0; i < text.Length; i++)
            {
                n += text[i] == '\n' ? endLength : 1;
            }

            return n;
        }

        private ClrFunction Fn(string name, Func<JsValue, JsValue[], JsValue> body)
        {
            return new ClrFunction(_engine, name, body);
        }

        private static JsValue Arg(JsValue[] args, int index) => index < args.Length ? args[index] : JsValue.Undefined;

        private static string Str(JsValue[] args, int index) => TypeConverter.ToString(Arg(args, index));

        private static string Latin1(ReadOnlySpan<byte> data)
        {
            var sb = new StringBuilder(data.Length);
            for (int i = 0; i < data.Length; i++)
            {
                sb.Append((char)data[i]);
            }

            return sb.ToString();
        }

        // ------------------------------------------------------------------ types

        private sealed class HostSession
        {
            public HostSession(int id, string login, string? calling, DateTime connectedAt)
            {
                Id = id;
                Login = login;
                Calling = calling;
                ConnectedAt = connectedAt;
            }

            public int Id { get; }

            public string Login { get; }

            public string? Calling { get; }

            public DateTime ConnectedAt { get; }

            /// <summary>False once the call is cleared or gone: sends are dropped.</summary>
            public bool Open { get; set; } = true;

            /// <summary>Bytes sent and not yet reported delivered.</summary>
            public long Pending { get; set; }

            /// <summary>The session object the script sees.</summary>
            public JsValue? Js { get; set; }
        }

        private sealed class HostTimer
        {
            public HostTimer(DateTime due, long sequence, JsValue function)
            {
                Due = due;
                Sequence = sequence;
                Function = function;
            }

            public DateTime Due { get; }

            public long Sequence { get; }

            public JsValue Function { get; }
        }
    }
}
