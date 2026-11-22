//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//


namespace RetroIO.Network.Ethernet
{
    /// <summary>
    /// The default <see cref="IEthernetBackend"/>: a NIC plugged into this carries no
    /// traffic off-box. Transmits are discarded and no frame is ever received, so a
    /// machine with an emulated NIC runs cleanly with no host networking attached.
    /// </summary>
    /// <remarks>
    /// An optional <see cref="Loopback"/> mode echoes each transmitted frame straight
    /// back through <see cref="IEthernetBackend.OnFrameReceived"/> — useful for tests
    /// and for exercising a driver's receive path without a real wire. Loopback honours
    /// the running state (nothing echoes while stopped).
    /// </remarks>
    public sealed class NullEthernetBackend : IEthernetBackend
    {
        // ── Fields ──

        /// <summary>
        /// When true, transmitted frames are echoed back as received frames.
        /// </summary>
        private readonly bool _loopback;

        /// <summary>
        /// Running state, toggled by Start/Stop.
        /// </summary>
        private bool _running;

        // ── Events ──

        /// <inheritdoc/>
        public event EthernetFrameHandler? OnFrameReceived;

        // ── Constructors ──

        /// <summary>
        /// Initializes a new <see cref="NullEthernetBackend"/>.
        /// </summary>
        /// <param name="loopback">
        /// <c>true</c> to echo transmitted frames back as received frames; <c>false</c>
        /// (the default) to discard transmits and never receive.
        /// </param>
        public NullEthernetBackend(bool loopback = false)
        {
            _loopback = loopback;
        }

        // ── Properties ──

        /// <inheritdoc/>
        public bool IsRunning => _running;

        /// <summary>
        /// Gets a value indicating whether this backend echoes transmits back as receives.
        /// </summary>
        /// <value>
        /// <c>true</c> in loopback mode; otherwise <c>false</c>.
        /// </value>
        public bool Loopback => _loopback;

        // ── Public methods ──

        /// <inheritdoc/>
        public void Poll()
        {
            // Nothing to drain — loopback delivers synchronously inside Send.
        }

        /// <inheritdoc/>
        public void Start()
        {
            _running = true;
        }

        /// <inheritdoc/>
        public void Stop()
        {
            _running = false;
        }

        /// <inheritdoc/>
        public void Send(byte[] frame, int offset, int length)
        {
            if (!_running || frame == null || length <= 0)
            {
                return;
            }

            // Default: the frame goes nowhere. Loopback: hand it straight back as a
            // received frame so the guest's RX path sees its own transmit.
            if (_loopback)
            {
                OnFrameReceived?.Invoke(frame, offset, length);
            }
        }
    }
}
