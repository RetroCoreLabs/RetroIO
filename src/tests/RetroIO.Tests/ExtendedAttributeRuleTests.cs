//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using NUnit.Framework;
using RetroIO.Storage.Core;
using RetroIO.Storage.Local.Drivers;
using RetroIO.Storage.Ndfs.Drivers;
using RetroIO.Storage.Ndfs.PropertyBags;

namespace RetroIO.Tests
{
    /// <summary>
    /// The file-property rule, checked on a blank image of every layout: a driver that says it has
    /// file properties reports exactly the keys it declares, and every key it says it writes comes
    /// back the same after a trip through a <c>.xat</c> file onto a fresh disk.
    /// </summary>
    [TestFixture]
    public class ExtendedAttributeRuleTests
    {
        private static IFileSystemDriverProvider[] AllProviders()
        {
            return new IFileSystemDriverProvider[]
            {
                new V7DriverProvider(),
                new CommodoreDiskDriverProvider(),
                new CpmDriverProvider(),
                new CrtDriverProvider(),
                new DragonDmkDriverProvider(),
                new DragonDskDriverProvider(),
                new DragonVdkDriverProvider(),
                new FfsDriverProvider(),
                new P00DriverProvider(),
                new PcFloppyDriverProvider(),
                new PrgDriverProvider(),
                new T64DriverProvider(),
                new TapDriverProvider(),
                new NdfsDriverProvider(),
            };
        }

        private static IEnumerable<TestCaseData> Layouts()
        {
            IFileSystemDriverProvider[] providers = AllProviders();
            for (int p = 0; p < providers.Length; p++)
            {
                if (providers[p] is IBlankImageProvider blank)
                {
                    IReadOnlyList<BlankImageLayout> layouts = blank.BlankLayouts;
                    for (int i = 0; i < layouts.Count; i++)
                    {
                        yield return new TestCaseData(p, layouts[i].Id).SetName("Properties " + layouts[i].Id);
                    }
                }
            }
        }

        [TestCaseSource(nameof(Layouts))]
        public void DeclaredKeys_AreTheKeysAFileCarries(int providerIndex, string layoutId)
        {
            IBlankImageProvider blank = (IBlankImageProvider)AllProviders()[providerIndex];
            IFileSystemDriver driver = blank.Mount(NewStream(blank.CreateBlank(layoutId, new Dictionary<string, string>())), false, layoutId);
            if (driver is not IExtendedAttributeSupport support)
            {
                Assert.Ignore("no file properties");
                return;
            }

            FileSystemCapabilities caps = support.GetCapabilities();
            Assert.That(caps.SupportsExtendedAttributes, Is.EqualTo(caps.SupportedPropertyKeys.Length > 0), "says it has properties exactly when it declares keys");
            for (int i = 0; i < caps.WritablePropertyKeys.Length; i++)
            {
                Assert.That(Array.IndexOf(caps.SupportedPropertyKeys, caps.WritablePropertyKeys[i]), Is.GreaterThanOrEqualTo(0), "writable key " + caps.WritablePropertyKeys[i] + " is declared");
            }

            string path = FirstFolder(driver) + TestFile(driver);
            driver.WriteFile(path, Data());
            IDictionary<string, object>? bag = support.GetFileProperties(path);
            Assert.That(bag, Is.Not.Null, "the written file has properties");

            string undeclared = string.Empty;
            using (IEnumerator<KeyValuePair<string, object>> pairs = bag!.GetEnumerator())
            {
                while (pairs.MoveNext())
                {
                    if (Array.IndexOf(caps.SupportedPropertyKeys, pairs.Current.Key) < 0)
                    {
                        undeclared += " " + pairs.Current.Key;
                    }
                }
            }

            string missing = string.Empty;
            for (int i = 0; i < caps.SupportedPropertyKeys.Length; i++)
            {
                if (!bag.ContainsKey(caps.SupportedPropertyKeys[i]))
                {
                    missing += " " + caps.SupportedPropertyKeys[i];
                }
            }

            Assert.That(undeclared, Is.Empty, "keys a file carries but the driver does not declare");
            Assert.That(missing, Is.Empty, "keys the driver declares but a file does not carry");
            Assert.That(support.GetFileProperties(FirstFolder(driver) + "NOFILE" + driver.ExtensionSeparator + "DAT"), Is.Null, "no such file gives null");
        }

        [TestCaseSource(nameof(Layouts))]
        public void WritableKeys_RoundTripThroughAXatFile(int providerIndex, string layoutId)
        {
            IBlankImageProvider blank = (IBlankImageProvider)AllProviders()[providerIndex];
            byte[] image = blank.CreateBlank(layoutId, new Dictionary<string, string>());

            // A plain write gives the values a file has when nothing is asked for.
            IFileSystemDriver plain = blank.Mount(NewStream(image), false, layoutId);
            if (plain is not IExtendedAttributeSupport plainSupport || plainSupport.GetCapabilities().WritablePropertyKeys.Length == 0)
            {
                Assert.Ignore("no writable file properties");
                return;
            }

            string[] writable = plainSupport.GetCapabilities().WritablePropertyKeys;
            string path = FirstFolder(plain) + TestFile(plain);
            plain.WriteFile(path, Data());
            IDictionary<string, object> before = plainSupport.GetFileProperties(path)!;

            // Every writable value is changed from what a plain write gives, so a driver that
            // ignores one is caught.
            Dictionary<string, object> wanted = new Dictionary<string, object>();
            for (int i = 0; i < writable.Length; i++)
            {
                wanted[writable[i]] = Changed(writable[i], before[writable[i]]);
            }

            IDictionary<string, object> fromXat = NdfsXatSerializer.DeserializeFromJson(NdfsXatSerializer.SerializeToJson(wanted));

            MemoryStream stream = NewStream(image);
            IFileSystemDriver target = blank.Mount(stream, false, layoutId);
            ((IExtendedAttributeSupport)target).WriteFileWithProperties(path, Data(), fromXat);
            if (target is IDisposable disposable)
            {
                disposable.Dispose();
            }

            IFileSystemDriver reread = blank.Mount(new MemoryStream(stream.ToArray(), writable: false), true, layoutId);
            IDictionary<string, object> after = ((IExtendedAttributeSupport)reread).GetFileProperties(path)!;
            Assert.That(reread.ReadFile(path).AsSpan(0, Data().Length).SequenceEqual(Data()), Is.True, "file data");

            string wrong = string.Empty;
            for (int i = 0; i < writable.Length; i++)
            {
                string want = Text(wanted[writable[i]]);
                string got = after.TryGetValue(writable[i], out object? value) ? Text(value) : "(missing)";
                if (want != got)
                {
                    wrong += " " + writable[i] + " wanted " + want + " got " + got + ";";
                }
            }

            Assert.That(wrong, Is.Empty);
        }

        // A value a plain write does not give. Flags flip; numbers flip their lowest bit, which
        // keeps them in range for every key here (types 0 to 3, attribute bits, owners, packed dates).
        private static object Changed(string key, object current)
        {
            switch (current)
            {
                case bool b:
                    return !b;
                case byte or sbyte or short or ushort or int or uint or long:
                    return Convert.ToInt64(current, CultureInfo.InvariantCulture) ^ 1;
                case string text when key == CommodoreDiskDriver.FileTypeKey:
                    return text == "SEQ" ? "USR" : "SEQ";
                default:
                    Assert.Fail("no changed value for " + key + " (" + current.GetType().Name + "); add one to this test");
                    return current;
            }
        }

        private static string Text(object? value)
        {
            return value switch
            {
                null => "(null)",
                bool b => b ? "true" : "false",
                byte or sbyte or short or ushort or int or uint or long or ulong => Convert.ToInt64(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture),
                _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty,
            };
        }

        private static byte[] Data()
        {
            byte[] data = new byte[700];
            for (int i = 0; i < data.Length; i++)
            {
                data[i] = (byte)(i * 5 + 3);
            }

            return data;
        }

        private static MemoryStream NewStream(byte[] image)
        {
            MemoryStream stream = new MemoryStream();
            stream.Write(image, 0, image.Length);
            stream.Position = 0;
            return stream;
        }

        private static string TestFile(IFileSystemDriver driver) => "TEST" + driver.ExtensionSeparator + "DAT";

        // The first folder of a blank disk (the SYSTEM user of an NDFS disk), or the root.
        private static string FirstFolder(IFileSystemDriver driver)
        {
            DirectoryEntry[] entries = driver.ListDirectory("/");
            for (int i = 0; i < entries.Length; i++)
            {
                if (entries[i].Metadata.IsDirectory)
                {
                    return "/" + entries[i].Name + "/";
                }
            }

            return "/";
        }
    }
}
