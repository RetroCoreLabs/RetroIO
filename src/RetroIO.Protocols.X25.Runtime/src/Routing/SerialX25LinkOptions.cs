//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;

namespace RetroIO.Protocols.X25.Runtime.Transport
{
    /// <summary>
    /// Which end of the X.25 DTE/DCE interface this side of a serial line is.
    /// </summary>
    /// <remarks>
    /// X.25 (10/96) gives the two ends different LAPB addresses (clause 2.4.2) and different packet
    /// layer duties (clause 3 and Annex A), but it does not say which end a given serial cable is.
    /// That is a property of how the line is wired and agreed, so there is no default: the owner
    /// of the configuration has to say it.
    /// </remarks>
    public enum SerialX25LinkRole
    {
        /// <summary>
        /// This side is the DTE. Its commands carry LAPB address B and its responses address A
        /// (X.25 2.4.2).
        /// </summary>
        Dte,

        /// <summary>
        /// This side is the DCE. Its commands carry LAPB address A and its responses address B
        /// (X.25 2.4.2).
        /// </summary>
        Dce,
    }

    /// <summary>
    /// What a serial transport needs, beyond the port itself, to run X.25 on the line: LAPB under
    /// the packet layer, with HDLC framing and the frame check sequence below that.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="Role"/> and <see cref="InitiateLinkSetup"/> are null until set, and the transport
    /// runs no X.25 on the line while either is null. There is no default for either, on purpose.
    /// X.25 does not decide them for a point to point serial cable. For the first, see
    /// <see cref="SerialX25LinkRole"/>. For the second, clause 2.4.4.1 reads "Either the DTE or the
    /// DCE may initiate data link set-up", so a station that only waits for the peer and a station
    /// that sends the first SABM are both correct, and which one a given line needs depends on the
    /// peer. They are properties that can be null (not constructor values) so that a configuration
    /// file that leaves one out stays unset instead of turning into the first enum value.
    /// </para>
    /// <para>
    /// The timer and window values are optional. When one is left null the value of
    /// <see cref="RetroIO.Protocols.Xmsg.Live.LapbOptions"/> is used. Those are this library's
    /// values, not X.25's: clause 2.4.9 calls T1, T2, T3, N1, N2 and k system parameters that the
    /// two ends agree on, and gives no figure for T1, T2, T3 or N2.
    /// </para>
    /// </remarks>
    public sealed class SerialX25LinkOptions
    {
        /// <summary>
        /// Creates options with nothing set.
        /// </summary>
        public SerialX25LinkOptions()
        {
        }

        /// <summary>
        /// Creates options with both required values set.
        /// </summary>
        /// <param name="role">
        /// Which end of the DTE/DCE interface this side is.
        /// </param>
        /// <param name="initiateLinkSetup">
        /// True to send the first SABM when the transport starts (X.25 2.4.4.1). False to wait for the
        /// peer's SABM and answer it.
        /// </param>
        public SerialX25LinkOptions(SerialX25LinkRole role, bool initiateLinkSetup)
        {
            Role = role;
            InitiateLinkSetup = initiateLinkSetup;
        }

        /// <summary>
        /// Which end of the DTE/DCE interface this side is. Null until set.
        /// </summary>
        public SerialX25LinkRole? Role { get; set; }

        /// <summary>
        /// Whether this side sends the first SABM when the transport starts. Null until set.
        /// </summary>
        public bool? InitiateLinkSetup { get; set; }

        /// <summary>
        /// True when both <see cref="Role"/> and <see cref="InitiateLinkSetup"/> are set.
        /// </summary>
        public bool IsComplete
        {
            get { return Role.HasValue && InitiateLinkSetup.HasValue; }
        }

        /// <summary>
        /// Timer T1 in milliseconds (X.25 2.4.9.1), or null for the library value.
        /// </summary>
        public long? T1Ms { get; set; }

        /// <summary>
        /// Parameter T2 in milliseconds (X.25 2.4.9.2), or null for the library value. Must be less
        /// than T1 when it is above zero.
        /// </summary>
        public long? T2Ms { get; set; }

        /// <summary>
        /// Timer T3 in milliseconds (X.25 2.4.9.3), or null for the library value.
        /// </summary>
        public long? T3Ms { get; set; }

        /// <summary>
        /// Parameter N2, the largest number of attempts to send a frame (X.25 2.4.9.4), or null for
        /// the library value.
        /// </summary>
        public int? N2 { get; set; }

        /// <summary>
        /// Window size k, the largest number of unacknowledged I frames (X.25 2.4.9.6), or null for
        /// the library value.
        /// </summary>
        public int? WindowSize { get; set; }

        /// <summary>
        /// Builds the LAPB options for this side: the ITU-T X.25 dialect, addressed for
        /// <see cref="Role"/>, with each timer and window value that was set.
        /// </summary>
        /// <returns>
        /// The options for a <c>LapbLayer</c>.
        /// </returns>
        /// <exception cref="InvalidOperationException">
        /// Thrown when <see cref="Role"/> is not set.
        /// </exception>
        internal RetroIO.Protocols.Xmsg.Live.LapbOptions ToLapbOptions()
        {
            if (!Role.HasValue)
            {
                throw new InvalidOperationException("The role (DTE or DCE) is not set and has no default.");
            }

            RetroIO.Protocols.Xmsg.Live.LapbOptions library = RetroIO.Protocols.Xmsg.Live.LapbOptions.Default;
            return new RetroIO.Protocols.Xmsg.Live.LapbOptions(
                t1: T1Ms ?? library.T1,
                t3: T3Ms ?? library.T3,
                n2: N2 ?? library.N2,
                windowSize: WindowSize ?? library.WindowSize,
                reestablishOnLinkFailure: false,
                variant: RetroIO.Protocols.Xmsg.Live.LapbVariant.Itu,
                isDte: Role.GetValueOrDefault() == SerialX25LinkRole.Dte,
                maxInformationLength: library.MaxInformationLength,
                t2: T2Ms ?? library.T2);
        }
    }
}
