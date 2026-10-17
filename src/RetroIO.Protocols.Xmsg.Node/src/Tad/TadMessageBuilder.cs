//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;
using System.Text;

namespace RetroIO.Protocols.Xmsg.Node.Tad
{
    /// <summary>
    /// A typed, fluent builder for a TAD message chain: one method per message type, each encoding
    /// its fields correctly and keeping the chain word-aligned. Produces the payload that follows the
    /// XMSG sub-header. Replaces hand-built <c>byte[]</c> / hex / string-concatenated chains.
    /// </summary>
    /// <remarks>
    /// <para><b>Word alignment (spec TAD-Message-Formats.md section 1)</b></para>
    /// Each TAD message MUST start on an even (word) offset because the ND reader (<c>GETMES</c>) is
    /// word-oriented. This builder inserts a <c>0x00</c> pad automatically before a message when the
    /// chain length is odd. Omitting the pad is what made odd-length terminal output (for example the
    /// menu and the Echo reply) hang: the trailing <see cref="Rfi"/> landed on an odd byte, the host
    /// mis-read it, the input credit was lost, and the session stalled to its idle timeout.
    /// <para><b>Field encoding</b></para>
    /// Multi-byte fields are big-endian (for example the 16-bit <see cref="Sycn"/> state). Terminal
    /// text is 7-bit ASCII (host output direction). Every message is <c>opcode(1) count(1) data</c>.
    /// </remarks>
    public sealed class TadMessageBuilder
    {
        private readonly List<byte> _buffer;

        /// <summary>
        /// Initialises an empty builder.
        /// </summary>
        public TadMessageBuilder()
        {
            _buffer = new List<byte>(64);
        }

        /// <summary>
        /// Appends a BDAT terminal-data message carrying raw bytes.
        /// </summary>
        /// <param name="data">
        /// The terminal data bytes (at most 255).
        /// </param>
        /// <returns>
        /// This builder, for chaining.
        /// </returns>
        public TadMessageBuilder Bdat(ReadOnlySpan<byte> data)
        {
            return Append(TadOp.Bdat, data);
        }

        /// <summary>
        /// Appends a BDAT terminal-data message carrying 7-bit ASCII text (host output direction).
        /// </summary>
        /// <param name="text">
        /// The terminal text to send (at most 255 bytes of ASCII).
        /// </param>
        /// <returns>
        /// This builder, for chaining.
        /// </returns>
        public TadMessageBuilder BdatText(string text)
        {
            byte[] ascii = Encoding.ASCII.GetBytes(text ?? string.Empty);
            return Append(TadOp.Bdat, ascii);
        }

        /// <summary>
        /// Appends an RFI (ready-for-input) flow-control credit - the message that lets the peer send
        /// the next input line. Every host frame that expects input MUST end with one.
        /// </summary>
        /// <returns>
        /// This builder, for chaining.
        /// </returns>
        public TadMessageBuilder Rfi()
        {
            return Append(TadOp.Rfi, ReadOnlySpan<byte>.Empty);
        }

        /// <summary>
        /// Appends a SYCN session-sync message carrying a 16-bit login state (big-endian).
        /// </summary>
        /// <param name="state">
        /// The login state to assert.
        /// </param>
        /// <returns>
        /// This builder, for chaining.
        /// </returns>
        public TadMessageBuilder Sycn(SycnState state)
        {
            Span<byte> data = stackalloc byte[2];
            data[0] = (byte)((ushort)state >> 8);
            data[1] = (byte)state;
            return Append(TadOp.Sycn, data);
        }

        /// <summary>
        /// Appends a CESC (escape-control) message carrying the given state.
        /// </summary>
        /// <param name="state">
        /// The escape-control state.
        /// </param>
        /// <returns>
        /// This builder, for chaining.
        /// </returns>
        public TadMessageBuilder Cesc(CescState state)
        {
            return Cesc((byte)state);
        }

        /// <summary>
        /// Appends a CESC (escape-control) message carrying a raw 1-byte state (escape hatch).
        /// </summary>
        /// <param name="state">
        /// The raw escape-control state byte.
        /// </param>
        /// <returns>
        /// This builder, for chaining.
        /// </returns>
        public TadMessageBuilder Cesc(byte state)
        {
            Span<byte> data = stackalloc byte[1];
            data[0] = state;
            return Append(TadOp.Cesc, data);
        }

        /// <summary>
        /// Appends an ECKM (echo-strategy) message carrying the given strategy.
        /// </summary>
        /// <param name="strategy">
        /// The echo strategy (local echo on, no-echo for a password, or teardown).
        /// </param>
        /// <returns>
        /// This builder, for chaining.
        /// </returns>
        public TadMessageBuilder Eckm(EchoStrategy strategy)
        {
            return Eckm((byte)strategy);
        }

        /// <summary>
        /// Appends an ECKM (echo-strategy) message carrying a raw 1-byte strategy code (escape hatch).
        /// </summary>
        /// <param name="strategy">
        /// The raw echo-strategy byte.
        /// </param>
        /// <returns>
        /// This builder, for chaining.
        /// </returns>
        public TadMessageBuilder Eckm(byte strategy)
        {
            Span<byte> data = stackalloc byte[1];
            data[0] = strategy;
            return Append(TadOp.Eckm, data);
        }

        /// <summary>
        /// Appends a BMMX (break-strategy / max-break) message: a 1-byte strategy and a 16-bit
        /// max-break level (big-endian).
        /// </summary>
        /// <param name="strategy">
        /// The break strategy code.
        /// </param>
        /// <param name="maxBreak">
        /// The maximum break level.
        /// </param>
        /// <returns>
        /// This builder, for chaining.
        /// </returns>
        public TadMessageBuilder Bmmx(byte strategy, ushort maxBreak)
        {
            Span<byte> data = stackalloc byte[3];
            data[0] = strategy;
            data[1] = (byte)(maxBreak >> 8);
            data[2] = (byte)maxBreak;
            return Append(TadOp.Bmmx, data);
        }

        /// <summary>
        /// Appends a TMOD (terminal-mode) message carrying a 1-byte flags value.
        /// </summary>
        /// <param name="flags">
        /// The terminal-mode flag bits.
        /// </param>
        /// <returns>
        /// This builder, for chaining.
        /// </returns>
        public TadMessageBuilder Tmod(byte flags)
        {
            Span<byte> data = stackalloc byte[1];
            data[0] = flags;
            return Append(TadOp.Tmod, data);
        }

        /// <summary>
        /// Appends a TTYP (terminal-type) message carrying a 16-bit type id (big-endian).
        /// </summary>
        /// <param name="terminalType">
        /// The terminal-type identifier.
        /// </param>
        /// <returns>
        /// This builder, for chaining.
        /// </returns>
        public TadMessageBuilder Ttyp(ushort terminalType)
        {
            Span<byte> data = stackalloc byte[2];
            data[0] = (byte)(terminalType >> 8);
            data[1] = (byte)terminalType;
            return Append(TadOp.Ttyp, data);
        }

        /// <summary>
        /// Appends a DESC (define-escape-character) message carrying the escape byte.
        /// </summary>
        /// <param name="escapeCharacter">
        /// The escape character.
        /// </param>
        /// <returns>
        /// This builder, for chaining.
        /// </returns>
        public TadMessageBuilder Desc(byte escapeCharacter)
        {
            Span<byte> data = stackalloc byte[1];
            data[0] = escapeCharacter;
            return Append(TadOp.Desc, data);
        }

        /// <summary>
        /// Appends an OPSV (OS / protocol version) handshake message.
        /// </summary>
        /// <param name="osVersion">
        /// The SINTRAN OS version code.
        /// </param>
        /// <param name="osSubVersion">
        /// The OS sub-version.
        /// </param>
        /// <param name="protocolVersion">
        /// The TAD protocol version (gates v4+ features).
        /// </param>
        /// <returns>
        /// This builder, for chaining.
        /// </returns>
        public TadMessageBuilder Opsv(byte osVersion, byte osSubVersion, byte protocolVersion)
        {
            Span<byte> data = stackalloc byte[3];
            data[0] = osVersion;
            data[1] = osSubVersion;
            data[2] = protocolVersion;
            return Append(TadOp.Opsv, data);
        }

        /// <summary>
        /// Appends a DUMM (dummy / filler) message.
        /// </summary>
        /// <returns>
        /// This builder, for chaining.
        /// </returns>
        public TadMessageBuilder Dumm()
        {
            return Append(TadOp.Dumm, ReadOnlySpan<byte>.Empty);
        }

        /// <summary>
        /// Appends a RESE (reset-request) message.
        /// </summary>
        /// <returns>
        /// This builder, for chaining.
        /// </returns>
        public TadMessageBuilder Rese()
        {
            return Append(TadOp.Rese, ReadOnlySpan<byte>.Empty);
        }

        /// <summary>
        /// Appends a RECO (reset-confirm) message.
        /// </summary>
        /// <returns>
        /// This builder, for chaining.
        /// </returns>
        public TadMessageBuilder Reco()
        {
            return Append(TadOp.Reco, ReadOnlySpan<byte>.Empty);
        }

        /// <summary>
        /// Appends a DCON (disconnect indication) message.
        /// </summary>
        /// <returns>
        /// This builder, for chaining.
        /// </returns>
        public TadMessageBuilder Dcon()
        {
            return Append(TadOp.Dcon, ReadOnlySpan<byte>.Empty);
        }

        /// <summary>
        /// Appends a REJE (reject) message carrying the type byte of the message being rejected.
        /// </summary>
        /// <remarks>
        /// Three bytes exactly - <c>FE 01 type</c> - matching <c>REJECT</c> in
        /// <c>SINTRAN/NPL-SOURCE-2/NPL-CLEAN/20-COS-TAD-POF-CODE.NPL</c>, which stores <c>7REJE</c>,
        /// then a count of 1, then <c>CURMES AND 0xFF</c>. When the rejected message was terminal data
        /// the ND driver appends an <see cref="Rfi"/> as well (its <c>SNDREJ</c> entry), so the caller
        /// decides whether to chain one.
        /// </remarks>
        /// <param name="rejectedOpcode">
        /// The opcode byte of the message being rejected.
        /// </param>
        /// <returns>
        /// This builder, for chaining.
        /// </returns>
        public TadMessageBuilder Reje(byte rejectedOpcode)
        {
            Span<byte> data = stackalloc byte[1];
            data[0] = rejectedOpcode;
            return Append(TadOp.Reje, data);
        }

        /// <summary>
        /// Appends an ISRQ (remote ISIZE request) message. Empty I-field.
        /// </summary>
        /// <remarks>
        /// <c>BISIZ</c>/<c>OISIZ</c> in <c>06-COS-TAD-RES-CODE.NPL</c> build it with a requested
        /// I-field size of zero, so the whole message on the wire is <c>22 00</c>.
        /// </remarks>
        /// <returns>
        /// This builder, for chaining.
        /// </returns>
        public TadMessageBuilder Isrq()
        {
            return Append(TadOp.Isrq, ReadOnlySpan<byte>.Empty);
        }

        /// <summary>
        /// Appends an ISRS (remote ISIZE response) message carrying a 16-bit character count
        /// (big-endian).
        /// </summary>
        /// <remarks>
        /// The ND driver reads the count as <c>byte6 * 256 + byte7</c> of the message head, so the two
        /// data bytes are high byte first.
        /// </remarks>
        /// <param name="characterCount">
        /// The number of characters waiting in the input buffer.
        /// </param>
        /// <returns>
        /// This builder, for chaining.
        /// </returns>
        public TadMessageBuilder Isrs(ushort characterCount)
        {
            Span<byte> data = stackalloc byte[2];
            data[0] = (byte)(characterCount >> 8);
            data[1] = (byte)characterCount;
            return Append(TadOp.Isrs, data);
        }

        /// <summary>
        /// Appends an ESRS (escape response) message - the answer to an <see cref="TadOp.Esca"/> when the
        /// escape function is ENABLED.
        /// </summary>
        /// <returns>
        /// This builder, for chaining.
        /// </returns>
        public TadMessageBuilder Esrs()
        {
            return Append(TadOp.Esrs, ReadOnlySpan<byte>.Empty);
        }

        /// <summary>
        /// Appends an EDRS (escape response, escape disabled) message - the answer to an
        /// <see cref="TadOp.Esca"/> or <see cref="TadOp.Rloc"/> when the escape function is DISABLED.
        /// </summary>
        /// <remarks>
        /// <c>ESCDIS</c> in <c>20-COS-TAD-POF-CODE.NPL</c> answers with the prebuilt <c>EDRSP</c> head
        /// instead of <c>ERESP</c> whenever the datafield's 5IESC (inhibit-escape) flag is set, and
        /// runs no escape handling at all in that case.
        /// </remarks>
        /// <returns>
        /// This builder, for chaining.
        /// </returns>
        public TadMessageBuilder Edrs()
        {
            return Append(TadOp.Edrs, ReadOnlySpan<byte>.Empty);
        }

        /// <summary>
        /// Appends a message with an arbitrary opcode and data (escape hatch for opcodes without a
        /// dedicated typed method yet).
        /// </summary>
        /// <param name="opcode">
        /// The TAD opcode.
        /// </param>
        /// <param name="data">
        /// The message data (at most 255 bytes).
        /// </param>
        /// <returns>
        /// This builder, for chaining.
        /// </returns>
        public TadMessageBuilder Raw(TadOp opcode, ReadOnlySpan<byte> data)
        {
            return Append(opcode, data);
        }

        /// <summary>
        /// Serialises the chain to its on-wire byte form (opcode, count, data per message, with the
        /// word-alignment pads inserted).
        /// </summary>
        /// <returns>
        /// The TAD payload bytes.
        /// </returns>
        public byte[] Build()
        {
            return _buffer.ToArray();
        }

        /// <summary>
        /// Appends one message, inserting a word-alignment pad first when the chain length is odd.
        /// </summary>
        /// <param name="opcode">
        /// The TAD opcode.
        /// </param>
        /// <param name="data">
        /// The message data.
        /// </param>
        /// <returns>
        /// This builder, for chaining.
        /// </returns>
        /// <exception cref="ArgumentOutOfRangeException">
        /// Thrown when <paramref name="data"/> is longer than 255 bytes (the count is a single byte).
        /// </exception>
        private TadMessageBuilder Append(TadOp opcode, ReadOnlySpan<byte> data)
        {
            if (data.Length > 255)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(data), "A single TAD message carries at most 255 data bytes (1-byte count).");
            }

            // Word alignment (spec section 1): a message must start on an even offset. Pad when odd.
            if ((_buffer.Count & 1) == 1)
            {
                _buffer.Add(0x00);
            }

            // ODD-START ALIGNMENT PAD - the mechanism behind what we used to call the "intrinsic 0x00
            // prefix".
            //
            // The bytes below are unchanged; what changed on 2026-08-18 is that we now know WHY they
            // are there, from the version J NPL source. The old comment said the extra 0x00 was
            // "a 16-bit opcode or a flag byte - UNKNOWN which". It is NEITHER.
            //
            // The ND builder has three header routines (SINTRAN/NPL-SOURCE-2/NPL-CLEAN/
            // 20-COS-TAD-POF-CODE.NPL): CREMES and CRHEEV lay the 2-byte header down on an EVEN byte,
            // CRHEOD lays it down on an ODD byte, and CRHEOD does that by writing one 0x00 filler first
            // when it finds itself on an even boundary - its own comment reads
            // "EVEN START, CREATE PAD BYTE".
            //
            // Only messages whose payload is written with WHOLE-WORD stores use CRHEOD: BDECHO (ECKM,
            // the 8-word echo table) and BDBREA (BMMX, the BRKMAX word and the 8-word break table).
            // Pushing the header one byte off makes the word-sized payload that follows it land on a
            // word boundary. The port-assign opcodes 0x07 / 0x0B carry the same extra 0x00 and their
            // payloads are words too (system number, port number) - same shape, but their builder lives
            // in TADADM, which is NOT in this source, so that one stays STRONG rather than PROVEN.
            //
            // Byte-stream messages (BDAT and friends) use CREMES and get no such pad. Together with the
            // even-offset alignment above this reproduces the captured MOTD byte-for-byte.
            if (opcode == TadOp.Eckm || opcode == TadOp.Bmmx || opcode == (TadOp)0x07 || opcode == (TadOp)0x0B)
            {
                _buffer.Add(0x00);
            }

            _buffer.Add((byte)opcode);
            _buffer.Add((byte)data.Length);
            for (int i = 0; i < data.Length; i++)
            {
                _buffer.Add(data[i]);
            }

            return this;
        }
    }
}
