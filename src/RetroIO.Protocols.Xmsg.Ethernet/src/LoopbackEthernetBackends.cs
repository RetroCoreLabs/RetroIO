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
        /// <summary>
        /// Occurs when a complete Ethernet frame arrives. This backend never raises it.
        /// </summary>
        public event EthernetPacketReceived? OnPacketReceived;

        /// <summary>
        /// Gets a value indicating whether the backend has been started and not stopped.
        /// </summary>
        /// <value>
        /// <c>true</c> after <see cref="Start"/>, <c>false</c> after <see cref="Stop"/> or before the first start.
        /// </value>
        public bool IsActive { get; private set; }

        /// <summary>
        /// Gets a short human-readable description of the transport, for logs.
        /// </summary>
        /// <value>
        /// Always <c>"null"</c>.
        /// </value>
        public string Description => "null";

        /// <summary>
        /// Starts the backend. This only sets <see cref="IsActive"/>; calling it again does nothing more.
        /// </summary>
        public void Start()
        {
            IsActive = true;
        }

        /// <summary>
        /// Stops the backend. This only clears <see cref="IsActive"/>; calling it again does nothing more.
        /// </summary>
        public void Stop()
        {
            IsActive = false;
        }

        /// <summary>
        /// Discards the frame. Nothing is sent and nothing is checked.
        /// </summary>
        /// <param name="data">
        /// The buffer holding the frame; ignored.
        /// </param>
        /// <param name="offset">
        /// The offset of the first frame byte; ignored.
        /// </param>
        /// <param name="length">
        /// The number of bytes to send; ignored.
        /// </param>
        public void SendPacket(byte[] data, int offset, int length)
        {
            // Deliberately empty - the whole point of this backend.
            // Referencing the event keeps the compiler from warning that it is never used.
            _ = OnPacketReceived;
        }

        /// <summary>
        /// Disposes the backend by calling <see cref="Stop"/>.
        /// </summary>
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

        /// <summary>
        /// Occurs when a frame sent by another active port on the same segment arrives.
        /// </summary>
        /// <remarks>
        /// Raised synchronously on the sending thread. The buffer is a copy made by the segment and
        /// shared by all receiving ports.
        /// </remarks>
        public event EthernetPacketReceived? OnPacketReceived;

        /// <summary>
        /// Gets a value indicating whether the port has been started and not stopped.
        /// </summary>
        /// <value>
        /// <c>true</c> after <see cref="Start"/>, <c>false</c> after <see cref="Stop"/> or before the
        /// first start. An inactive port neither sends nor receives frames.
        /// </value>
        public bool IsActive { get; private set; }

        /// <summary>
        /// Gets a short human-readable description of the transport, for logs.
        /// </summary>
        /// <value>
        /// <c>in-process:</c> followed by the port name.
        /// </value>
        public string Description => $"in-process:{_name}";

        /// <summary>
        /// Starts the port. This only sets <see cref="IsActive"/>; calling it again does nothing more.
        /// </summary>
        public void Start()
        {
            IsActive = true;
        }

        /// <summary>
        /// Stops the port. This only clears <see cref="IsActive"/>; the port stays attached to the segment.
        /// </summary>
        public void Stop()
        {
            IsActive = false;
        }

        /// <summary>
        /// Sends one raw Ethernet frame to every other active port on the segment.
        /// </summary>
        /// <remarks>
        /// Does nothing when the port is not active, <paramref name="data"/> is null, or
        /// <paramref name="length"/> is 0 or less. Delivery is synchronous on the calling thread.
        /// </remarks>
        /// <param name="data">
        /// The buffer holding the frame.
        /// </param>
        /// <param name="offset">
        /// The offset of the first frame byte within <paramref name="data"/>.
        /// </param>
        /// <param name="length">
        /// The number of bytes to send.
        /// </param>
        /// <exception cref="ArgumentException">
        /// Thrown when <paramref name="offset"/> and <paramref name="length"/> do not lie inside
        /// <paramref name="data"/>.
        /// </exception>
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

        /// <summary>
        /// Disposes the port by calling <see cref="Stop"/>. The port is not removed from the segment.
        /// </summary>
        public void Dispose()
        {
            Stop();
        }
    }
}
