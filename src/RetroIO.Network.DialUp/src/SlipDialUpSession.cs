//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Text;
using RetroIO.Protocols.Slip;

namespace RetroIO.Network.DialUp
{
    /// <summary>
    /// Says that a dial-up session hung up.
    /// </summary>
    /// <param name="reason">
    /// Why, in words for a log.
    /// </param>
    public delegate void DialUpEndedHandler(string reason);

    /// <summary>
    /// One answered dial-up call: an optional text login, then SLIP (RFC 1055) between the
    /// caller's bytes and the IP packets of an <see cref="IDialUpPacketSide"/>.
    /// </summary>
    /// <remarks>
    /// <para>The host subscribes to <see cref="OnEnded"/>, calls <see cref="Answer"/>, then feeds it the caller's bytes with <see cref="TryTakeFromCaller"/>, takes bytes
    /// for the caller with <see cref="TryGiveToCaller"/>, and calls <see cref="Poll"/> at the rate
    /// its packet side needs. All on one thread; nothing here runs on its own.</para>
    /// <para>The login: the name is echoed and the password is not; a line ends at CR or LF, and
    /// backspace or delete takes back one character. A wrong login gets <c>Login incorrect</c> and
    /// a new prompt, and after <see cref="DialUpSettings.MaxLoginTries"/> of them the session hangs
    /// up. SLIP has no login of its own; PPP's (PAP and CHAP) comes with the PPP support.</para>
    /// </remarks>
    public sealed class SlipDialUpSession
    {
        // ── Fields ──

        private const int RingSize = 64 * 1024;
        private const int MaxLoginLine = 64;

        private readonly DialUpSettings _settings;
        private readonly IDialUpPacketSide _side;
        private readonly SlipLink _slip;
        private readonly byte[] _toCaller = new byte[RingSize];
        private readonly StringBuilder _line = new StringBuilder();
        private string _name = string.Empty;
        private int _head;
        private int _count;
        private int _failedLogins;
        private Stage _stage;
        private long _packetsDropped;

        // ── Events ──

        /// <summary>
        /// Raised once when the session hangs up: a failed login, no room on the packet side, or
        /// <see cref="CallerHungUp"/>.
        /// </summary>
        public event DialUpEndedHandler? OnEnded;

        // ── Constructors ──

        /// <summary>
        /// Initializes a session; <see cref="Answer"/> greets the caller.
        /// </summary>
        /// <param name="settings">
        /// The greeting, the login check and the SLIP size.
        /// </param>
        /// <param name="side">
        /// Where the caller's packets go; started when SLIP starts, stopped at hang-up.
        /// </param>
        public SlipDialUpSession(DialUpSettings settings, IDialUpPacketSide side)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _side = side ?? throw new ArgumentNullException(nameof(side));
            _slip = new SlipLink(settings.MaxPacket);
            _slip.OnPacket += OnSlipPacket;
            _slip.OnLineOutput += OnSlipLineOutput;
            _stage = Stage.NotAnswered;
        }

        // ── Properties ──

        /// <summary>
        /// Gets a value indicating whether the session has hung up.
        /// </summary>
        public bool Ended => _stage == Stage.Ended;

        /// <summary>
        /// Gets a value indicating whether the login is over and SLIP carries the line.
        /// </summary>
        public bool InSlipMode => _stage == Stage.Slip;

        /// <summary>
        /// Gets the name the caller logged in with, or empty.
        /// </summary>
        public string UserName => _stage == Stage.Slip ? _name : string.Empty;

        /// <summary>
        /// Gets the packet side.
        /// </summary>
        public IDialUpPacketSide Side => _side;

        /// <summary>
        /// Gets the packets SLIP dropped as bad (a bad escape, or too long).
        /// </summary>
        public long SlipErrors => _slip.ErrorCount;

        /// <summary>
        /// Gets the packets for the caller that were dropped: too long for the line, or no room left
        /// in the queue toward the caller.
        /// </summary>
        public long PacketsDropped => _packetsDropped;

        // ── Public methods ──

        /// <summary>
        /// Greets the caller: the login prompt, or with no login the SLIP banner at once. Call it
        /// after subscribing to <see cref="OnEnded"/>, since a full packet side hangs up here.
        /// </summary>
        /// <exception cref="InvalidOperationException">
        /// Thrown when the call was already answered.
        /// </exception>
        public void Answer()
        {
            if (_stage != Stage.NotAnswered)
            {
                throw new InvalidOperationException("The call was already answered.");
            }

            if (_settings.LoginPrompt)
            {
                _stage = Stage.Name;
                Say("\r\n" + _settings.Greeting + "\r\n\r\nlogin: ");
            }
            else
            {
                StartSlip();
            }
        }

        /// <summary>
        /// Takes one byte the caller sent.
        /// </summary>
        /// <param name="value">
        /// The byte.
        /// </param>
        /// <returns>
        /// <c>false</c> before <see cref="Answer"/> and once the session has hung up.
        /// </returns>
        public bool TryTakeFromCaller(byte value)
        {
            if (_stage == Stage.Ended || _stage == Stage.NotAnswered)
            {
                return false;
            }

            if (_stage == Stage.Slip)
            {
                ReadOnlySpan<byte> one = stackalloc byte[1] { value };
                _slip.ReceiveFromLine(one);
                return true;
            }

            TakeLoginByte(value);
            return true;
        }

        /// <summary>
        /// Gives the next byte for the caller. Bytes said before a hang-up still come out.
        /// </summary>
        /// <param name="value">
        /// The byte, or 0.
        /// </param>
        /// <returns>
        /// <c>true</c> when there was a byte.
        /// </returns>
        public bool TryGiveToCaller(out byte value)
        {
            if (_count == 0)
            {
                value = 0;
                return false;
            }

            value = _toCaller[_head];
            _head = (_head + 1) % RingSize;
            _count--;
            return true;
        }

        /// <summary>
        /// Ends the call from the caller's side (carrier lost, modem hung up).
        /// </summary>
        public void CallerHungUp()
        {
            End("the caller hung up");
        }

        /// <summary>
        /// Moves the packet side along by one poll.
        /// </summary>
        public void Poll()
        {
            if (_stage == Stage.Slip)
            {
                _side.Poll();
            }
        }

        // ── Private methods ──

        private void TakeLoginByte(byte value)
        {
            if (value == (byte)'\r' || value == (byte)'\n')
            {
                EndLoginLine();
                return;
            }

            if (value == 8 || value == 127)
            {
                if (_line.Length > 0)
                {
                    _line.Length--;
                    if (_stage == Stage.Name)
                    {
                        Say("\b \b");
                    }
                }

                return;
            }

            if (value < 32 || _line.Length >= MaxLoginLine)
            {
                return;
            }

            _line.Append((char)value);
            if (_stage == Stage.Name)
            {
                Put(value);
            }
        }

        private void EndLoginLine()
        {
            if (_stage == Stage.Name)
            {
                if (_line.Length == 0)
                {
                    Say("\r\nlogin: ");
                    return;
                }

                _name = _line.ToString();
                _line.Clear();
                _stage = Stage.Password;
                Say("\r\nPassword: ");
                return;
            }

            string password = _line.ToString();
            _line.Clear();
            DialUpLoginCheck? check = _settings.LoginCheck;
            if (check == null || check(_name, password))
            {
                StartSlip();
                return;
            }

            _failedLogins++;
            if (_failedLogins >= _settings.MaxLoginTries)
            {
                Say("\r\nLogin incorrect\r\n");
                End("login refused for " + _name + " after " + _failedLogins + " tries");
                return;
            }

            _name = string.Empty;
            _stage = Stage.Name;
            Say("\r\nLogin incorrect\r\nlogin: ");
        }

        private void StartSlip()
        {
            if (!_side.Start(_name, OnSidePacket))
            {
                Say("\r\nNo line free, please call again later.\r\n");
                End("the packet side has no room: " + _side.RefusedReason);
                return;
            }

            StringBuilder banner = new StringBuilder();
            banner.Append("\r\nSLIP mode. Your address is ").Append(Dotted(_side.CallerAddress));
            banner.Append(", the gateway is ").Append(Dotted(_side.Gateway));
            if (_side.NameServer != 0)
            {
                banner.Append(", the name server is ").Append(Dotted(_side.NameServer));
            }

            banner.Append(", MTU ").Append(_slip.MaxPacket).Append(".\r\n");
            Say(banner.ToString());
            _stage = Stage.Slip;
        }

        private void End(string reason)
        {
            if (_stage == Stage.Ended)
            {
                return;
            }

            bool started = _stage == Stage.Slip;
            _stage = Stage.Ended;
            if (started)
            {
                _side.Stop();
            }

            OnEnded?.Invoke(reason);
        }

        private void OnSlipPacket(ReadOnlySpan<byte> packet)
        {
            _side.SendFromCaller(packet);
        }

        private void OnSlipLineOutput(ReadOnlySpan<byte> bytes)
        {
            for (int i = 0; i < bytes.Length; i++)
            {
                Put(bytes[i]);
            }
        }

        private void OnSidePacket(ReadOnlySpan<byte> packet)
        {
            if (_stage != Stage.Slip || packet.Length > _slip.MaxPacket)
            {
                _packetsDropped++;
                return;
            }

            // A whole SLIP frame can be twice the packet plus two END bytes; drop it whole rather
            // than send part of a frame.
            if (RingSize - _count < (2 * packet.Length) + 2)
            {
                _packetsDropped++;
                return;
            }

            _slip.SendPacket(packet);
        }

        private void Say(string text)
        {
            for (int i = 0; i < text.Length; i++)
            {
                Put((byte)text[i]);
            }
        }

        private void Put(byte value)
        {
            if (_count == RingSize)
            {
                return;
            }

            _toCaller[(_head + _count) % RingSize] = value;
            _count++;
        }

        private static string Dotted(uint address)
        {
            return (address >> 24) + "." + ((address >> 16) & 0xFF) + "." + ((address >> 8) & 0xFF) + "." + (address & 0xFF);
        }

        // ── Nested types ──

        private enum Stage
        {
            NotAnswered,
            Name,
            Password,
            Slip,
            Ended,
        }
    }
}
