//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// HackerCorp Labs — https://github.com/HackerCorpLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using System.Net;
using NUnit.Framework;
using RetroIO.Protocols.Econet.Protocol.Aun;

namespace RetroIO.Tests.Econet;

[TestFixture]
[Category("Unit")]
public sealed class AunAddressMapperTests
{
    [Test]
    public void TryResolve_Ipv4LastOctet_MapsToConfiguredNetwork()
    {
        AunAddressMapper mapper = new AunAddressMapper(network: 140);

        bool resolved = mapper.TryResolve(IPAddress.Parse("192.168.140.55"), out byte net, out byte station);

        Assert.That(resolved, Is.True);
        Assert.That(net, Is.EqualTo(140));
        Assert.That(station, Is.EqualTo(55));
    }

    [Test]
    public void TryResolve_ClassicSubnet_UsesLastOctetAsStation()
    {
        AunAddressMapper mapper = new AunAddressMapper(network: 1);

        bool resolved = mapper.TryResolve(IPAddress.Parse("192.168.0.12"), out byte net, out byte station);

        Assert.That(resolved, Is.True);
        Assert.That(net, Is.EqualTo(1));
        Assert.That(station, Is.EqualTo(12));
    }

    [Test]
    public void TryResolve_Ipv6_ReturnsFalse()
    {
        AunAddressMapper mapper = new AunAddressMapper(network: 1);

        bool resolved = mapper.TryResolve(IPAddress.IPv6Loopback, out byte net, out byte station);

        Assert.That(resolved, Is.False);
        Assert.That(net, Is.EqualTo(1));
        Assert.That(station, Is.EqualTo(0));
    }
}
