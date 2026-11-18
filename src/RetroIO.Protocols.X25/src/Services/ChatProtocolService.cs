//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;
using System.Threading;
using Microsoft.Extensions.Logging;
using RetroIO.Protocols.X25.Models;
using RetroIO.Protocols.X25.Protocols;

namespace RetroIO.Protocols.X25.Services
{
    /// <summary>
    /// The Altos CHAT 3.1 conference program as a built-in X.25 service, beside the TAD: a caller dials the chat's
    /// own X.121 address from a PAD and is in the chat. The chat itself is <see cref="AltosChatEngine"/>; this class
    /// carries calls, data and clears between the runtime and the engine, gives the engine the time, and tells it
    /// what output has left.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The original (Hans Korneder, Munich, 1987; source at https://archive.org/details/altoschat) ran on an Altos
    /// reached over X.25 at NUA 0262 45 8900 40004, and every X.25 caller logged in as <c>guest</c>. The PAD echoed
    /// and edited the user's line, as X.3 lets a host ask it to; on every accepted call this service sends the X.29
    /// set PAD message <see cref="SetPadMessage"/> that asks for exactly that (decision of 2026-11-18 in RetroX25
    /// docs/altos-chat-port-plan.md).
    /// </para>
    /// <para>
    /// Threads: the runtime calls in on its receive threads, the timer on a pool thread, the runtime's send worker
    /// reports delivered data. Every call into the engine is made under one lock (the engine is not thread safe). The
    /// engine's output is queued under that lock and handed to the runtime outside it by one drainer at a time, in the
    /// order the engine made it: a module in this process may answer from inside the hand-over, and its answer must
    /// not run the engine while the engine is still working.
    /// </para>
    /// </remarks>
    public sealed class ChatProtocolService : IProtocolService, IAltosChatSink, IAltosChatLog, IDisposable
    {
        /// <summary>
        /// The X.29 set PAD message sent on every accepted call, in a data packet with the Q bit set (X.29 (12/97) 1.5.2:
        /// "PAD messages are conveyed in the user data fields of complete packet sequences with Q = 1").
        /// </summary>
        /// <remarks>
        /// <para>
        /// Octet 1 is 0x02: control identifier 0000 (a PAD message, X.29 4.4.1) and message code 0010 (set, X.29 Table
        /// 2). Then one two-octet parameter field per parameter, reference then value (X.29 4.4.3, 4.4.5):
        /// </para>
        /// <list type="bullet">
        /// <item><description>2 = 1: echo (X.3 (03/00) 3.2, "echo - represented by decimal 1").</description></item>
        /// <item><description>3 = 2: forward on the character CR (X.3 3.3, "character CR - represented by decimal 2").</description></item>
        /// <item><description>13 = 4: "insert linefeed after each carriage return in the echo stream to the start-stop mode
        /// DTE" (X.3 3.13, text line 1148 of the Specs copy; "NOTE 2 - This function applies only in the data transfer
        /// state", line 1161). Without it the PAD echoed Enter as CR alone and the chat's answer overwrote the typed line
        /// (Ronny's decision, 2026-11-18).</description></item>
        /// <item><description>15 = 1: editing in the data transfer state (X.3 3.15, "use of editing in the data transfer state - represented by decimal 1").</description></item>
        /// <item><description>16 = 127: character delete is DEL (X.3 3.16 takes any IA5 character; 127 is the value of both
        /// standard profiles, X.28 (12/97) Table 1, "Set to character 7/15 (DEL) (value 127)").</description></item>
        /// <item><description>17 = 24: line delete is CAN (X.3 3.17; X.28 Table 1, "Set to character 1/8 (CAN) (value 24)").</description></item>
        /// <item><description>18 = 18: line display is DC2 (X.3 3.18; X.28 Table 1, "Set to character 1/2 (DC2) (value 18)").</description></item>
        /// </list>
        /// <para>
        /// The PAD answers a valid set message with nothing (X.29 3.1.4) and treats its arrival as a data forwarding
        /// condition (X.29 3.1.2). A terminal whose backspace key sends BS (8) does not delete with these values; the
        /// values are the standard ones, and the user can change parameter 16 at the PAD.
        /// </para>
        /// </remarks>
        public static readonly byte[] SetPadMessage = { 0x02, 2, 1, 3, 2, 13, 4, 15, 1, 16, 127, 17, 24, 18, 18 };

        /// <summary>
        /// The login every X.25 caller had on the Altos (CHAT 3.1 README: "login guest").
        /// </summary>
        public const string CallerLogin = "guest";

        /// <summary>
        /// Most user data in one data packet sent to a caller: 128 octets, "the standard maximum user data field length"
        /// (X.25 (10/96) 4.3.2), the same chunk the TAD uses. A message longer than that goes in several packets, every
        /// one but the last with the M bit set (X.25 4.3.4). Not taken from the call's negotiated size: a call that
        /// negotiated less than 128 would have the longer packets refused by the packet layer, the same as the TAD's.
        /// </summary>
        private const int MaxPacketUserData = 128;

        /// <summary>How often the engine is given the time, so the pause after "Waiting for connection." can end.</summary>
        private const int TickIntervalMs = 100;

        private readonly ILogger _logger;
        private readonly Action<TadOutputEventArgs> _deliver;
        private readonly string[] _localAddresses;
        private readonly Func<DateTime> _clock;
        private readonly AltosChatEngine _engine;
        private readonly object _gate = new object();
        private readonly Timer? _ticker;

        // Calls on the service, under _gate: the engine's session number per local channel and back. The session
        // number is this service's own counter, never the channel, so a late report about a channel that has since
        // been given to a new call cannot reach the new call's session.
        private readonly Dictionary<ushort, int> _sessionByLcn = new Dictionary<ushort, int>();
        private readonly Dictionary<int, ushort> _lcnBySession = new Dictionary<int, ushort>();
        private int _nextSession = 1;

        // Output waiting to be handed to the runtime, under _gate, and whether a thread is handing it over.
        private readonly Queue<TadOutputEventArgs> _outbox = new Queue<TadOutputEventArgs>();
        private bool _draining;
        private bool _disposed;

        /// <summary>
        /// Creates the service and starts the engine (the server log gets "Chat-Server Starting At: ...").
        /// </summary>
        /// <param name="logger">Diagnostics logger; the chat's own server log goes here too, one line each.</param>
        /// <param name="deliver">Hands one piece of output to the runtime that carries the calls (<see cref="IProtocolRuntime.DeliverServiceOutput"/>).</param>
        /// <param name="address">The chat's X.121 address: calls to it come here.</param>
        /// <param name="settings">The engine's settings (most users, output limit, pause, nickname, line end).</param>
        /// <param name="clock">The wall clock; null for <see cref="DateTime.Now"/> (local time, as the original's
        /// ctime). Given, the service runs no timer of its own and the caller ticks it (<see cref="Tick"/>).</param>
        public ChatProtocolService(ILogger logger, Action<TadOutputEventArgs> deliver, string address, AltosChatSettings settings, Func<DateTime>? clock = null)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _deliver = deliver ?? throw new ArgumentNullException(nameof(deliver));
            if (string.IsNullOrWhiteSpace(address))
            {
                throw new ArgumentException("The chat needs an X.121 address.", nameof(address));
            }

            _localAddresses = new[] { address.Trim() };
            _clock = clock ?? (() => DateTime.Now);
            _engine = new AltosChatEngine(settings ?? throw new ArgumentNullException(nameof(settings)), this, this);
            lock (_gate)
            {
                _engine.Start(_clock());
            }

            if (clock == null)
            {
                _ticker = new Timer(_ => Tick(), null, TickIntervalMs, TickIntervalMs);
            }

            _logger.LogInformation("CHAT service on address {Address}, at most {Max} users", _localAddresses[0], settings.MaxUsers);
        }

        /// <summary>Gets the name of this service, <c>CHAT</c>.</summary>
        public string ServiceName => "CHAT";

        /// <summary>Gets the chat's address.</summary>
        public string[] LocalAddresses => _localAddresses;

        /// <summary>
        /// Gets the priority, 110: above the TAD (100), so a PAD call to the chat's address is the chat's even if the
        /// TAD were ever given the same address. The TAD takes every call user data it does not know
        /// (<see cref="TadProtocolService.CanHandleCUD"/>); the chat takes only a PAD's (<see cref="CanHandleCUD"/>).
        /// </summary>
        public int Priority => 110;

        /// <summary>Gets the number of users in the chat now.</summary>
        public int UserCount
        {
            get
            {
                lock (_gate)
                {
                    return _engine.UserCount;
                }
            }
        }

        /// <summary>
        /// Raised after something happened in the chat that may have changed its users or channels: a call came, a line
        /// was read (a nickname or channel change, a leave), a call was cleared, a waiting caller joined. A monitor reads
        /// <see cref="GetUsers"/> and <see cref="GetChannels"/> again. Raised on the thread that did the work, outside the
        /// service's lock; it may also be raised when nothing a monitor shows has changed.
        /// </summary>
        public event EventHandler? Changed;

        /// <summary>
        /// Copies the users in the chat now, lowest place first (<see cref="AltosChatEngine.GetUsers"/>). Read only.
        /// </summary>
        /// <param name="into">The list to fill; cleared first.</param>
        /// <returns>The number of users.</returns>
        public int GetUsers(List<AltosChatUserInfo> into)
        {
            lock (_gate)
            {
                return _engine.GetUsers(into);
            }
        }

        /// <summary>
        /// Copies the channels that have users, lowest number first, with the protected flag
        /// (<see cref="AltosChatEngine.GetChannels"/>). Read only.
        /// </summary>
        /// <param name="into">The list to fill; cleared first.</param>
        /// <returns>The number of channels.</returns>
        public int GetChannels(List<AltosChatChannelInfo> into)
        {
            lock (_gate)
            {
                return _engine.GetChannels(into);
            }
        }

        private void RaiseChanged()
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// A call for the chat comes from a PAD: call user data that starts with the X.29 protocol identifier
        /// 01 00 00 00 (X.29 (12/97) 4.2.1: first octet bits 8-7 00 and bits 6-1 000001 "for indicating PAD messages
        /// relating to the packet assembly/disassembly facility for the start-stop mode DTE", octets 2-4 all 0), or no
        /// call user data at all (taken as a terminal call, the same as <see cref="TadProtocolService.CanHandleCUD"/>
        /// does). Anything else -
        /// XMSG, IP, another protocol - is not a terminal and is not the chat's.
        /// </summary>
        /// <param name="cud">The call user data.</param>
        /// <returns>True for a PAD call.</returns>
        public bool CanHandleCUD(byte[] cud)
        {
            if (cud == null || cud.Length == 0)
            {
                return true;
            }

            return cud.Length >= 4 && cud[0] == 0x01 && cud[1] == 0x00 && cud[2] == 0x00 && cud[3] == 0x00;
        }

        /// <summary>
        /// A caller arrives: the call is accepted, the PAD is set (<see cref="SetPadMessage"/>) and the caller gets the
        /// banner. "From" is the calling X.121 address, cut to 16 characters by the engine (CHAT 3.1 chat.c:325).
        /// </summary>
        public bool HandleIncomingCall(ushort lcn, string calledAddress, string callingAddress, byte[] cud, byte[] facilities)
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return false;
                }

                if (_sessionByLcn.ContainsKey(lcn))
                {
                    // The runtime gave a channel that still carries a call of ours: refuse rather than mix two callers.
                    _logger.LogError("CHAT: local channel {Lcn} still carries a call; the new call is refused", lcn);
                    return false;
                }

                int session = _nextSession++;
                _sessionByLcn[lcn] = session;
                _lcnBySession[session] = lcn;
                _logger.LogInformation("CHAT: call from {Calling} to {Called} on local channel {Lcn} (session {Session})", callingAddress, calledAddress, lcn, session);

                _outbox.Enqueue(new TadOutputEventArgs { Kind = TadOutputKind.CallAccepted, Lcn = lcn });
                _outbox.Enqueue(new TadOutputEventArgs { Kind = TadOutputKind.Data, Lcn = lcn, Data = (byte[])SetPadMessage.Clone(), QBit = true });
                _engine.Tick(_clock());
                _engine.Connect(session, CallerLogin, callingAddress);
            }

            Drain();
            RaiseChanged();
            return true;
        }

        /// <inheritdoc />
        public void HandleData(ushort lcn, byte[] data)
        {
            HandleData(lcn, data, false);
        }

        /// <summary>
        /// Data from a caller. A packet with the Q bit set is a PAD message (X.29 1.5.2), not text: the chat never asks
        /// the PAD for anything, so it is only logged.
        /// </summary>
        public void HandleData(ushort lcn, byte[] data, bool qBit)
        {
            if (data == null || data.Length == 0)
            {
                return;
            }

            if (qBit)
            {
                _logger.LogDebug("CHAT: PAD message 0x{Code:X2} ({Length} octets) on local channel {Lcn}, not text", data[0], data.Length, lcn);
                return;
            }

            lock (_gate)
            {
                if (!_sessionByLcn.TryGetValue(lcn, out int session))
                {
                    return;
                }

                _engine.Input(session, data);
            }

            Drain();
            RaiseChanged();
        }

        /// <summary>
        /// The call was cleared by the caller or the network: the user leaves the chat as with .x, and the others see
        /// "---nick---" once (CHAT 3.1 chat.c:246, the SIGHUP that ran log_off).
        /// </summary>
        public void HandleClear(ushort lcn, byte cause, byte diagnostic)
        {
            lock (_gate)
            {
                if (!_sessionByLcn.TryGetValue(lcn, out int session))
                {
                    return;
                }

                _sessionByLcn.Remove(lcn);
                _lcnBySession.Remove(session);
                _logger.LogInformation("CHAT: call on local channel {Lcn} cleared, cause 0x{Cause:X2} diagnostic {Diagnostic}", lcn, cause, diagnostic);
                _engine.Disconnect(session);
            }

            Drain();
            RaiseChanged();
        }

        /// <summary>
        /// An interrupt from the PAD (a break, X.29 3.3). The original chat read no break, so it is only logged.
        /// </summary>
        public void HandleInterrupt(ushort lcn, byte[] data)
        {
            _logger.LogDebug("CHAT: interrupt on local channel {Lcn}, ignored", lcn);
        }

        /// <summary>
        /// Gives the engine the time now: callers whose pause after "Waiting for connection." is over join the chat.
        /// The service's own timer calls this; a service made with a clock of its own is ticked by its owner.
        /// </summary>
        public void Tick()
        {
            bool joined;
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                // A tick changes the users only by letting waiting callers join (or be refused, which changes nothing a
                // monitor shows), so a monitor is told only when the number of users moved.
                int before = _engine.UserCount;
                _engine.Tick(_clock());
                joined = _engine.UserCount != before;
            }

            Drain();
            if (joined)
            {
                RaiseChanged();
            }
        }

        /// <summary>Stops the timer. Calls still up are left to the runtime.</summary>
        public void Dispose()
        {
            lock (_gate)
            {
                _disposed = true;
            }

            _ticker?.Dispose();
        }

        // ------------------------------------------------------------------ the engine's sink and log (under _gate)

        /// <summary>
        /// The engine sends bytes to a user: cut into data packets of at most <see cref="MaxPacketUserData"/> octets,
        /// every one but the last with the M bit (X.25 4.3.4). Each packet tells the engine
        /// when it has left, so the engine's count of output still on its way to that user goes down.
        /// </summary>
        void IAltosChatSink.Send(int session, ReadOnlySpan<byte> data)
        {
            if (!_lcnBySession.TryGetValue(session, out ushort lcn))
            {
                return;
            }

            int offset = 0;
            while (offset < data.Length)
            {
                int length = data.Length - offset > MaxPacketUserData ? MaxPacketUserData : data.Length - offset;
                byte[] chunk = data.Slice(offset, length).ToArray();
                offset += length;
                int drained = length;
                _outbox.Enqueue(new TadOutputEventArgs
                {
                    Kind = TadOutputKind.Data,
                    Lcn = lcn,
                    Data = chunk,
                    MBit = offset < data.Length,
                    Delivered = () => OnDelivered(session, drained),
                });
            }
        }

        /// <summary>The engine clears a user's call (table full, or the user left with .x).</summary>
        void IAltosChatSink.Clear(int session)
        {
            if (!_lcnBySession.TryGetValue(session, out ushort lcn))
            {
                return;
            }

            _lcnBySession.Remove(session);
            _sessionByLcn.Remove(lcn);

            // Cause 0, DTE originated: the chat is the called DTE clearing its own call (X.25 Table 5-7).
            _outbox.Enqueue(new TadOutputEventArgs { Kind = TadOutputKind.Clear, Lcn = lcn, Cause = 0, Diagnostic = 0 });
        }

        /// <summary>One line of the chat's server log.</summary>
        void IAltosChatLog.Line(string text)
        {
            _logger.LogInformation("CHAT: {Line}", text);
        }

        // ------------------------------------------------------------------ hand-over to the runtime

        private void OnDelivered(int session, int bytes)
        {
            lock (_gate)
            {
                _engine.OutputDrained(session, bytes);
            }
        }

        /// <summary>
        /// Hands the queued output to the runtime, in order, outside the lock. One thread at a time: a thread that finds
        /// another one handing over leaves its output to that one, which keeps going until the queue is empty.
        /// </summary>
        private void Drain()
        {
            lock (_gate)
            {
                if (_draining)
                {
                    return;
                }

                _draining = true;
            }

            bool done = false;
            try
            {
                while (true)
                {
                    TadOutputEventArgs next;
                    lock (_gate)
                    {
                        if (_outbox.Count == 0)
                        {
                            _draining = false;
                            done = true;
                            return;
                        }

                        next = _outbox.Dequeue();
                    }

                    _deliver(next);
                }
            }
            finally
            {
                // A hand-over that threw must not leave the queue marked as being handed over for ever; the exception
                // still goes to the caller.
                if (!done)
                {
                    lock (_gate)
                    {
                        _draining = false;
                    }
                }
            }
        }
    }
}
