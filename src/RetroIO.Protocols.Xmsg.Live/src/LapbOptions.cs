using System;

namespace RetroIO.Protocols.Xmsg.Live
{
    /// <summary>
    /// Configurable LAPB timers and window size, with the ND LAPB spec section 5 defaults.
    /// </summary>
    /// <remarks>
    /// <para><b>Time unit</b></para>
    /// <see cref="T1"/> and <see cref="T3"/> are durations in the SAME unit as the monotonic value
    /// passed to <see cref="LapbLayer.Tick(long)"/> and the other clocked methods. The unit is the
    /// caller's choice: unit tests inject small integer ticks, and the live runner injects elapsed
    /// milliseconds (so the defaults below read as 3000 ms and 30000 ms).
    /// <para><b>Defaults</b></para>
    /// The spec defaults are:
    ///  - <c>T1 = 3000</c> - retransmission / poll timer.
    ///  - <c>T3 = 30000</c> - idle keepalive timer.
    ///  - <c>N2 = 10</c> - maximum retransmissions before declaring link failure.
    ///  - <c>k = 7</c> - transmit window (unacknowledged I-frames outstanding, MUST be 1..7).
    /// </remarks>
    public sealed class LapbOptions
    {
        /// <summary>
        /// The retransmission / poll timer duration (spec T1), in the injected-clock unit.
        /// </summary>
        public long T1 { get; }

        /// <summary>
        /// The idle keepalive timer duration (spec T3), in the injected-clock unit.
        /// </summary>
        public long T3 { get; }

        /// <summary>
        /// The maximum number of retransmissions before the link is declared failed (spec N2).
        /// </summary>
        public int N2 { get; }

        /// <summary>
        /// The transmit window: the maximum number of unacknowledged I-frames outstanding (spec k).
        /// </summary>
        public int WindowSize { get; }

        /// <summary>
        /// Whether exhausting N2 on a live link re-establishes it with a fresh SABM.
        /// </summary>
        /// <remarks>
        /// <para><b>Defaults to <c>false</c>, which DEVIATES from the spec on purpose</b></para>
        /// Spec 5.1 step 1 says a link failure from CONNECTED should reset and send a new SABM. That
        /// is correct LAPB and it is what this layer used to do unconditionally. Against a real
        /// ND-100 running XMSG it is destructive:
        /// <para>
        /// Measured on D100, 2026-08-21, twice, with the console error device logged so both ends
        /// carry wall-clock stamps. XMSG died with <c>XMSG ERROR CODE: 27</c> (XXNER, network
        /// gateway error) at <c>PHYSICAL ADDRESS 141204</c> 147 ms and 201 ms after our SABM. In the
        /// first case the peer had COMPLETED a transfer 30 seconds earlier, so it was healthy and
        /// nothing else had touched it.
        /// </para>
        /// <para>
        /// A gateway holding session records for a conversation does not survive the link underneath
        /// it being re-established; it takes a fatal inconsistency instead of tidying up. Losing one
        /// transfer is cheap. Taking XMSG down costs XMSG, COSPO, the file server, TADAD and XMFIDO,
        /// and every one of them needs bringing back.
        /// </para>
        /// <para>
        /// So the default is to STOP and report through <see cref="LapbLayer.OnLinkFailure"/>,
        /// leaving the caller to end its run cleanly with a DISC. Set this to <c>true</c> only for
        /// conformance testing against a peer that is expected to survive a re-establish.
        /// </para>
        /// </remarks>
        public bool ReestablishOnLinkFailure { get; }

         /// <summary>
        /// Which dialect this link speaks. Defaults to <see cref="LapbVariant.Nd"/>, which is what
        /// every caller wanted when this engine had no choice.
        /// </summary>
        public LapbVariant Variant { get; }

        /// <summary>
        /// True when this station is the DTE, false when it is the DCE. Decides which of the two
        /// ITU addresses goes on a command and which on a response (X.25 2.4.2). Ignored by the ND
        /// dialect, which addresses by frame family instead.
        /// </summary>
        public bool IsDte { get; }

       /// <summary>
        /// Initialises LAPB options, validating each value and falling back to the spec default.
        /// </summary>
        /// <param name="t1">
        /// The T1 retransmission / poll timer; must be positive. Defaults to <c>3000</c>.
        /// </param>
        /// <param name="t3">
        /// The T3 idle keepalive timer; must be positive. Defaults to <c>30000</c>.
        /// </param>
        /// <param name="n2">
        /// The N2 maximum retransmissions; must be at least 1. Defaults to <c>10</c>.
        /// </param>
        /// <param name="windowSize">
        /// The transmit window k; must be in the range 1..7 for modulo-8. Defaults to <c>7</c>.
        /// </param>
        /// <param name="reestablishOnLinkFailure">
        /// Whether N2 exhaustion re-establishes the link with a fresh SABM. Defaults to
        /// <c>false</c> because that SABM kills a real XMSG gateway - see
        /// <see cref="ReestablishOnLinkFailure"/>.
        /// </param>
        /// <exception cref="ArgumentOutOfRangeException">
        /// Thrown when <paramref name="t1"/> or <paramref name="t3"/> is not positive, when
        /// <paramref name="n2"/> is less than 1, or when <paramref name="windowSize"/> is outside 1..7.
        /// </exception>
        /// <param name="variant">
        /// Which LAPB dialect the link speaks; ND by default.
        /// </param>
        /// <param name="isDte">
        /// True when this station is the DTE (ITU addressing only).
        /// </param>
        public LapbOptions(long t1 = 3000, long t3 = 30000, int n2 = 10, int windowSize = 7, bool reestablishOnLinkFailure = false, LapbVariant variant = LapbVariant.Nd, bool isDte = true)
        {
            if (t1 <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(t1), t1, "T1 must be positive.");
            }

            if (t3 <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(t3), t3, "T3 must be positive.");
            }

            if (n2 < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(n2), n2, "N2 must be at least 1.");
            }

            // Modulo-8 sequence numbering caps the window at 7 (spec 4.2); 0 would stall all sends.
            if (windowSize < 1 || windowSize > 7)
            {
                throw new ArgumentOutOfRangeException(nameof(windowSize), windowSize, "Window size k must be 1..7 for modulo-8.");
            }

            T1 = t1;
            T3 = t3;
            N2 = n2;
            WindowSize = windowSize;
            ReestablishOnLinkFailure = reestablishOnLinkFailure;
            Variant = variant;
            IsDte = isDte;
        }

        /// <summary>
        /// Gets a shared options instance carrying the spec section 5 defaults.
        /// </summary>
        public static LapbOptions Default { get; } = new LapbOptions();
    }
}
