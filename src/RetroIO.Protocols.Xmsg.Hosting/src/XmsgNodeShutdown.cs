//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;

using RetroIO.Protocols.Xmsg.Live;
using RetroIO.Protocols.Xmsg.Live.Seam;

namespace RetroIO.Protocols.Xmsg.Hosting
{
    /// <summary>
    /// The steps a node must take on the way down, while it can still transmit: log the TAD users
    /// out, then (on HDLC) say goodbye at the link layer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Call both from the place where the link is still up and nothing else is sending: the HDLC
    /// adapter's <c>Stopping</c> event (it fires on the pump thread before the closing flush), or
    /// the end of an Ethernet pump loop before the link is disposed.
    /// </para>
    /// <para>
    /// Moved here from the runner (2026-11-09). The runner did both on HDLC and NEITHER on
    /// Ethernet, so an Ethernet run that was stopped left the peer holding open sessions.
    /// </para>
    /// </remarks>
    public static class XmsgNodeShutdown
    {
        /// <summary>
        /// Logs every open TAD session out and sends the frames now.
        /// </summary>
        /// <param name="node">
        /// The node whose TAD server holds the sessions.
        /// </param>
        /// <param name="log">
        /// Where the number of frames sent is reported. Null discards it.
        /// </param>
        /// <returns>
        /// The number of frames sent; 0 when no session was open.
        /// </returns>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="node"/> is null.
        /// </exception>
        /// <remarks>
        /// LOG THE USERS OUT WHILE WE CAN STILL TRANSMIT. Stopping the runner with sessions open
        /// killed D100's XMSG on 2026-08-17 - it was left holding half a session each and took a
        /// fatal internal inconsistency, costing an emulator restart. A forced kill runs none of
        /// this, so the operating rule stands as well: get the users out before stopping a server
        /// the ND is still talking to.
        /// </remarks>
        public static int LogOutTadSessions(XmsgHostedNode node, XmsgLogHandler? log)
        {
            if (node == null)
            {
                throw new ArgumentNullException(nameof(node));
            }

            IReadOnlyList<XmsgFrame> teardown = node.TadServer.ShutdownAllSessions(node.Host.ServerHost);
            if (teardown.Count == 0)
            {
                return 0;
            }

            log?.Invoke($"[tad] stopping: logging out the open session(s), {teardown.Count} frame(s).");
            return node.Host.SendFrames(teardown);
        }

        /// <summary>
        /// Sends a LAPB DISC unless the link is already down or going down.
        /// </summary>
        /// <param name="link">
        /// The LAPB link under the HDLC adapter.
        /// </param>
        /// <param name="log">
        /// Where the DISC is reported. Null discards it.
        /// </param>
        /// <returns>
        /// <see langword="true"/> when a DISC was queued.
        /// </returns>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="link"/> is null.
        /// </exception>
        /// <remarks>
        /// AND SAY GOODBYE AT THE LINK LAYER TOO. Without this the next connection sat repeating
        /// SABM for ever and had to be rescued by hand from the machine with
        /// <c>X-C: STOP-LINK</c> and <c>START-LINK</c>. It happened on ordinary exits as well as
        /// hard kills, because <c>LapbLayer.Disconnect</c> existed and nothing called it: we closed
        /// the socket and left D100 holding a link it still believed was up.
        /// </remarks>
        public static bool DisconnectLapb(LapbLayer link, XmsgLogHandler? log)
        {
            if (link == null)
            {
                throw new ArgumentNullException(nameof(link));
            }

            if (link.State == LapbLayerState.Disconnected
                || link.State == LapbLayerState.DiscSent)
            {
                return false;
            }

            log?.Invoke("[link] stopping: sending DISC so the peer does not hold the link.");
            link.Disconnect(Environment.TickCount64);
            return true;
        }

        /// <summary>
        /// Waits until the Ethernet link has sent every parked datagram, or the grace runs out.
        /// </summary>
        /// <param name="link">
        /// The Ethernet link about to be disposed.
        /// </param>
        /// <param name="grace">
        /// The longest wait. The HDLC adapter uses 2 seconds for its own final flush.
        /// </param>
        /// <param name="log">
        /// Where a wait that ran out is reported. Null discards it.
        /// </param>
        /// <returns>
        /// <see langword="true"/> when nothing was left parked; <see langword="false"/> when the
        /// grace ran out first.
        /// </returns>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="link"/> is null.
        /// </exception>
        /// <remarks>
        /// <para>
        /// Call it after <see cref="LogOutTadSessions"/>. The logout frames are sent at once only
        /// when the peer's send window has room; otherwise the link parks them, and the peer's
        /// acknowledgements (handled on the backend's receive thread) release them. Disposing the
        /// link before that loses them, which is the exact case the logout exists to prevent.
        /// </para>
        /// <para>
        /// The wait is bounded so a peer that has gone away cannot hang the exit.
        /// </para>
        /// </remarks>
        public static async Task<bool> WaitForEthernetQueueAsync(EthernetLink link, TimeSpan grace, XmsgLogHandler? log)
        {
            if (link == null)
            {
                throw new ArgumentNullException(nameof(link));
            }

            Stopwatch waited = Stopwatch.StartNew();
            while (link.QueuedDatagrams > 0)
            {
                if (waited.Elapsed >= grace)
                {
                    log?.Invoke(
                        $"[link] stopping: {link.QueuedDatagrams} datagram(s) still parked after "
                        + $"{grace.TotalSeconds:F1}s; the peer did not open its window. They are dropped.");
                    return false;
                }

                await Task.Delay(20).ConfigureAwait(false);
            }

            return true;
        }
    }
}
