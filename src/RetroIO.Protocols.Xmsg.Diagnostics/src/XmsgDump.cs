//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;
using System.Text;

using RetroIO.Protocols.Xmsg.SubProtocol;

namespace RetroIO.Protocols.Xmsg.Diagnostics
{
    /// <summary>
    /// Renders an <see cref="XmsgFrame"/> as a stable, human-readable multi-line text dump.
    /// </summary>
    /// <remarks>
    /// The output is intended for eyeballing a capture: header fields with enum names, the
    /// XMSG sub-header, the decoded trailer (XROUT letter, TAD chain or ROUTING command),
    /// and hex of any raw / trailing bytes. It is a presentation view only and does not
    /// affect serialisation.
    /// </remarks>
    public static class XmsgDump
    {
        /// <summary>
        /// Formats a frame as a multi-line text block.
        /// </summary>
        /// <param name="frame">
        /// The frame to render.
        /// </param>
        /// <returns>
        /// The formatted text.
        /// </returns>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="frame"/> is null.
        /// </exception>
        public static string ToText(XmsgFrame frame)
        {
            if (frame == null)
            {
                throw new ArgumentNullException(nameof(frame));
            }

            StringBuilder sb = new StringBuilder();

            SintranHeader h = frame.Header;
            sb.Append("SINTRAN header: ")
              .Append(h.SourceNode).Append(" -> ").Append(h.DestinationNode)
              .Append("  subtype=").Append(h.Subtype)
              // Offsets 12-13 are the header checksum, not a protocol selector. The dump used to
              // print "proto=Tad (0x0E)" from the high byte alone, which invited the reader to
              // believe in a sub-protocol field that is not on the wire.
              .Append("  checksum=0x").Append(h.Checksum.ToString("X4"))
              .Append('\n');
            sb.Append("  markers=0x").Append(h.Marker1.ToString("X2"))
              .Append(" 0x").Append(h.Marker2.ToString("X2"))
              .Append("  packetType=0x").Append(h.PacketType.ToString("X2"))
              .Append("  flags1=0x").Append(h.Flags1.ToString("X4"))
              .Append("  flags2=0x").Append(h.Flags2.ToString("X4"))
              .Append('\n');

            if (frame.SubHeader != null)
            {
                XmsgSubHeader s = frame.SubHeader;
                sb.Append("XMSG sub-header: frameFlags=0x").Append(s.FrameFlags.ToString("X2"))
                  .Append("  role=0x").Append(s.Role.ToString("X2"))
                  .Append('\n');
                sb.Append("  endpoints: src ").Append(s.SourceSystem).Append(':').Append(s.SourcePort)
                  .Append(" -> dst ").Append(s.DestinationSystem).Append(':').Append(s.DestinationPort)
                  .Append('\n');
                // XMCSM is ONE word at wire 26-27. The 32-bit form is printed beside it because
                // every historical note and constant is written that way; it is XMCSM in the high
                // half and the first MESSAGE BODY word (wire 28-29) in the low half.
                sb.Append("  XMCSM=0x").Append(s.Xmcsm.ToString("X4"))
                  .Append("  (legacy 32-bit view 0x").Append(frame.XmcsmWithBodyWord.ToString("X8")).Append(')')
                  .Append('\n');
            }

            if (frame.Body != null)
            {
                AppendBody(sb, frame.Body);
            }

            if (frame.Tad != null)
            {
                AppendTad(sb, frame.Tad);
            }

            if (frame.Routing != null)
            {
                sb.Append("ROUTING command: 0x").Append(frame.Routing.Command.ToString("X2"))
                  .Append(" [").Append(frame.Routing.CommandName).Append(']');
                if (frame.Routing.Data.Length > 0)
                {
                    sb.Append("  data=").Append(HexBytes.ToHex(frame.Routing.Data));
                }

                sb.Append('\n');
            }

            if (frame.TrailingBytes != null && frame.TrailingBytes.Length > 0)
            {
                sb.Append("Trailing bytes: ").Append(HexBytes.ToHex(frame.TrailingBytes)).Append('\n');
            }

            if (frame.RawBytes != null)
            {
                sb.Append("Raw (").Append(frame.RawBytes.Length).Append(" bytes): ")
                  .Append(HexBytes.ToHex(frame.RawBytes)).Append('\n');
            }

            return sb.ToString();
        }

        /// <summary>
        /// Appends the XROUT letter body to the dump.
        /// </summary>
        /// <param name="sb">
        /// The output builder.
        /// </param>
        /// <param name="body">
        /// The XROUT letter to render.
        /// </param>
        private static void AppendBody(StringBuilder sb, XroutMessage body)
        {
            // Serial and service are NOT on the wire - they belong to the message-buffer form of
            // an XROUT message, and the service a frame acts on is its XMCSM word (printed with
            // the sub-header above). Print them only when something actually set them, so the dump
            // stops implying the wire carries a header it does not. See XroutMessageFraming.
            if (body.Serial != 0 || body.Service != 0)
            {
                sb.Append("XROUT letter [buffer header: serial=").Append(body.Serial)
                  .Append(" service/status=0x").Append(body.Service.ToString("X2")).Append(']');
            }
            else
            {
                sb.Append("XROUT letter:");
            }

            sb.Append("  params=").Append(body.Parameters.Count)
              .Append('\n');

            IReadOnlyList<XroutParameter> parameters = body.Parameters;
            for (int i = 0; i < parameters.Count; i++)
            {
                XroutParameter p = parameters[i];
                sb.Append("  param #").Append(p.ParameterNumber)
                  .Append(p.IsString ? " (string)" : " (int)")
                  .Append(" = ").Append(HexBytes.ToHex(p.Data));
                if (p.IsString)
                {
                    sb.Append("  \"").Append(Printable(p.Data)).Append('"');
                }

                sb.Append('\n');
            }
        }

        /// <summary>
        /// Appends a decoded TAD message chain to the dump.
        /// </summary>
        /// <param name="sb">
        /// The output builder.
        /// </param>
        /// <param name="tad">
        /// The TAD chain to render.
        /// </param>
        private static void AppendTad(StringBuilder sb, TadChain tad)
        {
            sb.Append("TAD chain: ").Append(tad.Messages.Count).Append(" message(s)").Append('\n');

            IReadOnlyList<TadMessage> messages = tad.Messages;
            for (int i = 0; i < messages.Count; i++)
            {
                TadMessage m = messages[i];
                sb.Append("  ").Append(m.OpcodeName)
                  .Append(" (0x").Append(m.Opcode.ToString("X2")).Append(')')
                  .Append(" count=").Append(m.DeclaredCount);
                if (m.Data.Length > 0)
                {
                    sb.Append("  data=").Append(HexBytes.ToHex(m.Data));
                }

                if (m.IsTruncated)
                {
                    sb.Append("  [truncated]");
                }

                sb.Append('\n');
            }

            if (tad.Remainder.Length > 0)
            {
                sb.Append("  remainder=").Append(HexBytes.ToHex(tad.Remainder)).Append('\n');
            }
        }

        /// <summary>
        /// Maps a byte blob to a printable-ASCII preview, replacing control bytes with dots.
        /// </summary>
        /// <param name="data">
        /// The bytes to preview.
        /// </param>
        /// <returns>
        /// The printable preview string.
        /// </returns>
        private static string Printable(byte[] data)
        {
            StringBuilder sb = new StringBuilder(data.Length);
            for (int i = 0; i < data.Length; i++)
            {
                byte b = data[i];
                if (b >= 0x20 && b < 0x7F)
                {
                    sb.Append((char)b);
                }
                else
                {
                    sb.Append('.');
                }
            }

            return sb.ToString();
        }
    }
}
