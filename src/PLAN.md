# Plan: outstanding work only

Next: nothing runnable; the one item left waits on the user ("not yet").

Proof level for all items: lean. One full suite run after a merge, a mutation check for the key new rule only.

## RetroCore (the sibling RetroCore repository)

RetroCore master (local, not pushed; the user pushes RetroCore) has the Dragon 64 and CoCo 3 boot work, the MOOH cartridge, the Becker port and `Nuget/BUGS.md`. The Dragon items (quick load for `.cas`, Dragon 32 graphics bands with Juxtaposition) are in `Machines.Dragon/TODO.md`; the Becker port is on all five 6809 machines (CoCo 1/2/3, Dragon 32/64); the CoCo 1/2 has a cartridge slot and boots HDB-DOS from the port (`Coco2BeckerDiskBasicTests`). DriveWire is done; the bit-banger serial line is not being built (owner's decision, 2026-10-31).

## RetroIO

1. **NuGet publish**: needs the `NUGET_API_KEY` secret and tag `v0.1.0`; the user said "not yet".
