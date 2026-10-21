# Plan: outstanding work only

Next: finish the RetroCore CoCo 3 merge (item 1), then the changed-sector writes (item 2), which unblock the DriveWire server (item 3).

Proof level for all items: lean. One full suite run after a merge, a mutation check for the key new rule only.

## RetroCore (the sibling RetroCore repository; its worktrees sit beside it)

1. **CoCo 3 merge.** Branch `merge-coco3-nitros9` (worktree `RetroCore-wt-merge`): merge commit `e5e5168ad` plus WIP `ad1672b05` (Nuget/BUGS.md created, Level 3 tests skipped with their proven reason). Still to do: build and run Coco on `ad1672b05`; run C128, CommodoreLCD and Cli.IntegrationTests; check the merged files are not among the other session's uncommitted files; `git merge --ff-only merge-coco3-nitros9` in the main checkout; remove the worktree. Do not commit `perf-history.jsonl` or the regenerated `nitros9-boot.png`. Not pushed: the user pushes RetroCore.
   - Level 3 cause, found: the 6809 Level 3 snapshot disks carry an HD6309 set-up module and a Level 2 6809 kernel that never calls it, so they cannot boot on an MC6809 as built; XRoar crashes the same way.
2. **MOOH cartridge (Dragon 64, CLI option `--cart=mooh --cart-rom=<rom> --sd=<img>`).** Branch `dragon64-mooh-cart` (worktree `RetroCore-wt-mooh`), WIP `c6670a66e`. Boots the real MOOH SD image to the NitrOS-9 Level 2 shell; `dir` works; matches XRoar 1.9. Includes a 6809 fix: NEG with operand $80 left C, N, Z stale. Still to do: remove `TempWriteProbe` and the boot test's dump code; add the 6 new projects to `Nuget/RetroCore.Nuget.sln`; run the Dragon suite and Cli.IntegrationTests; mutation-check the NEG fix and the write-through rule; update the Dragon README; final commit; merge after item 1.
3. **Becker port** (after the DriveWire server works): TCP client to 127.0.0.1:65504; CoCo status $FF41 (bit 1 = byte waiting), data $FF42; Dragon at $FF49/$FF4A; bytes both ways in order, no framing. CLI option, like MOOH. Proof: boot a `_becker` NitrOS-9 disk served by RetroServer to `OS9:`.

## RetroIO

1. **Changed-sector writes.** Branch `worktree-agent-a90216b08e6c3a153`, WIP `470248f`. Done: block device in 4 KB pages, flush writes changed blocks only, short DriveWire images grow like Toolshed (100,000-byte copy: 256,768 to 357,120 bytes) and mount writable. Red: the short-DriveWire OS-9 stress variant fails at call 1983, Toolshed dcheck reports bad descriptor segments for two files, image grown to 124 MB; cause not found (compare the descriptor sector dcheck reads with ours on the image kept in the session scratchpad `keep` folder). Then mutation checks, one full suite run, merge.
2. **DriveWire server.** Branch `worktree-agent-a5c2d45995712c7c3`, commit `64a906b` (on main `6a45f28`). New package `RetroIO.Protocols.DriveWire` plus hosting in RetroIO.Server and two management endpoints. 48 of 50 tests pass; the 2 red ones are writes past the end of a short image, which need item 1. Defaults: TCP 65504, loopback, 250 ms byte timeout, 256 drives. Spec: "DriveWire Protocol Version 4.0.0" (github.com/DrPitre/DriveWire). Then: merge after item 1; publish to the local feed with `src/eng/publish-local.ps1 -Feed <shared localfeed folder>` (its default is wrong from a worktree); in RetroServer, a new branch adding the package reference, the DriveWire keys in `--help`, and a hosting test.
3. **Game disks as DECB** (HTCHHKER.VDK, PLANETFALL.VDK): not started. PLANETFALL's FAT has GAME.BIN at granule 32 and zeros in bytes 68 to 255; HTCHHKER's FAT is all 0xFF with an empty directory. Add a valid 68-granule FAT check for 40-track disks in `PickDragonDosOrDecb`, check with Toolshed `decb` on a 35-track cut.
4. **Test-host memory warm-up** for the DECB stress memory check: not started. VSTest 17.13 sends run statistics on a 1.5 s timer while a test runs; wait for the first send before the first measured round, then prove it with filtered runs.
5. **dmk2jv3 in CI**: not added (native build step or WSL on the runner).
6. **NuGet publish**: needs the `NUGET_API_KEY` secret and tag `v0.1.0`; the user said "not yet".
