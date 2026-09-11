# RetroIO.Protocols.Tnfs

## What it is

**TNFS** — the Trivial Network File System: a filesystem served over UDP, designed for 8-bit machines
with network adapters. Both halves are here: a server that serves any RetroIO volume, and the client
types a caller needs.

## Why you need it

It is how a real Atari, Commodore or ZX Spectrum with a modern network adapter (FujiNet and its
relatives) gets at files. Those machines cannot run SMB or NFS — there is no room and no stack — so TNFS
is deliberately small: a 4-byte header, little-endian, over UDP.

Serving it from RetroIO means the machine can reach **anything RetroIO can mount**: a directory, a disk
image, a remote share. The 8-bit side sees a filesystem and knows nothing about what is behind it.

Two things in here are more careful than the protocol requires, on purpose:

- **`PathValidator`** prevents directory traversal. A filesystem protocol that takes a path from the
  network and opens it is an obvious way to escape the served root, and the server is exposed to a local
  network where anything might be speaking.
- **Pooled buffers.** `TnfsBuffer` wraps pooled memory and returns it on disposal, and `TnfsMessage` uses
  it, so message processing allocates nothing per request. A server answering a steady stream of small
  UDP datagrams is exactly where per-message allocation shows up.

## What you can use it for

- **Serving files to a real 8-bit machine** — the main use.
- Serving a disk image, a native directory or a remote mount, identically.
- **Mounting** a TNFS server as a volume, through `RetroIO.Storage.Providers`.
- Authenticating mounts: the server raises `TnfsAuthenticationRequest` and the host answers, so
  authentication is the host's policy rather than the protocol's.
- **Testing without a network**: `InMemoryTransport` gives bidirectional channels, so the protocol can be
  driven fast and deterministically.
- Serving over TCP as well as UDP — `ITransport` has both.
- Reporting real traffic: `TnfsServerStatistics` is live counters, shared as a singleton with the
  management API.

## Prerequisites

| Requirement | Needed for | Notes |
|---|---|---|
| .NET SDK as pinned in `global.json` | Everything | |
| `RetroIO.Storage.Core` | Serving files | Through `IFileSystemBackend` |
| `RetroIO.Server.Core` | The backend and path validation | |
| A free UDP port | Serving | 16384 is the conventional TNFS port |
| A machine or emulator with a network adapter | A real end-to-end test | `tnfs-stress` otherwise |

## The server

```mermaid
flowchart TB
    M8["An 8-bit machine<br/>with a network adapter"]
    UDP["UdpTransport"]
    TCP["TcpTransport"]
    MEM["InMemoryTransport<br/>tests"]
    SVC["TnfsServerService<br/>coordinates transports"]
    SESS["SessionManager<br/>thread-safe, with cleanup"]

    SESSC["SessionCommandHandler<br/>MOUNT, UNMOUNT"]
    FILEC["FileCommandHandler<br/>STAT, OPEN, READ, WRITE"]
    DIRC["DirectoryCommandHandler<br/>OPENDIR, READDIR, CLOSEDIR"]

    VAL["PathValidator<br/>no escaping the root"]
    BACK["IFileSystemBackend<br/>a directory, an image, a remote mount"]

    M8 --> UDP --> SVC
    TCP --> SVC
    MEM --> SVC
    SVC --> SESS
    SVC --> SESSC
    SVC --> FILEC
    SVC --> DIRC
    FILEC --> VAL
    DIRC --> VAL
    VAL --> BACK

    classDef ext   fill:#E91E63,stroke:#AD1457,stroke-width:2px,color:#fff
    classDef sock  fill:#3F51B5,stroke:#283593,stroke-width:2px,color:#fff
    classDef core  fill:#009688,stroke:#00695C,stroke-width:2px,color:#fff
    classDef ok    fill:#4CAF50,stroke:#2E7D32,stroke-width:2px,color:#fff
    classDef q     fill:#FFA726,stroke:#F57C00,stroke-width:2px,color:#000

    class M8 ext
    class UDP,TCP,MEM sock
    class SVC,SESS core
    class SESSC,FILEC,DIRC ok
    class VAL q
    class BACK core
```

## A session

```mermaid
sequenceDiagram
    participant C as The 8-bit client
    participant S as TnfsServerService
    participant H as The host application
    participant B as IFileSystemBackend

    C->>S: MOUNT (location, user, password)
    S->>H: TnfsAuthenticationRequest
    H-->>S: TnfsAuthenticationResponse
    S-->>C: a session id
    C->>S: OPENDIR "/"
    S->>B: list
    B-->>S: entries
    S-->>C: a directory handle
    loop per entry
        C->>S: READDIR
        S-->>C: one entry
    end
    C->>S: OPEN, then READ
    S->>B: read
    B-->>S: bytes
    S-->>C: the data
    C->>S: UNMOUNT
```

## Contents

| Area | Types |
|---|---|
| Server | `TnfsServerService`, `TnfsServerOptions`, `TnfsServerExtensions`, `TnfsServerStatistics` |
| Sessions | `SessionManager`, `TnfsSession`, `SessionStatistics`, `DirectoryHandle` |
| Commands | `SessionCommandHandler`, `FileCommandHandler`, `DirectoryCommandHandler` |
| Wire | `TnfsHeader` (4 bytes, little-endian), `TnfsMessage`, `TnfsBuffer` (pooled) |
| Transports | `ITransport`, `UdpTransport`, `TcpTransport`, `InMemoryTransport`, `TransportCapabilities` |
| Authentication | `TnfsAuthenticationRequest`, `TnfsAuthenticationResponse`, `TnfsAuthenticateEventHandler` |
| Client-side results | `TnfsResult<T>`, `TnfsDirectoryEntry`, `TnfsFileStat`, `TnfsVersionInfo`, `TnfsFreeSpace`, `TnfsDirEntryExtended`, `TnfsOpenDirXResult`, `TnfsReadDirXResult` |

## Endianness

**All multi-byte TNFS values are little-endian** — the opposite of most of the ND protocols in this repo.
`EndianHelper` in `RetroIO.Server.Core` has the read and write helpers; use them rather than assuming the
host's order.

## Related

- [`tnfs-stress`](../../tools/RetroIO.Tools.TnfsStress/README.md) — load and soak a server.
- `TnfsVolumeProvider` in [`RetroIO.Storage.Providers`](../RetroIO.Storage.Providers/README.md) — the
  client side, as a mountable volume.
- The management API exposes the live statistics; see
  [`RetroIO.Management.Api`](../RetroIO.Management.Api/README.md).
