//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using NUnit.Framework;
using RetroIO.Protocols.X25.Models;
using RetroIO.Protocols.X25.Modules;
using RetroIO.Protocols.X25.Protocols;
using RetroIO.Protocols.X25.Runtime.Models;

namespace RetroIO.Protocols.X25.Tests
{
    /// <summary>
    /// Password mode of the local TAD protocol: what the session remembers before it has entered
    /// the mode, and what counts as being in the mode.
    /// </summary>
    [TestFixture]
    public class LocalTadPasswordModeTests : TestBase
    {
        private string _padId = "";
        private PadClient _pad = null!;
        private LocalTadProtocol _tad = null!;
        private string _sessionId = "";

        public override void Setup()
        {
            base.Setup();
            _padId = Runtime.CreateManagedPadClient("Password_Mode_Defaults");
            _pad = Runtime.GetPadClient(_padId)!;
            _tad = Runtime.GetLocalTadProtocol();
            bool connected = false;
            _pad.ConnectionStateChanged += (sender, args) =>
            {
                if (args.State == PadConnectionState.Connected)
                {
                    connected = true;
                }
            };

            _pad.Connect("200", "100");
            Assert.That(WaitForCondition(() => connected, 5000), Is.True, "the PAD to TAD connection must come up");
            _sessionId = _tad.GetActiveSessions()[0].SessionId;
        }

        public override void TearDown()
        {
            _pad.Disconnect();
            Runtime.DisposePadClient(_padId);
            base.TearDown();
        }

        /// <summary>
        /// A session that never entered password mode remembers the default of each parameter, taken
        /// from the DefaultValue constants: leaving the mode puts echo, forwarding, idle timer and
        /// editing at those defaults whatever they were set to.
        /// </summary>
        [Test]
        public void ExitWithoutEnter_PutsTheDefaultsFromTheDefaultValueConstants()
        {
            Assert.That(_tad.SetSessionX3Parameter(_sessionId, 2, 0), Is.True);
            Assert.That(_tad.SetSessionX3Parameter(_sessionId, 3, 10), Is.True);
            Assert.That(_tad.SetSessionX3Parameter(_sessionId, 4, 7), Is.True);
            Assert.That(_tad.SetSessionX3Parameter(_sessionId, 15, 1), Is.True);

            Assert.That(_tad.ExitPasswordMode(_sessionId), Is.True);

            System.Collections.Generic.Dictionary<byte, byte> parameters = _tad.GetSessionX3Parameters(_sessionId)!;
            Assert.That(parameters[2], Is.EqualTo(X3Parameters.DefaultValue.ECHO));
            Assert.That(parameters[3], Is.EqualTo(X3Parameters.DefaultValue.DATA_FORWARDING));
            Assert.That(parameters[4], Is.EqualTo(X3Parameters.DefaultValue.IDLE_TIMER));
            Assert.That(parameters[15], Is.EqualTo(X3Parameters.DefaultValue.EDITING));
        }

        /// <summary>
        /// The session is in password mode only when all four parameters have the password mode
        /// value: echo 0, forwarding 127, idle timer 1, editing 0. Any one of them off the value
        /// ends the mode.
        /// </summary>
        [TestCase((byte)2, (byte)1)]
        [TestCase((byte)3, (byte)1)]
        [TestCase((byte)3, (byte)126)]
        [TestCase((byte)4, (byte)0)]
        [TestCase((byte)4, (byte)2)]
        [TestCase((byte)15, (byte)1)]
        public void OneParameterOffThePasswordValue_IsNotPasswordMode(byte parameter, byte value)
        {
            Assert.That(_tad.EnterPasswordMode(_sessionId), Is.True);
            Assert.That(_tad.IsInPasswordMode(_sessionId), Is.True);

            Assert.That(_tad.SetSessionX3Parameter(_sessionId, parameter, value), Is.True);

            Assert.That(_tad.IsInPasswordMode(_sessionId), Is.False);
        }

        /// <summary>
        /// Entering the mode a second time while it is on must not lose the values from before the
        /// first entry: leaving the mode puts them back.
        /// </summary>
        [Test]
        public void EnterTwiceThenExit_RestoresTheValuesFromBeforeTheFirstEntry()
        {
            Assert.That(_tad.SetSessionX3Parameter(_sessionId, 2, 1), Is.True);
            Assert.That(_tad.SetSessionX3Parameter(_sessionId, 3, 10), Is.True);
            Assert.That(_tad.SetSessionX3Parameter(_sessionId, 4, 7), Is.True);
            Assert.That(_tad.SetSessionX3Parameter(_sessionId, 15, 1), Is.True);

            Assert.That(_tad.EnterPasswordMode(_sessionId), Is.True);
            Assert.That(_tad.EnterPasswordMode(_sessionId), Is.True);
            Assert.That(_tad.ExitPasswordMode(_sessionId), Is.True);

            System.Collections.Generic.Dictionary<byte, byte> parameters = _tad.GetSessionX3Parameters(_sessionId)!;
            Assert.That(parameters[2], Is.EqualTo(1), "echo");
            Assert.That(parameters[3], Is.EqualTo(10), "forwarding");
            Assert.That(parameters[4], Is.EqualTo(7), "idle timer");
            Assert.That(parameters[15], Is.EqualTo(1), "editing");
        }
    }
}
