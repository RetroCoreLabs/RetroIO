//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System.Text;
using NUnit.Framework;
using RetroIO.Protocols.X25.Protocols;

namespace RetroIO.Protocols.X25.Tests
{
    /// <summary>
    /// The X.121 address checks, against ITU-T X.121 (10/2000) and X.25 (10/96) 5.2.1.
    /// Line numbers are in T-REC-X.121-200010-I!!PDF-E.txt and T-REC-X.25-199610-I!!PDF-E.txt
    /// (made with pdftotext -layout), kept in the specification folder beside this repository.
    /// </summary>
    [TestFixture]
    public class X121AddressTests
    {
        /// <summary>
        /// A number used inside one network. X.121 7.6.2 (lines 509-514): the NTN is "the full address that is
        /// used when calling the data terminal from within its serving public data network". Its digits are the
        /// network's own; SINTRAN's local X.25 number 110006 starts with 1 and must be callable.
        /// </summary>
        [TestCase("110006")]
        [TestCase("1")]
        [TestCase("0006")]
        public void ALocalNumber_IsAccepted_WhateverItsFirstDigit(string address)
        {
            Assert.That(new X121().IsValidAddress(address), Is.True);
        }

        /// <summary>
        /// DNIC 2402 followed by the subscriber number (7.6.1, line 508), and the same with a leading 0 as typed in
        /// the Tor address book. A prefix is "a national matter" (7.8.2, lines 562-568), so a one-digit prefix in
        /// front of an international number must not be refused (7.8 NOTE, lines 571-573).
        /// </summary>
        [TestCase("2402001487")]
        [TestCase("02402001487")]
        public void AnInternationalNumber_WithOrWithoutAPrefix_IsAccepted(string address)
        {
            Assert.That(new X121().IsValidAddress(address), Is.True);
        }

        /// <summary>
        /// An escape code 9 or 0 followed by an E.164 number (7.9, lines 586-588, Table 2 lines 628-631). The
        /// escape code is part of the "international X.121 format" and is carried across networks.
        /// </summary>
        [TestCase("947221234567")]
        [TestCase("047221234567")]
        [TestCase("8123456")]
        public void AnEscapeCodeAddress_IsAccepted(string address)
        {
            Assert.That(new X121().IsValidAddress(address), Is.True);
        }

        /// <summary>
        /// X.25 5.2.1 (lines 6243-6246): the non-TOA/NPI address field holds an X.121 address "whose length
        /// (including possible prefixes and/or escape codes) is not greater than 15 digits".
        /// </summary>
        [Test]
        public void FifteenDigits_IsAccepted_SixteenIsRefused()
        {
            X121 x121 = new X121();
            Assert.That(x121.IsValidAddress("024020014870123"), Is.True, "15 digits: a one-digit prefix + 14-digit number");
            Assert.That(x121.IsValidAddress("0240200148701234"), Is.False, "16 digits do not fit the field");
        }

        /// <summary>
        /// The address field carries decimal digits only (X.25 5.2.1.x, semi-octets of digits); and a call needs
        /// a called address, so the PAD refuses an empty one.
        /// </summary>
        [TestCase("")]
        [TestCase(null)]
        [TestCase("2402A01")]
        [TestCase("2402 001")]
        [TestCase("+4722")]
        public void NonDigitsOrEmpty_AreRefused(string? address)
        {
            Assert.That(new X121().IsValidAddress(address!), Is.False);
        }

        /// <summary>
        /// The international data number itself (no prefix): 5 to 14 digits (7.7, line 546), starting with a DNIC
        /// whose first digit is 1-7 (Table 1, lines 395-409; 8, 9 and 0 are escape codes, not DNIC digits, 7.2.2
        /// NOTE 2, line 392).
        /// </summary>
        [TestCase("2402001487", true)]
        [TestCase("11121234", true)]
        [TestCase("24020", true)]
        [TestCase("2402", false)]
        [TestCase("240200148701234", false)]
        [TestCase("02402001487", false)]
        [TestCase("92402001487", false)]
        [TestCase("82402001487", false)]
        public void InternationalDataNumber_FollowsTheLengthAndFirstDigitRules(string address, bool expected)
        {
            Assert.That(new X121().IsInternationalDataNumber(address), Is.EqualTo(expected));
        }

        /// <summary>
        /// The PAD's call command (X.28) with SINTRAN's local number: no "Invalid address format" answer, and the
        /// call goes on to the PAD client.
        /// </summary>
        [Test]
        public void ThePadCallCommand_TakesALocalNumber()
        {
            X28 pad = new X28(new X3());
            StringBuilder replies = new StringBuilder();
            string? called = null;
            pad.CommandResponse += (s, e) => replies.Append(e.Response);
            pad.CommandReceived += (s, e) => called = e.Command.Address;

            string typed = "CALL 110006\r";
            for (int i = 0; i < typed.Length; i++)
            {
                pad.ProcessCharacter(typed[i]);
            }

            Assert.That(replies.ToString(), Does.Not.Contain("Invalid address"));
            Assert.That(called, Is.EqualTo("110006"));
        }
    }
}
