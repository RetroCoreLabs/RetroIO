//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using NUnit.Framework;
using RetroIO.Protocols.X25.Modules;
using RetroIO.Protocols.X25.Protocols;

namespace RetroIO.Protocols.X25.Tests
{
    /// <summary>
    /// Settings that nothing read were removed rather than left as switches that do nothing.
    /// These tests keep them from coming back without a behaviour behind them.
    /// </summary>
    [TestFixture]
    public class NoDeadSettingsTests
    {
        [TestCase(typeof(PadClient), "IdleTimeoutMs")]
        [TestCase(typeof(PadClient), "AutoConnect")]
        [TestCase(typeof(TadServer), "EnableLoopbackTest")]
        public void ASettingWithNoBehaviour_IsNotPartOfThePublicSurface(Type type, string name)
        {
            Assert.That(type.GetProperty(name), Is.Null, name + " was never read by " + type.Name);
        }

        [Test]
        public void XmsgSessionState_HasNoMemberThatNothingAssigns()
        {
            Assert.That(Enum.IsDefined(typeof(XMSGSessionState), "DataTransfer"), Is.False);
        }
    }
}
