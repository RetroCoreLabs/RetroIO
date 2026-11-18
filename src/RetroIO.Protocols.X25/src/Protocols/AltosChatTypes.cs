//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;
using System.Text;

namespace RetroIO.Protocols.X25.Protocols
{
    /// <summary>
    /// Settings of the Altos CHAT 3.1 engine (<see cref="AltosChatEngine"/>).
    /// </summary>
    public sealed class AltosChatSettings
    {
        /// <summary>
        /// Most users in the chat at one time. The original table size was NUSERS = 30 (CHAT 3.1 chat.h:16).
        /// The caller after the last free place gets "open of answerpipe failed." and is cleared.
        /// </summary>
        public int MaxUsers { get; set; } = 30;

        /// <summary>
        /// Bytes that may be waiting to reach one user before a chat message to that user counts as a failed write.
        /// The original timed the write out after 2 seconds (CHAT 3.1 serv.c:200-204); the port counts bytes sent and
        /// not yet reported as delivered through <see cref="AltosChatEngine.OutputDrained"/>. The default 4096 is a
        /// choice of this port, not a number from the original.
        /// </summary>
        public int OutputLimitBytes { get; set; } = 4096;

        /// <summary>
        /// The nickname every caller starts with. Every X.25 caller on the Altos logged in as guest (README).
        /// Cut to 32 characters as the original did (CHAT 3.1 chat.c:317, chat.c:131).
        /// </summary>
        public string StartingNickname { get; set; } = "guest";

        /// <summary>
        /// Time between "Waiting for connection." and "Connection established.". The original slept 3 seconds there
        /// (CHAT 3.1 chat.c:278, alarm(3); pause()). Zero joins at once inside <see cref="AltosChatEngine.Connect"/>.
        /// </summary>
        public TimeSpan WaitPause { get; set; } = TimeSpan.FromSeconds(3);

        /// <summary>
        /// What each "\n" of the original texts becomes on the wire. The original wrote LF and the Unix terminal
        /// driver added the CR; with no terminal driver in between the port sends CR LF itself.
        /// </summary>
        public string LineEnd { get; set; } = "\r\n";
    }

    /// <summary>
    /// One user in the chat, as a monitor sees it: a copy taken by <see cref="AltosChatEngine.GetUsers"/>, not a live view.
    /// </summary>
    public readonly struct AltosChatUserInfo
    {
        /// <summary>Makes the copy.</summary>
        /// <param name="slot">The place in the user table, the number <c>.s</c> shows and <c>!N</c> whispers to.</param>
        /// <param name="nickname">The nickname now.</param>
        /// <param name="login">The login name, without the padding <c>.s</c> shows.</param>
        /// <param name="from">Where the user comes from (the calling X.121 address), without the padding.</param>
        /// <param name="channel">The channel the user is on.</param>
        /// <param name="connectedAt">When the call reached the chat, by the clock the engine was given.</param>
        public AltosChatUserInfo(int slot, string nickname, string login, string from, long channel, DateTime connectedAt)
        {
            Slot = slot;
            Nickname = nickname;
            Login = login;
            From = from;
            Channel = channel;
            ConnectedAt = connectedAt;
        }

        /// <summary>The place in the user table (0 up), the number <c>.s</c> shows.</summary>
        public int Slot { get; }

        /// <summary>The nickname now.</summary>
        public string Nickname { get; }

        /// <summary>The login name, without padding.</summary>
        public string Login { get; }

        /// <summary>Where the user comes from, without padding.</summary>
        public string From { get; }

        /// <summary>The channel the user is on.</summary>
        public long Channel { get; }

        /// <summary>When the call reached the chat.</summary>
        public DateTime ConnectedAt { get; }
    }

    /// <summary>
    /// One channel that has users on it: a copy taken by <see cref="AltosChatEngine.GetChannels"/>. The password of a
    /// protected channel is not part of it.
    /// </summary>
    public readonly struct AltosChatChannelInfo
    {
        /// <summary>Makes the copy.</summary>
        /// <param name="channel">The channel number.</param>
        /// <param name="users">How many users are on it.</param>
        /// <param name="isProtected">True when a user has set a password on it with <c>.p</c>.</param>
        public AltosChatChannelInfo(long channel, int users, bool isProtected)
        {
            Channel = channel;
            Users = users;
            IsProtected = isProtected;
        }

        /// <summary>The channel number.</summary>
        public long Channel { get; }

        /// <summary>How many users are on it.</summary>
        public int Users { get; }

        /// <summary>True when the channel has a password (<c>.p</c>).</summary>
        public bool IsProtected { get; }
    }

    /// <summary>
    /// Where the engine sends its output. Called synchronously from inside the engine's methods.
    /// </summary>
    public interface IAltosChatSink
    {
        /// <summary>Send these bytes to the user on <paramref name="session"/>. The span is only valid during the call.</summary>
        /// <param name="session">The caller's session number, as given to <see cref="AltosChatEngine.Connect"/>.</param>
        /// <param name="data">The bytes to send.</param>
        void Send(int session, ReadOnlySpan<byte> data);

        /// <summary>Clear the call on <paramref name="session"/>. The engine has already forgotten the session.</summary>
        /// <param name="session">The caller's session number.</param>
        void Clear(int session);
    }

    /// <summary>
    /// The server log (the original's -l file, CHAT 3.1 serv.c:147, 162). One call per line, without the line end.
    /// </summary>
    public interface IAltosChatLog
    {
        /// <summary>One log line, without its line end.</summary>
        /// <param name="text">The line.</param>
        void Line(string text);
    }

    /// <summary>What kind of action an <see cref="AltosChatAction"/> is.</summary>
    public enum AltosChatActionKind
    {
        /// <summary>Bytes sent to a session.</summary>
        Send,

        /// <summary>A session's call cleared.</summary>
        Clear,

        /// <summary>A server log line.</summary>
        Log,
    }

    /// <summary>One thing the engine did, as kept by <see cref="AltosChatActionList"/>.</summary>
    public readonly struct AltosChatAction
    {
        /// <summary>Make an action.</summary>
        /// <param name="kind">What kind it is.</param>
        /// <param name="session">The session, or -1 for a log line.</param>
        /// <param name="data">The bytes sent, for <see cref="AltosChatActionKind.Send"/>.</param>
        /// <param name="text">The log line, for <see cref="AltosChatActionKind.Log"/>.</param>
        public AltosChatAction(AltosChatActionKind kind, int session, byte[]? data, string? text)
        {
            Kind = kind;
            Session = session;
            Data = data;
            Text = text;
        }

        /// <summary>What kind of action.</summary>
        public AltosChatActionKind Kind { get; }

        /// <summary>The session, or -1 for a log line.</summary>
        public int Session { get; }

        /// <summary>The bytes sent (a copy), or null.</summary>
        public byte[]? Data { get; }

        /// <summary>The log line, or null.</summary>
        public string? Text { get; }
    }

    /// <summary>
    /// An action sink and log that keeps every action in order, in one list. Used by tests, and usable by a host that
    /// wants to run the engine first and deliver afterwards.
    /// </summary>
    public sealed class AltosChatActionList : IAltosChatSink, IAltosChatLog
    {
        private readonly List<AltosChatAction> _items = new List<AltosChatAction>();

        /// <summary>Every action so far, oldest first.</summary>
        public IReadOnlyList<AltosChatAction> Items => _items;

        /// <inheritdoc />
        public void Send(int session, ReadOnlySpan<byte> data)
        {
            _items.Add(new AltosChatAction(AltosChatActionKind.Send, session, data.ToArray(), null));
        }

        /// <inheritdoc />
        public void Clear(int session)
        {
            _items.Add(new AltosChatAction(AltosChatActionKind.Clear, session, null, null));
        }

        /// <inheritdoc />
        public void Line(string text)
        {
            _items.Add(new AltosChatAction(AltosChatActionKind.Log, -1, null, text));
        }

        /// <summary>Forget every action kept so far.</summary>
        public void Reset()
        {
            _items.Clear();
        }

        /// <summary>Everything sent to one session, joined, as ASCII text.</summary>
        /// <param name="session">The session.</param>
        /// <returns>The text.</returns>
        public string TextFor(int session)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < _items.Count; i++)
            {
                AltosChatAction a = _items[i];
                if (a.Kind == AltosChatActionKind.Send && a.Session == session)
                {
                    sb.Append(Encoding.ASCII.GetString(a.Data!));
                }
            }

            return sb.ToString();
        }

        /// <summary>How many times the call on one session was cleared.</summary>
        /// <param name="session">The session.</param>
        /// <returns>The count.</returns>
        public int ClearCount(int session)
        {
            int n = 0;
            for (int i = 0; i < _items.Count; i++)
            {
                if (_items[i].Kind == AltosChatActionKind.Clear && _items[i].Session == session)
                {
                    n++;
                }
            }

            return n;
        }

        /// <summary>Every log line so far, in order.</summary>
        /// <returns>The lines.</returns>
        public List<string> LogLines()
        {
            var lines = new List<string>();
            for (int i = 0; i < _items.Count; i++)
            {
                if (_items[i].Kind == AltosChatActionKind.Log)
                {
                    lines.Add(_items[i].Text!);
                }
            }

            return lines;
        }
    }
}
