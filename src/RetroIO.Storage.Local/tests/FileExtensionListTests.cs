//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using NUnit.Framework;
using RetroIO.Storage.Core;
using RetroIO.Storage.Local;
using RetroIO.Storage.Local.Drivers;

namespace RetroIO.Storage.Local.Tests
{
    /// <summary>
    /// Tests for the per-format extension lists (<see cref="IFileSystemDriverProvider.FileExtensions"/>)
    /// and <see cref="DriverFactory.GetKnownFileExtensions"/>. Programs use this list to link disk
    /// image files to themselves, so a missing or wrongly written extension shows up there.
    /// </summary>
    [TestFixture]
    public class FileExtensionListTests
    {
        private static DriverFactory CreateFactoryWithLocalDrivers()
        {
            DriverFactory factory = new DriverFactory();
            factory.Register(new CommodoreDiskDriverProvider());
            factory.Register(new T64DriverProvider());
            factory.Register(new P00DriverProvider());
            factory.Register(new PrgDriverProvider());
            factory.Register(new CrtDriverProvider());
            factory.Register(new TapDriverProvider());
            factory.Register(new DragonDskDriverProvider());
            factory.Register(new DragonVdkDriverProvider());
            factory.Register(new DragonDmkDriverProvider());
            factory.Register(new PcFloppyDriverProvider());
            factory.Register(new V7DriverProvider());
            factory.Register(new FfsDriverProvider());
            return factory;
        }

        [Test]
        public void GetKnownFileExtensions_ListsEveryLocalFormatOnceSorted()
        {
            string[] extensions = CreateFactoryWithLocalDrivers().GetKnownFileExtensions();

            // .img and .dsk are claimed by several formats and must still appear only once.
            string[] expected =
            {
                ".bsd", ".crt", ".d64", ".d71", ".d81", ".dmk", ".dsk", ".ffs", ".flp", ".hdd",
                ".ima", ".img", ".jvc", ".p00", ".prg", ".t64", ".tap", ".ufs", ".vdk", ".vfd", ".vhd"
            };
            Assert.That(extensions, Is.EqualTo(expected));
        }

        [Test]
        public void GetKnownFileExtensions_EmptyFactory_ReturnsEmpty()
        {
            Assert.That(new DriverFactory().GetKnownFileExtensions(), Is.Empty);
        }

        [Test]
        public void EveryListedExtension_IsLowerCaseWithLeadingDot()
        {
            DriverFactory factory = CreateFactoryWithLocalDrivers();
            string[] types = factory.GetRegisteredTypes();
            for (int t = 0; t < types.Length; t++)
            {
                IFileSystemDriverProvider? provider = factory.GetProvider(types[t]);
                Assert.That(provider, Is.Not.Null);
                System.Collections.Generic.IReadOnlyList<string> extensions = provider!.FileExtensions;
                Assert.That(extensions.Count, Is.GreaterThan(0), $"{types[t]} lists no extension");
                for (int i = 0; i < extensions.Count; i++)
                {
                    Assert.That(extensions[i], Does.StartWith("."), types[t]);
                    Assert.That(extensions[i], Is.EqualTo(extensions[i].ToLowerInvariant()), types[t]);
                }
            }
        }

        [Test]
        public void Detection_StillUsesTheExtension_CaseDoesNotMatter()
        {
            // A 174,848-byte D64 with only zero bytes: the size and the extension are the signals.
            // Upper and lower case extension must score the same, as before the list was shared.
            CommodoreDiskDriverProvider provider = new CommodoreDiskDriverProvider();
            DetectionHints lower = new DetectionHints { FileExtension = ".d64", FileSize = 174848, HeaderBytes = new byte[512] };
            DetectionHints upper = new DetectionHints { FileExtension = ".D64", FileSize = 174848, HeaderBytes = new byte[512] };
            DetectionHints other = new DetectionHints { FileExtension = ".bin", FileSize = 174848, HeaderBytes = new byte[512] };

            float lowerScore = provider.DetectConfidence(lower);
            Assert.That(provider.DetectConfidence(upper), Is.EqualTo(lowerScore));
            Assert.That(lowerScore, Is.GreaterThan(provider.DetectConfidence(other)));
        }
    }
}
