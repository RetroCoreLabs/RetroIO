//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using RetroIO.Network.Router;

namespace RetroIO.Network.DialUp
{
    /// <summary>
    /// Gives the fixed address of a dial-up user.
    /// </summary>
    /// <param name="userName">
    /// The name the caller logged in with.
    /// </param>
    /// <returns>
    /// The user's address, or 0 for the next free one from the router's pool.
    /// </returns>
    public delegate uint DialUpAddressLookup(string userName);

    /// <summary>
    /// The packet side of one caller on a shared <see cref="IpRouter"/>: the caller gets a link and
    /// an address of the router's subnet, so callers can reach each other and, through whatever
    /// the host joins to the router's upstream side (a NAT), the internet.
    /// </summary>
    /// <remarks>
    /// <para>The router is not safe for several threads; every session on it, and the host's own
    /// calls to it, must run on one thread.</para>
    /// <para>The caller is told the router's address as gateway. Its SLIP driver sends every
    /// packet down the line anyway, since a point-to-point line has one way out.</para>
    /// </remarks>
    public sealed class RouterDialUpSide : IDialUpPacketSide
    {
        // ── Fields ──

        private readonly IpRouter _router;
        private readonly uint _fixedAddress;
        private readonly int _mtu;
        private readonly DialUpAddressLookup? _addressLookup;
        private RouterLink? _link;
        private DialUpPacketHandler? _toCaller;

        // ── Constructors ──

        /// <summary>
        /// Initializes the side.
        /// </summary>
        /// <param name="router">
        /// The shared router.
        /// </param>
        /// <param name="nameServer">
        /// The name server address told to the caller, or 0 for none (for example the NAT's name
        /// server, one of the router's upstream addresses).
        /// </param>
        /// <param name="fixedAddress">
        /// The caller's address, or 0 for the next free one from the router's pool.
        /// </param>
        /// <param name="mtu">
        /// The largest packet the link carries; the session's SLIP size.
        /// </param>
        /// <param name="addressLookup">
        /// Gives a user's own address once the login is over, or <c>null</c>; it wins over
        /// <paramref name="fixedAddress"/> when it gives one.
        /// </param>
        public RouterDialUpSide(IpRouter router, uint nameServer, uint fixedAddress = 0, int mtu = 1006, DialUpAddressLookup? addressLookup = null)
        {
            _addressLookup = addressLookup;
            _router = router ?? throw new ArgumentNullException(nameof(router));
            NameServer = nameServer;
            _fixedAddress = fixedAddress;
            _mtu = mtu;
        }

        // ── Properties ──

        /// <inheritdoc />
        public uint CallerAddress => _link?.Address ?? _fixedAddress;

        /// <inheritdoc />
        public uint Gateway => _router.Settings.RouterAddress;

        /// <inheritdoc />
        public uint NameServer { get; }

        /// <summary>
        /// Gets the caller's link on the router, or <c>null</c> before <see cref="Start"/> and
        /// after <see cref="Stop"/>.
        /// </summary>
        public RouterLink? Link => _link;

        /// <inheritdoc />
        public string RefusedReason { get; private set; } = string.Empty;

        // ── Public methods ──

        /// <inheritdoc />
        /// <remarks>
        /// Returns <c>false</c> when the router has no free address, or the fixed address is
        /// taken or not one the router may give; <see cref="RefusedReason"/> then says which.
        /// </remarks>
        public bool Start(string userName, DialUpPacketHandler toCaller)
        {
            if (_link != null)
            {
                throw new InvalidOperationException("The router side is already started.");
            }

            _toCaller = toCaller ?? throw new ArgumentNullException(nameof(toCaller));
            uint address = _fixedAddress;
            if (_addressLookup != null && userName.Length > 0)
            {
                uint own = _addressLookup(userName);
                if (own != 0)
                {
                    address = own;
                }
            }

            if (!_router.TryAddLink(OnRouterPacket, address, _mtu, out _link, out string error))
            {
                RefusedReason = error;
                return false;
            }

            return true;
        }

        /// <inheritdoc />
        public void SendFromCaller(ReadOnlySpan<byte> packet)
        {
            _link?.Receive(packet);
        }

        /// <inheritdoc />
        /// <remarks>
        /// Nothing to do: the router works as packets arrive, and the host polls whatever sits
        /// on its upstream side.
        /// </remarks>
        public void Poll()
        {
        }

        /// <inheritdoc />
        public void Stop()
        {
            if (_link == null)
            {
                return;
            }

            _router.RemoveLink(_link);
            _link = null;
        }

        // ── Private methods ──

        private void OnRouterPacket(ReadOnlySpan<byte> packet)
        {
            _toCaller?.Invoke(packet);
        }
    }
}
