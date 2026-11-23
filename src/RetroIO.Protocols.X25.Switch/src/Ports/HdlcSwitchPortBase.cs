//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RetroIO.Link;
using RetroIO.Link.Hdlc;
using RetroIO.Link.Lapb;
using RetroIO.Link.Models;
using RetroIO.Protocols.X25.Models;
using RetroIO.Protocols.X25.PacketLayer;

namespace RetroIO.Protocols.X25.Switch.Ports
{
    /// <summary>
    /// The part that every HDLC trunk port has in common: X.25 over LAPB over 0x7E HDLC framing, on a byte
    /// stream that a subclass provides (a TCP connection, or a serial line).
    /// </summary>
    /// <remarks>
    /// <para><b>What a trunk is</b></para>
    /// <para>
    /// A trunk is meant to behave like a leased line between two switches. The bytes on the line are 0x7E
    /// flags, byte stuffing (0x7D, then the byte XOR 0x20) and a 16 bit checksum (the FCS), around a LAPB frame
    /// (X.25 clause 2), which carries one X.25 packet in each information frame. LAPB has its own numbering,
    /// acknowledgements, timers and retransmission, so a frame that is lost or damaged on the line is sent
    /// again and the packet layer above never sees the loss. XOT (RFC 1613) is something else: X.25 packets
    /// straight in TCP with a length header, no LAPB, and one TCP connection for each call.
    /// </para>
    /// <para><b>Layers, bottom to top</b></para>
    /// <list type="number">
    /// <item><description>The stream: <see cref="Stream"/> from a subclass. This class only reads and writes bytes.</description></item>
    /// <item><description>HDLC framing: <see cref="HdlcEncoder"/> and <see cref="HdlcDecoder"/> from <c>RetroIO.Hdlc</c>.
    /// A frame with a bad FCS is thrown away and counted; LAPB recovers it by its own timers.</description></item>
    /// <item><description>LAPB: <see cref="LapbStation"/>, the same station every other link in RetroIO uses. This
    /// class does not carry a second LAPB.</description></item>
    /// <item><description>X.25: one <see cref="X25Layer"/> for the whole port (a trunk is one link, so there is one
    /// packet layer, and the logical channel numbers on the wire are the channel numbers the engine uses; nothing is
    /// renumbered).</description></item>
    /// </list>
    /// <para><b>Roles</b></para>
    /// <para>
    /// <see cref="PortConfig.Mode"/> says DCE or DTE, as for the XOT port. DCE or DTE decides the LAPB address pair
    /// (X.25 2.4.2: a command from the DTE uses address 0x01 and a command from the DCE uses 0x03), the side of the
    /// channel range the X.25 layer starts its search from (Annex A), and which side wins when both place a call at
    /// once. For a TCP trunk the DCE listens and the DTE dials, again as for the XOT port. The end that dials sends
    /// the SABM; the end that listens waits for it, as the C emulator's threaded switch does. A serial line has no
    /// listening or dialling, so there both ends send a SABM and the LAPB layer settles a collision of the two.
    /// </para>
    /// <para><b>The X.25 restart</b></para>
    /// <para>
    /// X.25 section 4.1: the packet layer starts with a Restart, and every time the link layer comes up again
    /// after it was lost or reset, the packet layer restarts, because whatever was in flight is gone. As soon as
    /// LAPB is up this class sends a Restart Request (the packet layer holds back any call placed meanwhile until
    /// the Restart Confirmation comes). When both ends restart at once the packet layer takes it as completed
    /// (X.25 4.1.2). A link that goes down clears every call on it, with cause "out of order", which makes the
    /// switch clear the other leg of each one.
    /// </para>
    /// <para><b>Threads and locks</b></para>
    /// <para>
    /// The LAPB station has its own lock and its own timer thread, and raises its events inside that lock. If the
    /// port handled those events at once it would take the one lock shared by every port (<see cref="PortLock"/>)
    /// while holding the station's lock, and another port doing the opposite would wait for it in a circle. So
    /// the station's events (information received, state changed, link reset) are only put in a queue, and the queue
    /// is worked off by <c>Drain</c>, which takes the shared lock and holds no station lock. Drain runs from the
    /// reader thread after each frame and from the 100 ms tick. The order of events is kept. Sending goes the
    /// other way round (shared lock, then station lock), which is the safe order.
    /// </para>
    /// <para><b>What a monitor reads</b></para>
    /// <para>
    /// <see cref="PacketCaptured"/> shows every X.25 packet in both directions; <see cref="Counters"/> carries the
    /// packet and byte totals, counted at the X.25 packet layer as the XOT port and the C emulator count them;
    /// the connection counts come from <see cref="IMonitoredSwitchPort"/>; and <see cref="GetLinkSnapshot"/> gives
    /// the LAPB view: link state, frames, retransmissions, REJ, link resets, FCS errors and wire bytes.
    /// </para>
    /// </remarks>
    public abstract class HdlcSwitchPortBase : IMonitoredSwitchPort, IDisposable
    {
        /// <summary>
        /// How often the packet layer's timers are given the time, and the event queue is worked off.
        /// </summary>
        private const int TickIntervalMs = 100;

        /// <summary>
        /// How long to wait before dialling or opening again after a try failed, in milliseconds.
        /// </summary>
        protected const int RedialDelayMs = 2000;

        /// <summary>
        /// The clearing cause reported for a circuit whose link was lost: out of order.
        /// </summary>
        private const byte CauseOutOfOrder = 0x09;

        private const byte CallRequestType = 0x0B;

        /// <summary>
        /// The framing rules: 0x7E flags, only the flag and the escape octets are stuffed (as the C emulator
        /// does, and the minimum X.25 clause 2.2.2.2 asks for), and a 16 bit FCS on every frame. A receiver
        /// takes a stuffed control octet as well, so a peer that stuffs more is understood.
        /// </summary>
        private static readonly HdlcFramerOptions Framing = HdlcFramerOptions.X25 with
        {
            SendAccm = HdlcFramerOptions.AccmNone,
        };

        private readonly object _sync = PortLock.Shared;
        private readonly object _writeSync = new object();
        private readonly PortConfig _config;
        private readonly ILogger _logger;
        private readonly X25Layer _layer;
        private readonly LapbStation _station;
        private readonly Timer _ticker;
        private readonly ConcurrentQueue<LinkEvent> _events = new ConcurrentQueue<LinkEvent>();
        private readonly string _mediumName;

        private Thread? _supervisor;
        private volatile bool _running;
        private bool _active;
        private bool _linkUp;
        private bool _disposed;
        private int _nextConnectionNumber;
        private volatile HdlcConnection? _current;

        private int _connectAttempts;
        private int _connectFailures;
        private int _failuresSinceUp;
        private int _disconnects;
        private int _linkUps;
        private long _lastConnectedMs;
        private long _lastDisconnectedMs;
        private long _lastFailureMs;
        private long _lastLinkUpMs;
        private long _wireBytesIn;
        private long _wireBytesOut;
        private long _fcsErrors;
        private long _otherBadFrames;
        private string _lastError = string.Empty;

        /// <summary>
        /// Creates the port. Nothing happens on the line until <see cref="Start"/>.
        /// </summary>
        /// <param name="config">
        /// The port's configuration: <c>Mode</c>, the LAPB values (<c>LapbT1Ms</c> and so on), the channel range.
        /// </param>
        /// <param name="logger">
        /// Where the port reports what happens to its link. Null reports nothing.
        /// </param>
        /// <param name="mediumName">
        /// The word for the line in log lines and errors, for example <c>TCP</c> or <c>serial</c>.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="config"/> is null.
        /// </exception>
        /// <exception cref="InvalidDataException">
        /// Thrown when a LAPB value in the configuration cannot work (for example a window of 9, or T2 not less than T1).
        /// The message names the port and the value.
        /// </exception>
        protected HdlcSwitchPortBase(PortConfig config, ILogger? logger, string mediumName)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _logger = logger ?? NullLogger.Instance;
            _mediumName = mediumName;
            Id = config.Id;

            LapbSettings lapb = LapbSettings.From(config);
            RedialDelay = RedialDelayMs;

            X25Config layerConfig = new X25Config
            {
                // A DCE port faces a DTE, so it is the DCE. This is the direction the engine searches for a free
                // channel (Annex A) and which side wins a call collision (4.1.3).
                IsDte = !config.BetweenNetworks && config.Mode != PortMode.Dce,

                // A trunk to another network is an X.75 STE, neither DTE nor DCE. Its mode then only picks the
                // channel search direction X.75 3.1.4 leaves to agreement: dte searches from the highest, dce from
                // the lowest, the same split as X.25 Annex A, so two ends set dte/dce never search the same way.
                IsSte = config.BetweenNetworks,

                // The switch confirms an Interrupt when the far end has (X.25 4.3.7, X.75 3.3.5).
                ConfirmInterruptsFromFarEnd = true,
                SearchFromHighest = config.Mode == PortMode.Dte,
                LciBase = config.LciBase,
                LciCount = config.LciCount,
                DefaultWindowSize = config.DefaultWindowSize,
                DefaultPacketSize = config.DefaultPacketSize,
            };

            _layer = new X25Layer(new PacketSink(this), layerConfig);
            _layer.IncomingCall += OnLayerIncomingCall;
            _layer.CallConnected += OnLayerCallConnected;
            _layer.DataReceived += OnLayerData;
            _layer.Cleared += OnLayerCleared;
            _layer.Reset += OnLayerReset;
            _layer.InterruptReceived += OnLayerInterrupt;
            _layer.InterruptConfirmed += OnLayerInterruptConfirmed;
            _layer.WindowOpened += OnLayerWindowOpened;
            _layer.RestartOccurred += OnLayerRestart;
            _layer.RestartTimedOut += OnLayerRestartTimedOut;

            _station = new LapbStation(_logger, "port" + config.Id, lapb.Window, lapb.N2, lapb.T1, lapb.T2, lapb.T3,
                isPrimary: config.Mode != PortMode.Dce);
            _station.DataReceived += (sender, info) => _events.Enqueue(new LinkEvent(LinkEventKind.Information, info, LapbState.Disconnected));
            _station.StateChanged += (sender, e) => _events.Enqueue(new LinkEvent(LinkEventKind.State, null, e.NewState));
            _station.LinkReset += (sender, e) => _events.Enqueue(new LinkEvent(LinkEventKind.LinkReset, null, LapbState.Disconnected));
            _station.ErrorOccurred += (sender, reason) => _logger.LogError("Port {Id}: LAPB link failure: {Reason}", Id, reason);
            _station.FrameToTransmit += OnStationFrame;

            _ticker = new Timer(OnTick, null, Timeout.Infinite, Timeout.Infinite);
        }

        // ------------------------------------------------------------------ what a subclass provides

        /// <summary>
        /// True when this end sends the SABM as soon as a stream is attached. A dialling end does; a listening
        /// end waits for the peer's SABM. A serial line has no such difference, so both ends do.
        /// </summary>
        protected abstract bool SendsSabmOnConnect { get; }

        /// <summary>
        /// Gets the logger, for subclasses.
        /// </summary>
        protected ILogger Logger => _logger;

        /// <summary>
        /// Gets the configuration, for subclasses.
        /// </summary>
        protected PortConfig PortSettings => _config;

        /// <summary>
        /// Gets whether the port is meant to be running (started and not stopped).
        /// </summary>
        protected bool IsRunning => _running;

        /// <summary>
        /// Prepares the line when the port starts, for example binds a listener or opens the serial device. It runs
        /// before any thread starts. Throw to refuse: the port logs the error, stays down and <see cref="Start"/>
        /// returns false.
        /// </summary>
        protected abstract void OpenLine();

        /// <summary>
        /// Gets the stream of a line that is already open, or null if the line has to be acquired by the supervisor.
        /// Called once, from <see cref="Start"/>, after <see cref="OpenLine"/>. A serial port returns the stream of the
        /// device it opened; a TCP port returns null and gets its connections from <see cref="RunSupervisor"/>.
        /// </summary>
        protected abstract Stream? TakeOpenedStream();

        /// <summary>
        /// The supervisor loop: gets streams for the port for as long as it runs and hands each to
        /// <see cref="AttachStream"/>. A listening port accepts; a dialling port dials, and dials again
        /// every <see cref="RedialDelay"/> after a failure. Returns when <see cref="IsRunning"/> turns false.
        /// </summary>
        protected abstract void RunSupervisor();

        /// <summary>
        /// Releases what <see cref="OpenLine"/> made (closes the listener or the device). Called from <see cref="Stop"/>.
        /// </summary>
        protected abstract void CloseLine();

        // ------------------------------------------------------------------ the port contract

        /// <summary>
        /// Gets the port number given to the constructor.
        /// </summary>
        public int Id { get; }

        /// <summary>
        /// Gets the configuration this port was created with.
        /// </summary>
        public PortConfig Config => _config;

        /// <summary>
        /// Gets whether the port is up: started, and listening, dialling or holding the device.
        /// </summary>
        public bool Active
        {
            get { lock (_sync) { return _active; } }
        }

        /// <summary>
        /// Gets whether the LAPB link is up, which is what "the trunk is linked" means. A TCP connection that
        /// has not yet finished its SABM and UA exchange does not count.
        /// </summary>
        public bool PeerConnected
        {
            get { lock (_sync) { return _linkUp; } }
        }

        /// <summary>
        /// Gets the packet and byte totals of this port, counted at the X.25 packet layer.
        /// </summary>
        public PortCounters Counters { get; } = new PortCounters();

        /// <inheritdoc/>
        public event PortIncomingCall? IncomingCall;

        /// <inheritdoc/>
        public event PortCallConnected? CallConnected;

        /// <inheritdoc/>
        public event PortDataReceived? DataReceived;

        /// <inheritdoc/>
        public event PortCallCleared? CallCleared;

        /// <inheritdoc/>
        public event PortReset? CircuitReset;

        /// <inheritdoc/>
        public event PortInterrupt? InterruptReceived;

        /// <inheritdoc/>
        public event PortInterruptConfirmed? InterruptConfirmed;

        /// <inheritdoc/>
        public event PortReadyToSend? ReadyToSend;

        /// <inheritdoc/>
        public event PortStateChanged? StateChanged;

        /// <inheritdoc/>
        public event PortPacketCaptured? PacketCaptured;

        /// <inheritdoc/>
        public int ConnectAttempts => Volatile.Read(ref _connectAttempts);

        /// <inheritdoc/>
        /// <remarks>
        /// For an HDLC port this is the time the LAPB link last came up, which is what "connected" means for a trunk.
        /// </remarks>
        public long LastConnectedMs => Interlocked.Read(ref _lastConnectedMs);

        /// <inheritdoc/>
        public long LastDisconnectedMs => Interlocked.Read(ref _lastDisconnectedMs);

        /// <inheritdoc/>
        public int ConnectFailures => Volatile.Read(ref _connectFailures);

        /// <inheritdoc/>
        /// <remarks>
        /// Counts each time a connection or open device was lost. A LAPB link that goes down and comes back on the
        /// same connection is counted in <see cref="HdlcLinkSnapshot.LinkResets"/> and <see cref="HdlcLinkSnapshot.LinkUps"/>.
        /// </remarks>
        public int Disconnects => Volatile.Read(ref _disconnects);

        /// <inheritdoc/>
        public string LastError => Volatile.Read(ref _lastError);

        /// <inheritdoc/>
        public long LastFailureMs => Interlocked.Read(ref _lastFailureMs);

        /// <inheritdoc/>
        public int RedialDelay { get; }

        /// <summary>
        /// Gets a snapshot of the LAPB link and the line under it.
        /// </summary>
        /// <returns>
        /// The snapshot. It is a copy: it does not change after it is returned.
        /// </returns>
        public HdlcLinkSnapshot GetLinkSnapshot()
        {
            LapbStatistics s = _station.GetStatistics();
            HdlcConnection? connection = _current;
            bool linkUp;
            lock (_sync)
            {
                linkUp = _linkUp;
            }

            return new HdlcLinkSnapshot(
                medium: _mediumName,
                lapbState: _station.State.ToString(),
                lineOpen: connection != null && !connection.Closed,
                linkUp: linkUp,
                lastLinkUpMs: Interlocked.Read(ref _lastLinkUpMs),
                linkUps: Volatile.Read(ref _linkUps),
                linkResets: s.LinkResets,
                framesIn: s.IFramesReceived + s.SFramesReceived + s.UFramesReceived,
                framesOut: s.IFramesSent + s.SFramesSent + s.UFramesSent,
                informationFramesIn: s.IFramesReceived,
                informationFramesOut: s.IFramesSent,
                retransmissions: s.Retransmissions,
                rejectsSent: s.RejectionsSent,
                rejectsReceived: s.RejectionsReceived,
                t1Timeouts: s.T1Timeouts,
                fcsErrors: Interlocked.Read(ref _fcsErrors),
                badFramesOther: Interlocked.Read(ref _otherBadFrames),
                wireBytesIn: Interlocked.Read(ref _wireBytesIn),
                wireBytesOut: Interlocked.Read(ref _wireBytesOut));
        }

        /// <summary>
        /// Starts the port: opens the line, starts the supervisor and the timer.
        /// </summary>
        /// <remarks>
        /// A second call while the port is running changes nothing and returns <see cref="Active"/>. A port whose line
        /// cannot be prepared (the address is in use, the serial device cannot be opened, a value is not usable)
        /// logs an error that names the port and what failed, stays down, and returns <c>false</c>; it does not throw.
        /// </remarks>
        /// <returns>
        /// <c>true</c> when the port started; <c>false</c> when it could not.
        /// </returns>
        public bool Start()
        {
            lock (_sync)
            {
                if (_running)
                {
                    return _active;
                }

                _running = true;
            }

            try
            {
                OpenLine();

                Stream? opened = TakeOpenedStream();
                if (opened != null)
                {
                    Interlocked.Increment(ref _connectAttempts);
                    AttachStream(opened, "opened " + _mediumName + " line");
                }

                _supervisor = new Thread(RunSupervisor) { IsBackground = true, Name = "HdlcSwitchPort" + Id };
                _supervisor.Start();
                _ticker.Change(TickIntervalMs, TickIntervalMs);

                SetActive(true);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Port {Id} ({Mode} {Type}, transport {Transport}, HDLC over {Medium}): could NOT start - {Reason} - the port is down",
                    Id, _config.Mode, _config.Type, SwitchPortFactory.Describe(_config.Transport), _mediumName, ex.Message);
                _running = false;
                Interlocked.Increment(ref _connectFailures);
                Interlocked.Exchange(ref _lastFailureMs, Environment.TickCount64);
                Volatile.Write(ref _lastError, ex.Message);
                try
                {
                    CloseLine();
                }
                catch (Exception)
                {
                    // Already down.
                }

                SetActive(false);
                return false;
            }
        }

        /// <summary>
        /// Stops the port: tells the peer with a DISC if the link is up, closes the line, ends every circuit.
        /// </summary>
        /// <remarks>
        /// Safe to call more than once.
        /// </remarks>
        public void Stop()
        {
            _running = false;
            _ticker.Change(Timeout.Infinite, Timeout.Infinite);

            HdlcConnection? current = _current;
            if (current != null && !current.Closed && _station.State == LapbState.DataTransfer)
            {
                // Best effort: a peer that sees the DISC knows the link was ended on purpose.
                try
                {
                    _station.Disconnect();
                }
                catch (Exception)
                {
                    // The line may already be gone.
                }
            }

            try
            {
                CloseLine();
            }
            catch (Exception)
            {
                // Already closed; the supervisor leaves on its own.
            }

            if (current != null)
            {
                CloseConnection(current, "port stopped");
            }

            Thread? supervisor = _supervisor;
            _supervisor = null;
            supervisor?.Join(1000);

            SetActive(false);
        }

        /// <summary>
        /// Stops the port and disposes its timer and its LAPB station.
        /// </summary>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Stop();
            _ticker.Dispose();
            _station.Dispose();
        }

        /// <inheritdoc/>
        public ushort PlaceCall(string calledAddress, string callingAddress, byte[]? callUserData)
        {
            lock (_sync)
            {
                if (!_linkUp)
                {
                    return 0;
                }

                // A call placed while our Restart is unanswered is held by the packet layer until the
                // confirmation comes (X.25 4.1.1), so there is no need to wait for it here.
                return _layer.PlaceCall(calledAddress, callingAddress, callUserData);
            }
        }

        /// <inheritdoc/>
        public ushort PlaceCall(string calledAddress, string callingAddress, byte[]? callUserData, X25CallParameters requested)
        {
            lock (_sync)
            {
                if (!_linkUp)
                {
                    return 0;
                }

                return _layer.PlaceCall(calledAddress, callingAddress, callUserData, requested);
            }
        }

        /// <inheritdoc/>
        public bool TryGetCallParameters(ushort lci, out X25CallParameters parameters)
        {
            lock (_sync)
            {
                return _layer.TryGetCallParameters(lci, out parameters);
            }
        }

        /// <inheritdoc/>
        public void AcceptCall(ushort lci, X25CallParameters agreed)
        {
            lock (_sync)
            {
                _layer.AcceptCall(lci, agreed);
            }
        }

        /// <inheritdoc/>
        public void AcceptCall(ushort lci)
        {
            lock (_sync)
            {
                _layer.AcceptCall(lci);
            }
        }

        /// <inheritdoc/>
        public PortSendResult SendData(ushort lci, ReadOnlySpan<byte> data, bool qBit, bool mBit, bool dBit)
        {
            lock (_sync)
            {
                if (!_linkUp)
                {
                    return PortSendResult.Failed;
                }

                if (_layer.SendData(lci, data.ToArray(), qBit, mBit, dBit))
                {
                    return PortSendResult.Sent;
                }

                return WouldFitLater(lci, data.Length) ? PortSendResult.WindowFull : PortSendResult.Failed;
            }
        }

        /// <inheritdoc/>
        public void ClearCall(ushort lci, byte cause, byte diagnostic)
        {
            lock (_sync)
            {
                _layer.ClearCall(lci, cause, diagnostic);
            }
        }

        /// <inheritdoc/>
        public void ClearCall(ushort lci, byte cause, byte diagnostic, byte[] facilities)
        {
            lock (_sync)
            {
                _layer.ClearCall(lci, cause, diagnostic, facilities);
            }
        }

        /// <inheritdoc/>
        public byte[] GetClearUtilities(ushort lci)
        {
            lock (_sync)
            {
                return _layer.GetClearUtilities(lci);
            }
        }

        /// <inheritdoc/>
        public void ClearCall(ushort lci, byte cause, byte diagnostic, byte[] facilities, byte[]? transitList)
        {
            lock (_sync)
            {
                _layer.ClearCall(lci, cause, diagnostic, facilities, transitList);
            }
        }

        /// <inheritdoc/>
        public byte[] GetClearFacilities(ushort lci)
        {
            lock (_sync)
            {
                return _layer.GetClearFacilities(lci);
            }
        }

        /// <inheritdoc/>
        public Func<ushort, byte[]?>? ClearConfirmationFacilities
        {
            get { lock (_sync) { return _layer.ClearConfirmationFacilities; } }
            set { lock (_sync) { _layer.ClearConfirmationFacilities = value; } }
        }

        /// <inheritdoc/>
        public void ResetCircuit(ushort lci, byte cause, byte diagnostic)
        {
            lock (_sync)
            {
                _layer.ResetCall(lci, cause, diagnostic);
            }
        }

        /// <inheritdoc/>
        /// <remarks>
        /// One packet layer serves the whole line, so the PVC lives in it beside the calls; a restart of the line
        /// resets it (X.25 4.5) and leaves it in place.
        /// </remarks>
        public bool AddPvc(ushort lci)
        {
            lock (_sync)
            {
                _layer.AddPvc(lci, _config.DefaultWindowSize, _config.DefaultPacketSize);
                return true;
            }
        }

        /// <inheritdoc/>
        public void ConfirmInterrupt(ushort lci)
        {
            lock (_sync)
            {
                _layer.ConfirmInterrupt(lci);
            }
        }

        /// <inheritdoc/>
        public void SendInterrupt(ushort lci, byte[] userData)
        {
            lock (_sync)
            {
                _layer.SendInterrupt(lci, userData);
            }
        }

        /// <inheritdoc/>
        public void SetLocalBusy(ushort lci, bool busy)
        {
            lock (_sync)
            {
                _layer.SetBusy(lci, busy);
            }
        }

        private bool WouldFitLater(ushort lci, int length)
        {
            if (!_layer.Circuits.TryGetValue(lci, out X25Vc? vc))
            {
                return false;
            }

            if (vc.State != X25VcState.DataTransfer && vc.State != X25VcState.PvcReady)
            {
                return false;
            }

            if (length > vc.PacketSizeOut)
            {
                return false;
            }

            return vc.PeerBusy || vc.PendingSegments.Count > 0 || ((vc.SendSeq - vc.AckedSeq) & 0x07) >= vc.WindowSizeOut;
        }

        // ------------------------------------------------------------------ the line

        /// <summary>
        /// Notes a try to make a connection (a dial, an accept, an open) for <see cref="ConnectAttempts"/>.
        /// </summary>
        protected void NoteConnectAttempt()
        {
            Interlocked.Increment(ref _connectAttempts);
        }

        /// <summary>
        /// Notes that a try to get a line failed, and logs it the way the XOT port logs a failed dial: the first failure
        /// and then every fifteenth is a warning, the rest are debug lines.
        /// </summary>
        /// <param name="what">
        /// What was being tried, for example <c>dial to 10.0.0.2:2003</c>.
        /// </param>
        /// <param name="reason">
        /// Why it failed, in words.
        /// </param>
        protected void NoteConnectFailure(string what, string reason)
        {
            Interlocked.Increment(ref _connectFailures);
            int inARow = Interlocked.Increment(ref _failuresSinceUp);
            Interlocked.Exchange(ref _lastFailureMs, Environment.TickCount64);
            Volatile.Write(ref _lastError, reason);

            if (inARow == 1 || inARow % 15 == 0)
            {
                _logger.LogWarning("Port {Id} ({Mode} {Type}, HDLC over {Medium}): {What} failed: {Reason}. Failure {Count} in a row; the next try is in {Delay} seconds, and then every {Delay} seconds until it connects",
                    Id, _config.Mode, _config.Type, _mediumName, what, reason, inARow, RedialDelayMs / 1000, RedialDelayMs / 1000);
            }
            else
            {
                _logger.LogDebug("Port {Id}: {What} failed ({Reason}), failure {Count} in a row", Id, what, reason, inARow);
            }
        }

        /// <summary>
        /// Waits for the redial delay in short steps, so that a stop is not held up.
        /// </summary>
        protected void WaitRedialDelay()
        {
            for (int waited = 0; waited < RedialDelayMs && _running; waited += 100)
            {
                Thread.Sleep(100);
            }
        }

        /// <summary>
        /// Gets whether a line is attached now.
        /// </summary>
        protected bool HasLine
        {
            get
            {
                HdlcConnection? c = _current;
                return c != null && !c.Closed;
            }
        }

        /// <summary>
        /// Makes a connected stream the line of this port and starts reading it. A line that was attached before
        /// is closed first: a new connection from the peer replaces an old one, which may be half dead.
        /// </summary>
        /// <param name="stream">
        /// The stream of the new line. The port owns it from now on and disposes it.
        /// </param>
        /// <param name="how">
        /// How it came about, for the log (for example <c>accepted</c> or <c>dialled</c>).
        /// </param>
        protected void AttachStream(Stream stream, string how)
        {
            HdlcConnection? old = _current;
            if (old != null && !old.Closed)
            {
                CloseConnection(old, "replaced by a new connection");
            }

            HdlcConnection connection = new HdlcConnection(Interlocked.Increment(ref _nextConnectionNumber), stream);

            lock (_sync)
            {
                _current = connection;
            }

            int failedBefore = Interlocked.Exchange(ref _failuresSinceUp, 0);
            _logger.LogInformation("Port {Id} ({Mode} {Type}, HDLC over {Medium}): line up ({How}, connection {Conn}){After}",
                Id, _config.Mode, _config.Type, _mediumName, how, connection.Number,
                failedBefore > 0 ? " after " + failedBefore + " failed try(s)" : string.Empty);

            // Whatever the station held from an earlier line is gone with that line.
            _station.Reset();

            connection.Reader = new Thread(() => ReadUntilClosed(connection))
            {
                IsBackground = true,
                Name = "HdlcSwitchPort" + Id + "." + connection.Number,
            };
            connection.Reader.Start();

            if (SendsSabmOnConnect)
            {
                _station.Connect();
            }
        }

        private void ReadUntilClosed(HdlcConnection connection)
        {
            string reason = "closed by peer";
            byte[] buffer = new byte[4096];

            try
            {
                while (_running && !connection.Closed)
                {
                    int read = connection.Stream.Read(buffer, 0, buffer.Length);
                    if (read <= 0)
                    {
                        break;
                    }

                    Interlocked.Add(ref _wireBytesIn, read);
                    ProcessBytes(connection, buffer, read);
                }
            }
            catch (Exception ex)
            {
                // A dropped line is ordinary; the reason is kept for the "line down" message.
                reason = ex.Message;
            }

            CloseConnection(connection, reason);
            connection.Decoder.Dispose();
        }

        /// <summary>
        /// Takes bytes off the line: unstuffs them into frames, checks each FCS, and gives every good frame to LAPB.
        /// </summary>
        private void ProcessBytes(HdlcConnection connection, byte[] buffer, int count)
        {
            int offset = 0;
            while (offset < count)
            {
                HdlcDecodeStatus status = connection.Decoder.Decode(new ReadOnlySpan<byte>(buffer, offset, count - offset), out int consumed);
                offset += consumed;

                if (status == HdlcDecodeStatus.NeedMoreData)
                {
                    break;
                }

                if (status == HdlcDecodeStatus.FrameComplete)
                {
                    // The decoder hands the frame over with its FCS (two octets) on the end; LAPB gets address,
                    // control and information.
                    ReadOnlySpan<byte> frame = connection.Decoder.Frame;
                    byte[] body = frame.Slice(0, frame.Length - 2).ToArray();
                    _station.ReceiveFrame(body, body.Length);

                    // The station's events were only queued while it held its lock; work them off now that
                    // the lock is free (see the remarks on this class).
                    Drain();
                }
                else if (status == HdlcDecodeStatus.BadFcs)
                {
                    // A damaged frame is dropped and counted. LAPB asks for it again (REJ) or its T1
                    // runs out and it is sent again.
                    Interlocked.Increment(ref _fcsErrors);
                    _logger.LogDebug("Port {Id}: frame with a bad FCS dropped", Id);
                }
                else if (status == HdlcDecodeStatus.Runt || status == HdlcDecodeStatus.Overlong || status == HdlcDecodeStatus.Aborted)
                {
                    Interlocked.Increment(ref _otherBadFrames);
                    _logger.LogDebug("Port {Id}: HDLC frame dropped ({Status})", Id, status);
                }
            }
        }

        private void CloseConnection(HdlcConnection connection, string reason)
        {
            bool wasCurrent;
            lock (_sync)
            {
                if (connection.Closed)
                {
                    return;
                }

                connection.Closed = true;
                wasCurrent = ReferenceEquals(_current, connection);
                if (wasCurrent)
                {
                    _current = null;
                }
            }

            try
            {
                connection.Stream.Dispose();
            }
            catch (Exception)
            {
                // Already closed.
            }

            // The decoder is given back only when its reader thread has finished with it (see ReadUntilClosed).
            Interlocked.Exchange(ref _lastDisconnectedMs, Environment.TickCount64);
            Interlocked.Increment(ref _disconnects);

            if (_running)
            {
                _logger.LogWarning("Port {Id} ({Mode} {Type}, HDLC over {Medium}): line down ({Reason}, connection {Conn}){Retry}",
                    Id, _config.Mode, _config.Type, _mediumName, reason, connection.Number,
                    RetryText());
            }
            else
            {
                _logger.LogInformation("Port {Id} ({Mode} {Type}, HDLC over {Medium}): line down ({Reason}, connection {Conn})",
                    Id, _config.Mode, _config.Type, _mediumName, reason, connection.Number);
            }

            if (wasCurrent)
            {
                // Nobody is there to answer, so nothing is sent; the link goes down at once and the circuits
                // on it are cleared by the state event that follows.
                _station.Reset();
                Drain();
            }
        }

        /// <summary>
        /// Gives the words about trying again that go on the end of a "line down" warning. A dialling port and a
        /// serial port try again by themselves; a listening port waits for the peer.
        /// </summary>
        protected virtual string RetryText()
        {
            return string.Empty;
        }

        /// <summary>
        /// Writes one LAPB frame to the line: HDLC framing and FCS around it. Called by the station, with the
        /// station's lock held.
        /// </summary>
        private void OnStationFrame(object? sender, LapbFrameEventArgs e)
        {
            HdlcConnection? connection = _current;
            if (connection == null || connection.Closed)
            {
                return;
            }

            ReadOnlySpan<byte> body = new ReadOnlySpan<byte>(e.FrameData, 0, e.Length);
            int max = HdlcEncoder.GetMaxEncodedLength(body.Length);
            byte[] rented = ArrayPool<byte>.Shared.Rent(max);
            try
            {
                int written = HdlcEncoder.Encode(body, rented, Framing);
                lock (_writeSync)
                {
                    connection.Stream.Write(rented, 0, written);
                    connection.Stream.Flush();
                }

                Interlocked.Add(ref _wireBytesOut, written);
            }
            catch (Exception ex)
            {
                // Closing the stream makes the reader thread's read return, and that thread does the tidying up.
                // Doing it here would change the station's state in the middle of its own callback.
                _logger.LogDebug("Port {Id}: write failed ({Reason}); the line is being closed", Id, ex.Message);
                try
                {
                    connection.Stream.Dispose();
                }
                catch (Exception)
                {
                    // Already closed.
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }

        // ------------------------------------------------------------------ the event queue

        /// <summary>
        /// Works off what the LAPB station has queued: information frames, state changes, link resets. Takes the
        /// shared port lock and holds no station lock.
        /// </summary>
        private void Drain()
        {
            lock (_sync)
            {
                while (_events.TryDequeue(out LinkEvent ev))
                {
                    switch (ev.Kind)
                    {
                        case LinkEventKind.Information:
                            HandleInformation(ev.Data!);
                            break;
                        case LinkEventKind.State:
                            HandleState(ev.State);
                            break;
                        case LinkEventKind.LinkReset:
                            HandleLinkReset();
                            break;
                    }
                }
            }
        }

        private void HandleInformation(byte[] packet)
        {
            // Counted and shown to the tap before the engine sees it, so a packet the engine then refuses still
            // appears in the monitor and in the byte total, as it crossed the line (the XOT port does the same).
            Counters.AddBytesIn(packet.Length);
            Counters.IncrementAllPacketsIn();
            PortPacketCaptured? tap = PacketCaptured;
            if (tap != null)
            {
                tap(Id, true, packet);
            }

            if (!_linkUp)
            {
                // Information that arrives after the link was reported down belongs to a link that no longer exists.
                _logger.LogDebug("Port {Id}: information frame dropped, the link is down", Id);
                return;
            }

            _layer.ProcessPacket(packet);
        }

        private void HandleState(LapbState state)
        {
            if (state == LapbState.DataTransfer)
            {
                if (_linkUp)
                {
                    return;
                }

                _linkUp = true;
                Interlocked.Increment(ref _linkUps);
                long now = Environment.TickCount64;
                Interlocked.Exchange(ref _lastLinkUpMs, now);
                Interlocked.Exchange(ref _lastConnectedMs, now);
                _logger.LogInformation("Port {Id} ({Mode} {Type}, HDLC over {Medium}): LAPB link up; restarting X.25",
                    Id, _config.Mode, _config.Type, _mediumName);

                // X.25 4.1: the packet layer restarts whenever the link layer has come up. X.25 4.6.5: "When the out of
                // order condition is recovered ... the DCE will send a restart indication packet with the cause 'Network
                // operational'". Until 2026-11-17 this sent cause 0, which Table 5-9 does not give a DCE. On a DTE port
                // the engine turns it into 0, "DTE originated" (Table 5-9 note a).
                _layer.Restart(X25Cause.RestartingNetworkOperational);
                return;
            }

            if (!_linkUp)
            {
                return;
            }

            _linkUp = false;
            _logger.LogWarning("Port {Id} ({Mode} {Type}, HDLC over {Medium}): LAPB link down (state {State}); every call on it is cleared",
                Id, _config.Mode, _config.Type, _mediumName, state);
            AbortAllCircuits();
            StateChanged?.Invoke(this, false);
            StateChanged?.Invoke(this, _active);
        }

        private void HandleLinkReset()
        {
            // A reset throws away every frame in flight. If the link is still up after it (the peer sent a SABM
            // on a connected link) the packet layer has lost packets and has to restart. If the reset took the
            // link down (our own re-establish), the state events that follow do the work.
            if (!_linkUp || _station.State != LapbState.DataTransfer)
            {
                return;
            }

            _logger.LogWarning("Port {Id} ({Mode} {Type}, HDLC over {Medium}): LAPB link reset; restarting X.25",
                Id, _config.Mode, _config.Type, _mediumName);
            // X.25 4.6.2: a reset of the data link layer is an out of order condition; 4.6.5: on recovery the DCE
            // sends a restart with "Network operational" (Table 5-9 gives a DCE only 01, 03, 07). A DTE port's
            // cause is turned into 0 by the layer's cause rule (Table 5-9 note a).
            _layer.Restart(X25Cause.RestartingNetworkOperational);
        }

        private void AbortAllCircuits()
        {
            ushort[] keys = new ushort[_layer.Circuits.Count];
            int n = 0;
            foreach (ushort key in _layer.Circuits.Keys)
            {
                if (n < keys.Length)
                {
                    keys[n++] = key;
                }
            }

            for (int i = 0; i < n; i++)
            {
                _layer.AbortCircuit(keys[i], CauseOutOfOrder, 0);
            }
        }

        private void SetActive(bool active)
        {
            bool changed;
            lock (_sync)
            {
                changed = _active != active;
                _active = active;
            }

            if (changed)
            {
                StateChanged?.Invoke(this, active);
            }
        }

        private void OnTick(object? state)
        {
            try
            {
                lock (_sync)
                {
                    _layer.Tick(Environment.TickCount64);
                }

                Drain();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Port {Id}: tick failed", Id);
            }
        }

        // ------------------------------------------------------------------ packets out

        private X25SendResult WritePacket(ReadOnlySpan<byte> packet)
        {
            if (!_linkUp)
            {
                return X25SendResult.LinkDown;
            }

            byte[] onWire = packet.ToArray();
            if (!_station.SendData(onWire))
            {
                return X25SendResult.LinkDown;
            }

            Counters.IncrementAllPacketsOut();
            Counters.AddBytesOut(onWire.Length);

            // Only a packet that was handed to LAPB is shown and counted as sent.
            PortPacketCaptured? tap = PacketCaptured;
            if (tap != null)
            {
                tap(Id, false, onWire);
            }

            return X25SendResult.Sent;
        }

        // ------------------------------------------------------------------ engine events

        private void OnLayerIncomingCall(X25Layer layer, ushort lci, string called, string calling, byte[] cud)
        {
            // Not counted here: SwitchCore counts the call when it gets this event (see PortCounters).
            _logger.LogInformation("Port {Id}: incoming call {Calling} -> {Called} LCI={Lci}", Id, calling, called, lci);
            IncomingCall?.Invoke(this, lci, called, calling, cud);
        }

        private void OnLayerCallConnected(X25Layer layer, ushort lci)
        {
            _logger.LogInformation("Port {Id}: call accepted LCI={Lci}", Id, lci);
            CallConnected?.Invoke(this, lci);
        }

        private void OnLayerData(X25Layer layer, ushort lci, byte[] data, int length, bool qBit, bool mBit, bool dBit)
        {
            // Not counted here: SwitchCore counts the data packet when it gets this event (see PortCounters).
            DataReceived?.Invoke(this, lci, data, length, qBit, mBit, dBit);
        }

        private void OnLayerCleared(X25Layer layer, ushort lci, byte cause, byte diagnostic)
        {
            // Not counted here: SwitchCore counts the clear when it gets this event (see PortCounters).
            _logger.LogInformation("Port {Id}: clear LCI={Lci} cause={Cause} diag={Diag}", Id, lci, cause, diagnostic);
            CallCleared?.Invoke(this, lci, cause, diagnostic);
        }

        private void OnLayerReset(X25Layer layer, ushort lci, byte cause, byte diagnostic)
        {
            CircuitReset?.Invoke(this, lci, cause, diagnostic);
        }

        private void OnLayerInterrupt(X25Layer layer, ushort lci, byte[] userData)
        {
            InterruptReceived?.Invoke(this, lci, userData);
        }

        private void OnLayerInterruptConfirmed(X25Layer layer, ushort lci)
        {
            InterruptConfirmed?.Invoke(this, lci);
        }

        private void OnLayerWindowOpened(X25Layer layer, ushort lci)
        {
            ReadyToSend?.Invoke(this, lci);
        }

        private void OnLayerRestart(X25Layer layer, byte cause, byte diagnostic)
        {
            _logger.LogInformation("Port {Id}: X.25 restart done (cause {Cause}, diagnostic {Diag}); every call on the port was cleared",
                Id, cause, diagnostic);
        }

        private void OnLayerRestartTimedOut(X25Layer layer)
        {
            _logger.LogWarning("Port {Id}: the X.25 Restart Request was not answered after every retransmission", Id);
        }

        // ------------------------------------------------------------------ helper types

        private enum LinkEventKind
        {
            Information,
            State,
            LinkReset,
        }

        private readonly struct LinkEvent
        {
            public LinkEvent(LinkEventKind kind, byte[]? data, LapbState state)
            {
                Kind = kind;
                Data = data;
                State = state;
            }

            public LinkEventKind Kind { get; }

            public byte[]? Data { get; }

            public LapbState State { get; }
        }

        /// <summary>
        /// One line (one TCP connection, or the opened serial device) of the port.
        /// </summary>
        private sealed class HdlcConnection
        {
            public HdlcConnection(int number, Stream stream)
            {
                Number = number;
                Stream = stream;
                Decoder = new HdlcDecoder(Framing);
            }

            /// <summary>
            /// Gets the framing state of this line's byte stream. Each line has its own, so the bytes of a new
            /// connection never meet the half frame of the old one.
            /// </summary>
            public HdlcDecoder Decoder { get; }

            public int Number { get; }

            public Stream Stream { get; }

            public Thread? Reader { get; set; }

            public volatile bool Closed;
        }

        /// <summary>
        /// The LAPB values of a port, checked.
        /// </summary>
        private readonly struct LapbSettings
        {
            private LapbSettings(byte window, byte n2, int t1, int t2, int t3)
            {
                Window = window;
                N2 = n2;
                T1 = t1;
                T2 = t2;
                T3 = t3;
            }

            public byte Window { get; }

            public byte N2 { get; }

            public int T1 { get; }

            public int T2 { get; }

            public int T3 { get; }

            /// <summary>
            /// Reads the LAPB values of a port. A value that is zero keeps the default (the C emulator's: T1 3000,
            /// T3 30000, N2 10, k 7; T2 none). A value that cannot work is refused with a message that names the
            /// port, not changed to something else.
            /// </summary>
            public static LapbSettings From(PortConfig c)
            {
                string who = "Port " + c.Id + ": ";
                int t1 = c.LapbT1Ms == 0 ? 3000 : c.LapbT1Ms;
                int t3 = c.LapbT3Ms == 0 ? 30000 : c.LapbT3Ms;
                int n2 = c.LapbN2 == 0 ? 10 : c.LapbN2;
                int k = c.LapbK == 0 ? 7 : c.LapbK;

                if (t1 < 100)
                {
                    throw new InvalidDataException(who + "lapb_t1 = " + c.LapbT1Ms + " is not usable (at least 100 milliseconds)");
                }

                if (t3 < 100)
                {
                    throw new InvalidDataException(who + "lapb_t3 = " + c.LapbT3Ms + " is not usable (at least 100 milliseconds)");
                }

                if (n2 < 1 || n2 > 255)
                {
                    throw new InvalidDataException(who + "lapb_n2 = " + c.LapbN2 + " is not usable (1 to 255)");
                }

                if (k < 1 || k > 7)
                {
                    throw new InvalidDataException(who + "lapb_k = " + c.LapbK + " is not usable: modulo 8 allows a window of 1 to 7");
                }

                if (c.LapbT2Ms < 0 || (c.LapbT2Ms > 0 && c.LapbT2Ms >= t1))
                {
                    throw new InvalidDataException(who + "lapb_t2 = " + c.LapbT2Ms + " is not usable: it must be 0 (no delay) or less than T1 (" + t1 + ")");
                }

                return new LapbSettings((byte)k, (byte)n2, t1, c.LapbT2Ms, t3);
            }
        }

        /// <summary>
        /// Where the packet layer's output goes: one X.25 packet, as one LAPB information frame.
        /// </summary>
        private sealed class PacketSink : IX25PacketSink
        {
            private readonly HdlcSwitchPortBase _owner;

            public PacketSink(HdlcSwitchPortBase owner)
            {
                _owner = owner;
            }

            public X25SendResult SendPacket(ReadOnlySpan<byte> packet)
            {
                return _owner.WritePacket(packet);
            }
        }
    }

    /// <summary>
    /// A copy of the LAPB link and line counters of an HDLC trunk port at one moment.
    /// </summary>
    public sealed class HdlcLinkSnapshot
    {
        /// <summary>
        /// Creates the snapshot.
        /// </summary>
        public HdlcLinkSnapshot(
            string medium, string lapbState, bool lineOpen, bool linkUp, long lastLinkUpMs, int linkUps, long linkResets,
            long framesIn, long framesOut, long informationFramesIn, long informationFramesOut,
            long retransmissions, long rejectsSent, long rejectsReceived, long t1Timeouts,
            long fcsErrors, long badFramesOther, long wireBytesIn, long wireBytesOut)
        {
            Medium = medium;
            LapbState = lapbState;
            LineOpen = lineOpen;
            LinkUp = linkUp;
            LastLinkUpMs = lastLinkUpMs;
            LinkUps = linkUps;
            LinkResets = linkResets;
            FramesIn = framesIn;
            FramesOut = framesOut;
            InformationFramesIn = informationFramesIn;
            InformationFramesOut = informationFramesOut;
            Retransmissions = retransmissions;
            RejectsSent = rejectsSent;
            RejectsReceived = rejectsReceived;
            T1Timeouts = t1Timeouts;
            FcsErrors = fcsErrors;
            BadFramesOther = badFramesOther;
            WireBytesIn = wireBytesIn;
            WireBytesOut = wireBytesOut;
        }

        /// <summary>The line the link runs on: <c>TCP</c> or <c>serial</c>.</summary>
        public string Medium { get; }

        /// <summary>The LAPB state in words (Disconnected, Connecting, DataTransfer, Disconnecting).</summary>
        public string LapbState { get; }

        /// <summary>True while a TCP connection or an opened device is attached.</summary>
        public bool LineOpen { get; }

        /// <summary>True while the LAPB link is up (the SABM and UA exchange is done).</summary>
        public bool LinkUp { get; }

        /// <summary>When the link last came up, as <see cref="Environment.TickCount64"/>; 0 if never.</summary>
        public long LastLinkUpMs { get; }

        /// <summary>How many times the link came up.</summary>
        public int LinkUps { get; }

        /// <summary>
        /// How many times the link's sequence numbers were reset while it was up or coming up (a SABM from the peer on a
        /// connected link, or our own re-establish after N2 ran out). The first set-up is not counted.
        /// </summary>
        public long LinkResets { get; }

        /// <summary>LAPB frames received (information, supervisory and unnumbered).</summary>
        public long FramesIn { get; }

        /// <summary>LAPB frames sent, retransmissions included.</summary>
        public long FramesOut { get; }

        /// <summary>Information frames received.</summary>
        public long InformationFramesIn { get; }

        /// <summary>Information frames sent, retransmissions included.</summary>
        public long InformationFramesOut { get; }

        /// <summary>Information frames sent again.</summary>
        public long Retransmissions { get; }

        /// <summary>REJ frames sent (we saw a gap and asked for a resend).</summary>
        public long RejectsSent { get; }

        /// <summary>REJ frames received (the peer asked for a resend).</summary>
        public long RejectsReceived { get; }

        /// <summary>How many times T1 ran out.</summary>
        public long T1Timeouts { get; }

        /// <summary>Frames dropped because the FCS was wrong.</summary>
        public long FcsErrors { get; }

        /// <summary>Frames dropped as too short, too long or aborted.</summary>
        public long BadFramesOther { get; }

        /// <summary>Bytes read from the line, framing included.</summary>
        public long WireBytesIn { get; }

        /// <summary>Bytes written to the line, framing included.</summary>
        public long WireBytesOut { get; }
    }
}
