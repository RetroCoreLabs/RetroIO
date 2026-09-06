namespace RetroIO.Protocols.X25.PacketLayer
{
    /// <summary>
    /// Diagnostic codes from ITU-T X.25 (10/96) Annex E, Table E.1 - the values this engine puts in
    /// the diagnostic field of the Reset, Clear, Restart and Diagnostic packets it originates.
    /// </summary>
    /// <remarks>
    /// Only the codes the engine actually emits are listed; the table has many more. Text:
    /// <c>E:\Dev\RCL\Specs\X25\T-REC-X.25-199610-I!!PDF-E.txt</c>, "Coding of X.25 network
    /// generated diagnostic fields".
    /// </remarks>
    public static class X25Diagnostic
    {
        /// <summary>0 - No additional information.</summary>
        public const byte None = 0;

        /// <summary>1 - Invalid P(S): a duplicate or a gap in the send sequence numbering (4.4.1.3).</summary>
        public const byte InvalidPS = 1;

        /// <summary>2 - Invalid P(R): acknowledges a packet that was never sent.</summary>
        public const byte InvalidPR = 2;

        /// <summary>16 - Packet type invalid (the general code; 17-29 name the state).</summary>
        public const byte PacketTypeInvalid = 16;

        /// <summary>21 - Reject not subscribed to: a REJ arrived and packet retransmission is not supported.</summary>
        public const byte RejectNotSubscribed = 21;

        /// <summary>
        /// 0 with a locally meaningful name: the cause given to the application whose outgoing call
        /// lost a call collision at the DCE (X.25 4.1.6). No Annex E code exists for it because
        /// nothing crosses the interface - the cancelled call was never on the wire as a Clear.
        /// </summary>
        public const byte CallCollision = 0;

        /// <summary>33 - Packet not allowed (the general code; 34-45 say why).</summary>
        public const byte PacketNotAllowed = 33;

        /// <summary>36 - Packet on unassigned logical channel.</summary>
        public const byte UnassignedLogicalChannel = 36;

        /// <summary>38 - Packet too short.</summary>
        public const byte PacketTooShort = 38;

        /// <summary>39 - Packet too long.</summary>
        public const byte PacketTooLong = 39;
    }

    /// <summary>
    /// Cause codes this engine originates, from X.25 Tables 5-7 (clearing), 5-8 (resetting) and 5-9
    /// (restarting).
    /// </summary>
    public static class X25Cause
    {
        /// <summary>00000000 - DTE originated. The only cause a DTE may send without bit 8 set (Table 5-7/5-8 note a).</summary>
        public const byte DteOriginated = 0x00;

        /// <summary>00000101 - Local procedure error (Tables 5-7 and 5-8).</summary>
        public const byte LocalProcedureError = 0x05;

        /// <summary>00001001 - Out of order (Table 5-7).</summary>
        public const byte OutOfOrder = 0x09;

        /// <summary>00000011 - Invalid facility request (Table 5-7).</summary>
        public const byte InvalidFacilityRequest = 0x03;

        /// <summary>00001101 - Not obtainable (Table 5-7).</summary>
        public const byte NotObtainable = 0x0D;
    }
}
