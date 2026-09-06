# DeleteMe

`LapbInstance` and `LapbWindowManager`: the LAPB behind `CodecLink`, `SerialLink`,
`TcpLapbLink` and `XotLink`, replaced by the `LapbLayer` engine with an ITU/ND variant
flag (Migration/X25 one-stack plan.md, Phase D). Pool-thread T1 with no state lock,
retransmits into a peer RNR - pinned by `LapbInstancePinTests`. The namespace is
`RetroIO.Link.Lapb.DeleteMe` on purpose: nothing new may take a dependency on it.
