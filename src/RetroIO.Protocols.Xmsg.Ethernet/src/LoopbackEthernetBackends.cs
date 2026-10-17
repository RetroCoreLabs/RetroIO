//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;

namespace RetroIO.Protocols.Xmsg.Ethernet
{
    /// <summary>
    /// A backend that carries nothing: sends are discarded and nothing is ever received.
    /// </summary>
    /// <remarks>
    /// Used where a node is configured without a segment, so the layers above can run unchanged
    /// instead of needing a null check on every send.
    /// </remarks>
    public sealed class NullEthernetBackend : IEthernetBackend
    {
        /// <inheritdoc/>
        public event EthernetPacketReceived? OnPacketReceived;

        /// <inheritdoc/>
        public bool IsActive { get; private set; }

        /// <inheritdoc/>
        public string Description => "null";

        /// <inheritdoc/>
        public void Start()
        {
            IsActive = true;
        }

        /// <inheritdoc/>
        public void Stop()
        {
            IsActive = false;
        }

        /// <inheritdoc/>
        public void SendPacket(byte[] data, int offset, int length)
        {
            // Deliberately empty - the whole point of this backend.
            // Referencing the event keeps the compiler from warning that it is never used.
            _ = OnPacketReceived;
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            Stop();
        }
    }

    /// <summary>
    /// An in-process Ethernet segment: several backends attached to one bus, where a frame sent by
    /// any of them is delivered to all the OTHERS.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Mirrors the topology of a real hub, and of RetroCore's <c>TcpEthernetRelay</c>: a dumb
    /// repeater with no MAC learning that never echoes a frame back to its sender. That makes it a
    /// faithful stand-in for the real segment in tests, with no sockets, no threads and no timing.
    /// </para>
    /// <para>
    /// Delivery is synchronous on the sending thread, so a test can send a frame and assert on what
    /// arrived without waiting.
    /// </para>
    /// </remarks>
    public sealed class InProcessEthernetSegment
    {
        private readonly object _portsLock = new object();
        private readonly List<InProcessEthernetBackend> _ports = new List<InProcessEthernetBackend>();

        /// <summary>
        /// Gets the number of frames repeated across the segment. Each inbound frame counts once,
        /// however many ports it is fanned out to.
        /// </summary>
        public long FramesRepeated { get; private set; }

        /// <summary>
        /// Creates a new backend attached to this segment.
        /// </summary>
        /// <param name="name">
        /// A short name for logs and for <see cref="IEthernetBackend.Description"/>.
        /// </param>
        /// <returns>
        /// The new backend.
        /// </returns>
        public IEthernetBackend CreatePort(string name)
        {
            InProcessEthernetBackend port = new InProcessEthernetBackend(this, name);
            lock (_portsLock)
            {
                _ports.Add(port);
            }

            return port;
        }

        /// <summary>
        /// Repeats a frame to every attached port except its sender.
        /// </summary>
        /// <param name="sender">
        /// The port the frame arrived from.
        /// </param>
        /// <param name="data">
        /// The buffer holding the frame.
        /// </param>
        /// <param name="offset">
        /// The offset of the first frame byte.
        /// </param>
        /// <param name="length">
        /// The number of bytes in the frame.
        /// </param>
        internal void Repeat(InProcessEthernetBackend sender, byte[] data, int offset, int length)
        {
            byte[] copy = new byte[length];
            Array.Copy(data, offset, copy, 0, length);

            InProcessEthernetBackend[] targets;
            lock (_portsLock)
            {
                FramesRepeated++;
                targets = _ports.ToArray();
            }

            for (int i = 0; i < targets.Length; i++)
            {
                InProcessEthernetBackend target = targets[i];
                if (!ReferenceEquals(target, sender) && target.IsActive)
                {
                    target.Deliver(copy, copy.Length);
                }
            }
        }
    }

    /// <summary>
    /// One port on an <see cref="InProcessEthernetSegment"/>.
    /// </summary>
    public sealed class InProcessEthernetBackend : IEthernetBackend
    {
        private readonly InProcessEthernetSegment _segment;
        private readonly string _name;

        /// <summary>
        /// Initialises a port on a segment.
        /// </summary>
        /// <param name="segment">
        /// The segment this port attaches to.
        /// </param>
        /// <param name="name">
        /// A short name for logs.
        /// </param>
        internal InProcessEthernetBackend(InProcessEthernetSegment segment, string name)
        {
            _segment = segment;
            _name = name;
        }

        /// <inheritdoc/>
        public event EthernetPacketReceived? OnPacketReceived;

        /// <inheritdoc/>
        public bool IsActive { get; private set; }

        /// <inheritdoc/>
        public string Description => $"in-process:{_name}";

        /// <inheritdoc/>
        public void Start()
        {
            IsActive = true;
        }

        /// <inheritdoc/>
        public void Stop()
        {
            IsActive = false;
        }

        /// <inheritdoc/>
        public void SendPacket(byte[] data, int offset, int length)
        {
            if (!IsActive || data == null || length <= 0)
            {
                return;
            }

            _segment.Repeat(this, data, offset, length);
        }

        /// <summary>
        /// Delivers a frame up to this port's subscriber.
        /// </summary>
        /// <param name="data">
        /// The frame bytes.
        /// </param>
        /// <param name="length">
        /// The number of valid bytes.
        /// </param>
        internal void Deliver(byte[] data, int length)
        {
            OnPacketReceived?.Invoke(data, length);
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            Stop();
        }
    }
}
