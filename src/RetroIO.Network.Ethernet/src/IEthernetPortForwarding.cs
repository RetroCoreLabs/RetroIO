//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

namespace RetroIO.Network.Ethernet
{
    /// <summary>
    /// An optional extra an <see cref="IEthernetBackend"/> implements when it can forward host
    /// ports to the guest (the built-in NAT, host networking design section 7.7). The
    /// <c>net.forward</c> command and the machine settings window use it, so neither needs to
    /// know which backend is attached.
    /// </summary>
    /// <remarks>
    /// A rule is text such as <c>tcp:2323:23</c> (host port 2323 to guest port 23),
    /// <c>tcp:127.0.0.1:2323:23</c> (this computer only), <c>udp:5353:53</c> or
    /// <c>tcp:6000-6009:6000</c> (a range). Rules can be added and removed while the machine runs.
    /// Every method may be called from any thread.
    /// </remarks>
    public interface IEthernetPortForwarding
    {
        /// <summary>
        /// Adds a rule and, when the backend runs, starts listening at once.
        /// </summary>
        /// <param name="rule">
        /// The rule as text.
        /// </param>
        /// <param name="message">
        /// The rule in its full form when it was added; otherwise why not
        /// (cannot be read, already there, overlaps another rule, host port in use).
        /// </param>
        /// <returns>
        /// <c>true</c> when the rule was added.
        /// </returns>
        bool AddForward(string rule, out string message);

        /// <summary>
        /// Removes a rule and stops listening on its ports. Connections already open stay open.
        /// </summary>
        /// <param name="rule">
        /// The rule as text; any form that reads to the same rule.
        /// </param>
        /// <param name="message">
        /// The rule removed, or why nothing was removed.
        /// </param>
        /// <returns>
        /// <c>true</c> when a rule was removed.
        /// </returns>
        bool RemoveForward(string rule, out string message);

        /// <summary>
        /// Gets the rules in their full form, in the order they were added.
        /// </summary>
        /// <returns>
        /// One entry per rule; empty when there is none.
        /// </returns>
        string[] GetForwards();

        /// <summary>
        /// Gets one line per rule: the rule and whether it is listening (or why not).
        /// </summary>
        /// <returns>
        /// One entry per rule; empty when there is none.
        /// </returns>
        string[] DescribeForwards();
    }
}
