//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;

using RetroIO.Protocols.Xmsg.Node.Seam;

namespace RetroIO.Protocols.Xmsg.Node.Tests
{
    /// <summary>
    /// An <see cref="ILink"/> that records what was sent down it and can push a payload up.
    /// </summary>
    /// <remarks>
    /// <para><b>Why this one is shared and the other is not</b></para>
    /// This double starts Active and does nothing clever: it is for tests about ROUTING, where the
    /// question is which link a datagram came out of, not how the link behaves.
    /// <para>
    /// <c>FakeLinkAcceptanceTests</c> keeps its OWN nested fake on purpose. That one starts Stopped,
    /// walks the real status transitions, and hands the upper stack a single POOLED receive buffer
    /// that it reuses - which is the whole point of those tests, proving the stack copies anything
    /// it retains. Merging the two would delete that, so they are deliberately separate rather than
    /// duplication waiting to be tidied.
    /// </para>
    /// </remarks>
    internal sealed class FakeLink : ILink
    {
        /// <summary>
        /// Initialises the link in the Active state.
        /// </summary>
        /// <param name="name">
        /// The link name.
        /// </param>
        public FakeLink(string name)
        {
            Name = name;
            Status = LinkStatus.Active;
        }

        /// <summary>
        /// Occurs when a payload is pushed up with <see cref="RaisePayload"/>.
        /// </summary>
        public event LinkPayloadReceived? PayloadReceived;

        /// <summary>
        /// Occurs when <see cref="Stop"/> takes the link from another status to Stopped, with the
        /// reason <c>stopped</c>. It is not raised when the link was already stopped, and never by
        /// <see cref="Start"/>.
        /// </summary>
        public event LinkStatusChanged? StatusChanged;

        /// <summary>
        /// Gets the link name given to the constructor.
        /// </summary>
        public string Name { get; }

        /// <summary>
        /// Gets the current operational status: Active from construction or <see cref="Start"/>,
        /// Stopped after <see cref="Stop"/>.
        /// </summary>
        public LinkStatus Status { get; private set; }

        /// <summary>
        /// Gets the payloads sent down this link, in order.
        /// </summary>
        public List<byte[]> Sent { get; } = new List<byte[]>();

        /// <summary>
        /// Sets the status to Active without raising <see cref="StatusChanged"/>.
        /// </summary>
        /// <returns>
        /// Always <c>true</c>.
        /// </returns>
        public bool Start()
        {
            Status = LinkStatus.Active;
            return true;
        }

        /// <summary>
        /// Sets the status to Stopped and raises <see cref="StatusChanged"/> with the previous and
        /// new status. Does nothing when the link is already stopped.
        /// </summary>
        public void Stop()
        {
            LinkStatus previous = Status;
            if (previous == LinkStatus.Stopped)
            {
                return;
            }

            Status = LinkStatus.Stopped;
            StatusChanged?.Invoke(this, previous, Status, "stopped");
        }

        /// <summary>
        /// Stops the link by calling <see cref="Stop"/>.
        /// </summary>
        public void Dispose()
        {
            Stop();
        }

        /// <summary>
        /// Records a copy of the payload in <see cref="Sent"/> when the link is Active.
        /// </summary>
        /// <param name="payload">
        /// The payload bytes to send.
        /// </param>
        /// <returns>
        /// <c>true</c> when the payload was recorded; <c>false</c> when the link is not Active.
        /// </returns>
        public bool SendData(ReadOnlySpan<byte> payload)
        {
            if (Status != LinkStatus.Active)
            {
                return false;
            }

            Sent.Add(payload.ToArray());
            return true;
        }

        /// <summary>
        /// Pushes a payload up as if it had arrived on this link.
        /// </summary>
        /// <param name="payload">
        /// The payload bytes.
        /// </param>
        public void RaisePayload(byte[] payload)
        {
            PayloadReceived?.Invoke(this, payload, payload.Length);
        }
    }
}
