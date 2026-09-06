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

        /// <summary>20 - Packet type invalid for state p1 (ready). 21 p2, 22 p3, 23 p4, 24 p5, 25 p6, 26 p7; 27 d1, 28 d2, 29 d3.</summary>
        public const byte PacketTypeInvalidForP1 = 20;

        /// <summary>27 - Packet type invalid for state d1 (flow control ready).</summary>
        public const byte PacketTypeInvalidForD1 = 27;

        /// <summary>21 - Reject not subscribed to: a REJ arrived and packet retransmission is not supported.</summary>
        public const byte RejectNotSubscribed = 21;

        /// <summary>64 - Call set-up or call clearing problem (the general code; 65-73 say which).</summary>
        public const byte CallSetupProblem = 64;

        /// <summary>65 - Facility code not allowed.</summary>
        public const byte FacilityCodeNotAllowed = 65;

        /// <summary>66 - Facility parameter not allowed: a window or packet size outside what the clause or the negotiation permits.</summary>
        public const byte FacilityParameterNotAllowed = 66;

        /// <summary>67 - Invalid called DTE address.</summary>
        public const byte InvalidCalledAddress = 67;

        /// <summary>69 - Invalid facility length.</summary>
        public const byte InvalidFacilityLength = 69;

        /// <summary>72 - Call collision: the diagnostic given to the application whose outgoing call lost a collision at the DCE (X.25 4.1.6).</summary>
        public const byte CallCollision = 72;

        /// <summary>73 - Duplicate facility requested.</summary>
        public const byte DuplicateFacility = 73;

        /// <summary>33 - Packet not allowed (the general code; 34-45 say why).</summary>
        public const byte PacketNotAllowed = 33;

        /// <summary>36 - Packet on unassigned logical channel.</summary>
        public const byte UnassignedLogicalChannel = 36;

        /// <summary>38 - Packet too short.</summary>
        public const byte PacketTooShort = 38;

        /// <summary>39 - Packet too long.</summary>
        public const byte PacketTooLong = 39;

        /// <summary>40 - Invalid general format identifier.</summary>
        public const byte InvalidGfi = 40;

        /// <summary>41 - Restart or registration packet with a nonzero logical channel (bits 4-1 of octet 1 or octet 2 not zero).</summary>
        public const byte RestartWithNonzeroChannel = 41;

        /// <summary>48 - Time expired (the general code; 49-53 say which timer).</summary>
        public const byte TimeExpired = 48;

        /// <summary>49 - Time expired for incoming call: the call was not answered (T21 at a DTE, T11 at a DCE).</summary>
        public const byte TimeExpiredCall = 49;

        /// <summary>50 - Time expired for clear indication: the Clear Request was never confirmed (T23).</summary>
        public const byte TimeExpiredClear = 50;

        /// <summary>51 - Time expired for reset indication: the Reset Request was never confirmed (T22).</summary>
        public const byte TimeExpiredReset = 51;

        /// <summary>52 - Time expired for restart indication: the Restart Request was never confirmed (T20).</summary>
        public const byte TimeExpiredRestart = 52;
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
