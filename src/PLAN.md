# Plan: outstanding work only

Next: RetroCore item 2, the Becker port (the DriveWire server it talks to is merged and hosted by RetroServer).

Proof level for all items: lean. One full suite run after a merge, a mutation check for the key new rule only.

## RetroCore (the sibling RetroCore repository; its worktrees sit beside it)

RetroCore master (local, not pushed; the user pushes RetroCore) has the Dragon 64 and CoCo 3 boot work and `Nuget/BUGS.md`. The Dragon items (quick load for `.cas`, Dragon 32 graphics bands with Juxtaposition) are in `Machines.Dragon/TODO.md`.

1. **MOOH cartridge (Dragon 64, CLI option `--cart=mooh --cart-rom=<rom> --sd=<img>`).** Branch `dragon64-mooh-cart` (worktree `RetroCore-wt-mooh`), WIP `c6670a66e`. Boots the real MOOH SD image to the NitrOS-9 Level 2 shell; `dir` works; matches XRoar 1.9. Includes a 6809 fix: NEG with operand $80 left C, N, Z stale. Still to do: remove `TempWriteProbe` and the boot test's dump code; add the 6 new projects to `Nuget/RetroCore.Nuget.sln`; run the Dragon suite and Cli.IntegrationTests; mutation-check the NEG fix and the write-through rule; update the Dragon README; final commit; rebase on master `ea8b3b040` and merge.
2. **Becker port**: TCP client to 127.0.0.1:65504; CoCo status $FF41 (bit 1 = byte waiting), data $FF42; Dragon at $FF49/$FF4A; bytes both ways in order, no framing. CLI option, like MOOH. Proof: boot a `_becker` NitrOS-9 disk served by RetroServer to `OS9:`.

## RetroIO

1. **Game disks as DECB** (HTCHHKER.VDK, PLANETFALL.VDK): not started. PLANETFALL's FAT has GAME.BIN at granule 32 and zeros in bytes 68 to 255; HTCHHKER's FAT is all 0xFF with an empty directory. Add a valid 68-granule FAT check for 40-track disks in `PickDragonDosOrDecb`, check with Toolshed `decb` on a 35-track cut.
2. **Test-host memory warm-up** for the DECB stress memory check: not started. VSTest 17.13 sends run statistics on a 1.5 s timer while a test runs; wait for the first send before the first measured round, then prove it with filtered runs.
3. **dmk2jv3 in CI**: not added (native build step or WSL on the runner).
4. **NuGet publish**: needs the `NUGET_API_KEY` secret and tag `v0.1.0`; the user said "not yet".
