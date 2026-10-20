# Known problems in the code

Found on 2026-10-18 while writing the XML comments for every public member. A comment has to say what
the code does, so each place where the code does not match its interface (or its own name) was
written down. **None of these has been reproduced by running it unless the entry says so.** Each
one is a reading of the code by the person who wrote its comment.

How an entry is closed: a test that fails first, then the fix, then the entry moves to the
"Fixed" section with the commit. A problem is never closed by editing the interface comment to match
the code, unless the interface comment was the part that was wrong, and the entry says that.

Group letters: **R** real bug, **S** stub that reports success while doing nothing, **Q** needs the
real specification before anyone touches it, **I** the interface comment is probably the wrong part.

## Open

### R - the code is wrong and the right answer is clear

| Where | What |
|---|---|

### S - stubs that report success while doing nothing

| Where | What |
|---|---|
| `RetroIO.Protocols.X25.Runtime` `SerialTransport.IncomingCall`, `XotClientTransport.IncomingCall` | Never raised, on purpose for now. A serial Call Request sits inside a LAPB I frame and this class has no deframing, FCS or LAPB state; the XOT client packets already go to a per-connection X.25 engine in `ProtocolRuntime`, and raising the event too would route each call twice. Needs a decision on where the incoming call is raised first |
| `RetroIO.Protocols.X25` `X28.StatusUpdate` | Never raised, and nothing says what it should carry or when (no subscribers; `STAT` uses `CommandResponse`, mode changes use `ModeChanged`) |
| `RetroIO.Protocols.Xmsg.Api` `RrClient.RequestConnection` | `connection` is always 0 (documented, and the tests rely on it). A named remote system is refused. The COSMOS Programmer Guide (RRPCcnrq, section 4.3) says the number comes back in `remoteID` but not who picks it or how; needs that document or a capture of a real RR client against a real server |
| `RetroIO.Protocols.Econet` `FileServerConfiguration` flag bits | Default (0x82), permissions and wire layout now follow PiFS and `BigChunks` (0x04) works. The other flags (AcornHome 0x01, MdfsFunctions 0x02, InfColon 0x08, ManyHandle 0x10, MdfsInfo 0x20, PifsPerms 0x40, AcornDir 0x80) are stored and echoed but change no behaviour; several need changes in path handling, handle numbering and `*INFO` |

### Q - needs the real specification first

| Where | What |
|---|---|
| `RetroIO.Protocols.Tnfs` limits | The limits now match the reference tnfsd `config.h` by decision, because the spec text gives no numbers. `MAX_SESSIONS` (256) and `MAX_SESSIONS_PER_IP` (8) still differ from config.h (4096) and nothing reads the constants (`SessionManager` has its own default). The `TCP_KA_*` constants are not applied to any socket. `TNFS_O_RDONLY` (0x0001) disagrees with `FileCommandHandler.ConvertToFileAccess`, which treats low bits 1 as write only, so a read on a handle opened with the constant fails with EIO. `HandleReadDir` rents 256 bytes for a name plus terminator: a 256 character name overflows it |
| `XmsgKernel.Send` | Ignores the Route flag; always delivers to the destination. The interface says XROUT when Route is set |
| `XmsgKernel.MakePrivileged` | Always succeeds; never checks the password or the kind of task |
| `XmsgKernel.ReserveBuffer` | Ignores `options` and the XFDMM ceiling |
| `XmsgKernel.ConvertMagicToPort` | Never returns 3 ("a system") |
| `RrClient.Wait`, `RrServer.Wait` | Ignore `wanted` and `timeout` and never block, so Select and Transact work only when the answer is already queued |
| `DragonDosFileSystem.GetBlocksFree` | Counts BAM bytes equal to 0xFF as one free sector each; not checked against the DragonDOS format |
| `RetroIO.Protocols.X25.Sintran` `SintranPvcServer` | Picks a route from the first byte of the call user data (`0x01`, `0x02`, `0x03`, `0xDD`); that rule was not taken from a capture |

### I - the interface comment is probably the wrong part

| Where | What |
|---|---|
| `EthernetLink.Start`, `LapbLayerAdapter.Start` | `Start` starts no LAPB and returns the backend state; always true |
| `RetroIO.Storage.Local` `FfsDriver` and others | Write methods throw `NotImplementedException` where `IFileSystemDriver` says `UnauthorizedAccessException` or `NotSupportedException` |

### Tests

Nothing open. The GitHub `build` workflow and the C client test are both fixed (see below).

## Fixed

| What | Commit subject |
|---|---|
| `X3` defaults were neither standard profile; they are now the simple standard profile of Table 1/X.28 (parameters 3, 5, 7, 8, 12, 15, 19 changed), validity limits follow X.3 sections 3.1 to 3.21 for 11 parameters, and password mode now sets parameter 3 to 127 and the idle timer to 1 (X.3 value 1 meant alphanumerics only), restoring both on exit | X.3: simple profile default, validity limits to the spec, password mode forwards every character |
| `X28.CharacterProcessed` was never raised; it is now raised once per processed character | X28: raise CharacterProcessed |
| `PhysicalLayer.IncomingCall` and `SerialTransport.DataReceived` were never raised; the TCP handler now cuts RFC 1613 XOT frames and raises IncomingCall for each Call Request, and the serial transport forwards bytes for its own interface | X25 transports: raise IncomingCall and DataReceived |
| `SintranPvcServer.SendX25Data` and `SendX25Interrupt` threw NotImplementedException; they now emit X.25 Data and Interrupt packets (X.25 4.3.7, 5.3.2) with simplified P(S)/P(R) = 0; the SINTRAN peer behaviour is not known | SintranPvcServer: send data and interrupt packets |
| `EconetFileServerOptions.Station` was never read. It was first removed by mistake, then restored and used: `RetroServerComposition` sets it from the Econet config, `FileServerContext` carries the server net and station, and PiBridge call 19 replies "Fileserver at N.S shutting down" and logs every station off (as PiFS `fs.c` does). The other server-station uses in `fs.c` (source address in frames, client net 0 mapping, sequence counter) are not wired: an AUN datagram has no station field | Econet: restore the server Station option and use it in the shut down reply |
| `LocalTadProtocol.EnterPasswordMode` saved the "original" X.3 values on every entry, so entering twice overwrote them with the password-mode values and exit restored the wrong ones; it now saves only on the first entry (`PasswordModeActive`). The near-X.29-SET comment on parameter 3 = 126 now names the flags it is | LocalTad: entering password mode twice no longer overwrites the saved values, so exit restores the ones from before the first entry |
| `FileServerConfiguration` default flags, permissions and the config call layout now follow PiFS (0x82, file 0x03, directory 0x13, flags/0/file/dir/length) and `BigChunks` changes the SAVE window | Econet: file server configuration follows PiFS |
| `TcpTransport` reported 1000 connections and enforced none; it now enforces `MaxConnections` (default 4096) and reports the same number; TNFS message, path, directory handle, timeout and keepalive limits set to the reference tnfsd values; a ninth open directory is refused with EMFILE and READ is cut at 512 bytes | TNFS: limits match the reference daemon, TCP connection limit enforced |
| `LapbStation` ignored its `t2TimeoutMs` argument, so `T2Timeouts` stayed 0 on every link; it now passes T2 to the engine. A link config with T1 at or below T2 (default 1500 ms) now throws ArgumentOutOfRangeException, as X.25 2.4.9.2 says T2 is less than T1; default links now acknowledge up to 1.5 s late | LapbStation: pass T2 to the engine |
| `XmsgKernelServer.WirePort` had no random low 7 bits; it is now (logical port << 7) with a part drawn once from `XmsgRandomGenerator`, as the XMSG wire-port note says | XmsgKernelServer: the wire port now carries a random low part drawn from the XMSG generator |
| `ILapbCapable.T2Timeouts` was never set; the LAPB engine now has the X.25 T2 acknowledgement delay timer (off at 0, T2 below T1 enforced, ack piggybacked on an outgoing I frame, count on expiry) | LAPB: add the T2 acknowledgement delay timer so T2Timeouts counts something real |
| `TnfsConstants` had four unused second names for limits that already had one (`MaxMessageSize`, `MaxPathLength`, `MaxFilenameLength`, `MAX_IOSZ`); removed. No value changed: the spec is silent | TNFS constants: remove four unused second names for limits that already have one |
| `X3` and `X3Parameters` disagreed and were wrong against X.3: parameter 12 is flow control, parameter 3 is a sum of flags (13 is not CR), both standard profiles now match Table 1/X.28, `X28` reads parameter 3 as flags; `X3Parameters` now takes everything from `X3` | X.3 PAD parameters: one source of truth, values and profiles taken from X.3 and Table 1/X.28 |
| `TnfsHeader` existed twice in one package (`Protocol` and `Server`), identical but for how the session id was read; kept the protocol layer one, which does not depend on the server core, and it now reads and writes the session id without copying the buffer | TNFS: one TnfsHeader, in the protocol layer |
| `LiveDataService` pushed TNFS statistics as fixed zeros; it now reads `SessionManager` and `TnfsServerStatistics` like `TnfsController` (zero when no TNFS server runs in the process) | LiveDataService: push the real TNFS counters |
| `FileServerReply.DiscFreeSpaceClassic1983` comment had a `param` and no `returns` | FileServerReply.DiscFreeSpaceClassic1983: add the returns comment |
| `EconetUserBootstrap` comment said it purges legacy orphan keys: the comment was the wrong part (nothing in the repo defines an orphan key, nothing calls it but its test, and the code only adds defaults) | EconetUserBootstrap: the comment says it only adds defaults |
| `LapbStatistics.Retransmissions`, `RejectionsSent`, `RejectionsReceived`, `T1Timeouts` were never set | LAPB: count retransmissions, REJ sent and received, and T1 timeouts |
| `PadClient.IdleTimeoutMs`, `PadClient.AutoConnect`, `TadServer.EnableLoopbackTest` were never read; `XMSGSessionState.DataTransfer` was never assigned | X25: remove the settings and the state that nothing used (nothing in the repository referenced them and none had a defined behaviour) |
| `ZipFsPlugin` stayed latched after `Unmount`; `CanHandleAsync` returned false for a non-seekable stream | ZipFsPlugin: stay reusable after Unmount, honour the CanHandleAsync contract |
| `TCPServer.SendByte` returned false after a successful send | Fix TCPServer.SendByte and the NativeVolumeProvider mount-root check |
| `NativeVolumeProvider` accepted a sibling folder whose name starts with the mount root's name | same commit |
| `V7FileSystem` and `FfsFileSystem` swallowed "partition not in use" and "not a V7/FFS filesystem" | BSD V7 and FFS: report a label that names an unusable partition |
| FFS, TNFS and `RetroVFS` wrote debug dumps to the console | Remove debug output that the FFS and TNFS code wrote to the console |
| Default `admin`/`SYST` password `retroadmin` was not warned about after the first start | Warn at every start while admin or SYST still has the default password |
| `NdfsFileSystem.UpdateUserQuota` was not written to disk; `DeleteFile` and `Initialize` threw the wrong exception types | NDFS: write quota changes to disk, throw the exceptions the interface documents |
| `*SDISC` left the Acorn paths on the old disc; `GetOsCliDisplayName` mislabelled `*CA.` and `*CATALOGUE` | Econet: move the Acorn paths with *SDISC, and label *CAT the way the server runs it |
| `SftpVolumeProvider` trusted every SFTP host key | SFTP: check the server's host key, and refuse one that is unknown or changed |
| `NdfsFileSystem.ClearUserPassword(int)` changed the loaded user before noticing the device was read-only | NDFS: ClearUserPassword leaves the loaded user alone when the device is read-only |
| `LiveDataService` stopped pushing log lines once the 1000-entry buffer was full | Management API: keep pushing new log lines after the log buffer has filled up |
| `SubStream.Read/Write` rejected an empty buffer at offset 0 | SubStream: accept an empty buffer, and an offset equal to the buffer length with a count of zero |
| `FileServerBootstrap.EnsureLayout` created the disc folders on the host disk, not in the backend | Econet: build the disc folders through the file system backend, not on the host disk |
| The save-complete reply carried a wrong year for every date from 1997 on (two private `EncodeAcornDate` copies) | Econet: the save-complete reply carried a wrong year for every date from 1997 on |
| `DescribeCmd6Fields` called the return code and result code `loadLo`/`loadHi` | Econet diagnostics: name the first two bytes of a command-6 reply for what they are |
| `CrtDriver.Mount` doubled the chips on a second mount and let a failed mount damage the first cartridge | CrtDriver: a second Mount no longer doubles the chips, and a failed Mount keeps the old cartridge |
| `XmsgKernelServer.Handle` dropped datagrams that were queued but not yet drained | XmsgKernelServer.Handle: stop dropping datagrams that were queued but not yet drained |
| `ProtocolConfigurationLoader` rewrote an existing config file and said "Created default" | ProtocolConfigurationLoader: write the config file only when it is missing |
| `XotLink.SendData` returned true when nothing was sent; `StatusChanged` fired on every status set in four link classes and `FakeLink` | RetroIO.Link: XotLink.SendData says false when nothing was sent; StatusChanged only on a real change |
| `EthernetLink`/`LapbLayerAdapter.SendData` return true for a datagram parked behind a full window: the code was right (both callers treat false as lost) and `ILink.SendData` was the wrong part | ILink.SendData comment matches the queueing links; a test pins it |
| `SetObjectInfoOperation` read past a short parameter block | Econet SetObjectInfo: need 12 parameter bytes before reading the length field |
| `EconetActivityLog` threw `OverflowException` for a negative count | Econet activity log: a negative count gives an empty list |
| `GetRandomAccessInfo` returned the file length for any function but 0 | Econet GetRandomAccessInfo: only functions 1 and 2 return the file length |
| `AllocateHandle` returned handles in use; slot 0 of the session table could not be told from "not found" | Econet sessions: AllocateHandle skips open handles, session slot 0 is told from not found |
| `PRIV`, `DISCMASK`, `SETHOME`, `SETLIB` and the boot option change always replied success | Econet: reply an error when the user store refuses PRIV, DISCMASK, SETHOME, SETLIB or a boot option change |
| D81 subdirectory flag never set; `PcFloppyDriver` and `NdfsDriver` claimed write support on a read-only mount; NDFS label before init | CommodoreDiskDriver, PcFloppyDriver, Ndfs: flags follow the mount, empty label before init |
| `FfsDriver`/`V7Driver.SelectPartition` accepted any letter | FfsDriver.SelectPartition checks the disk label after mount; V7Driver also rejects an unused partition |
| DECB and DragonDOS `ListDirectory` threw `NotImplementedException`; CBM DOS write, delete and rename threw the wrong exception type on a read-only device | DECB and DragonDOS ListDirectory throw DirectoryNotFoundException; CBM DOS ... throw IOException |
| FAT12/16/32 directory rename across parents left the `..` entry on the old parent; FAT32 never read its volume label | FAT12/16/32: moving a directory rewrites its dot-dot entry; FAT32 reads the volume label |
| XOT server, serial transport and `X25ClientPadTransport` reported success for calls they never made, or ignored arguments | each now throws `NotSupportedException` (four commits: XotServerTransport, SerialTransport, X25ClientPadTransport, RrClient) |
| Econet delete went to the host disk and answered success with any other backend; `*RENAME` ignored the backend and answered "Not found" for a new name | Econet: delete and rename go through the file system backend (new DeleteFile, DeleteDirectory, MoveFile, MoveDirectory) |
| The Azure and VFS backends do not yet delete directories or move anything (they throw NotImplementedException, by design: fail loudly) | (still open, see the backend classes) |
| Econet handles 4 to 32 came from one pool shared by all stations, so a busy station could starve the others and one station could use another's handle number | Econet: each station has its own table of open file handles |
| 53 XMSG tests failed on every GitHub run because the recordings they read were not in the repository | Test data: add the 38 cleaned lab recordings the XMSG tests read (and the public registry data in an earlier commit) |
| `CClient_CallsThroughTheCSharpSwitch_AndItsDataArrives` failed: the C client got no data back | Merge branch 'x25-switch-port-hooks': one TCP connection for each virtual circuit in `XotSwitchPort`, no Restart before the first call (RFC 1613 6.4), Nagle off |
| `AnalyzeAllNdfsImages` looked for five disk images in a folder on the author's OneDrive, so it failed on GitHub | Test images: add the five NDFS images to the download, fetch them in CI, and read them from the fetched folder |
| `Server_` and `Client_BadVersionHeader_ClosesTheConnection` could fail on a slow machine: the test wrote the good frame after the transport had already closed the connection | XOT header tests: send the bad header and the good frame in one write |
| `V7Driver`/`FfsDriver.SelectPartition` after mount stored the letter only | V7Driver, FfsDriver: selecting a partition after mount opens the file system again on it; a failed mount leaves no half-built file system |
| `CbmDosFileSystem.ListDirectory` ignored its `path` argument | CbmDosFileSystem: ListDirectory throws DirectoryNotFoundException for any path but the root |
| `DeltaBlockDevice` stored `applyDeltaOnMount` and never used it | DeltaBlockDevice: applyDeltaOnMount false starts with an empty delta instead of loading the existing delta file |
| `VfsFileSystemBackend.GetFileInfo` returned a `FileInfo` for a deleted temp file; the Azure backend returned an empty one | IFileSystemBackend.GetFileInfo returns a new BackendFileInfo (length and times, null for a time the backend does not know); every backend and caller changed; Local returns null for a missing file or a directory |
| `VfsFileSystemBackend` and `CompositeVfsFileSystemBackend` `GetFullPath` only turned backslashes into slashes | Providers: GetFullPath resolves dot segments, merges repeated slashes, and throws UnauthorizedAccessException for a path that climbs above its root or out of the mount root (the Azure backend `GetFullPath` is a separate backend and was not changed) |
| `EconetController.GetStatistics` returned fixed zeros for requests, bytes and uptime, and `LiveDataService` pushed no Econet statistics; the Econet file server now counts requests, bytes in, bytes out, file operations and errors (`EconetServerStatistics`), and the API and the live push read it (zero when no Econet server runs in the process) | Econet statistics: the API and the live push report the real counters |
