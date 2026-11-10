# Changelog

All notable changes to RetroIO. One version per repo, so one entry covers every package.

## [Unreleased]

### Added
- RetroServer's own XMSG node (`XmsgNodeService`, in `RetroIO.Protocols.Xmsg.Plugins`): it joins an Ethernet segment, normally RetroServer's hub, and opens a gRPC port (default 127.0.0.1:5011) that client and server programs connect to. Settings in `xmsgnode-config.json` and `--XmsgNode:*`; off by default.
- Programs connected to RetroServer can send log lines (`Log` request, `PluginLoggerProvider`); they are kept under the category `XmsgProgram.<name>` and filtered by `ProgramLogMinimum`.
- The connection carries a protocol revision and a feature list both ways (`Hello.protocol_revision`, `NodeInfo`), so it can grow without breaking older programs.
- Developer documents for XMSG programs: `docfx/xmsg/` (guide, protocol reference, logging, versioning).
- `ChatClient(IXmsgKernel, IXroutNames, nickname)`, so a chat client can run on a connection to RetroServer.
- **`RetroIO.Protocols.Xmsg.Hosting`**, a new package with everything a program needs to run an
  XMSG node: the topology file, a node builder, the state-file names, the clean-shutdown steps (TAD
  logouts, LAPB DISC), the restart announcer, the folder sync daemon and the file push and pull
  drivers. It was internal code inside `xmsgrunner` (RetroXmsg); it moved so RetroServer and the
  runner share one copy. The state files now go in a folder the caller names, not beside the exe.
  The topology check also refuses a duplicate id or alias, a via chain naming an unknown node, an
  unknown transport and a `self` that is not listed.
- **`RetroIO.Protocols.Xmsg.Hub`**, a new package with the COSMOS Ethernet hub that ND machines
  join over TCP. It was the `xmsghub` program in RetroXmsg; that program now uses this package.
  New in the move: a listen address, counters per member, a buffer of recent frames (each kept
  once, from its sender to the hub, with what the hub did with it), a buffer of log lines, a pcap
  writer that writes to any stream, and `XmsgHubService`, a hosted service that reports `Failed`
  instead of stopping its host when the port is taken.
- RetroServer hosts the hub: `xmsghub-config.json` with the `XmsgHub:` keys, off by default
  (`RetroServerOptions.EnableXmsgHub` leaves it out entirely). The management API has
  `api/v1/xmsghub/` status, members, log, frames (JSON with the bytes) and `frames.pcap`, plus
  settings to read and write. `ManagementApiClient` has a call for each. The server manager starts
  and stops it with the other protocols.
- **XMSG plug-in servers over gRPC**, three new packages. A plug-in is a separate program, in any
  language, that runs only server code and dials the node over one two-way gRPC stream.
  `RetroIO.Protocols.Xmsg.Plugins.Contracts` holds the `.proto` file. `RetroIO.Protocols.Xmsg.Plugins`
  is the node's side: `PluginServerHost` does each request on the node's pump thread, and
  `PluginConnectionService` is the gRPC service. `RetroIO.Protocols.Xmsg.Plugins.Client` is the C#
  plug-in's side: `PluginConnection` gives the XMSG kernel and XROUT name calls carried over the
  stream. The C# chat server runs on it, tested over a real in-memory gRPC stream.
- `RetroIO.Protocols.Xmsg.Api`: `IXroutNames`, the XROUT name calls as an interface, and
  `XroutDirectory.For(kernel)` for the in-process table. `IXmsgKernel.SystemNumber`. `XroutLetter`
  builds and takes apart a letter. `XroutDirectory.ForwardLetter` forwards a letter that already
  carries its header.
- `RetroIO.Protocols.Xmsg.Node`: `IXmsgNameOwner`, so a bridge server can answer letters for names
  in its own name table.
- `RetroIO.Protocols.Xmsg.Chat`: `ChatServer(IXmsgKernel, IXroutNames)`, so the chat server runs
  inside a node or as a plug-in.
- `RetroIO.Protocols.Xmsg.Node`: `XmsgNodeHost.DeliverInboundOnPump`. When on, payloads from the
  link are queued and handled inside `Pump()`, so a link that delivers on its own thread (the
  Ethernet backends) no longer runs server code at the same time as the pump thread. Off by
  default; the HDLC path is unchanged.

### Changed
- `RetroIO.Protocols.Xmsg.Live`: `LapbLayerAdapter` and `EthernetLink` no longer write to the
  console. Their lines (a refused send, a link failure, a shutdown handler that threw) go to the
  new `Log` property, and are dropped when it is not set.
- **SFTP mounts now check the server's host key and refuse an unknown one.** An existing `sftp://`
  mount that worked before stops working until you give it `?hostKey=SHA256:...` (the fingerprint
  from `ssh-keygen -lf`), or `?hostKeyCheck=accept-new` to remember the key on the first
  connection. `?hostKeyCheck=off` restores the old behaviour. See `RetroIO.Storage.Providers`.

### Fixed
- A chat member whose program died kept its seat and nickname until the server restarted. The chat server now sends to members secure and removes a member when a message to it comes back, as the SINTRAN chat server does. The kernel's returned message now names the port it could not reach in its sender field (measured on D100), and the program connection carries returned messages (`Arrived.return_reason`).
- `XmsgKernelServer` now sends and reads a task's port-to-port data as a SINTRAN task does on the wire: frame flags 0x82, role 0x84, XMCSM = the length, the data at offset 28, split into fragments when long. Measured from D100's own chat Welcome. It used a layout of its own before (offset 32, control service 0x01080000), which no machine was seen to use.
- XMSG letters to a name. The node found the target server by scanning the frame for the first
  text starting with `*`; it now reads the letter's name parameter (XSLET parameter 1, COSMOS guide
  appendix B), so a name without a star can be reached. The in-process XROUT name table handed a
  server the letter's data alone; it now hands over the whole letter with its XSLET header, as XROUT
  does on a SINTRAN machine, and the chat server and the RR library find their data behind the
  header, as the SINTRAN chat server does. A letter from the wire to a name a task claimed reached
  nobody; with `XmsgKernelServer.Directory` set it now reaches that task.
- `RetroIO.Storage.Ndfs`: **new files get a date.** The write paths stamped
  `ToNdDateOrUnset(DateTime.Now)`, which is 0 from 2014 on, so SINTRAN showed every file RetroIO
  wrote as created `?0, 1950`. New `NdDateTime.FitNdRange` and `NdDateTime.Now` move a date into
  the 1950-2013 range by Ronny Hansen's rule: before 1950 becomes the same day in 1950, after 2013
  moves back 30 years until it fits (2026-11-07 becomes 1996-11-07). A contiguous file now gets
  only the creation date, as `@CREATE-FILE` gives it (measured); it also got a last-write date.
- `RetroIO.Protocols.Xmsg.Ndfs`: the COSMOS file server's listing dates use the same rule
  (`FaFolderEntry.ToListingDate` calls `NdDateTime.FitNdRange`). It had its own 64-year fold, so a
  file modified in 2026 was listed as 1962 while the pack writers gave 1996. The three
  `FaFolderEntry.NdDate*` constants are gone; nothing else used them.
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

