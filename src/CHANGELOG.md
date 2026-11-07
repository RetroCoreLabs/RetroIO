# Changelog

All notable changes to RetroIO. One version per repo, so one entry covers every package.

## [Unreleased]

### Changed
- **SFTP mounts now check the server's host key and refuse an unknown one.** An existing `sftp://`
  mount that worked before stops working until you give it `?hostKey=SHA256:...` (the fingerprint
  from `ssh-keygen -lf`), or `?hostKeyCheck=accept-new` to remember the key on the first
  connection. `?hostKeyCheck=off` restores the old behaviour. See `RetroIO.Storage.Providers`.

### Fixed
- `RetroIO.Storage.Ndfs`: **new files get a date.** The write paths stamped
  `ToNdDateOrUnset(DateTime.Now)`, which is 0 from 2014 on, so SINTRAN showed every file RetroIO
  wrote as created `?0, 1950`. New `NdDateTime.FitNdRange` and `NdDateTime.Now` move a date into
  the 1950-2013 range by Ronny Hansen's rule: before 1950 becomes the same day in 1950, after 2013
  moves back 30 years until it fits (2026-11-07 becomes 1996-11-07). A contiguous file now gets
  only the creation date, as `@CREATE-FILE` gives it (measured); it also got a last-write date.
- `RetroIO.Storage.Ndfs`: **an empty file owns no page, as SINTRAN writes one.** It used to get
  one data page and one index block. Measured on nd100x (a copy of `SMD0.IMG`):
  `@CREATE-FILE E0:SYMB,0` and `@COPY-FILE "C0:SYMB",E0:SYMB` store 0 pages, file pointer 0,
  bytes 0xFFFFFFFF and the indexed flag, and the free page map does not move. `WriteFile` now does
  the same, charges nothing, and gives all pages back when a file is overwritten with nothing.
- `RetroIO.Storage.Ndfs`: **`ReadFile` threw "Invalid file pointer" on an empty file SINTRAN had
  made**, because such a file has pointer 0. A 0 pointer with 0 bytes now reads as an empty file;
  with bytes recorded it is still an error. The `ndfs.holes` property is left out for every
  0-byte file, whether or not an older tool gave it a page.
- `RetroIO.Storage.Ndfs`: **rewriting an object page turned every empty file on it into a 1-byte
  file.** SINTRAN stores "bytes in file" as bytes minus one, so an empty file holds 0xFFFFFFFF
  (measured on nd100x `SMD0.IMG`: ten SINTRAN-made entries such as `(SYSTEM)SINTRAN:DATA`).
  `ObjectEntry.ToBytes` wrote 0 for it, which reads back as 1 byte; it now writes 0xFFFFFFFF. A
  pack rewritten by an older version may hold 1-byte files that were empty; nothing repairs them.
  The norskdata-ndfs C, Python and TypeScript writers had the same mistake and are fixed the same
  way.
- `RetroIO.Storage.Ndfs`: a new file gets its owner's default file access, indexed and contiguous
  alike, as SINTRAN gives it (measured on nd100x: with SYSTEM's default set to `N,RW,RWACD`,
  `@CREATE-FILE` stored 0x007F for both kinds). It was a fixed 0x03FF.

### Added
- `RetroIO.Storage.Ndfs`: the SINTRAN access and friend commands on `INdfsFileSystem`:
  `ListFriends` (`@LIST-FRIENDS`), `AddFriend` (`@CREATE-FRIEND`, RWA by default),
  `RemoveFriend` (`@DELETE-FRIEND`), `SetFriendAccess` (`@SET-FRIEND-ACCESS`),
  `SetFileAccess` / `GetFileAccess` (`@SET-FILE-ACCESS`) and `SetDefaultFileAccess` /
  `GetDefaultFileAccess` (`@SET-DEFAULT-FILE-ACCESS`). Each change is written to the device at
  once. `Security.SintranAccessForm` reads and writes SINTRAN's own form (`PUBLIC,FRIEND,OWN`, each
  field `N` or letters `RWACD`, an empty field keeps that part). The same calls exist in the
  norskdata-ndfs C, Python and TypeScript libraries and in ndtool. Checked against SINTRAN on
  nd100x: `@LIST-FRIENDS`, `@FILE-STATISTICS` and `@USER-STATISTICS` show what was written.
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

