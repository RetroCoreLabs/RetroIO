//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using RetroIO.Protocols.X25.Common;
using RetroIO.Protocols.X25.Models;
using RetroIO.Protocols.X25.PacketLayer;
using RetroIO.Protocols.X25.Routing;
using RetroIO.Protocols.X25.Runtime;
using RetroIO.Protocols.X25.Switch;
using RetroIO.Protocols.X25.Switch.Model;
using RetroIO.Protocols.X25.Switch.Ports;
using RetroIO.Protocols.X25.Transport;

namespace RetroIO.Protocols.X25.Tests
{
    /// <summary>
    /// The runtime reads the same one table as the switch: an old JSON file (regex patterns with
    /// priorities) is converted into it, and one table given to the switch and to the runtime gives the
    /// same answer for the same number.
    /// </summary>
    [TestFixture]
    public class OneRouteTableRuntimeTests
    {
        private static readonly JsonSerializerOptions Options = new JsonSerializerOptions
        {
            Converters = { new JsonStringEnumConverter() },
            PropertyNameCaseInsensitive = true,
        };

        // The routing section of a file written before 2026-11-18: regex patterns, a type, a destination
        // name and a priority. Kept word for word in the old shape.
        private const string OldJson = @"{
  ""Routing"": {
    ""Enabled"": true,
    ""RoutingTable"": [
      { ""Name"": ""Serial100"", ""X121AddressPattern"": ""100.*"", ""Type"": ""Interface"", ""Destination"": ""Serial0"", ""Priority"": 100, ""Enabled"": true },
      { ""Name"": ""LocalTAD"", ""X121AddressPattern"": ""200"", ""Type"": ""Local"", ""Destination"": ""LocalTAD"", ""Priority"": 200, ""Enabled"": true },
      { ""Name"": ""Thousands"", ""X121AddressPattern"": ""^10[0-9]{2}$"", ""Type"": ""Local"", ""Destination"": ""LocalTAD"", ""Priority"": 300, ""Enabled"": true },
      { ""Name"": ""Low"", ""X121AddressPattern"": ""^5.*"", ""Type"": ""Interface"", ""Destination"": ""XOT0"", ""Priority"": 10, ""Enabled"": true },
      { ""Name"": ""High"", ""X121AddressPattern"": ""^5.*"", ""Type"": ""Interface"", ""Destination"": ""Serial0"", ""Priority"": 90, ""Enabled"": true },
      { ""Name"": ""Off"", ""X121AddressPattern"": ""^7.*"", ""Type"": ""Interface"", ""Destination"": ""Serial0"", ""Priority"": 100, ""Enabled"": false }
    ]
  }
}";

        [Test]
        public void AnOldJsonFile_LoadsIntoTheOneTable()
        {
            RuntimeConfig config = JsonSerializer.Deserialize<RuntimeConfig>(OldJson, Options)!;

            X121NetworkConfig net = RoutingConfigConverter.ToNetworkConfig(config.Routing);

            Assert.That(net.Routes.Count, Is.EqualTo(6));
            X121RouteConfig serial = Find(net, "Serial100");
            Assert.That(serial.Kind, Is.EqualTo("prefix"), "100.* is a prefix");
            Assert.That(serial.Match, Is.EqualTo("100"));
            Assert.That(serial.Port, Is.EqualTo("Serial0"));
            X121RouteConfig tad = Find(net, "LocalTAD");
            Assert.That(tad.Kind, Is.EqualTo("exact"));
            Assert.That(tad.Port, Is.EqualTo(X121RoutingTable.LocalPortName), "a Local route goes to this node");
            X121RouteConfig thousands = Find(net, "Thousands");
            Assert.That(thousands.Kind, Is.EqualTo("regex"), "a real pattern stays a pattern route");
            Assert.That(thousands.Match, Is.EqualTo("^10[0-9]{2}$"));
            Assert.That(Find(net, "Off").Enabled, Is.False);

            X25Router router = new X25Router(NullLogger.Instance);
            router.LoadNetwork(net);
            Assert.That(router.Table.FindWinner("1001234")!.Name, Is.EqualTo("Serial100"));
            Assert.That(router.Table.FindWinner("1050")!.Name, Is.EqualTo("Thousands"), "the pattern route ranks above a prefix");
            Assert.That(router.Table.FindWinner("200")!.Name, Is.EqualTo("LocalTAD"));
            Assert.That(router.Table.FindWinner("5555")!.Name, Is.EqualTo("High"), "equal routes: the old priority still decides");
            Assert.That(router.Table.FindWinner("7777"), Is.Null, "disabled");
        }

        [Test]
        public void AnOldJsonFile_GivesTheSameAnswerThroughTheRuntime()
        {
            RuntimeConfig config = JsonSerializer.Deserialize<RuntimeConfig>(OldJson, Options)!;
            ProtocolRuntime runtime = new ProtocolRuntime(NullLogger<ProtocolRuntime>.Instance, config);

            Assert.That(runtime.FindRoute("1001234")!.Name, Is.EqualTo("Serial100"));
            Assert.That(runtime.ResolveRoute("200").Outcome, Is.EqualTo(X121RouteOutcome.Local));
        }

        [Test]
        public void TheNewJsonForm_IsReadAsWell_AndWinsOverNothing()
        {
            const string json = @"{ ""Routing"": {
                ""OwnDnics"": [ ""2422"" ], ""InternationalPrefix"": ""0"",
                ""Routes"": [ { ""Name"": ""Sweden"", ""Kind"": ""dnic"", ""Match"": ""2402"", ""Port"": ""XOT0"" } ] } }";
            RuntimeConfig config = JsonSerializer.Deserialize<RuntimeConfig>(json, Options)!;

            X121NetworkConfig net = RoutingConfigConverter.ToNetworkConfig(config.Routing);
            X25Router router = new X25Router(NullLogger.Instance);
            router.LoadNetwork(net);

            Assert.That(router.Table.OwnDnics, Is.EqualTo(new[] { "2422" }));
            Assert.That(router.Table.InternationalPrefix, Is.EqualTo("0"));
            Assert.That(router.Table.FindWinner("024020001730")!.Name, Is.EqualTo("Sweden"));
        }

        private static X121RouteConfig Find(X121NetworkConfig net, string name)
        {
            for (int i = 0; i < net.Routes.Count; i++)
            {
                if (net.Routes[i].Name == name)
                {
                    return net.Routes[i];
                }
            }

            throw new AssertionException("no route named " + name);
        }

        // ---- one table, the switch and the runtime ----

        private sealed class UpTransport : ITransportInterface
        {
            public UpTransport(string name) => Name = name;

            public string Name { get; }

            public TransportType Type => TransportType.Serial;

            public TransportStatus Status => TransportStatus.Active;

            public bool PlaceCall(X25CallRequest callRequest) => false;

            public bool AcceptCall(X25CallRequest callRequest) => false;

            public bool RejectCall(X25CallRequest callRequest, byte cause) => false;

            public bool Start() => true;

            public void Stop()
            {
            }

            public TransportStatistics GetStatistics() => new TransportStatistics();

            public void ResetStatistics()
            {
            }

            public event EventHandler<X25CallRequest>? IncomingCall
            {
                add { }
                remove { }
            }

            public event EventHandler<TransportStatusEventArgs>? StatusChanged
            {
                add { }
                remove { }
            }

            public event EventHandler<TransportDataEventArgs>? DataReceived
            {
                add { }
                remove { }
            }
        }

        private static readonly string[] PortNames = { "terminals", "x75-gw", "nordic" };

        /// <summary>
        /// The one shared table: an own network, a local number, a prefix, a DNIC with and without its
        /// fifth digit, a country, and the gateway as the default.
        /// </summary>
        private static X121NetworkConfig SharedTable()
        {
            X121NetworkConfig net = new X121NetworkConfig { InternationalPrefix = "0" };
            net.OwnDnics.Add("2422");
            net.Routes.Add(new X121RouteConfig { Name = "Here", Kind = "prefix", Match = "242211", Port = "terminals" });
            net.Routes.Add(new X121RouteConfig { Name = "Operator", Kind = "exact", Match = "2422110000", Port = "nordic" });
            net.Routes.Add(new X121RouteConfig { Name = "Sweden", Kind = "dnic", Match = "2402", Port = "x75-gw" });
            net.Routes.Add(new X121RouteConfig { Name = "Sweden 5", Kind = "dnic", Match = "24025", Port = "nordic" });
            net.Routes.Add(new X121RouteConfig { Name = "Denmark", Kind = "dcc", Match = "238", Port = "nordic" });
            net.Routes.Add(new X121RouteConfig { Name = "World", Kind = "default", Port = "x75-gw" });
            return net;
        }

        private static readonly string[] Numbers =
        {
            "110006", "0242211000", "2422110000", "024020001730", "024025000001", "023820000001", "031060000001", "2422990000",
        };

        [Test]
        public void TheSwitchAndTheRuntime_GiveTheSameAnswer_ForTheSameTable()
        {
            // The switch, with its ports named as in the table.
            SwitchConfig sw = new SwitchConfig();
            Dictionary<int, ISwitchPort> ports = new Dictionary<int, ISwitchPort>();
            for (int i = 0; i < PortNames.Length; i++)
            {
                PortConfig pc = new PortConfig { Id = i, Name = PortNames[i], LciBase = 1 };
                LoopbackSwitchPort p = new LoopbackSwitchPort(pc);
                p.Start();
                ports[i] = p;
                sw.Ports.Add(pc);
                sw.Routing.SetPortName(i, PortNames[i]);
            }

            X121TableBuilder.Fill(sw.Routing, SharedTable());
            SwitchCallRouter switchRouter = new SwitchCallRouter(sw.Routing, id => ports.TryGetValue(id, out ISwitchPort? p) ? p : null);

            // The runtime, with transports of the same names.
            X25Router runtimeRouter = new X25Router(NullLogger.Instance);
            for (int i = 0; i < PortNames.Length; i++)
            {
                runtimeRouter.RegisterInterface(new UpTransport(PortNames[i]));
            }

            runtimeRouter.LoadNetwork(SharedTable());

            for (int i = 0; i < Numbers.Length; i++)
            {
                string number = Numbers[i];
                X25CallDecision s = switchRouter.Decide(number, inPortId: -1);
                X121RouteResult r = runtimeRouter.ResolveCall(number, null, X121RouteDirection.Outgoing, null);

                Assert.That(s.Action, Is.EqualTo(X25CallAction.Forward), number + " through the switch");
                Assert.That(r.Outcome, Is.EqualTo(X121RouteOutcome.Forward), number + " through the runtime");
                Assert.That(runtimeRouter.Table.GetPortName(r.Port), Is.EqualTo(sw.Routing.GetPortName(s.OutPort)), number);
            }

            Assert.That(sw.Routing.GetPortName(switchRouter.Decide("024025000001", -1).OutPort), Is.EqualTo("nordic"), "the 5-digit DNIC wins");
            Assert.That(sw.Routing.GetPortName(switchRouter.Decide("110006", -1).OutPort), Is.EqualTo("terminals"), "a local number completed with 2422");
        }
    }
}
