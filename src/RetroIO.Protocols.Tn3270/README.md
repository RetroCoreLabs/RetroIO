# RetroIO.Protocols.Tn3270

The IBM 3270 display station as a library, with no window and no network of its own.

| Type | What it does |
|---|---|
| `Display3270` | The screen: buffer, fields, extended attributes, cursor, keyboard lock. Takes host records (`ProcessOutbound`) and operator keys (`Type`, `Tab`, `BackTab`, `Home`, `NewLine`, arrows, `ToggleInsert`, `Delete`, `EraseEndOfField`, `EraseInput`, `Dup`, `FieldMark`, `Reset`, `Attention`), and raises `InboundRecord` with the exact bytes the terminal sends back. |
| `BufferAddress` | 12-bit and 14-bit buffer addresses, and the 6-bit code table (GA23-0059-4 Appendix D). |
| `CodePage` | EBCDIC code pages 037, 273, 277, 278, 280, 284, 285, 297, 500, 871, 1047 and 1140-1149, plus the Graphic Escape set 310. Made from IBM's ICU tables and checked against .NET's own tables. |
| `IndFileTransfer` | IND$FILE file transfer in DFT mode (the X'D0' structured fields), download and upload. |
| `Tn3270EHeader` | The five-byte TN3270E record header and the RESPONSE message (RFC 2355). |
| `Command3270`, `Wcc`, `Order3270`, `FormatControl`, `FieldAttribute`, `Aid`, `StructuredFieldId`, `QueryCode`, `FieldValidation`, `FieldOutlining` | The byte values, each cited to its page. |

Write Structured Field handles Read Partition (Query, Query List, and the reads of partition 0),
Erase/Reset, Set Reply Mode (field, extended field and character mode) and Outbound 3270DS. The
query reply reports Usable Area, Character Sets, Color, Highlight, Reply Modes, Implicit
Partition, Field Validation (mandatory fill and mandatory entry), and DDM while file transfer
is on. The key rules follow the 3274 guide GA23-0061-2, Appendix C.

A renderer reads the screen, a test drives it, and a future System/360 or 370 emulator can
attach it directly as a console.

## Specifications

IBM GA23-0059-4 (3270 Data Stream Programmer's Reference), GA27-2837-9 (Character Set Reference),
GA27-2890-4 (3278 Operator's Guide), RFC 1576 and RFC 2355. Copies and notes are in the RetroTerm
repository under `docs/spec/IBM3270/`. x3270 and IBM's tnz are used as oracles only. Nothing is
copied from them.

## Not built yet

Each of these raises `ProtocolError` when the host uses it. None is skipped silently.

- the TN3270 and TN3270E client session over RetroIO.Protocols.Telnet (it lives in RetroTerm)
- partitions and 16-bit addresses
- trigger fields (field validation bit 7), which need the keystroke queue; a host that sets
  the bit gets a protocol error, and the query reply says trigger is not supported
- the selector pen and Cursor Select
- IND$FILE in CUT mode (screen frames), for a host that does not take DFT
- every structured field not named above, and BSC Copy
