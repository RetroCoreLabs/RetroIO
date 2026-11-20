# RetroIO

**Old computers, modern code.** RetroIO is a .NET library that lets today's software read the disks
and speak the networks of machines built between 1975 and 1995: Commodore, Dragon, Tandy, IBM PC,
Acorn, Sun, UNIX on the PDP-11, and the Norsk Data minicomputers.

The hard part of working with old machines is never the idea; it is the formats. Each one is
described in a manual that is thirty years old, leaves out the detail you need and disagrees with the
real disks. RetroIO has done that work once, checked it against real disk images, real captures and
real machines, and packed it into small libraries you can drop into an emulator, a file manager, a
server or a preservation tool.

The source, the issues and the releases are on [GitHub](https://github.com/RetroCoreLabs/RetroIO).
The packages are built from there; they are not on nuget.org yet.

## Disk images: open any old disk like a folder

One set of calls works for every format: list the directory, read a file, write a file, delete,
rename, make a new blank disk. Your program does not need to know where a Commodore keeps its block
map or how DragonDOS chains its sectors; it asks for the files and gets them.

- **Safe by default.** Writes can go to a separate overlay file, so the original image stays byte for
  byte as it was. Try anything, keep the original.
- **Whole hard disks too.** RetroIO finds the partitions in a disk image (MBR, GPT, BSD disk labels,
  Sun VTOC) and opens each one.
- **New disks that pass the real checks.** Blank Commodore images pass the checks of VICE's c1541
  disk tool; blank PC images are proper FAT disks.

| Machine | Formats | Support |
|---|---|---|
| Commodore 64 / 128 (1541, 1571, 1581 drives) | D64, D71, D81 | read, write, create |
| Commodore 64 | T64 tape archives, TAP tape recordings, CRT cartridges, PRG and P00 files | read |
| Dragon 32 / 64 | DragonDOS on VDK, JVC (.dsk) and DMK images | read, write |
| Tandy Color Computer (CoCo) | Disk Extended Color BASIC (DECB) | read, write |
| Dragon and CoCo running OS-9 / NitrOS-9 | OS-9 RBF | read, write |
| IBM PC and compatibles | FAT12, FAT16, FAT32: floppies from 360 KB to 2.88 MB, hard disks as .img, .hdd and fixed .vhd | read, write, create; raw and VHD converted both ways |
| Norsk Data ND-100 / ND-500 (SINTRAN III) | NDFS on floppies, SMD, Winchester and SCSI disk packs | read, write, create |
| PDP-11 and others | UNIX V7, 2.11BSD | read |
| Sun 2 (SunOS) and 4.2BSD | Berkeley FFS (UFS) | read |

## Norsk Data disks: a complete toolkit

NDFS is the filesystem SINTRAN III boots from. RetroIO knows it in depth: users and quotas, the
three-level permission model, the boot sector and the commands SINTRAN runs at start-up. On top of
that come the tools a museum or a collector needs: analyse a disk pack, repair a damaged one, clear a
lost password, and create a new image from scratch.

## File servers: share your files with real and emulated machines

Put your PC's files in front of an old machine, with no floppies and no serial cables. A RetroIO
server can share a folder, a disk image, an FTP or SFTP site or an Azure storage container, and the
old machine sees it as its own network drive.

- **TNFS** for 8-bit machines with a network adapter: a server for any RetroIO volume, and the client.
- **Econet** for the Acorn BBC Micro and Master: a full Level-3 file server over AUN. Log in with
  `*I AM`, catalogue, load, save, get the time, the way it worked on a school network in 1985.
- **DriveWire 4** for the Tandy CoCo and the Dragon running NitrOS-9: disk images served over TCP,
  which is how an emulator's Becker port connects.

Run it with a management API (REST with live updates), users and groups, a firewall, rate limits and
an audit log, so a shared server is safe to leave running.

## X.25: build a packet network

Before the internet, banks, airlines and universities talked over X.25. RetroIO lets you build that
network again, from a single link to a multi-switch network: the packet layer, LAPB and HDLC links, a
switching engine with routing, call records and accounting, the PAD that lets a terminal dial out
(X.3, X.28, X.29), X.121 addresses, monitoring, and XOT to carry X.25 over TCP. The XOT side has been
tested against a Cisco router, and the packet layer shares one set of conformance tests with a
separate C implementation, so the two can never quietly disagree.

## Norsk Data networks: join them as one more machine

XMSG is the messaging system that SINTRAN III and COSMOS are built on. With RetroIO a C# program can
take a place on that network as a full node, over Ethernet or HDLC lines:

- terminal sessions to and from SINTRAN machines (`connect-to`),
- the COSMOS file server, so a SINTRAN machine can list and read files on your PC,
- folder sync between a Windows folder and a SINTRAN user,
- chat rooms that span several machines,
- the name service and the kernel API a SINTRAN program would call.

## Where to start

| You want to... | Start with |
|---|---|
| read or write disk images | `RetroIO.Storage.Local`, over `RetroIO.Storage.Core` |
| work with Norsk Data disks | `RetroIO.Storage.Ndfs` |
| treat folders, FTP, SFTP, TNFS and Econet shares like disk images | `RetroIO.Storage.Providers` |
| run a file server for old machines | `RetroIO.Server` with `RetroIO.Protocols.Tnfs`, `.Econet` or `.DriveWire` |
| build an X.25 network | `RetroIO.Protocols.X25` and `RetroIO.Protocols.X25.Switch` |
| join a Norsk Data network | `RetroIO.Protocols.Xmsg.Node` |

[Packages and guides](packages/index.md) has the README of every package: what it is, why it exists
and how to use it. The [API reference](api/index.md) lists every namespace, type and member.
