# RetroIO.Protocols.Telnet

The one telnet byte layer for RetroCore Labs code. It turns a received byte stream into data,
commands, option negotiation, subnegotiation and end-of-record marks, escapes data for sending,
and negotiates options by the RFC 1143 "Q method", which cannot loop.

It owns no socket and no thread. Feed it bytes, give it a place to write bytes. That makes it
usable over TCP, over TLS (planned: any `Stream`), inside a test, or inside an emulated machine.

## Parts

| Type | What it does |
|---|---|
| `TelnetReader` | Byte-by-byte parser. Survives any split of the stream across reads. Undoubles IAC IAC. Reports to an `ITelnetReaderHandler`. |
| `TelnetEncoder` | Writes escaped data, IAC commands, IAC WILL/WONT/DO/DONT and IAC SB ... IAC SE into an `IBufferWriter<byte>`. |
| `TelnetOptionNegotiator` | RFC 1143 Q method for every option, both sides, with queued requests. An `ITelnetOptionPolicy` says what to accept. |
| `TelnetCommand`, `TelnetOption` | The byte values, each cited to its RFC. |

## Specifications

RFC 854, 855, 856, 885, 1091 and 1143. Copies are kept in the RetroTerm repository under
`docs/spec/IBM3270/RFC/`.

## Not done here, on purpose

- The NVT carriage-return rules (CR NUL, CR LF). They apply to text in non-binary mode and belong
  to the caller.
- TLS. Planned as a wrapper over the transport, for client and server, at low priority.

## Open questions

- Inside a subnegotiation, IAC followed by a byte other than SE or IAC is not settled by RFC 854 or
  RFC 855 as read. `TelnetReader` ends the subnegotiation there and treats the byte as a new
  command.

## Who uses it

New: RetroIO.Protocols.Tn3270. To be moved onto it, one at a time with their own tests: RetroTerm's
telnet client and telnet server, and RetroIO's X.25 telnet PAD and switch command line.
