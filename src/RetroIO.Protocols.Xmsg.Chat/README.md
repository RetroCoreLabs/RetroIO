# RetroIO.Protocols.Xmsg.Chat

## What it is

A chat service over XMSG: rooms, users, and **trunks** between nodes so rooms span more than one system.

## Why you need it

Partly because it is genuinely useful on a multi-machine network, and partly because it is the clearest
**end-to-end exercise** of the XMSG stack that is not a file transfer.

A chat service needs exactly the things that are easy to get subtly wrong: look a named service up and join it,
hold several conversations at once, deliver messages in order, notice when a peer has gone, and keep working
when a node in the middle restarts. If chat works between three machines, the name service, the sessions, the
sequences and the relay all work.

`ChatTrunkState` is "what a node **believes** about a peer it is trunked to" — which is the honest framing. A
node cannot know a peer is up; it can only know what it last heard, and `ChatTrunkTick` is what keeps that
belief current.

## What you can use it for

- A chat service across one or several ND machines.
- Joining a service **by name** through XROUT, which is the normal way a program is found.
- Exercising the XMSG stack end to end without moving files.
- Holding recent lines for a client that joins late (`ChatLineMemory`).
- Using ND calendar time correctly in timestamps (`NdCalendarTime`).

## Prerequisites

| Requirement | Needed for | Notes |
|---|---|---|
| .NET SDK as pinned in `global.json` | Everything | |
| `RetroIO.Protocols.Xmsg.Api` | The kernel and the name lookup | |
| A node | Anything live | |
| A registered service name | Clients finding the server | Through XROUT |
| Two or more nodes | Trunking | One node works on its own |

## Rooms, and trunks between nodes

```mermaid
flowchart TB
    subgraph N1["Node 1"]
        S1["ChatServer"]
        R1["ChatRoom 'general'"]
        C1["ChatClient - a user"]
        C2["ChatClient - a user"]
    end

    subgraph N2["Node 2"]
        S2["ChatServer"]
        R2["ChatRoom 'general'"]
        C3["ChatClient - a user"]
    end

    C1 --> S1 --> R1
    C2 --> S1
    C3 --> S2 --> R2
    S1 <-->|"ChatTrunks:<br/>what each node BELIEVES<br/>about the other"| S2

    classDef app  fill:#2196F3,stroke:#1565C0,stroke-width:2px,color:#fff
    classDef core fill:#009688,stroke:#00695C,stroke-width:2px,color:#fff
    classDef ok   fill:#4CAF50,stroke:#2E7D32,stroke-width:2px,color:#fff

    class C1,C2,C3 app
    class S1,S2 core
    class R1,R2 ok
```

## Joining

```mermaid
sequenceDiagram
    participant U as ChatClient
    participant X as XROUT
    participant S as ChatServer

    U->>X: look up the service by name
    X-->>U: its system and port
    U->>S: join a room
    S-->>U: recent lines from ChatLineMemory
    U->>S: a message
    S-->>U: it, and everyone else's
    Note over S: a trunked peer gets it too
    U->>S: leave
```

## Contents

| Type | Role |
|---|---|
| `ChatServer` | The service |
| `ChatClient` | One user: joins by name, then talks to the service directly |
| `ChatRoom`, `ChatRooms` | A room, and the set of them |
| `ChatMessage`, `ChatMessageKind`, `ChatMessageKinds` | The message types exchanged |
| `ChatLineMemory` | Recent lines, for a client that joins late |
| `ChatTrunks`, `ChatTrunkState`, `ChatTrunkTick` | Links between nodes, and what each believes about the other |
| `NdCalendarTime` | ND calendar time |

## Tests

`src/RetroIO.Protocols.Xmsg.Chat/tests`.
