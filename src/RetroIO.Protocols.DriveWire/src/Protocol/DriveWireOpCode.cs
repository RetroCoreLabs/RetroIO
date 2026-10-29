//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

namespace RetroIO.Protocols.DriveWire.Protocol;

/// <summary>
/// The operation codes a DriveWire client sends as the first byte of every transaction.
/// </summary>
/// <remarks>
/// <para>
/// Source: "DriveWire Protocol Version 4.0.0" (the DriveWire Specification, Appendix C "Table of all DW op codes"),
/// https://github.com/DrPitre/DriveWire/blob/main/DriveWire%20Specification.md. The same values are in the NitrOS-9
/// client definitions, defs/drivewire.d ("Opcodes", "Printer opcodes", "Serial opcodes"), which is what the client drivers
/// rbdw.asm, dwio.asm, boot_dw.asm, clock2_dw.asm, scdwv.asm and scdwp.asm assemble against.
/// </para>
/// <para>
/// Every transaction starts with one of these bytes from the client. The server never starts a transaction on its own
/// (the specification, "Low Level Protocol": "All transactions ... are generally initiated by the CoCo").
/// </para>
/// </remarks>
public static class DriveWireOpCode
{
    /// <summary>
    /// OP_NOP ($00). One byte, no answer. The server ignores it (specification, "The NOP Transaction").
    /// </summary>
    public const byte Nop = 0x00;

    /// <summary>
    /// OP_NAMEOBJ_MOUNT ($01). Followed by a length byte and that many name bytes; the server answers one byte, the drive
    /// number the object is now mounted in, or 0 when it could not be mounted (specification, "Named Objects").
    /// </summary>
    public const byte NamedObjectMount = 0x01;

    /// <summary>
    /// OP_NAMEOBJ_CREATE ($02). Same frame and answer as <see cref="NamedObjectMount"/>; fails when the object exists
    /// (specification, "Named Objects").
    /// </summary>
    public const byte NamedObjectCreate = 0x02;

    /// <summary>
    /// OP_TIME ($23, ASCII '#'). The server answers six bytes: year minus 1900, month, day, hour, minute, second
    /// (specification, "The TIME Transaction"). Sent by the NitrOS-9 clock module clock2_dw.asm (GetTime).
    /// </summary>
    public const byte Time = 0x23;

    /// <summary>
    /// OP_SETTIME ($24, ASCII '$'). Not in the specification. The NitrOS-9 clock module clock2_dw.asm (SetTime) sends it
    /// followed by six bytes from D.Year (year, month, day, hour, minute, second) and reads no answer.
    /// </summary>
    public const byte SetTime = 0x24;

    /// <summary>
    /// OP_SERREAD ($43, ASCII 'C'). The virtual serial poll; the server answers two bytes (specification, "The SERREAD /
    /// POLL Transaction"). Sent by the dwio.asm interrupt poller.
    /// </summary>
    public const byte SerialRead = 0x43;

    /// <summary>
    /// OP_SERGETSTAT ($44, ASCII 'D'). Followed by a channel and a GetStat code; no answer (specification, "The SERGETSTAT
    /// Transaction").
    /// </summary>
    public const byte SerialGetStat = 0x44;

    /// <summary>
    /// OP_SERINIT ($45, ASCII 'E'). Followed by a channel; opens it; no answer (specification, "The SERINIT Transaction").
    /// </summary>
    public const byte SerialInit = 0x45;

    /// <summary>
    /// OP_PRINTFLUSH ($46, ASCII 'F'). No arguments, no answer; the print buffer is complete (specification, "Print Flush
    /// Transaction").
    /// </summary>
    public const byte PrintFlush = 0x46;

    /// <summary>
    /// OP_GETSTAT ($47, ASCII 'G'). Followed by a drive number and a GetStat code; no answer (specification, "The
    /// GetStat/SetStat Transaction").
    /// </summary>
    public const byte GetStat = 0x47;

    /// <summary>
    /// OP_INIT ($49, ASCII 'I'). One byte, no answer; a client driver has started (specification, "The INIT Transaction").
    /// Sent by rbdw.asm Init.
    /// </summary>
    public const byte Init = 0x49;

    /// <summary>
    /// OP_PRINT ($50, ASCII 'P'). Followed by one data byte for the print buffer; no answer (specification, "Print
    /// Transaction").
    /// </summary>
    public const byte Print = 0x50;

    /// <summary>
    /// OP_READ ($52, ASCII 'R'). The DriveWire 3 sector read: drive and 24-bit LSN follow; the server answers an error byte,
    /// then on success the checksum and the sector (specification, "The Read Transaction").
    /// </summary>
    public const byte Read = 0x52;

    /// <summary>
    /// OP_SETSTAT ($53, ASCII 'S'). Followed by a drive number and a SetStat code; no answer (specification, "The
    /// GetStat/SetStat Transaction").
    /// </summary>
    public const byte SetStat = 0x53;

    /// <summary>
    /// OP_TERM ($54, ASCII 'T'). One byte, no answer; a client driver has stopped (specification, "The TERM Transaction").
    /// Sent by rbdw.asm Term.
    /// </summary>
    public const byte Term = 0x54;

    /// <summary>
    /// OP_WRITE ($57, ASCII 'W'). Drive, 24-bit LSN, 256 data bytes and a 16-bit checksum follow; the server answers one
    /// error byte (specification, "The Write Transaction"). Sent by rbdw.asm Write.
    /// </summary>
    public const byte Write = 0x57;

    /// <summary>
    /// OP_DWINIT ($5A, ASCII 'Z'). Followed by the driver version byte; the server answers its own version byte
    /// (specification, "The DWINIT Transaction"). Sent by dwio.asm Init with version 1.
    /// </summary>
    public const byte DriveWireInit = 0x5A;

    /// <summary>
    /// OP_SERREADM ($63, ASCII 'c'). Followed by a channel and a byte count; the server answers that many bytes from the
    /// channel (specification, "The SERREADM Transaction").
    /// </summary>
    public const byte SerialReadMultiple = 0x63;

    /// <summary>
    /// OP_SERWRITEM ($64, ASCII 'd'). Followed by a channel, a count and that many data bytes; no answer (specification,
    /// "The SERWRITEM Transaction").
    /// </summary>
    public const byte SerialWriteMultiple = 0x64;

    /// <summary>
    /// OP_REREAD ($72, ASCII 'r'). The retry of <see cref="Read"/>, same frame and answer (specification, "The ReRead
    /// Transaction").
    /// </summary>
    public const byte ReRead = 0x72;

    /// <summary>
    /// OP_REWRITE ($77, ASCII 'w'). The retry of <see cref="Write"/>, same frame and answer (specification, "The ReWrite
    /// Transaction"). Sent by rbdw.asm Write after an E$CRC answer.
    /// </summary>
    public const byte ReWrite = 0x77;

    /// <summary>
    /// OP_FASTWRITE base ($80). $80 plus a virtual serial channel number, followed by one data byte; no answer
    /// (specification, "The FASTWRITE Transaction"; Appendix C lists $80 to $8F). Sent by scdwv.asm Write.
    /// </summary>
    public const byte FastWriteBase = 0x80;

    /// <summary>
    /// The last FASTWRITE operation code, $8F (specification, Appendix C, OP_FASTWRITE_P15).
    /// </summary>
    public const byte FastWriteLast = 0x8F;

    /// <summary>
    /// OP_SERWRITE ($C3). Followed by a channel and one data byte; no answer (specification, "The SERWRITE Transaction").
    /// </summary>
    public const byte SerialWrite = 0xC3;

    /// <summary>
    /// OP_SERSETSTAT ($C4). Followed by a channel and a SetStat code, and 26 more bytes when the code is SS.ComSt ($28); no
    /// answer (specification, "The SERSETSTAT Transaction").
    /// </summary>
    public const byte SerialSetStat = 0xC4;

    /// <summary>
    /// OP_SERTERM ($C5). Followed by a channel; closes it; no answer (specification, "The SERTERM Transaction").
    /// </summary>
    public const byte SerialTerm = 0xC5;

    /// <summary>
    /// OP_READEX ($D2). The extended sector read used by NitrOS-9: drive and 24-bit LSN follow; the server sends 256 bytes,
    /// the client answers its checksum, and the server answers one error byte (specification, "The Read Extended
    /// Transaction"). Sent by rbdw.asm Read and boot_dw.asm HWRead.
    /// </summary>
    public const byte ReadExtended = 0xD2;

    /// <summary>
    /// OP_REREADEX ($F2). The retry of <see cref="ReadExtended"/>, same frame and answer (specification, "The ReRead
    /// Extended Transaction"). Sent by rbdw.asm and boot_dw.asm after an E$CRC answer.
    /// </summary>
    public const byte ReReadExtended = 0xF2;

    /// <summary>
    /// OP_RESET3 ($F8). One byte, no answer; the client was powered on or reset (specification, "The RESET Transaction").
    /// </summary>
    public const byte Reset3 = 0xF8;

    /// <summary>
    /// OP_RESET2 ($FE). See <see cref="Reset3"/>.
    /// </summary>
    public const byte Reset2 = 0xFE;

    /// <summary>
    /// OP_RESET1 ($FF). See <see cref="Reset3"/>.
    /// </summary>
    public const byte Reset1 = 0xFF;
}
