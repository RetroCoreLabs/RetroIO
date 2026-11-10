//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//


using System;

using RetroIO.Protocols.Xmsg.Node.Seam;
using RetroIO.Protocols.Xmsg.Node.Services;

namespace RetroIO.Protocols.Xmsg.Hosting
{
    /// <summary>
    /// The one definition of "may a transfer originate to this peer now".
    /// </summary>
    /// <remarks>
    /// Moved here from the runner (2026-11-09), unchanged, so the runner and a hosted service
    /// apply the same gate.
    /// </remarks>
    public static class XmsgSyncReadiness
    {
        /// <summary>
        /// Builds the readiness test a transfer driver must satisfy before it originates.
        /// </summary>
        /// <param name="host">
        /// The node whose link knowledge decides readiness.
        /// </param>
        /// <param name="peer">
        /// The node a transfer will address.
        /// </param>
        /// <param name="originateFromSeed">
        /// <see langword="true"/> to allow originating to a peer we have met before using its remembered
        /// envelope seed, instead of waiting for it to address us again. Opt-in; see the remarks.
        /// </param>
        /// <returns>
        /// A test that is true only once <paramref name="peer"/> has addressed us.
        /// </returns>
        /// <remarks>
        /// <para><b>Why this is a named function and not a lambda at the call site</b></para>
        /// <para>
        /// The gate was once written inline as "the LAPB link is Connected", which is true within
        /// milliseconds of startup. The sync daemon then originated before the peer had said anything,
        /// its connect letter was answered <c>XRUNN</c>, and because the driver discarded that answer it
        /// looked like silence. MEASURED 2026-08-17: with this gate instead, the same daemon carried two
        /// files to a real ND on the first attempt.
        /// </para>
        /// <para>
        /// <see cref="XmsgServerHost.CanReach"/> is true only once an INBOUND datagram has taught the
        /// layer the peer envelope seed - the point at which a frame we originate can be addressed at
        /// all. Naming it here means the rule is testable and has one definition rather than one per
        /// call site.
        /// </para>
        /// <para><b>The seed gate is an EXPERIMENT and is off by default</b></para>
        /// <para>
        /// With <paramref name="originateFromSeed"/> set, the gate becomes
        /// <see cref="XmsgServerHost.OpenLinkFromRememberedSeed"/>, which succeeds for any peer whose
        /// seed is in the store. That is what a daemon needs to work from cold - without it every
        /// transfer waits for a person to type a command on the far machine.
        /// </para>
        /// <para>
        /// It is opt-in because the last measurement went against it: D100 answered <c>XRUNN</c> to a
        /// letter sent from the remembered seed alone. That test predates both the access grant and the
        /// discovery that the outgoing counter was being zeroed on every run that day, so it is worth
        /// repeating - but not worth making the default until it has been.
        /// </para>
        /// </remarks>
        public static Func<bool> Build(XmsgNodeHost host, ushort peer, bool originateFromSeed)
        {
            if (host == null)
            {
                throw new ArgumentNullException(nameof(host));
            }

            if (originateFromSeed)
            {
                return () => host.ServerHost.OpenLinkFromRememberedSeed(peer);
            }

            return () => host.ServerHost.CanReach(peer);
        }
    }
}
