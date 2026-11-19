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
        // Replaced whole, never changed in place: the core reads it from its port threads while the console adds or
        // removes an account, so a reader that took the array once sees one consistent table.
        private volatile SwitchAccount[] _accounts = System.Array.Empty<SwitchAccount>();
        private readonly object _accountsLock = new object();

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
        /// <remarks>
        /// The table as it is now. A caller that looks at more than one entry takes it once into a local, because the
        /// console may replace it in between (<see cref="AddAccount"/>, <see cref="RemoveAccount"/>).
        /// </remarks>
        public IReadOnlyList<SwitchAccount> Accounts => _accounts;

        /// <summary>
        /// Adds an account while the switch runs, to the runtime table and to <see cref="Config"/> so a saved
        /// configuration keeps it. Refused when the name is taken.
        /// </summary>
        /// <param name="config">The account.</param>
        /// <returns>True when added; false when an account of that name exists.</returns>
        public bool AddAccount(AccountConfig config)
        {
            lock (_accountsLock)
            {
                SwitchAccount[] now = _accounts;
                for (int i = 0; i < now.Length; i++)
                {
                    if (string.Equals(now[i].Name, config.Name, System.StringComparison.Ordinal))
                    {
                        return false;
                    }
                }

                SwitchAccount[] next = new SwitchAccount[now.Length + 1];
                now.CopyTo(next, 0);
                next[now.Length] = new SwitchAccount(config);
                Config.Accounts.Add(config);
                _accounts = next;
                return true;
            }
        }

        /// <summary>
        /// Removes an account while the switch runs, from the runtime table and from <see cref="Config"/>.
        /// </summary>
        /// <remarks>
        /// A call already billed to it holds the account itself, so its traffic is still counted there, on an account no
        /// read-out shows any more; the caller refuses the removal while the account has calls up.
        /// </remarks>
        /// <param name="name">The account's name.</param>
        /// <returns>The removed account, or null when there is none of that name.</returns>
        public SwitchAccount? RemoveAccount(string name)
        {
            lock (_accountsLock)
            {
                SwitchAccount[] now = _accounts;
                int at = -1;
                for (int i = 0; i < now.Length; i++)
                {
                    if (string.Equals(now[i].Name, name, System.StringComparison.Ordinal))
                    {
                        at = i;
                    }
                }

                if (at < 0)
                {
                    return null;
                }

                SwitchAccount[] next = new SwitchAccount[now.Length - 1];
                System.Array.Copy(now, 0, next, 0, at);
                System.Array.Copy(now, at + 1, next, at, now.Length - at - 1);
                for (int i = 0; i < Config.Accounts.Count; i++)
                {
                    if (string.Equals(Config.Accounts[i].Name, name, System.StringComparison.Ordinal))
                    {
                        Config.Accounts.RemoveAt(i);
                        break;
                    }
                }

                _accounts = next;
                return now[at];
            }
        }

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
            SwitchAccount[] accounts = new SwitchAccount[config.Accounts.Count];
            for (int i = 0; i < accounts.Length; i++)
            {
                accounts[i] = new SwitchAccount(config.Accounts[i]);
            }

            _accounts = accounts;
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
            int segment = Config.SegmentSize;
            VcStitch stitch = new VcStitch(inPort, inLci, outPort, outLci)
            {
                Active = true,
                SegmentsForward = new SegmentCounter(segment),
                SegmentsReverse = new SegmentCounter(segment),
            };
            lock (_stitchLock)
            {
                _stitches.Add(stitch);
            }

            return stitch;
        }

        /// <summary>
        /// Removes a stitch from the switch.
        /// </summary>
        /// <remarks>
        /// The removal is atomic, and the return value says whether this call did it. That is what
        /// lets the clear handlers claim a stitch: when both legs are being cleared, the confirmation
        /// of the first clear raises a clear event on the other thread, and only the call that took
        /// the stitch out of service may go on to clear the legs and write the call record.
        /// </remarks>
        /// <param name="stitch">
        /// The stitch to remove.
        /// </param>
        /// <returns>
        /// <see langword="true"/> when this call removed the stitch; <see langword="false"/> when it
        /// had already been removed.
        /// </returns>
        public bool DestroyStitch(VcStitch stitch)
        {
            lock (_stitchLock)
            {
                bool wasActive = stitch.Active;
                stitch.Active = false;
                _stitches.Remove(stitch);
                return wasActive;
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

        private ulong _bytesForwardTotal;
        private ulong _bytesReverseTotal;

        /// <summary>
        /// Every data byte forwarded calling side to called side since the switch started, finished calls and permanent
        /// circuits included. A stitch's own byte count ends with its call (and a PVC's is zeroed at every record
        /// period), so these totals are kept here, counted where each packet is forwarded. x25emu's switch shows the
        /// same figure as the bytes of finished calls plus those of the calls still up (x25switch.c, bytes_fwd_done).
        /// </summary>
        public ulong BytesForwardTotal => Interlocked.Read(ref _bytesForwardTotal);

        /// <summary>
        /// Every data byte forwarded called side to calling side since the switch started; see
        /// <see cref="BytesForwardTotal"/>.
        /// </summary>
        public ulong BytesReverseTotal => Interlocked.Read(ref _bytesReverseTotal);

        /// <summary>
        /// Adds forwarded data bytes to the switch's totals.
        /// </summary>
        /// <param name="forward">
        /// True for calling side to called side.
        /// </param>
        /// <param name="length">
        /// The number of data bytes in the packet.
        /// </param>
        public void CountForwardedBytes(bool forward, int length)
        {
            if (forward)
            {
                Interlocked.Add(ref _bytesForwardTotal, (ulong)length);
            }
            else
            {
                Interlocked.Add(ref _bytesReverseTotal, (ulong)length);
            }
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
