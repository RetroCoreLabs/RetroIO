//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;
using NUnit.Framework;
using RetroIO.Protocols.X25.Models;
using RetroIO.Protocols.X25.Protocols;

namespace RetroIO.Protocols.X25.Tests
{
    /// <summary>
    /// The parameter field of X.29 set, read, set and read, and parameter indication PAD messages, as the host side
    /// builds and reads it.
    /// </summary>
    /// <remarks>
    /// X.29 (12/97) 4.4.5: a parameter field is a reference octet and a value octet. 4.4.5.3: "Value fields in read PAD
    /// messages are coded as all binary 0s"; in a parameter indication, "If bit 8 (error bit) is set to 1 in the
    /// preceding octet (i.e. the parameter reference field), the parameter value field will indicate the reason for the
    /// error, as given in Table 3". 4.4.5.1: reference 127 means the reference goes on in the next octet as the X.3
    /// reference minus 127. 4.4.5.4: reference 0 with value 0 is the separator, after which come national
    /// parameters. Until 2026-11-21 a read was sent as bare references (04 02 03, which a real PAD reads as parameter 2
    /// only), and a parameter in error was read as a parameter whose number had 128 added (RetroIO BUGS 45, RetroX25
    /// PLAN H2).
    /// </remarks>
    [TestFixture]
    public class X29ParameterFieldTests
    {
        [Test]
        public void ARead_CarriesEachReferenceWithAZeroValue()
        {
            Assert.That(X29Messages.CreateReadParameters(new byte[] { 2, 3 }), Is.EqualTo(new byte[] { 0x04, 2, 0, 3, 0 }));
            Assert.That(new X29().CreateReadParameters(3, 2), Is.EqualTo(new byte[] { 0x04, 2, 0, 3, 0 }), "the X29 builder too");
        }

        [Test]
        public void AReadOfEveryParameter_IsTheMessageCodeAlone()
        {
            Assert.That(X29Messages.CreateReadParameters(new byte[0]), Is.EqualTo(new byte[] { 0x04 }));
        }

        [Test]
        public void AReceivedRead_IsReadAsPairs()
        {
            Models.X29Message read = X29Messages.ParseMessage(new byte[] { 0x04, 2, 0, 3, 0 });

            Assert.That(read.ParameterNumbers, Is.EqualTo(new List<byte> { 2, 3 }));
        }

        [Test]
        public void AParameterIndication_WithTheErrorBit_ReportsThatParameterInError()
        {
            // Parameter 2 in error, Table 3 reason 1; parameter 3 is 1.
            Models.X29Message indication = X29Messages.ParseMessage(new byte[] { 0x00, 0x82, 0x01, 0x03, 0x01 });

            Assert.That(indication.Parameters, Is.EqualTo(new Dictionary<byte, byte> { { 3, 1 } }), "only the good value is a value");
            Assert.That(indication.ParametersInError, Is.EqualTo(new Dictionary<byte, byte> { { 2, 1 } }), "parameter 2, reason 1");
        }

        [Test]
        public void TheExtendedReference_IsTheNextOctetPlus127()
        {
            Models.X29Message indication = X29Messages.ParseMessage(new byte[] { 0x00, 0x7F, 0x01, 0x05 });

            Assert.That(indication.Parameters, Is.EqualTo(new Dictionary<byte, byte> { { 128, 5 } }));
        }

        [Test]
        public void TheSeparator_EndsTheX3Parameters()
        {
            // 4.4.5.4: what follows 00 00 is national; this PAD knows none, so it is not taken as X.3 parameter 0x63.
            Models.X29Message indication = X29Messages.ParseMessage(new byte[] { 0x00, 2, 1, 0, 0, 0x63, 5 });

            Assert.That(indication.Parameters, Is.EqualTo(new Dictionary<byte, byte> { { 2, 1 } }));
        }

        /// <summary>
        /// The error type enum holds exactly the seven codes of X.29 Table 4.
        /// </summary>
        /// <remarks>
        /// Until 2026-11-21 it held 01 to 05, of which 01, 03 and 05 are not in Table 4 (RetroIO BUGS 45).
        /// </remarks>
        [Test]
        public void TheErrorTypes_AreTheCodesOfTable4()
        {
            X29ErrorCode[] codes = Enum.GetValues<X29ErrorCode>();
            byte[] values = new byte[codes.Length];
            for (int i = 0; i < codes.Length; i++)
            {
                values[i] = (byte)codes[i];
            }

            Assert.That(values, Is.EquivalentTo(new byte[] { 0x00, 0x02, 0x04, 0x06, 0x08, 0x0A, 0x0C }));
            Assert.That(X29.ErrorType.UnauthorizedReselection, Is.EqualTo(0x0C));
        }

        [Test]
        public void TheX29Parser_ReadsTheFieldTheSameWay()
        {
            X29 x29 = new X29();
            Assert.That(x29.ParseMessage(new byte[] { 0x04, 2, 0, 3, 0 }).ParameterNumbers, Is.EqualTo(new List<byte> { 2, 3 }));

            Protocols.X29Message indication = x29.ParseMessage(new byte[] { 0x00, 0x82, 0x01, 0x03, 0x01 });
            Assert.That(indication.Parameters, Is.EqualTo(new Dictionary<byte, byte> { { 3, 1 } }));
            Assert.That(indication.ParametersInError, Is.EqualTo(new Dictionary<byte, byte> { { 2, 1 } }));
        }
    }
}
