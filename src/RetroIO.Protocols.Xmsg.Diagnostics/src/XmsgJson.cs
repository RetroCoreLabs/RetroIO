//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

using RetroIO.Protocols.Xmsg.SubProtocol;

namespace RetroIO.Protocols.Xmsg.Diagnostics
{
    /// <summary>
    /// Round-trippable JSON serialisation for <see cref="XmsgFrame"/>.
    /// </summary>
    /// <remarks>
    /// <para><b>Round-trip guarantee</b></para>
    /// <see cref="ToJson"/> emits both a structured, human-readable view and - for frames
    /// decoded from a capture - a <c>RawHex</c> copy of the original bytes.
    /// <see cref="FromJson"/> rebuilds a frame that re-serialises to the identical byte
    /// array:
    ///  - when <c>RawHex</c> is present it re-parses those exact bytes (captured frames).
    ///  - otherwise it rebuilds from the structured fields (frames built from scratch).
    /// Enums serialise by name; byte blobs are hex strings.
    /// </remarks>
    public static class XmsgJson
    {
        private static readonly JsonSerializerOptions s_options = CreateOptions();

        /// <summary>
        /// Serialises a frame to an indented JSON string.
        /// </summary>
        /// <param name="frame">
        /// The frame to serialise.
        /// </param>
        /// <returns>
        /// The JSON text.
        /// </returns>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="frame"/> is null.
        /// </exception>
        public static string ToJson(XmsgFrame frame)
        {
            if (frame == null)
            {
                throw new ArgumentNullException(nameof(frame));
            }

            XmsgFrameDto dto = ToDto(frame);
            return JsonSerializer.Serialize(dto, s_options);
        }

        /// <summary>
        /// Deserialises a frame from a JSON string produced by <see cref="ToJson"/>.
        /// </summary>
        /// <param name="json">
        /// The JSON text.
        /// </param>
        /// <returns>
        /// The reconstructed frame.
        /// </returns>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="json"/> is null.
        /// </exception>
        /// <exception cref="FormatException">
        /// Thrown when the JSON is not a valid frame document.
        /// </exception>
        public static XmsgFrame FromJson(string json)
        {
            if (json == null)
            {
                throw new ArgumentNullException(nameof(json));
            }

            XmsgFrameDto? dto = JsonSerializer.Deserialize<XmsgFrameDto>(json, s_options);
            if (dto == null)
            {
                throw new FormatException("JSON did not deserialise to a frame.");
            }

            return FromDto(dto);
        }

        /// <summary>
        /// Builds the DTO view of a frame.
        /// </summary>
        /// <param name="frame">
        /// The source frame.
        /// </param>
        /// <returns>
        /// The populated DTO.
        /// </returns>
        private static XmsgFrameDto ToDto(XmsgFrame frame)
        {
            XmsgFrameDto dto = new XmsgFrameDto();

            SintranHeader header = frame.Header;
            dto.Header.Marker1 = header.Marker1;
            dto.Header.Marker2 = header.Marker2;
            dto.Header.PacketType = header.PacketType;
            dto.Header.Subtype = header.Subtype;
            dto.Header.DestinationNode = header.DestinationNode;
            dto.Header.SourceNode = header.SourceNode;
            dto.Header.Flags1 = header.Flags1;
            dto.Header.Flags2 = header.Flags2;
            // Historical DTO field name, honest source: offset 12 is the checksum's high byte.
            dto.Header.ProtocolId = (SintranProtocolId)(byte)(header.Checksum >> 8);

            if (frame.SubHeader != null)
            {
                XmsgSubHeader sub = frame.SubHeader;
                // The DTO keeps the historical field names so existing JSON stays readable. They
                // are COMPUTED from the correct model: Counter is the header checksum's low byte,
                // and Pad / UserDataLength are body bytes 2 and 3 (wire 30-31).
                byte[] bodyBytes = frame.GetBodyBytes();
                dto.SubHeader = new XmsgSubHeaderDto
                {
                    Counter = (byte)(header.Checksum & 0x00FF),
                    FrameFlags = sub.FrameFlags,
                    Role = sub.Role,
                    DestinationSystem = sub.DestinationSystem,
                    DestinationPort = sub.DestinationPort,
                    SourceSystem = sub.SourceSystem,
                    SourcePort = sub.SourcePort,
                    ControlService = frame.XmcsmWithBodyWord,
                    Pad = bodyBytes.Length > 2 ? bodyBytes[2] : (byte)0,
                    UserDataLength = bodyBytes.Length > 3 ? bodyBytes[3] : (byte)0,
                };
            }

            if (frame.Body != null)
            {
                XmsgBodyDto body = new XmsgBodyDto
                {
                    Serial = frame.Body.Serial,
                    Service = frame.Body.Service,
                };

                IReadOnlyList<XroutParameter> parameters = frame.Body.Parameters;
                for (int i = 0; i < parameters.Count; i++)
                {
                    XroutParameter p = parameters[i];
                    body.Parameters.Add(new XroutParameterDto
                    {
                        ParameterNumber = p.ParameterNumber,
                        IsString = p.IsString,
                        DataHex = HexBytes.ToHex(p.Data),
                    });
                }

                dto.Body = body;
            }

            if (frame.Tad != null)
            {
                TadChainDto tad = new TadChainDto
                {
                    RemainderHex = HexBytes.ToHex(frame.Tad.Remainder),
                };

                IReadOnlyList<TadMessage> messages = frame.Tad.Messages;
                for (int i = 0; i < messages.Count; i++)
                {
                    TadMessage m = messages[i];
                    tad.Messages.Add(new TadMessageDto
                    {
                        Opcode = m.Opcode,
                        OpcodeName = m.OpcodeName,
                        DeclaredCount = m.DeclaredCount,
                        DataHex = HexBytes.ToHex(m.Data),
                    });
                }

                dto.Tad = tad;
            }

            if (frame.Routing != null)
            {
                dto.Routing = new RoutingCommandDto
                {
                    Command = frame.Routing.Command,
                    CommandName = frame.Routing.CommandName,
                    DataHex = HexBytes.ToHex(frame.Routing.Data),
                };
            }

            if (frame.TrailingBytes != null && frame.TrailingBytes.Length > 0)
            {
                dto.TrailingHex = HexBytes.ToHex(frame.TrailingBytes);
            }

            if (frame.RawBytes != null)
            {
                dto.RawHex = HexBytes.ToHex(frame.RawBytes);
            }

            return dto;
        }

        /// <summary>
        /// Reconstructs a frame from its DTO view.
        /// </summary>
        /// <param name="dto">
        /// The DTO to rebuild from.
        /// </param>
        /// <returns>
        /// The reconstructed frame.
        /// </returns>
        private static XmsgFrame FromDto(XmsgFrameDto dto)
        {
            // Captured frames carry their exact bytes; re-parsing them rebuilds the full
            // structured view AND guarantees a byte-identical re-serialisation.
            if (!string.IsNullOrEmpty(dto.RawHex))
            {
                byte[] raw = HexBytes.FromHex(dto.RawHex);
                return XmsgFrame.Parse(raw);
            }

            // Frames built from scratch: rebuild from the structured fields. No raw bytes,
            // so ToArray re-serialises from this model.
            XmsgFrame frame = new XmsgFrame();

            XmsgHeaderDto h = dto.Header;
            frame.Header = new SintranHeader
            {
                Marker1 = h.Marker1,
                Marker2 = h.Marker2,
                PacketType = h.PacketType,
                Subtype = h.Subtype,
                DestinationNode = h.DestinationNode,
                SourceNode = h.SourceNode,
                Flags1 = h.Flags1,
                Flags2 = h.Flags2,

                // The DTO keeps the historical ProtocolId/Counter field names so existing JSON
                // still loads, but the header stores one thing: the 16-bit checksum at offsets
                // 12-13. ProtocolId is its high byte and Counter its low byte, so both are folded
                // back into Checksum here rather than written through compatibility properties
                // that set one half and leave the other stale.
                Checksum = (ushort)(((ushort)(byte)h.ProtocolId << 8)
                                    | (dto.SubHeader != null ? dto.SubHeader.Counter : (byte)0)),
            };

            if (dto.SubHeader != null)
            {
                XmsgSubHeaderDto s = dto.SubHeader;
                frame.SubHeader = new XmsgSubHeader
                {
                    FrameFlags = s.FrameFlags,
                    Role = s.Role,
                    DestinationSystem = s.DestinationSystem,
                    DestinationPort = s.DestinationPort,
                    SourceSystem = s.SourceSystem,
                    SourcePort = s.SourcePort,
                    Xmcsm = (ushort)(s.ControlService >> 16),
                };

                // Counter went into Header.Checksum above, with ProtocolId as its high byte -
                // the two halves have to be written together or the second write clears the first.
            }

            if (dto.Body != null)
            {
                XroutMessage body = new XroutMessage
                {
                    Serial = dto.Body.Serial,
                    Service = dto.Body.Service,
                };

                for (int i = 0; i < dto.Body.Parameters.Count; i++)
                {
                    XroutParameterDto p = dto.Body.Parameters[i];
                    body.AddParameter(new XroutParameter(p.ParameterNumber, p.IsString, HexBytes.FromHex(p.DataHex)));
                }

                frame.Body = body;
            }

            if (!string.IsNullOrEmpty(dto.TrailingHex))
            {
                frame.TrailingBytes = HexBytes.FromHex(dto.TrailingHex);
            }

            return frame;
        }

        /// <summary>
        /// Creates the shared serialiser options (enum-by-name, indented, skip nulls).
        /// </summary>
        /// <returns>
        /// The configured options.
        /// </returns>
        private static JsonSerializerOptions CreateOptions()
        {
            JsonSerializerOptions options = new JsonSerializerOptions
            {
                WriteIndented = true,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            };
            options.Converters.Add(new JsonStringEnumConverter());
            return options;
        }
    }
}
