# RetroIO.Protocols.X25.Routing

## What it is

The X.121 routing table: the thing that answers "which port does this address live behind?" It holds
four kinds of entry and evaluates them in a fixed order of precedence, and it supports hunt groups.

## Why you need it

A switch receives a call to an **address** and has to choose a **port**. That choice is not a simple
lookup, because real networks need several kinds of rule at once: one exact address that goes
somewhere special, a block of addresses that leave by a trunk, a whole foreign network reached by its
DNIC, and a default for everything else.

The part that must not be got wrong is the **order**. A more specific rule has to win over a general
one, or the default route swallows everything and nothing else ever matches. That precedence is
encoded in `X121RouteType` rather than left to the order entries happen to be added in.

## What you can use it for

- Resolving a called address to a port, with the precedence already right.
- Spreading calls across a set of ports with a **hunt group**, round-robin — which is how a pool of
  receiving lines is reached by one configured name.
- Holding a routing table to show in a management CLI or a GUI, without a switch attached.

## Prerequisites

| Requirement | Notes |
|---|---|
| .NET SDK as pinned in `global.json` | |
| No transport, no sockets | It is a lookup table; it carries no traffic |

## Precedence, which is the point

```mermaid
flowchart TB
    ADDR["A called X.121 address"]
    PVC{"A permanent circuit<br/>for this address?"}
    EXACT{"An exact match?"}
    HUNT{"A hunt group?"}
    PREFIX{"The longest matching prefix?"}
    DNIC{"A DNIC route?"}
    DEF{"A default route?"}
    PORT["The egress port"]
    NONE["No route:<br/>clear the call, not obtainable"]

    ADDR --> PVC
    PVC -->|"yes"| PORT
    PVC -->|"no"| EXACT
    EXACT -->|"yes"| PORT
    EXACT -->|"no"| HUNT
    HUNT -->|"yes, pick the next member"| PORT
    HUNT -->|"no"| PREFIX
    PREFIX -->|"yes"| PORT
    PREFIX -->|"no"| DNIC
    DNIC -->|"yes"| PORT
    DNIC -->|"no"| DEF
    DEF -->|"yes"| PORT
    DEF -->|"no"| NONE

    classDef core fill:#009688,stroke:#00695C,stroke-width:2px,color:#fff
    classDef q    fill:#FFA726,stroke:#F57C00,stroke-width:2px,color:#000
    classDef ok   fill:#4CAF50,stroke:#2E7D32,stroke-width:2px,color:#fff
    classDef bad  fill:#F44336,stroke:#C62828,stroke-width:2px,color:#fff

    class ADDR core
    class PVC,EXACT,HUNT,PREFIX,DNIC,DEF q
    class PORT ok
    class NONE bad
```

**Most specific first.** That ordering is why a default route can exist at all.

## Contents

| Type | Role |
|---|---|
| `X121RoutingTable` | The table: add entries, resolve an address |
| `X121Route` | One entry |
| `X121RouteType` | The kind of entry **and its precedence** |
| `X121HuntGroup` | A set of member ports a hunt route distributes calls across, round-robin |

## Hunt groups

A hunt group turns one configured name into several ports tried in turn. It is how a bank of
receiving lines was reached historically: the caller dials one number, and whichever line is free
answers.

```mermaid
flowchart LR
    CALL["A call to the<br/>hunt group's address"]
    HG["X121HuntGroup<br/>round-robin"]
    P1["Port 3"]
    P2["Port 4"]
    P3["Port 5"]

    CALL --> HG
    HG -->|"first free"| P1
    HG --> P2
    HG --> P3

    classDef core fill:#009688,stroke:#00695C,stroke-width:2px,color:#fff
    classDef ok   fill:#4CAF50,stroke:#2E7D32,stroke-width:2px,color:#fff

    class CALL,HG core
    class P1,P2,P3 ok
```
