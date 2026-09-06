# RetroIO.Lapb

The one LAPB data link layer for every RetroIO link. Zero dependencies.

`LapbLayer` decodes and emits frames, runs the ITU-T X.25 (10/96) clause 2 state machine with its
T1/T3 timers, N2 retries, go-back-N window, reject condition and FRMR reporting, and speaks either
of two dialects, chosen by `LapbOptions.Variant`:

- **`LapbVariant.Nd`** (the default) - the Norsk Data dialect the bench machines speak: address
  `0x01` on link management and `0x09` on data transfer, with `0x80` set when the information field
  has an odd byte count (the ND-100's word DMA needs to know), and the sender's two-octet node
  number carried in SABM, UA, DISC, DM and the supervisory frames.
- **`LapbVariant.Itu`** - address A (`0x03`) on commands from the DCE and responses from the DTE,
  address B (`0x01`) on commands from the DTE and responses from the DCE (2.4.2); no information
  field on anything but I and FRMR (2.3.4); the C/R bit in the FRMR diagnostic (2.3.4.10).
  `LapbOptions.IsDte` says which side this station is.

The engine is driven by its caller: `OnFrameReceived` for each frame, `Tick(now)` for time,
`SendInformation` to send. It starts no threads and owns no clock, so a test can drive a whole
link failure and recovery in microseconds.

`LapbFrame` and `LapbControl` (the frame decode and the control-byte constants) live here too;
the pcap tooling in `RetroIO.Protocols.Xmsg.Hdlc` reads them from here.

## Namespaces

The types keep the namespaces they had before the move (`RetroIO.Protocols.Xmsg.Live` for the
engine, `RetroIO.Protocols.Xmsg.Hdlc` for the frame types), so no consumer had to change. They
are historical, not a statement about ownership: this project belongs to the link layer, not to
XMSG. Renaming them is a separate cosmetic step whenever it is wanted.

## Specification and tests

Text: `E:\Dev\RCL\Specs\X25` (X.25 10/96 clause 2 for ITU; `DOC\LAPB-REQUIREMENTS.md` and the
capture corpus for the ND dialect). Tests live in
`src/RetroIO.Protocols.Xmsg.Live/tests` (`LapbLayerTests`, `LapbTimerTests`, `LapbFrmrTests`,
`LapbConformanceVectorsTests`, `LapbItuVariantSeamTests`, ...) until they follow the code here.

Created 2026-09-06 as Phase D2 of `E:\Dev\RCL\Migration\X25 one-stack plan.md`. The second LAPB
implementation it replaced, `LapbInstance`, was deleted in Phase D3; `LapbStation` in
`src/RetroIO.Link/src/Links/Lapb` is the event surface the links use over this engine.
