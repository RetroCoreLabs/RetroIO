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
| `RetroIO.Protocols.Econet` `EconetUserBootstrap` | Class comment says it purges legacy orphan keys; the code only adds defaults |
| `RetroIO.Protocols.Econet` `FileServerReply.DiscFreeSpaceClassic1983` | Existing comment has a `param` and no `returns` |
| `RetroIO.Protocols.Xmsg.Api` `XmsgKernel` | `WirePort` has no incarnation bits |
| `RetroIO.Link` `ILapbCapable` counters | `Retransmissions`, `RejectionsSent`, `RejectionsReceived`, `T1Timeouts`, `T2Timeouts` are never set anywhere |
| `RetroIO.Storage.Local` `BsdDriver`, `FfsDriver` partition select after mount | Stores the letter only; the mounted file system keeps the old partition |
| `RetroIO.Storage.Local` `CbmDosFileSystem.ListDirectory` | Ignores its `path` argument |
| `RetroIO.Storage.Local` `DeltaBlockDevice` constructor | `applyDeltaOnMount` is stored and never used |
| `RetroIO.Storage.Providers` `VfsFileSystemBackend.GetFileInfo` | Returns a `FileInfo` for a temp file that is already deleted; only the length and write time are real |
| `RetroIO.Storage.Providers`, both backends `GetFullPath` | Only turns backslashes into slashes; the contract says normalized and resolved |
| `RetroIO.Management.Api` `LiveDataService` | TNFS and Econet statistics are pushed as fixed zeros (the log-line half of this entry is fixed) |
| `RetroIO.Protocols.Tnfs` `TnfsConstants` | `MaxPathLength` is 256 but `MAX_FILEPATH`/`MAX_TNFSPATH` are 1024; `TnfsHeader` exists twice in two namespaces |
| `RetroIO.Protocols.X25` `X3Parameters` against `X3` | Disagree on parameter 12 (`FLOW_CONTROL_X_ON_X_OFF` or `FORWARD_ON_N_CHARACTERS`) and on the `DATA_FORWARDING` default (2 or 13) |
| `RetroIO.Protocols.X25` `PadClient`, `TadServer` | `IdleTimeoutMs`, `AutoConnect`, `EnableLoopbackTest` are never read; `XMSGSessionState.DataTransfer` is never assigned |

### S - stubs that report success while doing nothing

| Where | What |
|---|---|
| `RetroIO.Protocols.X25.Runtime` `SerialTransport`, `PhysicalLayer`, `XotClientTransport` | `IncomingCall` and `DataReceived` are never raised |
| `RetroIO.Protocols.X25` `X28` | `CharacterProcessed` and `StatusUpdate` events are never raised |
| `RetroIO.Protocols.X25.Sintran` `SintranPvcServer.SendX25Data/SendX25Interrupt` | Always throw `NotImplementedException` (documented that way) |
| `RetroIO.Protocols.Xmsg.Api` `RrClient.RequestConnection` | `connection` is always 0 (documented, and the tests rely on it). A named remote system is now refused |
| `RetroIO.Protocols.Econet` `EconetFileServerOptions.Station` | Not read anywhere |
| `RetroIO.Protocols.Econet` `FileServerConfiguration` flag bits | Stored and echoed, change no behaviour; PiFS default is 0x82, here 0x30; PiFS sends the flags as four bytes |
| `RetroIO.Protocols.Tnfs` `TcpTransport.Capabilities` | Reports a 1000-connection limit nothing enforces |

### Q - needs the real specification first

| Where | What |
|---|---|
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

| Where | What |
|---|---|
| GitHub `build` workflow | 53 XMSG tests read recordings of real terminal sessions that are not in this repository; they fail there |
| `RetroIO.Protocols.X25.Tests` `CClient_CallsThroughTheCSharpSwitch_AndItsDataArrives` | Fails on this machine and on the commit before today's changes: the C client gets no data back. Cause not found |

## Fixed

| What | Commit subject |
|---|---|
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
