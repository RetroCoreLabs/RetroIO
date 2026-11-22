# RetroIO.Protocols.Tn3270

The IBM 3270 display station as a library, with no window and no network of its own.

| Type | What it does |
|---|---|
| `Display3270` | The screen: buffer, fields, extended attributes, cursor, keyboard lock. Takes host records (`ProcessOutbound`) and operator keys (`Type`, `Tab`, `Home`, `Reset`, `Attention`), and raises `InboundRecord` with the exact bytes the terminal sends back. |
| `BufferAddress` | 12-bit and 14-bit buffer addresses, and the 6-bit code table (GA23-0059-4 Appendix D). |
| `CodePage037` | EBCDIC code page 037 for display and typing. |
| `Command3270`, `Wcc`, `Order3270`, `FormatControl`, `FieldAttribute`, `Aid` | The byte values, each cited to its page. |

A renderer reads the screen, a test drives it, and a future System/360 or 370 emulator can
attach it directly as a console.

## Specifications

IBM GA23-0059-4 (3270 Data Stream Programmer's Reference), GA27-2837-9 (Character Set Reference),
GA27-2890-4 (3278 Operator's Guide), RFC 1576 and RFC 2355. Copies and notes are in the RetroTerm
repository under `docs/spec/IBM3270/`. x3270 and IBM's tnz are used as oracles only. Nothing is
copied from them.

## Not built yet

Each of these raises `ProtocolError` when the host uses it. None is skipped silently.

- Write Structured Field, so Query Reply and DFT-mode IND$FILE do not work yet
- the TN3270 client session over RetroIO.Protocols.Telnet
- TN3270E
- partitions and 16-bit addresses
- insert mode, Erase EOF, Erase Input, Dup and Field Mark keys
- field validation, the selector pen, and code pages other than 037
