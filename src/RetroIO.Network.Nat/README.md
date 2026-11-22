# RetroIO.Network.Nat

The built-in NAT for emulated machines. A machine on it gets an address by DHCP, a gateway and a
name server. Its UDP, TCP and ping reach the internet through ordinary host sockets, so no
administrator rights are needed, and port forwards let host programs reach the machine. Moved from
RetroCore on 2026-11-22.

It takes two kinds of guest:

- **An emulated network card**: `NatEthernetBackend` is an `IEthernetBackend`, so it takes and
  gives whole Ethernet frames (ARP, DHCP, BOOTP).
- **A serial line (SLIP)**: bare IP packets go in through `SendPacket` and come out through
  `OnPacketReceived`, with no Ethernet header either way.

## The default address plan

`NatAddressPlan()`: subnet 10.0.2.0/24, gateway 10.0.2.2, name server 10.0.2.3, first guest
10.0.2.15. The gateway address reaches the host's own 127.0.0.1 (can be turned off with
`hostloopback=off`); the name server address reaches the host's name server, or the host's own
name lookups when the host has none.

## With RetroIO.Network.Router

For dial-up with many callers, the router passes their packets and the NAT takes what goes
outside. Give the router the NAT's subnet, and the NAT's gateway and name server as upstream
addresses, so packets for them go to the NAT and their replies are let in:

```csharp
IpRouter router = new IpRouter(new RouterSettings
{
    Network = Ipv4.Parse("10.0.2.0"),
    RouterAddress = Ipv4.Parse("10.0.2.1"),
    PoolFirst = Ipv4.Parse("10.0.2.15"),
    UpstreamAddresses = new[] { Ipv4.Parse("10.0.2.2"), Ipv4.Parse("10.0.2.3") },
});
NatEthernetBackend nat = new NatEthernetBackend();
router.OnUpstream += p => nat.SendPacket(p.ToArray(), 0, p.Length);
nat.OnPacketReceived += (packet, offset, length) => router.SendFromUpstream(packet.AsSpan(offset, length));
nat.Start();
// then call nat.Poll() from the thread that drives the router
```

`RouterNatTests` runs exactly this against a real socket.

## Parts

| Type | What it does |
|---|---|
| `NatEthernetBackend` | The backend: one NAT thread does all socket work; `Poll` hands frames or packets to the guest. Port forwards through `IEthernetPortForwarding`. |
| `NatRouter` | The router inside it: ARP, DHCP and BOOTP, ping to the gateway, UDP and TCP to host sockets. |
| `NatAddressPlan` | Subnet, gateway, name server, first guest and the gateway's MAC address. |
| `NatPortForward`, `NatForwardBinding`, `NatUdpForwardSession` | Port forward rules (`tcp:2323:23`, ranges, a listen address) and what they open. |
| `NatNameResolver` | Answers name lookups from the host's own resolver when the host has no name server address. |
| `NatTcpConnection`, `NatUdpMapping` | One guest TCP connection and one UDP mapping. |
| `NatPacket` | Building and reading the headers and checksums. |

The design is RetroCore's host networking design (host-networking-design.md in the RetroCore
repository), section 7.
