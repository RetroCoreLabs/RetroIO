//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using Microsoft.Extensions.Logging;
using RetroIO.Protocols.X25.Modules;

namespace RetroIO.Protocols.X25.Runtime
{
    /// <summary>
    /// The runtime's telnet PAD: a server that lets an ordinary terminal program use an X.28 PAD of its
    /// own, one PAD per connection, switched by <see cref="RuntimeConfig.TelnetPad"/>.
    /// </summary>
    public partial class ProtocolRuntime
    {
        private TelnetPadServer? _telnetPad;

        /// <summary>
        /// Raised when the telnet PAD changes state or gains or loses a session. It runs on a server thread.
        /// </summary>
        public event EventHandler? TelnetPadStatusChanged;

        /// <summary>
        /// The state of the telnet PAD as one snapshot: off, listening (with the address, port and open
        /// sessions) or failed (with the reason, for example that the port is in use).
        /// </summary>
        public TelnetPadStatus TelnetPadStatus
        {
            get
            {
                TelnetPadServer? server = _telnetPad;
                return server != null
                    ? server.GetStatus()
                    : new TelnetPadStatus(TelnetPadState.Off, string.Empty, 0, 0, string.Empty);
            }
        }

        /// <summary>
        /// Starts, restarts or stops the telnet PAD to match <see cref="RuntimeConfig.TelnetPad"/>. The
        /// runtime does this once when it is built; call it again after changing that section. A listen
        /// that fails (port in use, bad address) does not throw: <see cref="TelnetPadStatus"/> reports it.
        /// </summary>
        public void ApplyTelnetPadConfig()
        {
            TelnetPadConfig settings = _config.TelnetPad ?? new TelnetPadConfig();
            if (!settings.Enabled)
            {
                _telnetPad?.Stop();
                return;
            }

            if (_telnetPad == null)
            {
                TelnetPadServer created = new TelnetPadServer(
                    _logger,
                    name => CreatePadClient(name),
                    pad =>
                    {
                        UnregisterModule(pad);
                        pad.Dispose();
                    });
                created.StatusChanged += (s, e) => TelnetPadStatusChanged?.Invoke(this, EventArgs.Empty);
                _telnetPad = created;
            }

            _telnetPad.Start(settings);
        }

        private void StopTelnetPad()
        {
            TelnetPadServer? server = _telnetPad;
            _telnetPad = null;
            server?.Dispose();
        }
    }
}
