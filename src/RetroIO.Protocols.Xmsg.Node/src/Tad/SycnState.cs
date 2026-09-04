namespace RetroIO.Protocols.Xmsg.Node.Tad
{
    /// <summary>
    /// The 16-bit 7SYCN session-sync / login-state word (spec TAD-Message-Formats.md section 21).
    /// </summary>
    /// <remarks>
    /// VERIFIED from three captured logins. The host drives the client through these states; reaching
    /// <see cref="LoggedIn"/> is what stops SINTRAN applying its 1-minute "not logged in" idle drop.
    /// </remarks>
    public enum SycnState : ushort
    {
        /// <summary>
        /// Waiting for the username (asserted after the banner and after a failed password) - <c>0x0002</c>.
        /// </summary>
        WaitingForUsername = 0x0002,

        /// <summary>
        /// The username was accepted - <c>0x0003</c>.
        /// </summary>
        UsernameAccepted = 0x0003,

        /// <summary>
        /// The password was accepted ("OK") - <c>0x0006</c>.
        /// </summary>
        PasswordAccepted = 0x0006,

        /// <summary>
        /// Logged in; re-asserted after every completed command - <c>0x000A</c>.
        /// </summary>
        LoggedIn = 0x000A,

        /// <summary>
        /// Logged out (accompanies "--EXIT--") - <c>0x000B</c>.
        /// </summary>
        LoggedOut = 0x000B,

        /// <summary>
        /// Error-text wrapper (for example "AMBIGUOUS COMMAND"), followed by <see cref="LoggedIn"/> and
        /// the prompt - <c>0x000C</c>.
        /// </summary>
        ErrorText = 0x000C,
    }
}
