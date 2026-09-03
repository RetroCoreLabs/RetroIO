# RetroIO.Protocols.Xmsg.Api - the user-friendly XMSG library

`RetroIO.Protocols.Xmsg.Api` models the XMSG **programming interface** as the official manual
describes it, so applications can be written against XMSG concepts (tasks, ports, magic numbers,
message buffers, letters, services) instead of against wire bytes.

**Source of truth:** `../../../Reference-Manuals/ND-60.164.3 EN COSMOS Programmer Guide.md`
Every type below cites the section it came from. Nothing here is inferred from captures; the
capture-derived transport rules live one layer down in `RetroIO.Protocols.Xmsg` and are documented in
`../../DOC/XMSG-PROTOCOL.md`.

## Layering

```
RetroIO.Protocols.Xmsg.Api        <- this project: XF* functions, XROUT services, RR-LIB model   (manual)
RetroIO.Protocols.Xmsg   <- envelope, sequencing, secure ACK, XROUT TLV wire format     (captures)
RetroIO.Protocols.Xmsg.Hdlc / RetroIO.Protocols.Xmsg.Live  <- LAPB and the byte transport
```

Hard rule: this project owns no wire bytes. Anything here that starts computing a Counter, a
channel byte or a Flags1 sequence is a layering leak and belongs in `RetroIO.Protocols.Xmsg`.

## What is here

| Area | Types | Manual |
|---|---|---|
| Status and errors | `XmsgStatus`, `XmsgException` | appendix A section 2, appendix D |
| Addressing | `XmsgMagicNumber`, `XmsgHashedMagicNumber`, `XmsgPortNumber` | section 1.2.3, appendix A section 3.1 |
| Message buffers | `XmsgMessageIdentifier`, `XmsgMessageBuffer` | section 1.2.4, appendix A section 3.2 |
| Results | `XmsgPortStatus`, `XmsgReceiveResult`, `XmsgMessageStatus` | appendix A sections 3.1.3, 3.2.13-3.2.17 |
| Function set | `IXmsgKernel` | appendix A sections 3 and 4 |
| Call options | `XmsgWaitOptions`, `XmsgBufferOptions`, `XmsgSendFlags` | section 1.7.1, appendix A |
| XROUT services | `XroutRequests` (naming, routing, management), `XroutReply` | appendix B |
| Request-response | `RrEvent`, `RrStatus`, `RrError`, `IRrServer`, `IRrClient`, `RrServer`, `RrClient`, `RrEndpointBase` | chapter 4 |
| Kernel implementation | `XmsgKernel`, `IXmsgDatagramSink` | appendix A |
| Node bridge | `XmsgKernelServer` (in `RetroIO.Protocols.Xmsg.Api.Node`) | - |
| XROUT naming | `XroutDirectory` | appendix B sections 3.1-3.4 |

`XmsgMessageBuffer` is a full behavioural implementation, not a data holder: it reproduces the
size/length/displacement triple, the round-odd-displacement-up rule (including the zero
"garbage byte"), and the whole-message-read flag that recycles a received buffer into a reply.

## Deliberate non-decisions

- **The magic-number bit layout is modelled, but carved rather than documented.** The manual never
  publishes the packing - it exposes XFMP2P and XFP2M instead. The layout
  (`system << 16 | port << 7 | random`, 9-bit one-based port, 7-bit random) was disassembled out of
  the XMSG L03 kernel; see `../../DOC/XMSG-MAGIC-NUMBER-LAYOUT-CARVED-2026-07-26.md` for the
  evidence. Application code with a kernel available should still convert through the kernel, which
  additionally validates that the port number names a real port.
- **Driver, privileged and absolute-memory functions are absent** from `IXmsgKernel`. They serve
  drivers and XROUT inside SINTRAN and have no meaning for a task on this side of the wire.

## Working end to end

`XmsgKernel` + `XroutDirectory` + `RrServer` + `RrClient` run a whole request-response session in
process: the client sends a letter to a NAME, the server learns the client's magic number from the
arriving message, accepts, and the two exchange requests and responses before an orderly
disconnect. The connection-port capacity gate (XSCRS plus XSNSP) works too.

One kernel instance is one SYSTEM, not one task - the manual defines a system as "a processing
unit that runs an independent XMSG kernel", so tasks on the same system share one and each owns
the ports it opened. Two instances are two systems and only reach each other through
`IXmsgDatagramSink`.

The request-response WIRE FORMAT is ours, not ND's: the manual documents RR-LIB's calls and events
but not its bytes, and no RR-LIB traffic has been captured. See `RrMessageKind`.

## Not yet implemented

- Registering `XmsgKernelServer` with a live node and running a cross-node session end to end.
  The bridge and both directions of the mapping are tested; what is untested is a real link.
