namespace RetroIO.Protocols.Xmsg.Sync
{
    /// <summary>
    /// Carries out ONE file transfer at a time on behalf of <see cref="SyncRunner"/>.
    /// </summary>
    /// <remarks>
    /// <para><b>Why this is an interface and not the file-access driver itself</b></para>
    /// <para>
    /// Everything else in this assembly decides without touching a machine - that is what makes
    /// the awkward cases testable at all. Referring to the file-access drivers here would drag the
    /// whole node, its transport and a live peer into every test of "what should happen when a
    /// push fails halfway". So the wire stays on the other side of this boundary: the runner
    /// implements it with the real drivers, and the tests implement it with a class that simply
    /// says yes or no.
    /// </para>
    /// <para><b>One at a time, deliberately</b></para>
    /// <para>
    /// A file-access conversation owns a session port, a conversation number and a place in our
    /// datagram sequence. Two transfers at once would interleave their frames in that one sequence
    /// - and an interleaved sequence is exactly what has cost this project the most time
    /// (see <c>DOC\PULL-PROVED-AND-PUSH-XENSE-BURST-2026-08-11.md</c>). The runner therefore
    /// starts a transfer only when the previous one has reported, and this interface is shaped so
    /// that it cannot do otherwise: there is one <see cref="Begin"/> and one <see cref="Poll"/>.
    /// </para>
    /// <para><b>Polled, not awaited</b></para>
    /// <para>
    /// The node is driven by a loop tick that must not block - a transfer that waited would stop
    /// the file server answering the machine at the other end, which times out the far side. So a
    /// transfer is started, then asked "are you done yet" once per tick.
    /// </para>
    /// </remarks>
    public interface ISyncTransferAgent
    {
        /// <summary>
        /// Gets whether the agent can start a transfer right now.
        /// </summary>
        /// <remarks>
        /// False while a link is still coming up, or while the peer has not yet said anything we
        /// can address it by. The runner holds its queue rather than failing the work.
        /// </remarks>
        bool Ready { get; }

        /// <summary>
        /// Starts one transfer.
        /// </summary>
        /// <param name="request">
        /// What to carry, in which direction, and under what name on the machine.
        /// </param>
        /// <returns>
        /// <see langword="true"/> when the transfer was accepted and is now running;
        /// <see langword="false"/> when it could not be started at all.
        /// </returns>
        bool Begin(SyncTransferRequest request);

        /// <summary>
        /// Asks whether the transfer started by <see cref="Begin"/> has finished.
        /// </summary>
        /// <param name="result">
        /// The outcome when this returns <see langword="true"/>; otherwise null.
        /// </param>
        /// <returns>
        /// <see langword="true"/> when the transfer is over, whether it worked or not.
        /// </returns>
        bool Poll(out SyncTransferResult? result);
    }
}
