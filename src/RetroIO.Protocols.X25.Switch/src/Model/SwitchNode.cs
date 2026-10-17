//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System.Collections.Generic;
using RetroIO.Protocols.X25.Switch.Ports;

namespace RetroIO.Protocols.X25.Switch.Model
{
    /// <summary>
    /// The runtime state of a switch: its configuration, ports, active virtual-circuit stitches, accounts,
    /// and uptime. This is the shared model the switching engine mutates and the management surfaces read.
    /// </summary>
    public sealed class SwitchNode
    {
        private readonly object _stitchLock = new object();
        private readonly List<VcStitch> _stitches = new List<VcStitch>();
        private readonly List<ISwitchPort> _ports = new List<ISwitchPort>();
        private readonly List<SwitchAccount> _accounts = new List<SwitchAccount>();

        /// <summary>
        /// The switch's configuration.
        /// </summary>
        public SwitchConfig Config { get; }

        /// <summary>
        /// The ports attached to this switch, in port-number order.
        /// </summary>
        public IReadOnlyList<ISwitchPort> Ports => _ports;

        /// <summary>
        /// The switch's accounts.
        /// </summary>
        public IReadOnlyList<SwitchAccount> Accounts => _accounts;

        /// <summary>
        /// Timestamp (milliseconds) when the switch started, used to compute uptime.
        /// </summary>
        public long UptimeStartMs { get; set; }

        /// <summary>
        /// Creates a switch runtime from its configuration, building the runtime account objects.
        /// </summary>
        /// <param name="config">
        /// The switch configuration.
        /// </param>
        public SwitchNode(SwitchConfig config)
        {
            Config = config;
            for (int i = 0; i < config.Accounts.Count; i++)
            {
                _accounts.Add(new SwitchAccount(config.Accounts[i]));
            }
        }

        /// <summary>
        /// Attaches a port to the switch.
        /// </summary>
        /// <param name="port">
        /// The port to attach.
        /// </param>
        public void AddPort(ISwitchPort port)
        {
            _ports.Add(port);
        }

        /// <summary>
        /// Finds a port by its number.
        /// </summary>
        /// <param name="id">
        /// Port number.
        /// </param>
        /// <returns>
        /// The port, or null if there is none with that number.
        /// </returns>
        public ISwitchPort? GetPort(int id)
        {
            for (int i = 0; i < _ports.Count; i++)
            {
                if (_ports[i].Id == id)
                {
                    return _ports[i];
                }
            }

            return null;
        }

        /// <summary>
        /// Creates and registers a stitch between an ingress and egress circuit.
        /// </summary>
        /// <param name="inPort">
        /// Ingress port.
        /// </param>
        /// <param name="inLci">
        /// Ingress logical channel.
        /// </param>
        /// <param name="outPort">
        /// Egress port.
        /// </param>
        /// <param name="outLci">
        /// Egress logical channel.
        /// </param>
        /// <returns>
        /// The new active stitch.
        /// </returns>
        public VcStitch CreateStitch(ISwitchPort inPort, ushort inLci, ISwitchPort outPort, ushort outLci)
        {
            VcStitch stitch = new VcStitch(inPort, inLci, outPort, outLci) { Active = true };
            lock (_stitchLock)
            {
                _stitches.Add(stitch);
            }

            return stitch;
        }

        /// <summary>
        /// Removes a stitch from the switch.
        /// </summary>
        /// <param name="stitch">
        /// The stitch to remove.
        /// </param>
        public void DestroyStitch(VcStitch stitch)
        {
            lock (_stitchLock)
            {
                stitch.Active = false;
                _stitches.Remove(stitch);
            }
        }

        /// <summary>
        /// Finds the active stitch whose ingress side matches a port and logical channel.
        /// </summary>
        /// <param name="port">
        /// Ingress port.
        /// </param>
        /// <param name="lci">
        /// Ingress logical channel.
        /// </param>
        /// <returns>
        /// The matching stitch, or null.
        /// </returns>
        public VcStitch? FindIngress(ISwitchPort port, ushort lci)
        {
            lock (_stitchLock)
            {
                for (int i = 0; i < _stitches.Count; i++)
                {
                    VcStitch s = _stitches[i];
                    if (s.Active && ReferenceEquals(s.InPort, port) && s.InLci == lci)
                    {
                        return s;
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// Finds the active stitch whose egress side matches a port and logical channel.
        /// </summary>
        /// <param name="port">
        /// Egress port.
        /// </param>
        /// <param name="lci">
        /// Egress logical channel.
        /// </param>
        /// <returns>
        /// The matching stitch, or null.
        /// </returns>
        public VcStitch? FindEgress(ISwitchPort port, ushort lci)
        {
            lock (_stitchLock)
            {
                for (int i = 0; i < _stitches.Count; i++)
                {
                    VcStitch s = _stitches[i];
                    if (s.Active && ReferenceEquals(s.OutPort, port) && s.OutLci == lci)
                    {
                        return s;
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// Returns a snapshot of the currently active stitches.
        /// </summary>
        /// <returns>
        /// A copy of the active stitch list.
        /// </returns>
        public List<VcStitch> SnapshotStitches()
        {
            List<VcStitch> copy = new List<VcStitch>();
            lock (_stitchLock)
            {
                for (int i = 0; i < _stitches.Count; i++)
                {
                    if (_stitches[i].Active)
                    {
                        copy.Add(_stitches[i]);
                    }
                }
            }

            return copy;
        }

        /// <summary>
        /// The number of active stitches.
        /// </summary>
        public int ActiveStitchCount
        {
            get
            {
                int count = 0;
                lock (_stitchLock)
                {
                    for (int i = 0; i < _stitches.Count; i++)
                    {
                        if (_stitches[i].Active)
                        {
                            count++;
                        }
                    }
                }

                return count;
            }
        }
    }
}
