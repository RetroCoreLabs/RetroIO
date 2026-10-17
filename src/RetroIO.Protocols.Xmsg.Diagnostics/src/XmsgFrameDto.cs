//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System.Collections.Generic;

namespace RetroIO.Protocols.Xmsg.Diagnostics
{
    /// <summary>
    /// Data-transfer object mirroring an <see cref="XmsgFrame"/> for JSON serialisation.
    /// </summary>
    /// <remarks>
    /// The structured members exist for human inspection; <see cref="RawHex"/> carries an
    /// exact copy of the frame's information-field bytes so that a captured frame is
    /// reconstructed byte-for-byte. Byte blobs are hex strings; enums serialise by name.
    /// </remarks>
    public sealed class XmsgFrameDto
    {
        /// <summary>
        /// Gets or sets the SINTRAN header.
        /// </summary>
        public XmsgHeaderDto Header { get; set; } = new XmsgHeaderDto();

        /// <summary>
        /// Gets or sets the XMSG sub-header, or <c>null</c> when the frame carries none.
        /// </summary>
        public XmsgSubHeaderDto? SubHeader { get; set; }

        /// <summary>
        /// Gets or sets the XROUT letter body, or <c>null</c> when the frame carries none.
        /// </summary>
        public XmsgBodyDto? Body { get; set; }

        /// <summary>
        /// Gets or sets the decoded TAD message chain, or <c>null</c> when not a TAD trailer.
        /// </summary>
        public TadChainDto? Tad { get; set; }

        /// <summary>
        /// Gets or sets the decoded ROUTING command, or <c>null</c> when not present.
        /// </summary>
        public RoutingCommandDto? Routing { get; set; }

        /// <summary>
        /// Gets or sets the trailing control bytes as a hex string, or <c>null</c>/empty.
        /// </summary>
        public string? TrailingHex { get; set; }

        /// <summary>
        /// Gets or sets the exact original information-field bytes as a hex string, or
        /// <c>null</c> for a frame built from scratch.
        /// </summary>
        public string? RawHex { get; set; }
    }

    /// <summary>
    /// JSON view of a <see cref="SintranHeader"/>.
    /// </summary>
    public sealed class XmsgHeaderDto
    {
        /// <summary>
        /// Gets or sets the Marker 1 byte.
        /// </summary>
        public byte Marker1 { get; set; }

        /// <summary>
        /// Gets or sets the Marker 2 byte.
        /// </summary>
        public byte Marker2 { get; set; }

        /// <summary>
        /// Gets or sets the Packet Type byte.
        /// </summary>
        public byte PacketType { get; set; }

        /// <summary>
        /// Gets or sets the packet subtype.
        /// </summary>
        public SintranPacketSubtype Subtype { get; set; }

        /// <summary>
        /// Gets or sets the destination node number.
        /// </summary>
        public ushort DestinationNode { get; set; }

        /// <summary>
        /// Gets or sets the source node number.
        /// </summary>
        public ushort SourceNode { get; set; }

        /// <summary>
        /// Gets or sets the Flags 1 word.
        /// </summary>
        public ushort Flags1 { get; set; }

        /// <summary>
        /// Gets or sets the Flags 2 word.
        /// </summary>
        public ushort Flags2 { get; set; }

        /// <summary>
        /// Gets or sets the protocol-id channel tag.
        /// </summary>
        public SintranProtocolId ProtocolId { get; set; }
    }

    /// <summary>
    /// JSON view of an <see cref="XmsgSubHeader"/>.
    /// </summary>
    public sealed class XmsgSubHeaderDto
    {
        /// <summary>
        /// Gets or sets the per-direction counter.
        /// </summary>
        public byte Counter { get; set; }

        /// <summary>
        /// Gets or sets the frame-flags byte.
        /// </summary>
        public byte FrameFlags { get; set; }

        /// <summary>
        /// Gets or sets the role byte.
        /// </summary>
        public byte Role { get; set; }

        /// <summary>
        /// Gets or sets the XMDSY destination system.
        /// </summary>
        public ushort DestinationSystem { get; set; }

        /// <summary>
        /// Gets or sets the XMDPT destination port.
        /// </summary>
        public ushort DestinationPort { get; set; }

        /// <summary>
        /// Gets or sets the XMSSY source system.
        /// </summary>
        public ushort SourceSystem { get; set; }

        /// <summary>
        /// Gets or sets the XMSPT source port.
        /// </summary>
        public ushort SourcePort { get; set; }

        /// <summary>
        /// Gets or sets the XMCSM control/service word.
        /// </summary>
        public uint ControlService { get; set; }

        /// <summary>
        /// Gets or sets the pad byte.
        /// </summary>
        public byte Pad { get; set; }

        /// <summary>
        /// Gets or sets the XMLEN user-data length low byte.
        /// </summary>
        public byte UserDataLength { get; set; }
    }

    /// <summary>
    /// JSON view of an <see cref="XroutMessage"/> letter body.
    /// </summary>
    public sealed class XmsgBodyDto
    {
        /// <summary>
        /// Gets or sets the letter serial number.
        /// </summary>
        public byte Serial { get; set; }

        /// <summary>
        /// Gets or sets the letter service / status byte.
        /// </summary>
        public byte Service { get; set; }

        /// <summary>
        /// Gets or sets the TLV parameter blocks.
        /// </summary>
        public List<XroutParameterDto> Parameters { get; set; } = new List<XroutParameterDto>();
    }

    /// <summary>
    /// JSON view of an <see cref="XroutParameter"/>.
    /// </summary>
    public sealed class XroutParameterDto
    {
        /// <summary>
        /// Gets or sets the one-based parameter number.
        /// </summary>
        public int ParameterNumber { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether this is a string parameter.
        /// </summary>
        public bool IsString { get; set; }

        /// <summary>
        /// Gets or sets the parameter data as a hex string.
        /// </summary>
        public string DataHex { get; set; } = string.Empty;
    }

    /// <summary>
    /// JSON view of a decoded TAD message chain.
    /// </summary>
    public sealed class TadChainDto
    {
        /// <summary>
        /// Gets or sets the decoded messages.
        /// </summary>
        public List<TadMessageDto> Messages { get; set; } = new List<TadMessageDto>();

        /// <summary>
        /// Gets or sets any undecoded trailing bytes as a hex string.
        /// </summary>
        public string? RemainderHex { get; set; }
    }

    /// <summary>
    /// JSON view of a single TAD message.
    /// </summary>
    public sealed class TadMessageDto
    {
        /// <summary>
        /// Gets or sets the opcode byte.
        /// </summary>
        public byte Opcode { get; set; }

        /// <summary>
        /// Gets or sets the opcode mnemonic name.
        /// </summary>
        public string OpcodeName { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the declared data count byte.
        /// </summary>
        public byte DeclaredCount { get; set; }

        /// <summary>
        /// Gets or sets the captured data as a hex string.
        /// </summary>
        public string DataHex { get; set; } = string.Empty;
    }

    /// <summary>
    /// JSON view of a decoded ROUTING command.
    /// </summary>
    public sealed class RoutingCommandDto
    {
        /// <summary>
        /// Gets or sets the routing command byte.
        /// </summary>
        public byte Command { get; set; }

        /// <summary>
        /// Gets or sets the routing command mnemonic name.
        /// </summary>
        public string CommandName { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets any bytes following the command byte as a hex string.
        /// </summary>
        public string DataHex { get; set; } = string.Empty;
    }
}
