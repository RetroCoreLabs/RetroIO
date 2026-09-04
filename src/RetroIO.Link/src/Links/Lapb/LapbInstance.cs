using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using RetroIO.Link.Lapb;
using RetroIO.Protocols.X25.Common;
using RetroIO.Link.Models;

namespace RetroIO.Link.Lapb
{
    /// <summary>
    /// Per-link LAPB state machine with full ITU-T X.25 compliance.
    /// Implements window enforcement, frame tracking, and Go-Back-N recovery.
    /// Each link (SerialLink, TcpLapbLink) has its own LapbInstance.
    /// </summary>
    public class LapbInstance : IDisposable
    {
        private readonly ILogger _logger;
        private readonly string _linkName;
        private readonly LapbWindowManager _windowManager;
        private readonly bool _isPrimary;

        // LAPB Parameters
        private readonly byte _modulo = 8;
        private readonly byte _windowSize;
        private readonly byte _maxRetries;
        private readonly int _t1TimeoutMs;
        private readonly int _t2TimeoutMs;
        private readonly int _t3TimeoutMs;

        // LAPB State Machine Variables
        private LapbState _currentState = LapbState.Disconnected;
        private byte _vs = 0;  // V(S) - Send sequence number
        private byte _vr = 0;  // V(R) - Receive sequence number
        private byte _va = 0;  // V(A) - Acknowledge sequence number
        private bool _remoteBusy = false;
#pragma warning disable CS0414 // LAPB state flag per ITU-T X.25 specification - for future flow control
        private bool _localBusy = false;
#pragma warning restore CS0414
        private bool _rejOutstanding = false;
        private byte _retryCount = 0;

        // Timers
        private System.Threading.Timer? _t1Timer;
        private System.Threading.Timer? _t2Timer;
        private System.Threading.Timer? _t3Timer;
        private readonly object _timerLock = new object();

        // Statistics
        private readonly LapbStatistics _statistics;

        private bool _disposed = false;

        /// <summary>
        /// Create a new LAPB instance for a link
        /// </summary>
        public LapbInstance(
            ILogger logger,
            string linkName,
            byte windowSize = 7,
            byte maxRetries = 10,
            int t1TimeoutMs = 3000,
            int t2TimeoutMs = 1500,
            int t3TimeoutMs = 0,
            bool isPrimary = true)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _linkName = linkName ?? throw new ArgumentNullException(nameof(linkName));

            _windowSize = windowSize;
            _maxRetries = maxRetries;
            _t1TimeoutMs = t1TimeoutMs;
            _t2TimeoutMs = t2TimeoutMs;
            _t3TimeoutMs = t3TimeoutMs;
            _isPrimary = isPrimary;

            _windowManager = new LapbWindowManager(logger, windowSize, _modulo);

            _statistics = new LapbStatistics
            {
                WindowSize = windowSize
            };

            _logger.LogInformation("LAPB[{Link}]: Instance created - K={K}, N2={N2}, T1={T1}ms, T2={T2}ms, T3={T3}ms, Primary={Primary}",
                _linkName, windowSize, maxRetries, t1TimeoutMs, t2TimeoutMs, t3TimeoutMs, isPrimary);
        }

        #region Properties

        /// <summary>
        /// Current LAPB state
        /// </summary>
        public LapbState State => _currentState;

        /// <summary>
        /// Name of the link this instance belongs to
        /// </summary>
        public string LinkName => _linkName;

        /// <summary>
        /// Whether this side is primary (provides T3 supervision)
        /// </summary>
        public bool IsPrimary => _isPrimary;

        /// <summary>
        /// V(S) - Send sequence number
        /// </summary>
        public byte VS => _vs;

        /// <summary>
        /// V(R) - Receive sequence number
        /// </summary>
        public byte VR => _vr;

        /// <summary>
        /// V(A) - Acknowledge sequence number
        /// </summary>
        public byte VA => _va;

        /// <summary>
        /// Number of unacknowledged frames
        /// </summary>
        public int UnackedFrameCount => _windowManager.UnackedFrameCount;

        #endregion

        #region Events

        /// <summary>
        /// Raised when LAPB frame should be transmitted to physical layer
        /// </summary>
        public event EventHandler<LapbFrameEventArgs>? FrameToTransmit;

        /// <summary>
        /// Raised when a frame is received from physical layer (before processing)
        /// </summary>
        public event EventHandler<LapbFrameEventArgs>? FrameReceived;

        /// <summary>
        /// Raised when I-frame payload should be delivered to upper layer
        /// </summary>
        public event EventHandler<byte[]>? DataReceived;

        /// <summary>
        /// Raised when LAPB state changes
        /// </summary>
        public event EventHandler<LapbStateChangedEventArgs>? StateChanged;

        /// <summary>
        /// Raised on protocol errors
        /// </summary>
        public event EventHandler<string>? ErrorOccurred;

        #endregion

        #region Public Methods

        /// <summary>
        /// Get current sequence numbers
        /// </summary>
        public (byte VS, byte VR, byte VA) GetSequenceNumbers()
        {
            return (_vs, _vr, _va);
        }

        /// <summary>
        /// Initiate connection (send SABM)
        /// </summary>
        public void Connect()
        {
            if (_currentState != LapbState.Disconnected)
            {
                _logger.LogWarning("LAPB[{Link}]: Connect ignored - state is {State}", _linkName, _currentState);
                return;
            }

            _logger.LogInformation("LAPB[{Link}]: Initiating connection", _linkName);

            ChangeState(LapbState.Connecting);
            _retryCount = 0;

            // Send SABM with P=1
            SendUFrame(0x2F, true); // SABM
            StartT1Timer();
        }

        /// <summary>
        /// Initiate disconnection (send DISC)
        /// </summary>
        public void Disconnect()
        {
            if (_currentState != LapbState.DataTransfer)
            {
                _logger.LogWarning("LAPB[{Link}]: Disconnect ignored - state is {State}", _linkName, _currentState);
                return;
            }

            _logger.LogInformation("LAPB[{Link}]: Initiating disconnection", _linkName);

            ChangeState(LapbState.Disconnecting);
            _retryCount = 0;

            // Send DISC with P=1
            SendUFrame(0x43, true); // DISC
            StartT1Timer();
            StopT3Timer();
        }

        /// <summary>
        /// Send data in I-frame
        /// </summary>
        /// <param name="data">Data to send (copied into the framed buffer before returning)</param>
        /// <param name="pollFinal">Set P/F bit</param>
        /// <returns>True if sent, false if window full or not in DataTransfer state</returns>
        public bool SendData(ReadOnlySpan<byte> data, bool pollFinal = false)
        {
            if (_currentState != LapbState.DataTransfer)
            {
                _logger.LogWarning("LAPB[{Link}]: Cannot send data - not in DataTransfer state", _linkName);
                return false;
            }

            if (_remoteBusy)
            {
                _logger.LogWarning("LAPB[{Link}]: Cannot send data - remote busy (RNR)", _linkName);
                return false;
            }

            if (!_windowManager.CanSend(_vs, _va))
            {
                _logger.LogWarning("LAPB[{Link}]: Cannot send data - window full", _linkName);
                return false;
            }

            // Build I-frame
            // Control byte: bits 7-5 = N(R), bit 4 = P/F, bits 3-1 = N(S), bit 0 = 0
            byte control = (byte)((_vr << 5) | (pollFinal ? 0x10 : 0x00) | (_vs << 1));
            var frame = new byte[2 + data.Length];
            frame[0] = 0x01; // DTE address
            frame[1] = control;
            data.CopyTo(frame.AsSpan(2));

            // Track frame BEFORE incrementing V(S)
            _windowManager.TrackFrame(_vs, _vr, frame);

            // Transmit frame
            TransmitFrame(frame, LapbFrameType.Information);

            // Increment V(S) modulo 8
            _vs = (byte)((_vs + 1) % _modulo);

            // Start T1 timer if not already running
            StartT1Timer();

            // Reset T3 timer (activity detected)
            if (_isPrimary)
            {
                RestartT3Timer();
            }

            _statistics.IFramesSent++;

            _logger.LogDebug("LAPB[{Link}]: Sent I-frame N(S)={NS}, N(R)={NR}, Len={Len}",
                _linkName, (control >> 1) & 0x07, (control >> 5) & 0x07, data.Length);

            return true;
        }

        /// <summary>
        /// Send UI frame (for unsequenced data like Restart packets)
        /// </summary>
        public void SendUIFrame(byte[] data, int length)
        {
            var frame = new byte[2 + length];
            frame[0] = 0x01; // DTE address
            frame[1] = 0x03; // UI control byte
            Array.Copy(data, 0, frame, 2, length);

            TransmitFrame(frame, LapbFrameType.Unnumbered);
            _statistics.UFramesSent++;
        }

        /// <summary>
        /// Send raw frame through LAPB (bypasses sequencing)
        /// </summary>
        /// <param name="frame">Frame to send (Address + Control + Information)</param>
        /// <param name="length">Frame length</param>
        public void SendFrame(byte[] frame, int length)
        {
            if (frame == null || length < 2)
            {
                _logger.LogWarning("LAPB[{Link}]: Invalid frame - too short", _linkName);
                return;
            }

            byte control = frame[1];
            LapbFrameType frameType = LapbFrameUtils.DetectFrameType(control);

            var sendFrame = new byte[length];
            Array.Copy(frame, 0, sendFrame, 0, length);

            TransmitFrame(sendFrame, frameType);
        }

        /// <summary>
        /// Process received HDLC frame (after FCS validation)
        /// </summary>
        /// <param name="frameData">Frame data (Address + Control + Information)</param>
        /// <param name="length">Frame length</param>
        public void ReceiveFrame(byte[] frameData, int length)
        {
            if (frameData == null || length < 2)
            {
                _logger.LogWarning("LAPB[{Link}]: Invalid frame - too short", _linkName);
                return;
            }

            byte address = frameData[0];
            byte control = frameData[1];
            bool pollFinal = (control & 0x10) != 0;

            // Fire FrameReceived event for monitoring/testing
            LapbFrameType frameType = LapbFrameUtils.DetectFrameType(control);

            var eventArgs = new LapbFrameEventArgs
            {
                FrameData = frameData,
                Length = (ushort)length,
                Direction = FrameDirection.Receive,
                Timestamp = DateTime.UtcNow,
                FrameType = frameType,
                Address = address,
                Control = control,
                PF = pollFinal
            };

            if (frameType == LapbFrameType.Information)
            {
                eventArgs.NS = LapbFrameUtils.ExtractNS(control);
                eventArgs.NR = LapbFrameUtils.ExtractNR(control);
            }
            else if (frameType == LapbFrameType.Supervisory)
            {
                eventArgs.NR = LapbFrameUtils.ExtractNR(control);
            }

            if (length > 2)
            {
                eventArgs.Information = LapbFrameUtils.ExtractInformation(frameData, length);
            }

            FrameReceived?.Invoke(this, eventArgs);

            // Reset T3 timer on any received frame (activity detected)
            if (_isPrimary && _currentState == LapbState.DataTransfer)
            {
                RestartT3Timer();
            }

            // Determine frame type and process
            if ((control & 0x01) == 0)
            {
                // I-frame: bit 0 = 0
                ProcessIFrame(frameData, length, control, pollFinal);
                _statistics.IFramesReceived++;
            }
            else if ((control & 0x03) == 0x01)
            {
                // S-frame: bits 1-0 = 01
                ProcessSFrame(frameData, length, control, pollFinal);
                _statistics.SFramesReceived++;
            }
            else
            {
                // U-frame: bits 1-0 = 11
                ProcessUFrame(frameData, length, control, pollFinal);
                _statistics.UFramesReceived++;
            }
        }

        /// <summary>
        /// Get current statistics
        /// </summary>
        public LapbStatistics GetStatistics()
        {
            _statistics.VS = _vs;
            _statistics.VR = _vr;
            _statistics.VA = _va;
            _statistics.UnackedFrameCount = _windowManager.UnackedFrameCount;
            return _statistics;
        }

        /// <summary>
        /// Reset the LAPB instance
        /// </summary>
        public void Reset()
        {
            StopAllTimers();

            _currentState = LapbState.Disconnected;
            _vs = 0;
            _vr = 0;
            _va = 0;
            _remoteBusy = false;
            _localBusy = false;
            _rejOutstanding = false;
            _retryCount = 0;

            _windowManager.Clear();

            _logger.LogInformation("LAPB[{Link}]: Reset", _linkName);
        }

        #endregion

        #region Frame Processing

        private void ProcessIFrame(byte[] frameData, int length, byte control, bool pollFinal)
        {
            if (_currentState != LapbState.DataTransfer)
            {
                _logger.LogWarning("LAPB[{Link}]: I-frame received in state {State} - sending DM", _linkName, _currentState);
                SendUFrame(0x0F, pollFinal); // DM
                return;
            }

            byte ns = LapbFrameUtils.ExtractNS(control);
            byte nr = LapbFrameUtils.ExtractNR(control);

            _logger.LogDebug("LAPB[{Link}]: I-frame received N(S)={NS}, N(R)={NR}, P/F={PF}",
                _linkName, ns, nr, pollFinal);

            // Validate N(R)
            if (!_windowManager.IsValidNR(nr, _va, _vs))
            {
                _logger.LogError("LAPB[{Link}]: Invalid N(R)={NR} (V(A)={VA}, V(S)={VS})",
                    _linkName, nr, _va, _vs);
                SendFrameReject("N(R) out of range");
                return;
            }

            // Acknowledge frames
            int acked = _windowManager.AcknowledgeFrames(nr, _va);
            _va = nr;

            // Stop T1 if all frames acknowledged
            if (_windowManager.UnackedFrameCount == 0)
            {
                StopT1Timer();
            }

            // Check sequence number
            if (ns == _vr)
            {
                // In sequence - deliver data to upper layer
                _vr = (byte)((_vr + 1) % _modulo);
                _rejOutstanding = false;

                if (length > 2)
                {
                    var information = LapbFrameUtils.ExtractInformation(frameData, length);
                    DataReceived?.Invoke(this, information);
                }

                // Start T2 for delayed acknowledgment
                StartT2Timer();

                // Send RR response if P bit set
                if (pollFinal)
                {
                    StopT2Timer();
                    SendSFrame(0x01, true); // RR with F=1
                }
            }
            else
            {
                // Out of sequence
                if (!_rejOutstanding)
                {
                    _logger.LogWarning("LAPB[{Link}]: Out of sequence - N(S)={NS}, expected V(R)={VR}",
                        _linkName, ns, _vr);
                    SendSFrame(0x09, pollFinal); // REJ
                    _rejOutstanding = true;
                    _statistics.RejectionsSent++;
                }
                else if (pollFinal)
                {
                    SendSFrame(0x01, true); // RR with F=1
                }
            }
        }

        private void ProcessSFrame(byte[] frameData, int length, byte control, bool pollFinal)
        {
            if (_currentState != LapbState.DataTransfer)
            {
                _logger.LogWarning("LAPB[{Link}]: S-frame received in state {State}", _linkName, _currentState);
                return;
            }

            byte nr = LapbFrameUtils.ExtractNR(control);
            byte sType = (byte)(control & 0x0F);

            string typeName = sType switch
            {
                0x01 => "RR",
                0x05 => "RNR",
                0x09 => "REJ",
                _ => $"0x{sType:X2}"
            };

            _logger.LogDebug("LAPB[{Link}]: S-frame {Type} received N(R)={NR}, P/F={PF}",
                _linkName, typeName, nr, pollFinal);

            // Validate N(R)
            if (!_windowManager.IsValidNR(nr, _va, _vs))
            {
                _logger.LogError("LAPB[{Link}]: Invalid N(R)={NR} in S-frame", _linkName, nr);
                SendFrameReject("N(R) out of range");
                return;
            }

            // Acknowledge frames
            int acked = _windowManager.AcknowledgeFrames(nr, _va);
            _va = nr;

            // Stop T1 if all frames acknowledged
            if (_windowManager.UnackedFrameCount == 0)
            {
                StopT1Timer();
            }

            switch (sType)
            {
                case 0x01: // RR
                    _remoteBusy = false;
                    if (pollFinal)
                    {
                        SendSFrame(0x01, true); // RR with F=1
                    }
                    break;

                case 0x05: // RNR
                    _remoteBusy = true;
                    _logger.LogInformation("LAPB[{Link}]: Remote station busy", _linkName);
                    if (pollFinal)
                    {
                        SendSFrame(0x01, true); // RR with F=1
                    }
                    break;

                case 0x09: // REJ
                    _remoteBusy = false;
                    _statistics.RejectionsReceived++;
                    _logger.LogInformation("LAPB[{Link}]: REJ received - Go-Back-N from N(R)={NR}", _linkName, nr);
                    RetransmitFromNR(nr);
                    break;
            }
        }

        private void ProcessUFrame(byte[] frameData, int length, byte control, bool pollFinal)
        {
            byte frameType = (byte)(control & 0xEF); // Remove P/F bit

            switch (_currentState)
            {
                case LapbState.Disconnected:
                    ProcessUFrameDisconnected(frameData, length, frameType, pollFinal);
                    break;

                case LapbState.Connecting:
                    ProcessUFrameConnecting(frameData, length, frameType, pollFinal);
                    break;

                case LapbState.DataTransfer:
                    ProcessUFrameDataTransfer(frameData, length, frameType, pollFinal);
                    break;

                case LapbState.Disconnecting:
                    ProcessUFrameDisconnecting(frameData, length, frameType, pollFinal);
                    break;
            }
        }

        private void ProcessUFrameDisconnected(byte[] frameData, int length, byte frameType, bool pollFinal)
        {
            switch (frameType)
            {
                case 0x2F: // SABM
                case 0x6F: // SABME
                    _logger.LogInformation("LAPB[{Link}]: SABM received - establishing connection", _linkName);

                    // Accept connection
                    ChangeState(LapbState.DataTransfer);
                    InitializeDataTransfer();

                    // Send UA response
                    SendUFrame(0x63, true); // UA with F=1

                    if (_isPrimary)
                    {
                        StartT3Timer();
                    }
                    break;

                default:
                    // Send DM to indicate disconnected
                    if (pollFinal)
                    {
                        SendUFrame(0x0F, true); // DM with F=1
                    }
                    break;
            }
        }

        private void ProcessUFrameConnecting(byte[] frameData, int length, byte frameType, bool pollFinal)
        {
            switch (frameType)
            {
                case 0x63: // UA
                    if (pollFinal)
                    {
                        _logger.LogInformation("LAPB[{Link}]: UA received - connection established", _linkName);

                        StopT1Timer();
                        ChangeState(LapbState.DataTransfer);
                        InitializeDataTransfer();

                        if (_isPrimary)
                        {
                            StartT3Timer();
                        }
                    }
                    break;

                case 0x0F: // DM
                    _logger.LogWarning("LAPB[{Link}]: DM received - connection rejected", _linkName);

                    StopT1Timer();
                    ChangeState(LapbState.Disconnected);
                    break;

                case 0x2F: // SABM collision
                case 0x6F: // SABME collision
                    _logger.LogInformation("LAPB[{Link}]: SABM collision - accepting remote connection", _linkName);

                    StopT1Timer();
                    ChangeState(LapbState.DataTransfer);
                    InitializeDataTransfer();

                    // Send UA response
                    SendUFrame(0x63, true); // UA with F=1

                    if (_isPrimary)
                    {
                        StartT3Timer();
                    }
                    break;
            }
        }

        private void ProcessUFrameDataTransfer(byte[] frameData, int length, byte frameType, bool pollFinal)
        {
            switch (frameType)
            {
                case 0x43: // DISC
                    _logger.LogInformation("LAPB[{Link}]: DISC received - disconnecting", _linkName);

                    SendUFrame(0x63, true); // UA with F=1
                    ChangeState(LapbState.Disconnected);
                    Reset();
                    break;

                case 0x2F: // SABM (link reset)
                case 0x6F: // SABME
                    _logger.LogInformation("LAPB[{Link}]: SABM received in DataTransfer - link reset", _linkName);

                    InitializeDataTransfer();
                    SendUFrame(0x63, true); // UA with F=1
                    break;

                case 0x03: // UI - deliver to upper layer
                    if (length > 2)
                    {
                        var information = LapbFrameUtils.ExtractInformation(frameData, length);
                        DataReceived?.Invoke(this, information);
                    }
                    break;

                default:
                    _logger.LogWarning("LAPB[{Link}]: Unexpected U-frame 0x{Type:X2} in DataTransfer",
                        _linkName, frameType);
                    break;
            }
        }

        private void ProcessUFrameDisconnecting(byte[] frameData, int length, byte frameType, bool pollFinal)
        {
            switch (frameType)
            {
                case 0x63: // UA
                    if (pollFinal)
                    {
                        _logger.LogInformation("LAPB[{Link}]: UA received - disconnection complete", _linkName);

                        StopT1Timer();
                        ChangeState(LapbState.Disconnected);
                        Reset();
                    }
                    break;

                case 0x0F: // DM
                    _logger.LogInformation("LAPB[{Link}]: DM received - already disconnected", _linkName);

                    StopT1Timer();
                    ChangeState(LapbState.Disconnected);
                    Reset();
                    break;
            }
        }

        #endregion

        #region Frame Transmission

        private void SendUFrame(byte controlType, bool pollFinal)
        {
            var frame = new byte[2];
            frame[0] = 0x01; // DTE address
            frame[1] = (byte)(controlType | (pollFinal ? 0x10 : 0x00));

            TransmitFrame(frame, LapbFrameType.Unnumbered);
            _statistics.UFramesSent++;
        }

        private void SendSFrame(byte sType, bool final)
        {
            // S-frame: bits 7-5 = N(R), bit 4 = P/F, bits 3-2 = S-type, bits 1-0 = 01
            byte control = (byte)((_vr << 5) | (final ? 0x10 : 0x00) | sType);
            var frame = new byte[] { 0x01, control };

            TransmitFrame(frame, LapbFrameType.Supervisory);
            _statistics.SFramesSent++;

            _logger.LogDebug("LAPB[{Link}]: Sent S-frame 0x{Type:X2} N(R)={NR}, F={F}",
                _linkName, sType, _vr, final);
        }

        private void SendFrameReject(string reason)
        {
            SendUFrame(0x87, true); // FRMR with F=1
            _logger.LogError("LAPB[{Link}]: Sent FRMR - {Reason}", _linkName, reason);
            ErrorOccurred?.Invoke(this, $"Frame reject: {reason}");
        }

        private void TransmitFrame(byte[] frame, LapbFrameType frameType)
        {
            var eventArgs = new LapbFrameEventArgs
            {
                FrameData = frame,
                Length = (ushort)frame.Length,
                Direction = FrameDirection.Transmit,
                Timestamp = DateTime.UtcNow,
                FrameType = frameType,
                Address = frame[0],
                Control = frame[1]
            };

            if (frameType == LapbFrameType.Information)
            {
                eventArgs.NS = LapbFrameUtils.ExtractNS(frame[1]);
                eventArgs.NR = LapbFrameUtils.ExtractNR(frame[1]);
            }
            else if (frameType == LapbFrameType.Supervisory)
            {
                eventArgs.NR = LapbFrameUtils.ExtractNR(frame[1]);
            }

            eventArgs.PF = (frame[1] & 0x10) != 0;

            if (frame.Length > 2)
            {
                eventArgs.Information = LapbFrameUtils.ExtractInformation(frame, frame.Length);
            }

            FrameToTransmit?.Invoke(this, eventArgs);
        }

        private void RetransmitFromNR(byte nr)
        {
            var frames = _windowManager.GetFramesForRetransmission(nr, _vs, _vr);

            // Reset V(S) to nr for Go-Back-N
            _vs = nr;

            for (int i = 0; i < frames.Count; i++)
            {
                var frame = frames[i];
                TransmitFrame(frame, LapbFrameType.Information);
                _vs = (byte)((_vs + 1) % _modulo);
                _statistics.Retransmissions++;
            }

            if (frames.Count > 0)
            {
                StartT1Timer();
            }
        }

        #endregion

        #region State Management

        private void ChangeState(LapbState newState)
        {
            var oldState = _currentState;
            _currentState = newState;

            _logger.LogInformation("LAPB[{Link}]: State change {OldState} -> {NewState}",
                _linkName, oldState, newState);

            StateChanged?.Invoke(this, new LapbStateChangedEventArgs
            {
                LinkName = _linkName,
                OldState = oldState,
                NewState = newState,
                Timestamp = DateTime.UtcNow
            });
        }

        private void InitializeDataTransfer()
        {
            _vs = 0;
            _vr = 0;
            _va = 0;
            _remoteBusy = false;
            _localBusy = false;
            _rejOutstanding = false;
            _retryCount = 0;
            _windowManager.Clear();

            _logger.LogDebug("LAPB[{Link}]: DataTransfer state initialized", _linkName);
        }

        #endregion

        #region Timers

        private void StartT1Timer()
        {
            lock (_timerLock)
            {
                if (_t1TimeoutMs <= 0) return;

                _t1Timer?.Dispose();
                _t1Timer = new System.Threading.Timer(OnT1Elapsed, null, _t1TimeoutMs, System.Threading.Timeout.Infinite);

                _logger.LogDebug("LAPB[{Link}]: T1 timer started ({T1}ms)", _linkName, _t1TimeoutMs);
            }
        }

        private void StopT1Timer()
        {
            lock (_timerLock)
            {
                _t1Timer?.Dispose();
                _t1Timer = null;
            }
        }

        private void StartT2Timer()
        {
            lock (_timerLock)
            {
                if (_t2TimeoutMs <= 0) return;

                _t2Timer?.Dispose();
                _t2Timer = new System.Threading.Timer(OnT2Elapsed, null, _t2TimeoutMs, System.Threading.Timeout.Infinite);
            }
        }

        private void StopT2Timer()
        {
            lock (_timerLock)
            {
                _t2Timer?.Dispose();
                _t2Timer = null;
            }
        }

        private void StartT3Timer()
        {
            lock (_timerLock)
            {
                if (_t3TimeoutMs <= 0) return;

                _t3Timer?.Dispose();
                _t3Timer = new System.Threading.Timer(OnT3Elapsed, null, _t3TimeoutMs, System.Threading.Timeout.Infinite);
            }
        }

        private void StopT3Timer()
        {
            lock (_timerLock)
            {
                _t3Timer?.Dispose();
                _t3Timer = null;
            }
        }

        private void RestartT3Timer()
        {
            lock (_timerLock)
            {
                if (_t3TimeoutMs <= 0) return;

                _t3Timer?.Dispose();
                _t3Timer = new System.Threading.Timer(OnT3Elapsed, null, _t3TimeoutMs, System.Threading.Timeout.Infinite);
            }
        }

        private void StopAllTimers()
        {
            lock (_timerLock)
            {
                _t1Timer?.Dispose();
                _t1Timer = null;
                _t2Timer?.Dispose();
                _t2Timer = null;
                _t3Timer?.Dispose();
                _t3Timer = null;
            }
        }

        private void OnT1Elapsed(object? state)
        {
            if (_disposed) return;

            _statistics.T1Timeouts++;
            _logger.LogDebug("LAPB[{Link}]: T1 timeout in state {State}", _linkName, _currentState);

            switch (_currentState)
            {
                case LapbState.Connecting:
                    _retryCount++;
                    if (_retryCount >= _maxRetries)
                    {
                        _logger.LogError("LAPB[{Link}]: Connection failed - N2 retries exhausted", _linkName);
                        ChangeState(LapbState.Disconnected);
                        ErrorOccurred?.Invoke(this, "Connection timeout - N2 retries exhausted");
                    }
                    else
                    {
                        _logger.LogInformation("LAPB[{Link}]: Retransmitting SABM - retry {N}/{N2}",
                            _linkName, _retryCount, _maxRetries);
                        SendUFrame(0x2F, true); // SABM
                        StartT1Timer();
                    }
                    break;

                case LapbState.DataTransfer:
                    if (_windowManager.UnackedFrameCount > 0)
                    {
                        if (_windowManager.AnyRetryLimitExceeded(_maxRetries))
                        {
                            _logger.LogError("LAPB[{Link}]: Link failure - frame retry limit exceeded", _linkName);
                            ChangeState(LapbState.Disconnected);
                            Reset();
                            ErrorOccurred?.Invoke(this, "Link failure - frame retry limit exceeded");
                        }
                        else
                        {
                            // Retransmit from oldest unacked frame
                            RetransmitFromNR(_va);
                        }
                    }
                    break;

                case LapbState.Disconnecting:
                    _retryCount++;
                    if (_retryCount >= _maxRetries)
                    {
                        _logger.LogWarning("LAPB[{Link}]: Disconnect timeout - forcing disconnect", _linkName);
                        ChangeState(LapbState.Disconnected);
                        Reset();
                    }
                    else
                    {
                        _logger.LogInformation("LAPB[{Link}]: Retransmitting DISC - retry {N}/{N2}",
                            _linkName, _retryCount, _maxRetries);
                        SendUFrame(0x43, true); // DISC
                        StartT1Timer();
                    }
                    break;
            }
        }

        private void OnT2Elapsed(object? state)
        {
            if (_disposed) return;

            _statistics.T2Timeouts++;

            if (_currentState == LapbState.DataTransfer)
            {
                // Send explicit RR acknowledgment
                SendSFrame(0x01, false); // RR without P/F
                _logger.LogDebug("LAPB[{Link}]: T2 timeout - sent explicit RR", _linkName);
            }
        }

        private void OnT3Elapsed(object? state)
        {
            if (_disposed) return;

            if (_currentState == LapbState.DataTransfer)
            {
                // Poll remote station
                SendSFrame(0x01, true); // RR with P=1
                _logger.LogDebug("LAPB[{Link}]: T3 idle supervision - sent RR poll", _linkName);

                // Restart T3 for continuous supervision
                StartT3Timer();
            }
        }

        #endregion

        #region IDisposable

        public void Dispose()
        {
            if (_disposed) return;

            _disposed = true;
            StopAllTimers();

            _logger.LogInformation("LAPB[{Link}]: Instance disposed", _linkName);
        }

        #endregion
    }
}
