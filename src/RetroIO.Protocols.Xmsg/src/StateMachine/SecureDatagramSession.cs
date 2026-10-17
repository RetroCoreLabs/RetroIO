//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;

namespace RetroIO.Protocols.Xmsg
{
    /// <summary>
    /// Send-side state machine for XFSEC secure-datagram delivery.
    /// </summary>
    /// <remarks>
    /// <para><b>Model</b></para>
    /// A secure data message is sent and the sender awaits a delivery ACK (a
    /// subtype-<c>0x03</c> frame whose Flags 1 echoes the sent datagram sequence,
    /// XMSG-PROTOCOL.md section 6). If no matching ACK arrives within the timeout the
    /// message is retransmitted, up to a bounded number of resends; on exhaustion the
    /// message is returned to the sender (state <see cref="SecureDatagramState.Returned"/>)
    /// with a negative reason.
    /// <para><b>Time</b></para>
    /// The machine is driven entirely by injected tick values passed to
    /// <see cref="Tick(long)"/> and the event methods; it never reads the wall clock.
    /// One tick is intended to represent one XMSG Time Unit (XTU = 0.1 s) but the unit
    /// is the caller's choice.
    /// </remarks>
    public sealed class SecureDatagramSession
    {
        /// <summary>
        /// Default maximum number of retransmissions before the message is returned.
        /// </summary>
        public const int DefaultMaxResends = 3;

        private readonly long _timeoutTicks;
        private readonly int _maxResends;

        private SecureDatagramState _state;
        private ushort _datagramSequence;
        private long _lastSendTick;
        private int _resendCount;
        private XmsgError _returnReason;

        /// <summary>
        /// Raised when the message is retransmitted after a timeout or a NACK.
        /// </summary>
        /// <param name="datagramSequence">
        /// The datagram sequence of the retransmitted message.
        /// </param>
        /// <param name="attempt">
        /// The retransmission attempt number (1 for the first resend).
        /// </param>
        public delegate void DatagramResend(ushort datagramSequence, int attempt);

        /// <summary>
        /// Raised when a matching delivery ACK completes the transfer.
        /// </summary>
        /// <param name="datagramSequence">
        /// The datagram sequence that was delivered.
        /// </param>
        public delegate void DatagramDelivered(ushort datagramSequence);

        /// <summary>
        /// Raised when the message is returned to the sender after a failure.
        /// </summary>
        /// <param name="datagramSequence">
        /// The datagram sequence that failed to be delivered.
        /// </param>
        /// <param name="reason">
        /// The negative reason code carried back to the sender.
        /// </param>
        public delegate void DatagramReturned(ushort datagramSequence, XmsgError reason);

        /// <summary>
        /// Occurs when the message is retransmitted.
        /// </summary>
        public event DatagramResend? OnResend;

        /// <summary>
        /// Occurs when the message is delivered (acknowledged).
        /// </summary>
        public event DatagramDelivered? OnDelivered;

        /// <summary>
        /// Occurs when the message is returned to the sender.
        /// </summary>
        public event DatagramReturned? OnReturned;

        /// <summary>
        /// Initialises a new session.
        /// </summary>
        /// <param name="timeoutTicks">
        /// The number of ticks (XTUs) to wait for an ACK before retransmitting.
        /// </param>
        /// <param name="maxResends">
        /// The maximum number of retransmissions before the message is returned.
        /// </param>
        /// <exception cref="ArgumentOutOfRangeException">
        /// Thrown when <paramref name="timeoutTicks"/> is not positive or
        /// <paramref name="maxResends"/> is negative.
        /// </exception>
        public SecureDatagramSession(long timeoutTicks, int maxResends = DefaultMaxResends)
        {
            if (timeoutTicks <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(timeoutTicks), "Timeout must be positive.");
            }

            if (maxResends < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maxResends), "Max resends cannot be negative.");
            }

            _timeoutTicks = timeoutTicks;
            _maxResends = maxResends;
            _state = SecureDatagramState.Idle;
        }

        /// <summary>
        /// Gets the current state of the delivery machine.
        /// </summary>
        public SecureDatagramState State
        {
            get { return _state; }
        }

        /// <summary>
        /// Gets the datagram sequence number of the outstanding message.
        /// </summary>
        public ushort DatagramSequence
        {
            get { return _datagramSequence; }
        }

        /// <summary>
        /// Gets the number of retransmissions performed so far.
        /// </summary>
        public int ResendCount
        {
            get { return _resendCount; }
        }

        /// <summary>
        /// Gets the maximum number of retransmissions permitted.
        /// </summary>
        public int MaxResends
        {
            get { return _maxResends; }
        }

        /// <summary>
        /// Gets the negative reason code once the message has been returned.
        /// </summary>
        public XmsgError ReturnReason
        {
            get { return _returnReason; }
        }

        /// <summary>
        /// Gets the message type carried back to the sender on a return (always
        /// <see cref="XmsgMessageType.XMTRE"/>).
        /// </summary>
        public XmsgMessageType ReturnMessageType
        {
            get { return XmsgMessageType.XMTRE; }
        }

        /// <summary>
        /// Gets a value indicating whether the machine has reached a terminal state.
        /// </summary>
        public bool IsTerminal
        {
            get { return _state == SecureDatagramState.Delivered || _state == SecureDatagramState.Returned; }
        }

        /// <summary>
        /// Sends the secure message, moving from Idle to AwaitingAck.
        /// </summary>
        /// <param name="datagramSequence">
        /// The datagram sequence (Flags 1) stamped on the data frame.
        /// </param>
        /// <param name="currentTicks">
        /// The current tick count used to start the ACK timeout.
        /// </param>
        /// <exception cref="InvalidOperationException">
        /// Thrown when the machine is not in the Idle state.
        /// </exception>
        public void Send(ushort datagramSequence, long currentTicks)
        {
            if (_state != SecureDatagramState.Idle)
            {
                throw new InvalidOperationException("Send is only valid from the Idle state.");
            }

            _datagramSequence = datagramSequence;
            _lastSendTick = currentTicks;
            _resendCount = 0;
            _state = SecureDatagramState.AwaitingAck;
        }

        /// <summary>
        /// Processes a received delivery ACK.
        /// </summary>
        /// <param name="ackedFlags1">
        /// The Flags 1 value of the ACK frame, which should echo the sent datagram sequence.
        /// </param>
        /// <returns>
        /// <c>true</c> when the ACK matches the outstanding datagram and delivery completes;
        /// <c>false</c> when the ACK does not match (the message stays outstanding).
        /// </returns>
        public bool AckReceived(ushort ackedFlags1)
        {
            if (_state != SecureDatagramState.AwaitingAck)
            {
                return false;
            }

            if (ackedFlags1 != _datagramSequence)
            {
                // A non-matching ACK leaves the message outstanding (section 6.1).
                return false;
            }

            _state = SecureDatagramState.Delivered;
            OnDelivered?.Invoke(_datagramSequence);
            return true;
        }

        /// <summary>
        /// Processes a network remote-reject NACK (XEREJ), requesting retransmission.
        /// </summary>
        /// <param name="currentTicks">
        /// The current tick count used to restart the ACK timeout on resend.
        /// </param>
        /// <returns>
        /// <c>true</c> when a retransmission is issued; <c>false</c> when the resend
        /// budget is exhausted and the message is returned.
        /// </returns>
        /// <remarks>
        /// Models <see cref="XmsgError.XEREJ"/> (network remote reject). If resends
        /// remain the message is retransmitted; otherwise it is returned with reason
        /// <see cref="XmsgError.XEREJ"/>.
        /// </remarks>
        public bool Nack(long currentTicks)
        {
            if (_state != SecureDatagramState.AwaitingAck)
            {
                return false;
            }

            if (_resendCount < _maxResends)
            {
                Resend(currentTicks);
                return true;
            }

            EnterReturned(XmsgError.XEREJ);
            return false;
        }

        /// <summary>
        /// Advances the machine to the given tick, retransmitting or returning on timeout.
        /// </summary>
        /// <param name="currentTicks">
        /// The current tick count.
        /// </param>
        /// <returns>
        /// <c>true</c> when a timeout was handled (a resend was issued or the message
        /// was returned); <c>false</c> when nothing was due.
        /// </returns>
        public bool Tick(long currentTicks)
        {
            if (_state != SecureDatagramState.AwaitingAck)
            {
                return false;
            }

            if (currentTicks - _lastSendTick < _timeoutTicks)
            {
                return false; // ACK window has not elapsed yet
            }

            if (_resendCount < _maxResends)
            {
                Resend(currentTicks);
                return true;
            }

            // Resend budget exhausted after a timeout: return the message to the sender.
            EnterReturned(XmsgError.XENTO);
            return true;
        }

        private void Resend(long currentTicks)
        {
            _resendCount++;
            _lastSendTick = currentTicks;
            OnResend?.Invoke(_datagramSequence, _resendCount);
        }

        private void EnterReturned(XmsgError reason)
        {
            _returnReason = reason;
            _state = SecureDatagramState.Returned;
            OnReturned?.Invoke(_datagramSequence, reason);
        }
    }
}
