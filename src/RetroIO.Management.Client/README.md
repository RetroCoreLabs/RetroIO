# RetroIO.Management.Client

## What it is

A typed client for the management API — both halves. `ManagementApiClient` for the REST calls,
`ManagementHubClient` for the SignalR push channel.

## Why you need it

So a client application contains no HTTP plumbing, no string URLs and no hand-written SignalR subscriptions —
and, more importantly, so it does **not reference the server**. Together with
[`RetroIO.Management.Contracts`](../RetroIO.Management.Contracts/README.md), which depends on nothing, a desktop
admin app can talk to a RetroIO server without shipping the protocol stack it is administering.

`ProbeResult` is the small thing that matters in practice. A client needs to answer "is there a server at this
address, and can I reach it?" **before** asking the user for credentials — otherwise a wrong port and a wrong
password look the same.

It also puts the token in the right place on both surfaces. The REST calls use an `Authorization: Bearer`
header; the hub connection puts the token in the `access_token` query string, because a WebSocket handshake
cannot carry a custom header. Doing that by hand is the classic way to end up with working REST and a hub that
never connects.

## What you can use it for

- Writing an admin tool, a dashboard or a script against typed methods.
- **Probing** an endpoint before logging in.
- Receiving live status and statistics without writing SignalR code.
- Reconnecting: the hub client owns that, so the application does not have to.

## Prerequisites

| Requirement | Needed for | Notes |
|---|---|---|
| .NET SDK as pinned in `global.json` | Everything | |
| `RetroIO.Management.Contracts` | The DTOs | |
| A reachable management API | Anything | |
| Credentials | Anything past probing | A RetroIDP user |
| HTTPS where it is not local | Safety | The token is a credential |

## Both surfaces, one client

```mermaid
flowchart TB
    APP["RetroAdmin, a script,<br/>your own tool"]
    API["ManagementApiClient<br/>typed REST"]
    HUB["ManagementHubClient<br/>live push over SignalR"]
    PROBE["ProbeResult<br/>is anything there?"]
    SERVER["The management API"]

    APP --> PROBE --> SERVER
    APP --> API -->|"Authorization: Bearer"| SERVER
    APP --> HUB -->|"?access_token=..."| SERVER

    classDef app  fill:#2196F3,stroke:#1565C0,stroke-width:2px,color:#fff
    classDef core fill:#009688,stroke:#00695C,stroke-width:2px,color:#fff
    classDef q    fill:#FFA726,stroke:#F57C00,stroke-width:2px,color:#000
    classDef sock fill:#3F51B5,stroke:#283593,stroke-width:2px,color:#fff

    class APP app
    class API,HUB core
    class PROBE q
    class SERVER sock
```

## The order to do things in

```mermaid
flowchart TB
    START["Start"]
    P{"Probe: is a server there?"}
    ASK["Ask the user for credentials"]
    LOGIN{"Login accepted?"}
    TOKEN["Hold the token"]
    REST["REST calls"]
    CONNECT["Open the hub with the token<br/>in the query string"]
    FAIL["Report 'no server at this address'<br/>- NOT 'wrong password'"]
    BAD["Report 'wrong credentials'"]

    START --> P
    P -->|"no"| FAIL
    P -->|"yes"| ASK --> LOGIN
    LOGIN -->|"no"| BAD
    LOGIN -->|"yes"| TOKEN
    TOKEN --> REST
    TOKEN --> CONNECT

    classDef core fill:#009688,stroke:#00695C,stroke-width:2px,color:#fff
    classDef q    fill:#FFA726,stroke:#F57C00,stroke-width:2px,color:#000
    classDef ok   fill:#4CAF50,stroke:#2E7D32,stroke-width:2px,color:#fff
    classDef bad  fill:#F44336,stroke:#C62828,stroke-width:2px,color:#fff

    class START,ASK,TOKEN core
    class P,LOGIN q
    class REST,CONNECT ok
    class FAIL,BAD bad
```

Probing first is what lets the two failures be reported differently — and "no server at this address" is a much
more useful message than "login failed".

## Contents

| Type | Role |
|---|---|
| `ManagementApiClient` | Typed HTTP client for the REST API |
| `ProbeResult` | Whether a server is reachable at an endpoint |
| `ManagementHubClient` | The live push channel over SignalR |
