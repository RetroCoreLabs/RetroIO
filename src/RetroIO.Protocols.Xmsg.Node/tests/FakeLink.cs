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

        /// <inheritdoc />
        public event LinkPayloadReceived? PayloadReceived;

        /// <inheritdoc />
        public event LinkStatusChanged? StatusChanged;

        /// <inheritdoc />
        public string Name { get; }

        /// <inheritdoc />
        public LinkStatus Status { get; private set; }

        /// <summary>
        /// Gets the payloads sent down this link, in order.
        /// </summary>
        public List<byte[]> Sent { get; } = new List<byte[]>();

        /// <inheritdoc />
        public bool Start()
        {
            Status = LinkStatus.Active;
            return true;
        }

        /// <inheritdoc />
        public void Stop()
        {
            LinkStatus previous = Status;
            Status = LinkStatus.Stopped;
            StatusChanged?.Invoke(this, previous, Status, "stopped");
        }

        /// <inheritdoc />
        public void Dispose()
        {
            Stop();
        }

        /// <inheritdoc />
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
