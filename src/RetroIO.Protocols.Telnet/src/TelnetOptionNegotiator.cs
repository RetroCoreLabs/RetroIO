//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;

namespace RetroIO.Protocols.Telnet
{
    /// <summary>
    /// Decides which options this side and the other side accept.
    /// </summary>
    public interface ITelnetOptionPolicy
    {
        /// <summary>
        /// The other side sent DO: may WE perform this option?
        /// </summary>
        /// <param name="option">
        /// The option code.
        /// </param>
        /// <returns>
        /// True to answer WILL, false to answer WONT.
        /// </returns>
        bool AcceptLocal(byte option);

        /// <summary>
        /// The other side sent WILL: may THEY perform this option?
        /// </summary>
        /// <param name="option">
        /// The option code.
        /// </param>
        /// <returns>
        /// True to answer DO, false to answer DONT.
        /// </returns>
        bool AcceptRemote(byte option);
    }

    /// <summary>
    /// Telnet option negotiation by the RFC 1143 "Q method", which never loops and never leaves
    /// the two sides disagreeing about an option.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Each option has two states: one for the other side ("him" in RFC 1143) and one for this side
    /// ("us"), each with a one-entry queue. The tables in <see cref="ReceiveWill"/>,
    /// <see cref="ReceiveWont"/>, <see cref="RequestRemoteEnable"/> and
    /// <see cref="RequestRemoteDisable"/> are RFC 1143 section 7, line for line. RFC 1143 says "We
    /// handle the option on our side by the same procedures, with DO-WILL, DONT-WONT, him-us,
    /// himq-usq swapped", which is what <see cref="ReceiveDo"/>, <see cref="ReceiveDont"/>,
    /// <see cref="RequestLocalEnable"/> and <see cref="RequestLocalDisable"/> do.
    /// </para>
    /// <para>
    /// The negotiator sends nothing itself: every reply goes to the send callback as
    /// (verb, option), and the caller writes it with <see cref="TelnetEncoder.WriteNegotiation"/>.
    /// </para>
    /// </remarks>
    public sealed class TelnetOptionNegotiator
    {
        private readonly ITelnetOptionPolicy _policy;
        private readonly Action<byte, byte> _send;

        private readonly OptionState[] _him = new OptionState[256];
        private readonly QueueState[] _himQueue = new QueueState[256];
        private readonly OptionState[] _us = new OptionState[256];
        private readonly QueueState[] _usQueue = new QueueState[256];

        /// <summary>
        /// Creates a negotiator. Every option starts as NO on both sides (RFC 1143 section 7).
        /// </summary>
        /// <param name="policy">
        /// Decides what to accept.
        /// </param>
        /// <param name="send">
        /// Called with (verb, option) for every negotiation byte triple to send.
        /// </param>
        public TelnetOptionNegotiator(ITelnetOptionPolicy policy, Action<byte, byte> send)
        {
            ArgumentNullException.ThrowIfNull(policy);
            ArgumentNullException.ThrowIfNull(send);
            _policy = policy;
            _send = send;
        }

        /// <summary>
        /// Raised when an option becomes fully on or fully off.
        /// </summary>
        /// <remarks>
        /// Arguments: the option code, true for this side (false for the other side), and the
        /// new state (true = enabled).
        /// </remarks>
        public event Action<byte, bool, bool>? OptionChanged;

        /// <summary>
        /// The state of one side of one option (RFC 1143 section 7).
        /// </summary>
        public enum OptionState : byte
        {
            /// <summary>
            /// Disabled.
            /// </summary>
            No,

            /// <summary>
            /// Enabled.
            /// </summary>
            Yes,

            /// <summary>
            /// We asked for it to be disabled and wait for the answer.
            /// </summary>
            WantNo,

            /// <summary>
            /// We asked for it to be enabled and wait for the answer.
            /// </summary>
            WantYes,
        }

        private enum QueueState : byte
        {
            Empty,
            Opposite,
        }

        /// <summary>
        /// True when the option is fully on for this side (we perform it).
        /// </summary>
        /// <param name="option">
        /// The option code.
        /// </param>
        /// <returns>
        /// True when enabled.
        /// </returns>
        public bool IsLocalEnabled(byte option) => _us[option] == OptionState.Yes;

        /// <summary>
        /// True when the option is fully on for the other side (they perform it).
        /// </summary>
        /// <param name="option">
        /// The option code.
        /// </param>
        /// <returns>
        /// True when enabled.
        /// </returns>
        public bool IsRemoteEnabled(byte option) => _him[option] == OptionState.Yes;

        /// <summary>
        /// Dispatches a received negotiation to the matching Receive method.
        /// </summary>
        /// <param name="verb">
        /// WILL, WONT, DO or DONT.
        /// </param>
        /// <param name="option">
        /// The option code.
        /// </param>
        public void Receive(byte verb, byte option)
        {
            switch (verb)
            {
                case TelnetCommand.Will: ReceiveWill(option); break;
                case TelnetCommand.Wont: ReceiveWont(option); break;
                case TelnetCommand.Do: ReceiveDo(option); break;
                case TelnetCommand.Dont: ReceiveDont(option); break;
                default: throw new ArgumentOutOfRangeException(nameof(verb), verb, "Must be WILL, WONT, DO or DONT.");
            }
        }

        /// <summary>
        /// Received WILL (RFC 1143 section 7, "Upon receipt of WILL").
        /// </summary>
        /// <param name="option">
        /// The option code.
        /// </param>
        public void ReceiveWill(byte option)
        {
            switch (_him[option])
            {
                case OptionState.No:
                    if (_policy.AcceptRemote(option))
                    {
                        SetHim(option, OptionState.Yes);
                        _send(TelnetCommand.Do, option);
                    }
                    else
                    {
                        _send(TelnetCommand.Dont, option);
                    }

                    break;

                case OptionState.Yes:
                    // Ignore.
                    break;

                case OptionState.WantNo:
                    if (_himQueue[option] == QueueState.Empty)
                    {
                        // "Error: DONT answered by WILL. him=NO."
                        SetHim(option, OptionState.No);
                    }
                    else
                    {
                        // "Error: DONT answered by WILL. him=YES, himq=EMPTY."
                        SetHim(option, OptionState.Yes);
                        _himQueue[option] = QueueState.Empty;
                    }

                    break;

                case OptionState.WantYes:
                    if (_himQueue[option] == QueueState.Empty)
                    {
                        SetHim(option, OptionState.Yes);
                    }
                    else
                    {
                        SetHim(option, OptionState.WantNo);
                        _himQueue[option] = QueueState.Empty;
                        _send(TelnetCommand.Dont, option);
                    }

                    break;
            }
        }

        /// <summary>
        /// Received WONT (RFC 1143 section 7, "Upon receipt of WONT").
        /// </summary>
        /// <param name="option">
        /// The option code.
        /// </param>
        public void ReceiveWont(byte option)
        {
            switch (_him[option])
            {
                case OptionState.No:
                    // Ignore.
                    break;

                case OptionState.Yes:
                    SetHim(option, OptionState.No);
                    _send(TelnetCommand.Dont, option);
                    break;

                case OptionState.WantNo:
                    if (_himQueue[option] == QueueState.Empty)
                    {
                        SetHim(option, OptionState.No);
                    }
                    else
                    {
                        // RFC 1143 writes "himq=NONE" here; NONE is its EMPTY.
                        SetHim(option, OptionState.WantYes);
                        _himQueue[option] = QueueState.Empty;
                        _send(TelnetCommand.Do, option);
                    }

                    break;

                case OptionState.WantYes:
                    SetHim(option, OptionState.No);
                    _himQueue[option] = QueueState.Empty;
                    break;
            }
        }

        /// <summary>
        /// Received DO: the WILL table with him and us swapped (RFC 1143 section 7, last paragraph).
        /// </summary>
        /// <param name="option">
        /// The option code.
        /// </param>
        public void ReceiveDo(byte option)
        {
            switch (_us[option])
            {
                case OptionState.No:
                    if (_policy.AcceptLocal(option))
                    {
                        SetUs(option, OptionState.Yes);
                        _send(TelnetCommand.Will, option);
                    }
                    else
                    {
                        _send(TelnetCommand.Wont, option);
                    }

                    break;

                case OptionState.Yes:
                    break;

                case OptionState.WantNo:
                    if (_usQueue[option] == QueueState.Empty)
                    {
                        SetUs(option, OptionState.No);
                    }
                    else
                    {
                        SetUs(option, OptionState.Yes);
                        _usQueue[option] = QueueState.Empty;
                    }

                    break;

                case OptionState.WantYes:
                    if (_usQueue[option] == QueueState.Empty)
                    {
                        SetUs(option, OptionState.Yes);
                    }
                    else
                    {
                        SetUs(option, OptionState.WantNo);
                        _usQueue[option] = QueueState.Empty;
                        _send(TelnetCommand.Wont, option);
                    }

                    break;
            }
        }

        /// <summary>
        /// Received DONT: the WONT table with him and us swapped.
        /// </summary>
        /// <param name="option">
        /// The option code.
        /// </param>
        public void ReceiveDont(byte option)
        {
            switch (_us[option])
            {
                case OptionState.No:
                    break;

                case OptionState.Yes:
                    SetUs(option, OptionState.No);
                    _send(TelnetCommand.Wont, option);
                    break;

                case OptionState.WantNo:
                    if (_usQueue[option] == QueueState.Empty)
                    {
                        SetUs(option, OptionState.No);
                    }
                    else
                    {
                        SetUs(option, OptionState.WantYes);
                        _usQueue[option] = QueueState.Empty;
                        _send(TelnetCommand.Will, option);
                    }

                    break;

                case OptionState.WantYes:
                    SetUs(option, OptionState.No);
                    _usQueue[option] = QueueState.Empty;
                    break;
            }
        }

        /// <summary>
        /// Ask the other side to enable an option (send DO). RFC 1143 "If we decide to ask him to
        /// enable". This negotiator queues requests.
        /// </summary>
        /// <param name="option">
        /// The option code.
        /// </param>
        /// <returns>
        /// False where RFC 1143 says "Error" (already enabled, or already asked).
        /// </returns>
        public bool RequestRemoteEnable(byte option)
        {
            switch (_him[option])
            {
                case OptionState.No:
                    SetHim(option, OptionState.WantYes);
                    _send(TelnetCommand.Do, option);
                    return true;

                case OptionState.WantNo:
                    if (_himQueue[option] == QueueState.Empty)
                    {
                        _himQueue[option] = QueueState.Opposite;
                        return true;
                    }

                    return false;

                case OptionState.WantYes:
                    if (_himQueue[option] == QueueState.Opposite)
                    {
                        _himQueue[option] = QueueState.Empty;
                        return true;
                    }

                    return false;

                default:
                    return false;
            }
        }

        /// <summary>
        /// Ask the other side to disable an option (send DONT). RFC 1143 "If we decide to ask him to
        /// disable".
        /// </summary>
        /// <param name="option">
        /// The option code.
        /// </param>
        /// <returns>
        /// False where RFC 1143 says "Error".
        /// </returns>
        public bool RequestRemoteDisable(byte option)
        {
            switch (_him[option])
            {
                case OptionState.Yes:
                    SetHim(option, OptionState.WantNo);
                    _send(TelnetCommand.Dont, option);
                    return true;

                case OptionState.WantNo:
                    if (_himQueue[option] == QueueState.Opposite)
                    {
                        _himQueue[option] = QueueState.Empty;
                        return true;
                    }

                    return false;

                case OptionState.WantYes:
                    if (_himQueue[option] == QueueState.Empty)
                    {
                        _himQueue[option] = QueueState.Opposite;
                        return true;
                    }

                    return false;

                default:
                    return false;
            }
        }

        /// <summary>
        /// Offer to enable an option on this side (send WILL): the remote-enable table with him and
        /// us swapped.
        /// </summary>
        /// <param name="option">
        /// The option code.
        /// </param>
        /// <returns>
        /// False where RFC 1143 says "Error".
        /// </returns>
        public bool RequestLocalEnable(byte option)
        {
            switch (_us[option])
            {
                case OptionState.No:
                    SetUs(option, OptionState.WantYes);
                    _send(TelnetCommand.Will, option);
                    return true;

                case OptionState.WantNo:
                    if (_usQueue[option] == QueueState.Empty)
                    {
                        _usQueue[option] = QueueState.Opposite;
                        return true;
                    }

                    return false;

                case OptionState.WantYes:
                    if (_usQueue[option] == QueueState.Opposite)
                    {
                        _usQueue[option] = QueueState.Empty;
                        return true;
                    }

                    return false;

                default:
                    return false;
            }
        }

        /// <summary>
        /// Stop performing an option on this side (send WONT).
        /// </summary>
        /// <param name="option">
        /// The option code.
        /// </param>
        /// <returns>
        /// False where RFC 1143 says "Error".
        /// </returns>
        public bool RequestLocalDisable(byte option)
        {
            switch (_us[option])
            {
                case OptionState.Yes:
                    SetUs(option, OptionState.WantNo);
                    _send(TelnetCommand.Wont, option);
                    return true;

                case OptionState.WantNo:
                    if (_usQueue[option] == QueueState.Opposite)
                    {
                        _usQueue[option] = QueueState.Empty;
                        return true;
                    }

                    return false;

                case OptionState.WantYes:
                    if (_usQueue[option] == QueueState.Empty)
                    {
                        _usQueue[option] = QueueState.Opposite;
                        return true;
                    }

                    return false;

                default:
                    return false;
            }
        }

        private void SetHim(byte option, OptionState state)
        {
            bool wasOn = _him[option] == OptionState.Yes;
            _him[option] = state;
            bool isOn = state == OptionState.Yes;
            if (wasOn != isOn)
            {
                OptionChanged?.Invoke(option, false, isOn);
            }
        }

        private void SetUs(byte option, OptionState state)
        {
            bool wasOn = _us[option] == OptionState.Yes;
            _us[option] = state;
            bool isOn = state == OptionState.Yes;
            if (wasOn != isOn)
            {
                OptionChanged?.Invoke(option, true, isOn);
            }
        }
    }
}
