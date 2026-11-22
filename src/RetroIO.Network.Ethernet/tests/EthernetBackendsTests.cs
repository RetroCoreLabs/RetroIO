//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using NUnit.Framework;

namespace RetroIO.Network.Ethernet.Tests
{
    /// <summary>
    /// Verifies the <see cref="EthernetBackends"/> registry that turns a machine's
    /// <c>net</c> option spec into a backend via host-registered factories.
    /// </summary>
    [TestFixture]
    public class EthernetBackendsTests
    {
        /// <summary>A minimal backend test double.</summary>
        private sealed class FakeBackend : IEthernetBackend
        {
            public event EthernetFrameHandler? OnFrameReceived;
            public bool IsRunning { get; private set; }
            public void Send(byte[] frame, int offset, int length) { }
            public void Poll() { _ = OnFrameReceived; }
            public void Start() => IsRunning = true;
            public void Stop() => IsRunning = false;
        }

        [SetUp]
        public void Reset() => EthernetBackends.Clear();

        [TearDown]
        public void Cleanup() => EthernetBackends.Clear();

        [Test]
        public void Create_None_And_Empty_ReturnNull()
        {
            EthernetBackends.Register(spec => new FakeBackend());
            Assert.That(EthernetBackends.Create("none"), Is.Null);
            Assert.That(EthernetBackends.Create(""), Is.Null);
            Assert.That(EthernetBackends.Create(null), Is.Null);
        }

        [Test]
        public void Create_UsesFirstFactoryThatHandlesSpec()
        {
            EthernetBackends.Register(spec => spec == "pcap" ? new FakeBackend() : null);

            IEthernetBackend? made = EthernetBackends.Create("pcap");
            Assert.That(made, Is.Not.Null);
            Assert.That(made, Is.InstanceOf<FakeBackend>());
        }

        [Test]
        public void Create_UnknownSpec_ReturnsNull()
        {
            EthernetBackends.Register(spec => spec == "pcap" ? new FakeBackend() : null);
            Assert.That(EthernetBackends.Create("slirp"), Is.Null);
        }

        [Test]
        public void Register_IsIdempotentForSameDelegate()
        {
            int calls = 0;
            EthernetBackendFactory f = spec => { calls++; return null; };
            EthernetBackends.Register(f);
            EthernetBackends.Register(f);

            EthernetBackends.Create("x");
            Assert.That(calls, Is.EqualTo(1), "a re-registered factory must be consulted only once");
        }

        [Test]
        public void Clear_RemovesFactories()
        {
            EthernetBackends.Register(spec => new FakeBackend());
            EthernetBackends.Clear();
            Assert.That(EthernetBackends.Create("pcap"), Is.Null);
        }
    }
}
