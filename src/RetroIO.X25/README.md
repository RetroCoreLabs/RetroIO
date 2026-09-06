# RetroIO.X25

The one X.25 packet layer for every RetroIO stack: the desktop runtime, `X25Client`,
the switch and the XOT links all build on this project and nothing else carries
X.25 packet-layer state.

Zero dependencies. Namespaces are unchanged from where the code came from
(`RetroIO.Protocols.X25.Models` for the stateless codec, `RetroIO.Protocols.X25.PacketLayer`
for the engine) so consumers did not move.

Contents:

- `Models/` - `X25PacketBuilder`, `X25PacketParser`, `X25Packet`, `X25PacketTypeIdentifier`,
  `X25FlowControlFacilities`: stateless encode and decode.
- `Layer/` - `X25Layer` (the virtual-circuit state machine), `X25Vc`, `X25Config`,
  `X25VcState`, `X25LayerBase`, and the sink/event seam in `X25LayerSeam`.

Specification text: `E:\Dev\RCL\Specs\X25` (ITU-T X.25 10/96, X.3, X.28, X.29, X.75,
RFC 1613, RFC 1662). Conformance vectors shared with the C emulator:
`tests/x25-vectors/x25-vectors.json`.

Created 2026-09-06 as Phase B1 of `Migration/X25 one-stack plan.md`. The files were moved
here unchanged; the rules the engine still has to grow into are listed there (R1-R21) and
pinned by `X25PacketLayerRuleTests`.
