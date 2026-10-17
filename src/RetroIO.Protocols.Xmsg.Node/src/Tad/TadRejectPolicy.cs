//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;

namespace RetroIO.Protocols.Xmsg.Node.Tad
{
    /// <summary>
    /// Decides whether an incoming TAD message is one we can act on, or one that must be answered
    /// with a <see cref="TadOp.Reje"/>.
    /// </summary>
    /// <remarks>
    /// <para><b>Why a real TAD rejects, and why we must too</b></para>
    /// <para>
    /// The version J driver never ignores a message it does not understand. Its normal-priority walk
    /// (the <c>NXMES</c> loop in <c>SINTRAN/NPL-SOURCE-2/NPL-CLEAN/20-COS-TAD-POF-CODE.NPL</c>, and
    /// the same ladder again in <c>CTRMES</c>) accepts exactly five types and falls through to
    /// <c>CALL REJECT</c> for everything else, and <c>ESCDIS</c> converts an unrecognised
    /// high-priority head into a reject as well. Staying silent instead leaves a real peer waiting on
    /// a response that will never come, until its own timeout.
    /// </para>
    /// <para><b>What we do NOT copy, and why - read before "fixing" this to match J</b></para>
    /// <para>
    /// The J accept set is only BDAT, TMOD, TTYP, DESC and DUMM for normal priority, plus the
    /// prebuilt high-priority heads DUMM, ESCA, RLOC, DCON, CERS, RECO, NWRE, ISRS, ERRS and TREP.
    /// Copying that list verbatim would make us reject <see cref="TadOp.Opsv"/>, which real clients
    /// send in their terminal-setup burst on every capture we hold, against Release L machines that
    /// plainly accept it. So J's list is evidence about J, not a specification for the L peers we
    /// talk to.
    /// </para>
    /// <para>
    /// We therefore reject on a weaker, safer rule: an opcode we cannot even NAME. Everything in
    /// <see cref="TadOp"/> is a message whose existence we have evidence for, so it is passed
    /// through to the handlers whether or not this particular endpoint acts on it. That is strictly
    /// more forgiving than a real TAD and strictly better than the silence we had before.
    /// </para>
    /// </remarks>
    public static class TadRejectPolicy
    {
        /// <summary>
        /// Returns true when the opcode is one <see cref="TadOp"/> names.
        /// </summary>
        /// <param name="opcode">
        /// The opcode byte from the head of a TAD message.
        /// </param>
        /// <returns>
        /// True when the byte matches a defined <see cref="TadOp"/> member.
        /// </returns>
        public static bool IsKnownOpcode(byte opcode)
        {
            // Enum.IsDefined boxes and allocates on some runtimes, and this sits on the receive path,
            // so the membership test is spelled out. Keep it in step with TadOp.
            switch ((TadOp)opcode)
            {
                case TadOp.Bdat:
                case TadOp.Rfi:
                case TadOp.Eckm:
                case TadOp.Bmmx:
                case TadOp.Esca:
                case TadOp.Dcon:
                case TadOp.Lun:
                case TadOp.Tmod:
                case TadOp.Ttyp:
                case TadOp.Cesc:
                case TadOp.Desc:
                case TadOp.Sycn:
                case TadOp.Uscn:
                case TadOp.Fbsi:
                case TadOp.Rese:
                case TadOp.Reco:
                case TadOp.Dumm:
                case TadOp.Opsv:
                case TadOp.Esrs:
                case TadOp.Cers:
                case TadOp.Isrq:
                case TadOp.Isrs:
                case TadOp.Nowt:
                case TadOp.Tnow:
                case TadOp.Nwre:
                case TadOp.Rloc:
                case TadOp.Edrs:
                case TadOp.Trep:
                case TadOp.Umod:
                case TadOp.Mod8:
                case TadOp.Cpco:
                case TadOp.Errs:
                case TadOp.Reje:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// Returns true when the version J TAD driver would have acted on this opcode arriving in a
        /// normal-priority buffer.
        /// </summary>
        /// <remarks>
        /// Recorded for reference and for tests; it is NOT the rule we enforce, for the reason set out
        /// on the class. The five types are the ones <c>NXMES</c> and <c>CTRMES</c> test for before
        /// falling through to <c>REJECT</c>.
        /// </remarks>
        /// <param name="opcode">
        /// The opcode byte.
        /// </param>
        /// <returns>
        /// True for BDAT, TMOD, TTYP, DESC and DUMM; false for anything else.
        /// </returns>
        public static bool IsAcceptedByVersionJNormalPriority(byte opcode)
        {
            switch ((TadOp)opcode)
            {
                case TadOp.Bdat:
                case TadOp.Tmod:
                case TadOp.Ttyp:
                case TadOp.Desc:
                case TadOp.Dumm:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// Builds the three-byte reject chain for an offending opcode.
        /// </summary>
        /// <remarks>
        /// <c>FE 01 opcode</c>, exactly as <c>REJECT</c> writes it. Kept here so a caller that has only
        /// the offending byte does not have to know the framing.
        /// </remarks>
        /// <param name="rejectedOpcode">
        /// The opcode being rejected.
        /// </param>
        /// <returns>
        /// The TAD payload bytes of the reject message.
        /// </returns>
        public static byte[] BuildReject(byte rejectedOpcode)
        {
            return new TadMessageBuilder().Reje(rejectedOpcode).Build();
        }

        /// <summary>
        /// Reads the offending opcode out of a reject message body.
        /// </summary>
        /// <param name="body">
        /// The TAD message body, starting at the <c>0xFE</c> opcode byte.
        /// </param>
        /// <param name="rejectedOpcode">
        /// Receives the rejected opcode when the body is a well-formed reject.
        /// </param>
        /// <returns>
        /// True when <paramref name="body"/> is a REJE carrying one data byte.
        /// </returns>
        public static bool TryReadReject(ReadOnlySpan<byte> body, out byte rejectedOpcode)
        {
            rejectedOpcode = 0;
            if (body.Length < 3 || body[0] != (byte)TadOp.Reje || body[1] != 1)
            {
                return false;
            }

            rejectedOpcode = body[2];
            return true;
        }
    }
}
