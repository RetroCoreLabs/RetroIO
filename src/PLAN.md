# Plan: outstanding work only

Next: RetroCore item 1, the Becker port (DriveWire server and MOOH are both on master).

Proof level for all items: lean. One full suite run after a merge, a mutation check for the key new rule only.

## RetroCore (the sibling RetroCore repository; its worktrees sit beside it)

RetroCore master (local, not pushed; the user pushes RetroCore) has the Dragon 64 and CoCo 3 boot work and `Nuget/BUGS.md`. The Dragon items (quick load for `.cas`, Dragon 32 graphics bands with Juxtaposition) are in `Machines.Dragon/TODO.md`.

1. **Becker port**: TCP client to 127.0.0.1:65504; CoCo status $FF41 (bit 1 = byte waiting), data $FF42; Dragon at $FF49/$FF4A; bytes both ways in order, no framing. CLI option, like MOOH. Proof: boot a `_becker` NitrOS-9 disk served by RetroServer to `OS9:`.

## RetroIO

1. **NuGet publish**: needs the `NUGET_API_KEY` secret and tag `v0.1.0`; the user said "not yet".
