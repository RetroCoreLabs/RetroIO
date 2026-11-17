# X.121 addresses and X.25 call routing

How an X.25 address is built, how a local number differs from an international one, and how a switch
should decide where a call goes. Every rule below cites its clause in the ITU-T text:

- [ITU-T X.121](https://www.itu.int/rec/T-REC-X.121/en) (10/2000) - the international numbering plan
- [ITU-T X.25](https://www.itu.int/rec/T-REC-X.25/en) (10/96) - the DTE/DCE interface, address field and clearing causes
- [ITU-T X.75](https://www.itu.int/rec/T-REC-X.75/en) (10/96) - the signalling between public networks (the gateway link)

## 1. What an address is made of

An **international data number** is `DNIC + NTN`, or `DCC + NN` where a country has one numbering
scheme for all its networks (X.121 7.6.1).

```mermaid
flowchart LR
    subgraph DNIC["DNIC - 4 digits (7.2.3)"]
        direction LR
        Z["Z<br/>zone 2-7"] --> XX["X X<br/>rest of DCC"] --> N["X<br/>network digit"]
    end
    N --> NTN["NTN - up to 10 digits<br/>the number inside that network"]

    style Z fill:#3F51B5,stroke:#3F51B5,color:#fff
    style XX fill:#3F51B5,stroke:#3F51B5,color:#fff
    style N fill:#9C27B0,stroke:#9C27B0,color:#fff
    style NTN fill:#009688,stroke:#009688,color:#fff
```

| Part | Digits | Meaning | Clause |
|---|---|---|---|
| DCC (Data Country Code) | 3 | The country; the first digit is the zone | 7.2.3, 7.2.7 |
| Network digit | 1 | Which network in that country (up to 10 per DCC) | 7.2.4 |
| DNIC (Data Network Identification Code) | 4 | DCC + network digit | 7.2.3 |
| NTN (Network Terminal Number) | up to 10 | The full number used **inside** the network, no prefix | 7.6.2, 7.7 |
| NN (National Number) | up to 11 | Same idea, used after a bare DCC | 7.6.2, 7.7 |
| International number | 5 to 14 | DNIC + NTN, or DCC + NN | 7.7 |

**The first digit** (Table 1/X.121, 7.2.2):

| First digit | Meaning |
|---|---|
| 1 | Global: mobile satellite (`111S`, 7.4) and global public data networks (7.5) |
| 2 - 7 | A country or region; the digit is the geographic zone (7.2.7) |
| 8, 9, 0 | **Escape codes**, not part of any DNIC: the rest is from another numbering plan (7.9, Table 2) |

**The X.25 address field** holds at most **15 digits including any prefix and escape code** in the normal
(non-TOA/NPI) format (X.25 5.2.1). That is why a prefix for international format can be only one digit:
14 + 1 = 15 (X.121 7.8, note).

## 2. Local and international dialling

A DTE may dial a short **local** number (the NTN alone) or a full **international** number. A national
**prefix** tells the two apart. The prefix is not part of the address and is **never signalled across a
network boundary** (X.121 7.8.1); what it is, is a national matter.

| Dialled | Reads as | Notes |
|---|---|---|
| `110006` | NTN `110006` in our own network | A local SINTRAN number; the switch adds its own DNIC |
| `02402001487` | prefix `0` + DNIC `2402` + NTN `001487` | From the Tor address book; DCC `240` is Sweden in Annex J, network digit `2` |

Annex J lists only the DCCs; which network carries the digit `2` is in the ITU's published DNIC list, not
in X.121 itself.

## 3. Our own DNIC

Which DNIC is "ours" is **configured**, not derived. A switch can own several (a country may get more
DCCs, 7.2.6), and networks may **share one DNIC and split it on the 5th digit**: X.121 Annex A.2 gives
the example of DNIC `7501` shared, with `75011` for one network and `75012`, `75013` for another.

## 4. How a call should be routed

```mermaid
flowchart TD
    IN["Call request arrives<br/>on port P"] --> LOC{"Called number<br/>short / local?"}
    LOC -- yes --> ADD["Put own DNIC in front"]
    LOC -- no --> STRIP["Drop the prefix"]
    ADD --> OWN
    STRIP --> OWN{"DNIC is one<br/>of ours?"}
    OWN -- yes --> LT["Local table:<br/>NTN to port"]
    OWN -- no --> LM["Longest match:<br/>full number, then DNIC+5th,<br/>then DNIC, then DCC"]
    LT --> HIT{"Found a port<br/>other than P?"}
    LM --> HIT
    HIT -- no --> GW{"Default gateway<br/>(X.75 link) set?"}
    GW -- no --> NO["Clear: Not obtainable 0x0D<br/>diag 67, or 121 unknown DNIC"]
    HIT -- yes --> UP{"Port up?"}
    GW -- yes --> UP
    UP -- no --> OOO["Clear: Out of order 0x09"]
    UP -- yes --> FWD["Forward the full international<br/>number, no prefix"]

    style IN fill:#2196F3,stroke:#2196F3,color:#fff
    style OWN fill:#3F51B5,stroke:#3F51B5,color:#fff
    style LM fill:#9C27B0,stroke:#9C27B0,color:#fff
    style GW fill:#FFA726,stroke:#FFA726,color:#fff
    style NO fill:#F44336,stroke:#F44336,color:#fff
    style OOO fill:#F44336,stroke:#F44336,color:#fff
    style FWD fill:#4CAF50,stroke:#4CAF50,color:#fff
```

The rules in the picture:

- **Complete the number first.** A local number gets our own DNIC in front, so every later step sees one
  form (7.6.1, 7.6.2).
- **Longest match wins**: an exact number before `DNIC + 5th digit`, before the DNIC, before the DCC.
- **Never route a call back out of the port it came in on.** That only makes a loop.
- **Toward another network, send the full international number without the prefix** (7.8.1). Between
  public networks that link is X.75.
- **No route at all** clears with *Not obtainable*; **a route whose port is down** clears with *Out of order*.

## 5. Clearing causes a router uses

Cause field, Table 5-7/X.25; network diagnostic, Table E.1/X.25.

| Cause | Code | When |
|---|---|---|
| Number busy | `0x01` | The called DTE is busy |
| Network congestion | `0x05` | The network cannot take the call now |
| Out of order | `0x09` | The route exists, its link is down (X.25 4.6.2 - 4.6.3) |
| Not obtainable | `0x0D` | No route for the called address |
| Local procedure error | `0x13` | The call request itself is bad |

| Diagnostic | Decimal | When |
|---|---|---|
| Invalid called DTE address | 67 | No such number in our network, or a malformed one (Annex E, call request rule 7) |
| International routing problem | 119 | No way out to another network |
| Unknown called DNIC | 121 | The DNIC is not ours and no route or gateway knows it |

## 6. Status in RetroIO

Done: one router decides every incoming call on every line and switch port, with the causes of section 5,
and the address check follows section 1 (any 1-15 digits; the DNIC rules only for international numbers).

Not done yet: own-DNIC completion, the longest-match order over DNIC and DCC, and the default gateway. The
lookup in `RetroIO.Protocols.X25.Routing` still ranks every address prefix above a DNIC route.
