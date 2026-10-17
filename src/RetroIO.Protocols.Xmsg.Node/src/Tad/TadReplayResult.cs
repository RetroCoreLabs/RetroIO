//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

namespace RetroIO.Protocols.Xmsg.Node.Tad
{
    /// <summary>
    /// The outcome of feeding one client frame to a <see cref="TadReplayServer"/>.
    /// </summary>
    /// <remarks>
    /// The result deliberately separates the two things a server does: the VERIFIED secure
    /// delivery ACK (always producible for a data frame) and the application-level RESPONSE
    /// (only known when the exact input was recorded from the capture). When
    /// <see cref="HasRecordedResponse"/> is <c>false</c> the server did not fabricate a reply -
    /// <see cref="Note"/> explains that the input was never observed.
    /// </remarks>
    public sealed class TadReplayResult
    {
        /// <summary>
        /// Initialises a replay result.
        /// </summary>
        /// <param name="ack">
        /// The secure delivery ACK for the client frame, or <c>null</c> when the frame was not
        /// an acknowledgeable data frame.
        /// </param>
        /// <param name="recordedResponse">
        /// The recorded server response for this exact input, or <c>null</c> when none was
        /// recorded.
        /// </param>
        /// <param name="note">
        /// A human-readable, provenance-tagged note describing what was and was not produced.
        /// </param>
        public TadReplayResult(XmsgFrame? ack, XmsgFrame? recordedResponse, string note)
        {
            Ack = ack;
            RecordedResponse = recordedResponse;
            Note = note;
        }

        /// <summary>
        /// Gets the secure delivery ACK (subtype <c>0x03</c>) for the client frame, or
        /// <c>null</c> when the frame was not an acknowledgeable data frame.
        /// </summary>
        public XmsgFrame? Ack { get; }

        /// <summary>
        /// Gets the recorded server response replayed for this exact input, or <c>null</c>
        /// when the input was never observed in the capture.
        /// </summary>
        public XmsgFrame? RecordedResponse { get; }

        /// <summary>
        /// Gets a value indicating whether a recorded response was replayed.
        /// </summary>
        public bool HasRecordedResponse
        {
            get { return RecordedResponse != null; }
        }

        /// <summary>
        /// Gets a provenance-tagged note describing the outcome.
        /// </summary>
        public string Note { get; }
    }
}
