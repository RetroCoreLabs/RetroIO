//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;
using System.Text;
using NUnit.Framework;
using RetroIO.Storage.Core;
using RetroIO.Storage.Local.Drivers;
using RetroIO.Storage.Ndfs.Drivers;

namespace RetroIO.Tests
{
    /// <summary>
    /// The copy-conversion rule, checked over every provider that offers conversions: plain text
    /// survives copy in and copy out under every choice of options, and each driver's real rule
    /// holds.
    /// </summary>
    [TestFixture]
    public class TransferConversionRuleTests
    {
        private static readonly byte[] Sample = Encoding.ASCII.GetBytes("HELLO WORLD 123.\nSECOND LINE, (X+Y)=Z; [OK]?\n\nEND\n");

        private static ITransferConversionProvider[] AllProviders()
        {
            return new ITransferConversionProvider[]
            {
                new CommodoreDiskDriverProvider(),
                new CpmDriverProvider(),
                new NdfsDriverProvider(),
            };
        }

        private static IEnumerable<TestCaseData> Cases()
        {
            ITransferConversionProvider[] providers = AllProviders();
            for (int p = 0; p < providers.Length; p++)
            {
                IReadOnlyList<TransferConversion> conversions = providers[p].Conversions;
                for (int c = 0; c < conversions.Count; c++)
                {
                    IReadOnlyList<BlankImageOption> options = conversions[c].Options;
                    yield return new TestCaseData(p, conversions[c].Id, null, null).SetName("Convert " + conversions[c].Id + " defaults");
                    for (int o = 0; o < options.Count; o++)
                    {
                        for (int v = 0; v < options[o].Choices.Count; v++)
                        {
                            yield return new TestCaseData(p, conversions[c].Id, options[o].Id, options[o].Choices[v])
                                .SetName("Convert " + conversions[c].Id + " " + options[o].Id + "=" + options[o].Choices[v]);
                        }
                    }
                }
            }
        }

        [TestCaseSource(nameof(Cases))]
        public void PlainText_RoundTrips(int providerIndex, string conversionId, string? optionId, string? value)
        {
            ITransferConversionProvider provider = AllProviders()[providerIndex];
            Dictionary<string, string>? options = optionId == null ? null : new Dictionary<string, string> { { optionId, value! } };

            // The sample is written in the host line ends the options choose.
            byte[] sample = HostLineEnds(provider, conversionId, options) == "CR LF" ? TransferText.LfToCrLf(Sample) : Sample;
            byte[] stored = provider.FromHost(conversionId, sample, options);
            Assert.That(provider.ToHost(conversionId, stored, options), Is.EqualTo(sample), "host text back");
            Assert.That(provider.FromHost(conversionId, provider.ToHost(conversionId, stored, options), options), Is.EqualTo(stored), "stored bytes back");
        }

        private static string HostLineEnds(ITransferConversionProvider provider, string conversionId, Dictionary<string, string>? options)
        {
            TransferConversion conversion = TransferText.Find(provider.Conversions, conversionId);
            for (int o = 0; o < conversion.Options.Count; o++)
            {
                if (conversion.Options[o].Id == "hostLineEnds")
                {
                    return conversion.Options[o].ValueIn(options);
                }
            }

            return "LF";
        }

        [Test]
        public void EveryConversion_HasAUniqueIdAndValidDefaults()
        {
            ITransferConversionProvider[] providers = AllProviders();
            HashSet<string> ids = new HashSet<string>();
            for (int p = 0; p < providers.Length; p++)
            {
                for (int c = 0; c < providers[p].Conversions.Count; c++)
                {
                    TransferConversion conversion = providers[p].Conversions[c];
                    Assert.That(ids.Add(conversion.Id), Is.True, "duplicate id " + conversion.Id);
                    Assert.That(conversion.Directions, Is.Not.EqualTo(TransferDirections.None), conversion.Id);
                    for (int o = 0; o < conversion.Options.Count; o++)
                    {
                        Assert.That(conversion.Options[o].Check(conversion.Options[o].Default), Is.Null, conversion.Id + " " + conversion.Options[o].Id);
                    }
                }
            }
        }

        [Test]
        public void UnknownIdOrOption_IsRefused()
        {
            CpmDriverProvider cpm = new CpmDriverProvider();
            Assert.Throws<ArgumentException>(() => cpm.ToHost("no-such", Sample, null));
            Dictionary<string, string> bad = new Dictionary<string, string> { { "hostLineEnds", "CR" } };
            Assert.Throws<ArgumentException>(() => cpm.ToHost(CpmTextConversion.Id, Sample, bad));
        }

        [Test]
        public void Cpm_CopyOutStopsAtCtrlZ_AndCopyInRefusesOne()
        {
            CpmDriverProvider cpm = new CpmDriverProvider();
            byte[] stored = { (byte)'A', 0x0D, 0x0A, 0x1A, 0x1A, (byte)'X' };
            Dictionary<string, string> lf = new Dictionary<string, string> { { "hostLineEnds", "LF" } };
            Assert.That(cpm.ToHost(CpmTextConversion.Id, stored, null), Is.EqualTo(new byte[] { (byte)'A', 0x0D, 0x0A }));
            Assert.That(cpm.ToHost(CpmTextConversion.Id, stored, lf), Is.EqualTo(new byte[] { (byte)'A', 0x0A }));
            ArgumentException? e = Assert.Throws<ArgumentException>(() => cpm.FromHost(CpmTextConversion.Id, new byte[] { (byte)'A', 0x1A }, null));
            Assert.That(e!.Message, Does.Contain("1Ah at position 1"));
        }

        [Test]
        public void Sintran_SetsEvenParityOnCopyIn_AndClearsBit7OnCopyOut()
        {
            NdfsDriverProvider ndfs = new NdfsDriverProvider();
            Dictionary<string, string> even = new Dictionary<string, string> { { "parity", "even parity" } };

            // A (41h) has two ones and stays; C (43h) has three and gets bit 7.
            byte[] stored = ndfs.FromHost(NdfsTextConversion.Id, new byte[] { (byte)'A', (byte)'C' }, even);
            Assert.That(stored, Is.EqualTo(new byte[] { 0x41, 0xC3 }));
            Assert.That(ndfs.ToHost(NdfsTextConversion.Id, stored, null), Is.EqualTo(new byte[] { 0x41, 0x43 }));
            Assert.Throws<ArgumentException>(() => ndfs.FromHost(NdfsTextConversion.Id, new byte[] { 0xE6 }, null));
        }

        [Test]
        public void Petscii_MapsBothCharacterSets()
        {
            CommodoreDiskDriverProvider cbm = new CommodoreDiskDriverProvider();
            Dictionary<string, string> upper = new Dictionary<string, string> { { "charset", "upper case and graphics" } };
            byte[] host = Encoding.ASCII.GetBytes("Ab\r\n");

            Assert.That(cbm.FromHost(PetsciiTextConversion.Id, host, null), Is.EqualTo(new byte[] { 0xC1, 0x42, 0x0D }));
            Assert.That(cbm.FromHost(PetsciiTextConversion.Id, host, upper), Is.EqualTo(new byte[] { 0x41, 0x42, 0x0D }));
            Assert.That(cbm.ToHost(PetsciiTextConversion.Id, new byte[] { 0xC1, 0x61, 0x42, 0x0D }, null), Is.EqualTo(Encoding.ASCII.GetBytes("AAb\n")));

            // A colour code and a graphic of the upper-case set have no ASCII meaning.
            Assert.Throws<ArgumentException>(() => cbm.ToHost(PetsciiTextConversion.Id, new byte[] { 0x1C }, null));
            Assert.Throws<ArgumentException>(() => cbm.ToHost(PetsciiTextConversion.Id, new byte[] { 0xC1 }, upper));
            Assert.Throws<ArgumentException>(() => cbm.FromHost(PetsciiTextConversion.Id, Encoding.ASCII.GetBytes("a_b"), null));
        }
    }
}
