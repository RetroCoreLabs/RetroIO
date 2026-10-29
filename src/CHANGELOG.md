# Changelog

All notable changes to RetroIO. One version per repo, so one entry covers every package.

## [Unreleased]

### Changed
- **SFTP mounts now check the server's host key and refuse an unknown one.** An existing `sftp://`
  mount that worked before stops working until you give it `?hostKey=SHA256:...` (the fingerprint
  from `ssh-keygen -lf`), or `?hostKeyCheck=accept-new` to remember the key on the first
  connection. `?hostKeyCheck=off` restores the old behaviour. See `RetroIO.Storage.Providers`.

### Added
- `RetroIO.Storage.Ndfs`: the SINTRAN CPU number and CPU type of a system pack can be read and
  changed (`ReadSintranCpuIdentity`, `WriteSintranCpuIdentity`): both disk copies of the resident
  data page (`SYSNO`, `FCPUN`, `HWINF+2` in the segments `S3IDPIT` and `S3SDPIT`), the generation
  symbols `33CPN` / `33CPU` rewritten in place with the list's parity, and the directory's "entered
  by system N" stamp (`ReleaseDirectory`). Word addresses come from the pack's own symbol lists and
  segment numbers from the RT-loader name records, so nothing is hard-coded per SINTRAN version.
  Also `ListSintranSegments` / `ExportSintranSegment` / `ImportSintranSegment` (segment files from the
  kernel's `DBLST` / `ABLPAGE` / `MASSNO` tables) and raw page access (`TotalPages`, `ReadDiskPage`,
  `WriteDiskPage`). The volume info shows the identity on a system pack. Ported from norskdata-ndfs
  (`sintran_identity.c`, `sintran_segments.c`); tests mirror its cases and run on copies of a K and
  an L pack named by `RETROIO_SINTRAN_K_IMAGE` / `RETROIO_SINTRAN_L_IMAGE`.
- First import: the RetroFS, X25Emulator and NDInsight XMSG libraries, moved in by
  `Migration/tools/rename.py` and split into one package per project.

