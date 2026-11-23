# Changelog

All notable changes to RetroIO. One version per repo, so one entry covers every package.

## [Unreleased]

### Added
- `RetroIO.Storage.Ndfs`: **a new 1.2 MB floppy boots, and FLOMON starts its program.**
  Measured on nd100x with the TPE floppy 210523I01-XX-01D: FLOMON is in page 0, in bytes 2000-2015
  of page 0, and in the bit file page after the map. The copy RetroIO carried came from the X.25
  install floppies, which do not boot; the 616 page floppy now gets all three parts from the TPE
  floppy. FLOMON loads and starts the one file of type BPUN on the floppy, whatever its name; with
  none or two it stops at `FLOPPY-MON-H02`. Proven on nd100x and on the RetroCore ND-100.
- `RetroIO.Storage.Ndfs`: **writing the bit file wiped the rest of its page.** It built each page
  from zeros; it now changes only the map bytes, so a file write no longer breaks a FLOMON floppy.
- `RetroIO.Storage.Ndfs`: **zero pages are stored.** A plain write made every all-zero page a hole,
  and FLOMON cannot load a file with holes. Now every page is stored, unless the write carries an
  `ndfs.holes` list (from a `.xat` file), which makes those pages holes again.
- `RetroIO.Storage.Ndfs`: a path with a doubled slash (`/SYSTEM//TPE:BPUN`) stored the name
  `/TPE:BPUN`.
- The Clear key puts the inbound reply mode back to field mode, as GA23-0059-4 Table C-1 (p. C-1) says.
- `StatusLine3270.Build` with a `tn3270e` flag: column 1 shows "B" instead of "A" while the connection runs TN3270E (wc3270 draw_oia).
- `KeyboardLock.MinusFunction` and `Display3270.RefuseFunction`: a key whose function is not available now locks the keyboard with Minus Function, shown as "X -f", until Reset (x3270 KL_OIA_MINUS; GA27-2890-4 p. 3-32). Used for Attn on a TN3270E connection with no SNA session bound.
- `Display3270.Erase`: erase left, the PC Backspace of wc3270. The cursor moves one place left and that character is deleted the way `Delete` does it; at the first position of a field nothing happens. Follows x3270's `Erase` action step by step.
- **Five network packages, moved from RetroCore**: `RetroIO.Network.Ethernet` (the backend interface for an emulated network card, the backend registry, TCP, hub, UDP-multicast and TAP links), `RetroIO.Network.Nat` (the built-in NAT, on any subnet), `RetroIO.Network.Pcap` (a real host adapter through SharpPcap), `RetroIO.Network.Router` (the dial-up router; new `IpRouter.TryAddLink`) and the new `RetroIO.Network.DialUp` (`SlipDialUpSession`: an optional text login with a quick or a slow check, then SLIP to `NatDialUpSide` or `RouterDialUpSide`).
- Copy conversions in `RetroIO.Storage.Core`: `ITransferConversionProvider`, `TransferConversion`, `TransferText`. A driver says, as data, how a file can be changed on copy out and copy in. CP/M (`cpm-text`, cut at the first Ctrl-Z), NDFS (`sintran-text`, bit 7 cleared on copy out, clear or ND even parity on copy in) and Commodore (`petscii-text`, either character set) offer one each. A byte with no meaning under a rule is refused.
- Raw directory names on Commodore disks, for directory art: `CommodoreDiskDriver.ReadRawDirectory`, `WriteRawName`, `InsertDelEntry`, `ReadRawDiskName`, `ReadRawDiskId`, `WriteRawDiskHeader`.
- Trunks as leased lines in `RetroIO.Protocols.X25.Switch`: `HdlcTcpSwitchPort` (`transport = hdlc-tcp`, alias `hdlc-ppp`) and `HdlcSerialSwitchPort` (`transport = hdlc-serial`) carry X.25 over LAPB over 0x7E byte-stuffed HDLC framing on a TCP connection or a serial device, with DCE and DTE roles, redial, X.25 restart after the link comes up, and link counters (`HdlcSwitchPortBase.GetLinkSnapshot()`: link ups, link resets, frames, retransmissions, REJ, FCS errors, wire bytes). New port keys: `device`, `baud`, `databits`, `parity`, `stopbits`, `flow`, `lapb_t1`, `lapb_t2`, `lapb_t3`, `lapb_n2`, `lapb_k`. The real serial device is not exercised by the tests (no virtual serial pair was available); the serial port is tested over an in-memory line.
- `SwitchPortFactory.Create(PortConfig, ILogger)`: the one place that turns a port configuration into a port by its transport. A transport it cannot run throws `NotSupportedException` naming the port and the transport. `IMonitoredSwitchPort`: the monitor view of a port with a line (`PacketCaptured`, connection counts, `LastError`), implemented by `XotSwitchPort` and the HDLC ports.
- `LapbLayer.LinkResets` and `OnLinkReset` (also `LapbStation.LinkReset` and `LapbStatistics.LinkResets`): count of times a link that was up was reset (SABM or FRMR received, or set up again after N2 tries).
- A telnet PAD in `RetroIO.Protocols.X25.Runtime` (`TelnetPadServer`, switched by the new `RuntimeConfig.TelnetPad` section: `Enabled`, `Port`, `BindAddress`, `MaxSessions`; off by default, loopback and port 2323). Every telnet connection gets its own `PadClient` and acts as an X.28 terminal. `ProtocolRuntime.TelnetPadStatus` says off, listening (with the session count) or failed with the reason (for example port in use); a failed listen never stops the runtime. `ProtocolRuntime.ApplyTelnetPadConfig()` restarts it after a settings change.
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
- X.25 switch, end-to-end interrupt confirmation (X.25 4.3.7, X.75 3.3.5): `X25Config.ConfirmInterruptsFromFarEnd`, `X25Layer.ConfirmInterrupt`, `ISwitchPort.InterruptConfirmed` / `ConfirmInterrupt`. The switch passes the far end's confirmation back across the call instead of confirming at once; a second interrupt before it is a procedure error (reset, diagnostic 44).

### Changed
- `SwitchConfigLoader` now throws `InvalidDataException` for a `transport` value it does not know (it was read as XOT). A missing `transport` key still means XOT. `x25switch` and the Desktop built-in switch build their ports through `SwitchPortFactory` and stop with a message naming the port and the transport when one cannot be run, instead of running every port as XOT.
- `RetroIO.Protocols.Xmsg.Live`: `LapbLayerAdapter` and `EthernetLink` no longer write to the
  console. Their lines (a refused send, a link failure, a shutdown handler that threw) go to the
  new `Log` property, and are dropped when it is not set.
- **SFTP mounts now check the server's host key and refuse an unknown one.** An existing `sftp://`
  mount that worked before stops working until you give it `?hostKey=SHA256:...` (the fingerprint
  from `ssh-keygen -lf`), or `?hostKeyCheck=accept-new` to remember the key on the first
  connection. `?hostKeyCheck=off` restores the old behaviour. See `RetroIO.Storage.Providers`.
- X.25 / X.75 (2026-11-23, matched with x25emu):
  - At an STE, a short data packet with M=1 and D=0 resets the circuit with diagnostic 103 (X.75 3.3.4).
  - An STE no longer times out an unanswered incoming call; X.75 Annex D has no T11.
  - A reset or clear for a local procedure error goes to the other leg of the call as remote procedure error, reset 0x03 and clear 0x11 (X.25 Table C-4).
  - The owner of the circuit is told about a procedure-error reset when the reset is sent, not when it is confirmed.
  - A transit network that refuses a call adds its own DNIC to the transit list in the refusal (X.75 5.3.1).

### Fixed
- `IndFileTransfer` (`RetroIO.Protocols.Tn3270`) never ended a transfer on a real host: it waited for a Close of the message file, which neither IBM's figures nor TK5 send. It now ends on the Data ack for the host's TRANSxx message, and cuts the message at its `$` end mark (TK5 sends `TRANS03   File transfer complete$` and blanks, in ASCII). Measured on TK5, 23 November 2026; a test replays the recorded upload.
- `RetroIO.Network.Pcap` dropped every unicast frame for a card until the card had sent its first frame, so a host with the card's address in its ARP cache got no ping replies. New optional `IEthernetGuestAddresses` (`AddGuestAddress`, `RemoveGuestAddress`) in `RetroIO.Network.Ethernet`, implemented by `PcapEthernetBackend`, takes the address up front; learning from sent frames stays as the fallback.
- A driver now closes the stream it was mounted from when it is disposed, as `IFileSystemDriver.Mount` now says. CP/M, Dragon DSK, DMK and VDK, and PC hard disks left it open, so an image file stayed locked until the program quit. A rule test covers every blank layout.
- FAT12, FAT16 and FAT32 `WriteFile` now refuse a file that does not fit BEFORE the disk changes, with an `IOException` that names the file and says how much room it needs and how much is free. An overwrite used to free the old file's clusters first and then run out part way, so the old file was lost and a broken chain was left behind; a new file that did not fit kept the clusters it had taken. The check counts the room the old file gives back and the cluster a sub-directory needs to grow; a full FAT12/16 root directory is also refused before any data is written. Tests: `FatWriteDiskFullTests`.
- `X25Layer` no longer resets a circuit because a complete packet sequence is long. It used to reset with cause 0, diagnostic 39 after 64 KB of full packets with the M bit set (a Cisco router's PAD sends an endless run of them to a fast sender). X.25 (4.3.4, 4.3.5) sets no length limit on a sequence and names no reset for one. `MessageReceived` now has a `moreData` argument: a long sequence is handed on in parts of about 64 KB, every part but the last with `moreData` true. `DataReceived` is unchanged and still gives every packet with its own M bit. The delegate `X25MessageReceived` gained the argument (a breaking change for a subscriber of that event); `X25Vc.MaxReassembly` is now `X25Vc.MaxMessagePart`.
- `XotSwitchPort`: a call placed on a connection the port dialled for it now connects when the peer answers on another channel number than the Call Request used (RFC 1613 section 6: the number has no significance). x25emu does this (a call sent on channel 16 is answered on channel 1); the port cleared such a call at once with diagnostic 20. The peer's number is bound to the one call of that connection, and our Clear and data go out on it.
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

