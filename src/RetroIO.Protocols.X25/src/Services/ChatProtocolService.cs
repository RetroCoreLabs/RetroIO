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
        /// <item><description>19 = 2: "editing PAD service signals for display terminals - represented by decimal 2" (X.3
        /// 3.19, text lines 1269-1271 of the Specs copy). A deleted character is then rubbed out on the screen: X.28 (12/97)
        /// 3.5.24, "Value 2: the characters 0/8 (BS) 2/0 (SP) 0/8 (BS) will be sent" (line 1673), and a deleted line is
        /// rubbed out the same way, once per graphic character (3.5.25). Both standard profiles have 1, the backslash of a
        /// printing terminal (X.28 Table 1); the chat's users sit at screens (Ronny's decision, 2026-11-18).</description></item>
        /// </list>
        /// <para>
        /// The PAD answers a valid set message with nothing (X.29 3.1.4) and treats its arrival as a data forwarding
        /// condition (X.29 3.1.2). A terminal whose backspace key sends BS (8) does not delete with these values; the
        /// values are the standard ones, and the user can change parameter 16 at the PAD.
        /// </para>
        /// </remarks>
        public static readonly byte[] SetPadMessage = { 0x02, 2, 1, 3, 2, 13, 4, 15, 1, 16, 127, 17, 24, 18, 18, 19, 2 };

        /// <summary>
        /// Octets of output that may wait to leave for one call: 32768, the same as the C service (X25CHAT_OUT_MAX in
        /// x25emu tools/x25chat.h). Waiting means handed to the runtime and not yet reported sent
        /// (<see cref="TadOutputEventArgs.Delivered"/>). The engine's own limit
        /// (<see cref="AltosChatSettings.OutputLimitBytes"/>, 4096 by default) throws a slow user out long before this
        /// for chat lines; what can still pile up is the user's own command answers typed while not reading. Output
        /// that would go past it is not queued and the call is ended: the user leaves the chat (the others see
        /// "---nick---") and the call is cleared after what is already queued, so nothing is lost without a word.
        /// </summary>
        public const int WaitingOutputLimit = 32768;

        /// <summary>
        /// The login every X.25 caller had on the Altos (CHAT 3.1 README: "login guest").
        /// </summary>
        public const string CallerLogin = "guest";

        /// <summary>
        /// Most user data in one data packet sent to a caller: 128 octets, "the standard maximum user data field length"
        /// (X.25 (10/96) 4.3.2), the same as the C service (X25CHAT_PKT_MAX). A call that agreed a smaller packet size
        /// for the direction from the called DTE (facility 0x42, X.25 7.2.2.1: "The packet size for the direction of
        /// transmission from the called DTE is indicated in bits 4, 3, 2 and 1 of the first octet", line 12771) gets
        /// packets of that size, as the C service does from the circuit's pkt_size_out. A message longer than one packet goes in
        /// several, every one but the last with the M bit set (X.25 4.3.4).
        /// </summary>
        private const int MaxPacketUserData = 128;

        /// <summary>How often the engine is given the time, so the pause after "Waiting for connection." can end.</summary>
        private const int TickIntervalMs = 100;

        private readonly ILogger _logger;
        private readonly Action<TadOutputEventArgs> _deliver;
        private readonly string[] _localAddresses;
        private readonly Func<DateTime> _clock;
        private readonly AltosChatSettings _settings;
        private readonly AltosChatEngine _engine;
        private readonly object _gate = new object();
        private readonly Timer? _ticker;

        // Calls on the service, under _gate: the engine's session number per local channel and back. The session
        // number is this service's own counter, never the channel, so a late report about a channel that has since
        // been given to a new call cannot reach the new call's session.
        private readonly Dictionary<ushort, int> _sessionByLcn = new Dictionary<ushort, int>();
        private readonly Dictionary<int, ChatCall> _callBySession = new Dictionary<int, ChatCall>();
        private int _nextSession = 1;

        // Calls whose waiting output went past WaitingOutputLimit inside an engine call, ended once the engine returns.
        private readonly List<ChatCall> _overflowed = new List<ChatCall>();

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
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _engine = new AltosChatEngine(_settings, this, this);
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

                // The cap on calls (AltosChatSettings.MaxCalls), counted here and in the engine: the call past it is
                // refused before it is accepted. The runtime then answers the Incoming Call with a clear request of cause
                // 0 "DTE originated", diagnostic 0 (X25CallDecision.CalledSideRefused; X.25 (10/96) 4.1.7, Table 5-7),
                // the same as the C service's x25_clear(X25_CAUSE_DTE_ORIGINATED, 0).
                int maxCalls = _settings.MaxCalls;
                if (_sessionByLcn.Count >= maxCalls || _engine.CallCount >= maxCalls)
                {
                    _logger.LogError("CHAT: no room for the call from {Calling} ({Max} calls), refusing it", callingAddress, maxCalls);
                    return false;
                }

                int session = _nextSession++;
                ChatCall call = new ChatCall(session, lcn, AgreedPacketSize(facilities));
                _sessionByLcn[lcn] = session;
                _callBySession[session] = call;
                _logger.LogInformation("CHAT: call from {Calling} to {Called} on local channel {Lcn} (session {Session}, packets of {Size})", callingAddress, calledAddress, lcn, session, call.PacketSize);

                _outbox.Enqueue(new TadOutputEventArgs { Kind = TadOutputKind.CallAccepted, Lcn = lcn });
                _outbox.Enqueue(new TadOutputEventArgs { Kind = TadOutputKind.Data, Lcn = lcn, Data = (byte[])SetPadMessage.Clone(), QBit = true });
                _engine.Tick(_clock());
                bool connected = _engine.Connect(session, CallerLogin, callingAddress);
                if (!connected)
                {
                    // Cannot happen while the count above holds; kept as the C service does it (cleared, cause 0).
                    _logger.LogError("CHAT: the engine keeps too many calls, clearing local channel {Lcn}", lcn);
                    ((IAltosChatSink)this).Clear(session);
                }

                EndOverflowedCalls();
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
                EndOverflowedCalls();
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
                _callBySession.Remove(session);
                _logger.LogInformation("CHAT: call on local channel {Lcn} cleared, cause 0x{Cause:X2} diagnostic {Diagnostic}", lcn, cause, diagnostic);
                _engine.Disconnect(session);
                EndOverflowedCalls();
            }

            Drain();
            RaiseChanged();
        }

        /// <summary>
        /// The call was reset. "The reset procedure is used to reinitialize the virtual call ... and in so doing removes
        /// in each direction all data and interrupt packets which may be in the network" (X.25 (10/96) 4.4.3, text lines
        /// 5784-5785 of the Specs copy). The output still waiting for this call goes with it: the runtime has already
        /// dropped what it held for the call (ProtocolRuntime.OnEndpointReset), the engine is told those octets are gone so
        /// its count of waiting output is right, and a packet from before the reset that reports itself sent later is
        /// not counted again. The same as the C service's x25chat_on_reset. The user stays in the chat.
        /// </summary>
        /// <param name="lcn">The local channel of the call.</param>
        /// <param name="cause">The resetting cause.</param>
        /// <param name="diagnostic">The diagnostic code.</param>
        public void HandleReset(ushort lcn, byte cause, byte diagnostic)
        {
            lock (_gate)
            {
                _logger.LogInformation("CHAT: call on local channel {Lcn} reset, cause 0x{Cause:X2} diagnostic {Diagnostic}", lcn, cause, diagnostic);
                if (!_sessionByLcn.TryGetValue(lcn, out int session) || !_callBySession.TryGetValue(session, out ChatCall? call))
                {
                    return;
                }

                // Requests this service still holds for the call, not yet handed to the runtime, go too.
                int kept = _outbox.Count;
                for (int i = 0; i < kept; i++)
                {
                    TadOutputEventArgs o = _outbox.Dequeue();
                    if (o.Lcn == lcn && o.Kind == TadOutputKind.Data)
                    {
                        continue;
                    }

                    _outbox.Enqueue(o);
                }

                if (call.Waiting > 0)
                {
                    _engine.OutputDrained(session, call.Waiting);
                }

                call.Waiting = 0;
                call.Generation++;
            }
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
                EndOverflowedCalls();
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
        /// The engine sends bytes to a user: cut into data packets of at most the call's packet size
        /// (<see cref="ChatCall.PacketSize"/>, never above <see cref="MaxPacketUserData"/>), every one but the last with
        /// the M bit (X.25 4.3.4). Each packet tells the service and the engine when it has left, so their counts of
        /// output still on its way to that user go down. Output that would take the call past
        /// <see cref="WaitingOutputLimit"/> is not queued, and the call is ended when the engine returns
        /// (<see cref="EndOverflowedCalls"/>), as the C service's sink_send and drain_call do.
        /// </summary>
        void IAltosChatSink.Send(int session, ReadOnlySpan<byte> data)
        {
            if (!_callBySession.TryGetValue(session, out ChatCall? call))
            {
                return;
            }

            if (call.Overflowed || call.Waiting + data.Length > WaitingOutputLimit)
            {
                // The caller has not read for a long time and keeps asking for answers. The engine is not told here -
                // this is inside its own call.
                if (!call.Overflowed)
                {
                    call.Overflowed = true;
                    _overflowed.Add(call);
                }

                call.OverflowBytes += data.Length;
                return;
            }

            call.Waiting += data.Length;
            int generation = call.Generation;
            int offset = 0;
            while (offset < data.Length)
            {
                int length = data.Length - offset > call.PacketSize ? call.PacketSize : data.Length - offset;
                byte[] chunk = data.Slice(offset, length).ToArray();
                offset += length;
                int drained = length;
                _outbox.Enqueue(new TadOutputEventArgs
                {
                    Kind = TadOutputKind.Data,
                    Lcn = call.Lcn,
                    Data = chunk,
                    MBit = offset < data.Length,
                    Delivered = () => OnDelivered(call, generation, drained),
                });
            }
        }

        /// <summary>The engine clears a user's call (table full, or the user left with .x).</summary>
        void IAltosChatSink.Clear(int session)
        {
            if (!_callBySession.TryGetValue(session, out ChatCall? call))
            {
                return;
            }

            _callBySession.Remove(session);
            _sessionByLcn.Remove(call.Lcn);

            // Cause 0, DTE originated: the chat is the called DTE clearing its own call (X.25 Table 5-7).
            _outbox.Enqueue(new TadOutputEventArgs { Kind = TadOutputKind.Clear, Lcn = call.Lcn, Cause = 0, Diagnostic = 0 });
        }

        /// <summary>
        /// Ends every call whose waiting output went past <see cref="WaitingOutputLimit"/> during the last engine call:
        /// the user leaves the chat (the others see "---nick---" once) and the call is cleared, cause 0 "DTE originated"
        /// (X.25 (10/96) Table 5-7), after the output already queued for it - the runtime sends a call's requests in
        /// order. The same as the C service's drain_call. Called under the lock, after the engine has returned; the
        /// leave can push another call past its limit, so the list is walked until it stays empty.
        /// </summary>
        private void EndOverflowedCalls()
        {
            while (_overflowed.Count > 0)
            {
                ChatCall call = _overflowed[_overflowed.Count - 1];
                _overflowed.RemoveAt(_overflowed.Count - 1);
                if (!_callBySession.ContainsKey(call.Session))
                {
                    continue; // already gone (cleared, or left with .x in the same input)
                }

                _logger.LogError("CHAT: output for local channel {Lcn} is over {Max} octets ({More} more did not fit); ending the call", call.Lcn, WaitingOutputLimit, call.OverflowBytes);
                _callBySession.Remove(call.Session);
                _sessionByLcn.Remove(call.Lcn);
                _engine.Disconnect(call.Session);
                _outbox.Enqueue(new TadOutputEventArgs { Kind = TadOutputKind.Clear, Lcn = call.Lcn, Cause = 0, Diagnostic = 0 });
            }
        }

        /// <summary>
        /// The packet size this service sends on a call: the size for the direction from the called DTE in the call's
        /// packet size facility (X.25 (10/96) 7.2.2.1), or the standard 128 without one; never above
        /// <see cref="MaxPacketUserData"/>. The runtime's packet layer accepts the request's facilities as asked
        /// (X25Layer, incoming call), so this is the size the call agreed. A facility field that does not decode gives 128.
        /// </summary>
        private static int AgreedPacketSize(byte[]? facilities)
        {
            if (facilities == null || facilities.Length == 0)
            {
                return MaxPacketUserData;
            }

            if (X25Facilities.TryDecode(facilities, out X25Facilities decoded) != X25FacilityError.None)
            {
                return MaxPacketUserData;
            }

            int size = decoded.PacketSizeFromCalled;
            return size > 0 && size < MaxPacketUserData ? size : MaxPacketUserData;
        }

        /// <summary>One line of the chat's server log.</summary>
        void IAltosChatLog.Line(string text)
        {
            _logger.LogInformation("CHAT: {Line}", text);
        }

        // ------------------------------------------------------------------ hand-over to the runtime

        private void OnDelivered(ChatCall call, int generation, int bytes)
        {
            lock (_gate)
            {
                // A packet from before a reset: the reset already took its octets off both counts.
                if (generation != call.Generation)
                {
                    return;
                }

                call.Waiting -= bytes;
                if (call.Waiting < 0)
                {
                    call.Waiting = 0;
                }

                _engine.OutputDrained(call.Session, bytes);
            }
        }

        /// <summary>
        /// One call on the service, under _gate. The session number is this service's own counter, never the channel,
        /// so a late report about a channel that has since been given to a new call cannot reach the new call.
        /// </summary>
        private sealed class ChatCall
        {
            public ChatCall(int session, ushort lcn, int packetSize)
            {
                Session = session;
                Lcn = lcn;
                PacketSize = packetSize;
            }

            /// <summary>The engine's session number.</summary>
            public int Session { get; }

            /// <summary>The local channel.</summary>
            public ushort Lcn { get; }

            /// <summary>Most octets in one data packet to this caller (<see cref="AgreedPacketSize"/>).</summary>
            public int PacketSize { get; }

            /// <summary>Octets handed to the runtime and not yet reported sent; checked against <see cref="WaitingOutputLimit"/>.</summary>
            public int Waiting { get; set; }

            /// <summary>Counts resets; a sent report from an older generation is ignored.</summary>
            public int Generation { get; set; }

            /// <summary>The waiting output went past the limit; the call is being ended.</summary>
            public bool Overflowed { get; set; }

            /// <summary>Octets that did not fit after the limit was reached (logged).</summary>
            public long OverflowBytes { get; set; }
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
