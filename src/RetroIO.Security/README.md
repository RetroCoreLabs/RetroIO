# RetroIO.Security

## What it is

The security **abstractions**: authentication, authorisation, auditing, rate limiting and path validation — as
interfaces and request/result types, with no implementation.

## Why you need it

Because a server that exposes a filesystem to a network needs all five, and because they must be
**replaceable**. A home lab wants a JSON file; somebody else wants an enterprise directory. Interfaces here,
implementations in [`RetroIO.Security.Implementation`](../RetroIO.Security.Implementation/README.md), means
either is a composition choice rather than a fork.

The one that is not optional is `PathValidator`. A protocol that takes a path from the network and opens it is
the classic way to escape a served root, and every protocol in RetroIO does exactly that. Validating paths is
not a hardening step to add later; it is the difference between serving a directory and serving the disk.

`RateLimitConfig` uses a **token bucket** — which matters because the alternative, a fixed window, lets a
caller send a whole window's worth in one burst at the boundary.

## What you can use it for

- Authenticating through a provider of your choosing (`IAuthenticationProvider`).
- Authorising an operation, with ACLs and inheritance (`IAuthorizationProvider`, `AccessControlEntry`,
  `AclInheritanceFlags`).
- **Auditing** with typed entries — authentication, authorisation and file access each have their own — so an
  audit log can be read by a program.
- Rate limiting per user, per address or server-wide.
- **Validating a path** before opening it.
- Mapping an external identity onto an internal user (`IIdentityProvider`, `ExternalIdentityMapping`).

## Prerequisites

| Requirement | Notes |
|---|---|
| .NET SDK as pinned in `global.json` | |
| Nothing else | These are interfaces; `RetroIO.Security.Implementation` supplies the behaviour |

## The pipeline

```mermaid
flowchart TB
    REQ["A request from the network"]
    PATH{"PathValidator:<br/>does the path stay inside the root?"}
    AUTH{"IAuthenticationProvider:<br/>who is this?"}
    PRIN["SecurityPrincipal"]
    RATE{"IRateLimiter:<br/>too many, too fast?"}
    AUTHZ{"IAuthorizationProvider:<br/>may they do THIS?"}
    DO["Carry out the operation"]
    DENY["Refused"]
    AUDIT["ISecurityAuditLogger<br/>records the outcome either way"]

    REQ --> PATH
    PATH -->|"no"| DENY
    PATH -->|"yes"| AUTH
    AUTH -->|"no"| DENY
    AUTH -->|"yes"| PRIN --> RATE
    RATE -->|"over the limit"| DENY
    RATE -->|"within it"| AUTHZ
    AUTHZ -->|"no"| DENY
    AUTHZ -->|"yes"| DO
    DO --> AUDIT
    DENY --> AUDIT

    classDef core fill:#009688,stroke:#00695C,stroke-width:2px,color:#fff
    classDef q    fill:#FFA726,stroke:#F57C00,stroke-width:2px,color:#000
    classDef ok   fill:#4CAF50,stroke:#2E7D32,stroke-width:2px,color:#fff
    classDef bad  fill:#F44336,stroke:#C62828,stroke-width:2px,color:#fff

    class REQ,PRIN core
    class PATH,AUTH,RATE,AUTHZ q
    class DO ok
    class DENY bad
    class AUDIT core
```

**Both outcomes are audited.** A log that records only failures cannot answer "who read that file?", and one
that records only successes cannot show an attack.

## Contents

| Area | Types |
|---|---|
| Authentication | `IAuthenticationProvider`, `AuthenticationRequest`, `AuthenticationResult`, `AuthenticationMethod`, `AuthenticationFailureReason` |
| Principals | `SecurityPrincipal`, `SecurityPrincipalConstants` |
| Authorisation | `IAuthorizationProvider`, `AuthorizationRequest`, `AuthorizationResult`, `AccessControlEntry`, `PathPermissions`, `AclInheritanceFlags`, `FileSystemOperation` |
| Auditing | `ISecurityAuditLogger`, `SecurityAuditEntry`, `AuthenticationAuditEntry`, `AuthorizationAuditEntry`, `FileAccessAuditEntry`, `SecurityEventType`, `SecurityEventSeverity` |
| Rate limiting | `IRateLimiter`, `RateLimitKey`, `RateLimitConfig` (token bucket), `RateLimitResult` |
| Paths | `PathValidator`, `PathValidationResult` |
| External identity | `IIdentityProvider`, `IdentityProviderType`, `ExternalIdentityMapping`, `InternalUser`, `OAuthTokenInfo` |

## Related

- [`RetroIO.Identity`](../RetroIO.Identity/README.md) — RetroIDP, which supplies the users.
- [`RetroIO.Security.Implementation`](../RetroIO.Security.Implementation/README.md) — the concrete providers.
- [`RetroIO.Security.Firewall`](../RetroIO.Security.Firewall/README.md) — rule-based access control in front of
  all of this.
