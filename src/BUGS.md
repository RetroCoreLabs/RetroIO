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
| `RetroIO.Protocols.Xmsg.Api.Node` `XmsgKernelServer.Handle` | Empties the outgoing queue before delivering, so datagrams that `DrainPending` has not collected are dropped |
| `RetroIO.Protocols.Econet` `SetObjectInfoOperation` | Checks for at least 10 parameter bytes, then reads bytes 10 and 11: `IndexOutOfRangeException` on a short block. Load and exec bytes are also read from the offset the path is read from |
| `RetroIO.Protocols.Econet` `EconetActivityLog` | A negative `count` throws `OverflowException` |
| `RetroIO.Protocols.Econet` `GetRandomAccessInfoOperation` | Any function value other than 0 returns the file length, not only 1 or 2 |
| `RetroIO.Protocols.Econet` `FileServerSessionManager.AllocateHandle`, `FileIoCommandHandler.HandleOpen` | One server-wide pool of handles 4 to 32, not one per station. `HandleOpen` opens the stream first (`FileMode.Create` when the file need not exist, which truncates it) and asks for a handle afterwards; if the handle is refused the file is already truncated and the client is told "Already open". Needs a per-station handle table |
| `RetroIO.Protocols.Econet` `FileServerSessionManager.GetSessionIndex` | Returns 0 both for "not found" and for the session in slot 0 |
| `RetroIO.Protocols.Econet` `FileIoCommandHandler.HandleDelete` | Deletes with `File.Delete`/`Directory.Delete` on the host disk, after asking the backend whether the path exists. `IFileSystemBackend` has no delete method, so with any backend other than the local disk it deletes nothing and still answers success. Needs the interface to grow (a public API change) |
| `RetroIO.Protocols.Econet` `RenameCommand` | Moves with `File.Move`/`Directory.Move` directly instead of the backend; requires the new name to resolve, which may answer "Not found" for a name that does not exist yet |
| `RetroIO.Protocols.Econet` `EconetUserBootstrap` | Class comment says it purges legacy orphan keys; the code only adds defaults |
| `RetroIO.Protocols.Econet` `FileServerReply.DiscFreeSpaceClassic1983` | Existing comment has a `param` and no `returns` |
| `RetroIO.Protocols.Xmsg.Api` `XmsgKernel` | `WirePort` has no incarnation bits |
| `RetroIO.Link` `ILapbCapable` counters | `Retransmissions`, `RejectionsSent`, `RejectionsReceived`, `T1Timeouts`, `T2Timeouts` are never set anywhere |
| `RetroIO.Link` `EthernetLink.SendData`, `LapbLayerAdapter.SendData` | Return true when the send window is full and the datagram is parked; the interface says false |
| `RetroIO.Link` `XotLink.SendData` | In RFC 1613 mode returns true with no connection or when the codec rejects the length (only logged) |
| `RetroIO.Storage.Local` `CommodoreDiskDriver.Capabilities` | Compares the descriptor by reference with a new `D81` instance on each call, so the subdirectory flag is never set for D81. **Confirmed by reading the code** |
| `RetroIO.Storage.Local` `FfsDriver.SelectPartition` | Returns true for any letter a to h without checking the disk label |
| `RetroIO.Storage.Local` `BsdDriver`, `FfsDriver` partition select after mount | Stores the letter only; the mounted file system keeps the old partition |
| `RetroIO.Storage.Local` `PcFloppyDriver` | `CanWrite` and the write capability flags stay true on a read-only mount |
| `RetroIO.Storage.Local` `DecbFileSystem`, `DragonDosFileSystem` `ListDirectory` | Throw `NotImplementedException` for a non-root path; the interface says `DirectoryNotFoundException` |
| `RetroIO.Storage.Local` `CbmDosFileSystem` | `WriteFile`/`DeleteFile`/`Rename` throw `InvalidOperationException` on a read-only device, not `IOException`; `ListDirectory` ignores its path |
| `RetroIO.Storage.Local` `Fat32FileSystem.VolumeLabel` | Never read; always an empty string |
| `RetroIO.Storage.Local` `Fat12/16/32FileSystem.Rename` | Moving a directory to another parent does not rewrite its `..` entry |
| `RetroIO.Storage.Local` `DeltaBlockDevice` constructor | `applyDeltaOnMount` is stored and never used |
| `RetroIO.Storage.Ndfs` `NdfsFileSystem.VolumeLabel` | Returns `"NDFS"` before a master block is read; the contract says an empty string |
| `RetroIO.Storage.Ndfs` `NdfsDriver.CanWrite` | Always true, also for a read-only mount. **Confirmed by reading the code** |
| `RetroIO.Storage.Providers` `VfsFileSystemBackend.GetFileInfo` | Returns a `FileInfo` for a temp file that is already deleted; only the length and write time are real |
| `RetroIO.Storage.Providers`, both backends `GetFullPath` | Only turns backslashes into slashes; the contract says normalized and resolved |
| `RetroIO.Server.Hosting` `ProtocolConfigurationLoader` | Always rewrites the config file but says "Created default ... configuration" |
| `RetroIO.Management.Api` `LiveDataService` | TNFS and Econet statistics are pushed as fixed zeros (the log-line half of this entry is fixed) |
| `RetroIO.Protocols.Tnfs` `TnfsConstants` | `MaxPathLength` is 256 but `MAX_FILEPATH`/`MAX_TNFSPATH` are 1024; `TnfsHeader` exists twice in two namespaces |
| `RetroIO.Protocols.X25` `X3Parameters` against `X3` | Disagree on parameter 12 (`FLOW_CONTROL_X_ON_X_OFF` or `FORWARD_ON_N_CHARACTERS`) and on the `DATA_FORWARDING` default (2 or 13) |
| `RetroIO.Protocols.X25` `PadClient`, `TadServer` | `IdleTimeoutMs`, `AutoConnect`, `EnableLoopbackTest` are never read; `XMSGSessionState.DataTransfer` is never assigned |

### S - stubs that report success while doing nothing

| Where | What |
|---|---|
| `RetroIO.Protocols.X25` `XotServerTransport.AcceptCall/RejectCall`, `XotClientTransport` | Bump counters and return true; send no packet. The XOT classes are not implemented (see the README) |
| `RetroIO.Protocols.X25.Runtime` `SerialTransport.AcceptCall/RejectCall/PlaceCall` | Log, set success true, send nothing; `PlaceCall` always uses LCN 1 |
| `RetroIO.Protocols.X25.Runtime` `SerialTransport`, `PhysicalLayer`, `XotClientTransport` | `IncomingCall` and `DataReceived` are never raised |
| `RetroIO.Protocols.X25` `X28` | `CharacterProcessed` and `StatusUpdate` events are never raised |
| `RetroIO.Protocols.X25.Sintran` `SintranPvcServer.SendX25Data/SendX25Interrupt` | Always throw `NotImplementedException` (documented that way) |
| `RetroIO.Protocols.X25.Client` `X25ClientPadTransport` | `callingAddress`, `lcn`, `mBit`, `dBit` are ignored |
| `RetroIO.Protocols.Xmsg.Api` `RrClient.RequestConnection` | `connection` always 0, `systemName` ignored |
| `RetroIO.Protocols.Econet` `PrivCommand`, `SetDiscMaskCommand`, `SetHomeCommand`, `SetLibCommand`, `SetBootOptOperation` | Ignore the result of the user-store call and always reply success |
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
| `StatusChanged` in `CodecLink`, `SerialLink`, `TcpLapbLink`, `XotLink`, `FakeLink` | Fires on every status set, even when it did not change; `ILink` says only on transitions. Decide by looking at who subscribes |
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
