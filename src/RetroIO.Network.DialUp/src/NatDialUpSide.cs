//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using RetroIO.Network.Nat;

namespace RetroIO.Network.DialUp
{
    /// <summary>
    /// The packet side of one caller with a NAT of its own: the caller is the NAT's only guest.
    /// </summary>
    /// <remarks>
    /// <para>For an emulator's built-in ISP with one modem. The NAT counts emulated time in polls
    /// (<see cref="NatRouter.PollsPerSecond"/> a second), so the host polls the session at that
    /// rate.</para>
    /// <para>For many callers at once use <see cref="RouterDialUpSide"/>, with one NAT behind the
    /// router.</para>
    /// </remarks>
    public sealed class NatDialUpSide : IDialUpPacketSide
    {
        // ── Fields ──

        private readonly NatEthernetBackend _nat;
        private readonly NatAddressPlan _plan;
        private byte[] _packet = Array.Empty<byte>();
        private DialUpPacketHandler? _toCaller;
        private bool _started;

        // ── Constructors ──

        /// <summary>
        /// Initializes the side with a NAT on the default plan (10.0.2.0/24, caller 10.0.2.15).
        /// </summary>
        /// <param name="hostLoopback">
        /// <c>true</c> when the gateway address reaches this computer's own 127.0.0.1.
        /// </param>
        public NatDialUpSide(bool hostLoopback = true)
            : this(new NatAddressPlan(), hostLoopback)
        {
        }

        /// <summary>
        /// Initializes the side with a NAT on the given plan.
        /// </summary>
        /// <param name="plan">
        /// The NAT's addresses; the caller gets <see cref="NatAddressPlan.FirstGuest"/>.
        /// </param>
        /// <param name="hostLoopback">
        /// <c>true</c> when the gateway address reaches this computer's own 127.0.0.1.
        /// </param>
        public NatDialUpSide(NatAddressPlan plan, bool hostLoopback)
        {
            _plan = plan ?? throw new ArgumentNullException(nameof(plan));
            _nat = new NatEthernetBackend(plan, hostLoopback, null, true);
        }

        // ── Properties ──

        /// <inheritdoc />
        public uint CallerAddress => _plan.FirstGuest;

        /// <inheritdoc />
        public uint Gateway => _plan.Gateway;

        /// <inheritdoc />
        public uint NameServer => _plan.NameServer;

        /// <summary>
        /// Gets the NAT, for its port forwards and counters.
        /// </summary>
        public NatEthernetBackend Nat => _nat;

        /// <inheritdoc />
        /// <remarks>
        /// Always empty: a NAT of its own always has room for its one caller.
        /// </remarks>
        public string RefusedReason => string.Empty;

        // ── Public methods ──

        /// <inheritdoc />
        public bool Start(string userName, DialUpPacketHandler toCaller)
        {
            if (_started)
            {
                throw new InvalidOperationException("The NAT side is already started.");
            }

            _toCaller = toCaller ?? throw new ArgumentNullException(nameof(toCaller));
            _nat.OnPacketReceived += OnNatPacket;
            _nat.Start();
            _started = true;
            return true;
        }

        /// <inheritdoc />
        public void SendFromCaller(ReadOnlySpan<byte> packet)
        {
            if (!_started)
            {
                return;
            }

            // SendPacket copies the bytes into the NAT's own queue, so one buffer is reused.
            if (_packet.Length < packet.Length)
            {
                _packet = new byte[packet.Length];
            }

            packet.CopyTo(_packet);
            _nat.SendPacket(_packet, 0, packet.Length);
        }

        /// <inheritdoc />
        public void Poll()
        {
            if (_started)
            {
                _nat.Poll();
            }
        }

        /// <inheritdoc />
        public void Stop()
        {
            if (!_started)
            {
                return;
            }

            _started = false;
            _nat.OnPacketReceived -= OnNatPacket;
            _nat.Stop();
        }

        // ── Private methods ──

        private void OnNatPacket(byte[] packet, int offset, int length)
        {
            _toCaller?.Invoke(new ReadOnlySpan<byte>(packet, offset, length));
        }
    }
}
