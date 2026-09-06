# DeleteMe

Code that is being replaced by the one X.25 stack in `src/RetroIO.X25/` and will be
deleted when the last consumer has moved (Migration/X25 one-stack plan.md, Phase C3).
The namespace is `RetroIO.Protocols.X25.DeleteMe` on purpose: nothing new may take a
dependency on it, and every file that still does says so in its usings.

- `X25.cs` - the old packet-layer engine (channel table, two sequence counters on one circuit, REJ on a gap).
- `X25Layer.cs` - the wrapper the desktop runtime drives it through.
- `X25FlowControlFacilities.cs` - the lenient facility codec (window 0 accepted, marker misread); replaced by `X25Facilities`.
