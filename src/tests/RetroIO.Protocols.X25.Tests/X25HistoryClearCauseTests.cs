//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using NUnit.Framework;
using RetroIO.Protocols.X25.Models;

namespace RetroIO.Protocols.X25.Tests
{
    /// <summary>
    /// The history shows a clearing cause by its X.25 (10/96) Table 5-7 name.
    /// </summary>
    [TestFixture]
    public class X25HistoryClearCauseTests
    {
        [TestCase((byte)0x00, "DTE originated")]
        [TestCase((byte)0x01, "Number busy")]
        [TestCase((byte)0x03, "Invalid facility request")]
        [TestCase((byte)0x05, "Network congestion")]
        [TestCase((byte)0x09, "Out of order")]
        [TestCase((byte)0x0B, "Access barred")]
        [TestCase((byte)0x0D, "Not obtainable")]
        [TestCase((byte)0x11, "Remote procedure error")]
        [TestCase((byte)0x13, "Local procedure error")]
        [TestCase((byte)0x15, "ROA out of order")]
        [TestCase((byte)0x19, "Reverse charging acceptance not subscribed")]
        [TestCase((byte)0x21, "Incompatible destination")]
        [TestCase((byte)0x29, "Fast select acceptance not subscribed")]
        [TestCase((byte)0x39, "Ship absent")]
        [TestCase((byte)0x8D, "DTE originated")]
        public void ACause_IsShownByItsTable57Name(byte cause, string name)
        {
            Assert.That(X25HistoryItem.GetClearCauseDescription(cause), Is.EqualTo(name));
        }
    }
}
