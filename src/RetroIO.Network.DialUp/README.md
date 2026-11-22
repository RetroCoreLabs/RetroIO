# RetroIO.Network.DialUp

The answering side of a dial-up internet call. A caller's bytes come in from a modem line (an
emulated Hayes modem, or a TCP connection to a server); the session gives an optional text login,
then carries the caller's IP packets over SLIP (RFC 1055). Moved from RetroCore on 2026-11-22 and
made pluggable in two places:

- **The login check** (`DialUpSettings.LoginCheck`): `null` takes any name, as an emulator's
  built-in ISP does; a server checks its own users. Three wrong logins hang up.
- **The packet side** (`IDialUpPacketSide`), started only after a good login:
  - `NatDialUpSide`: a NAT of its own (`RetroIO.Network.Nat`), for one modem in an emulator.
  - `RouterDialUpSide`: a link on a shared `IpRouter` (`RetroIO.Network.Router`), so many callers
    share one subnet and one NAT behind it. A user can have a fixed address through
    `DialUpAddressLookup`; otherwise the router's pool gives one, and a full pool hangs up with
    "No line free".

## Use

```csharp
SlipDialUpSession session = new SlipDialUpSession(
    new DialUpSettings { LoginCheck = (name, password) => users.Check(name, password) },
    new RouterDialUpSide(router, nameServer: Ipv4.Parse("10.42.0.3")));
session.OnEnded += reason => line.HangUp();
session.Answer();
// then, on one thread: session.TryTakeFromCaller(b) for each byte from the line,
// session.TryGiveToCaller(out b) for each byte to it, and session.Poll() regularly.
```

See `RetroIO.Network.Nat`'s README for joining the router to a NAT. PPP, with its PAP and CHAP
logins, comes into this package later.
