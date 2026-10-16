//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen 
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using RetroIO.Storage.Core;
using RetroIO.Storage.Local.Drivers;
using RetroIO.Storage.Ndfs.Drivers;
using RetroIO.Storage.Ndfs.PropertyBags;

namespace RetroIO.Tests
{
    [TestFixture]
    public class XatFileTests
    {
        private static string TestImagePath1 => TestPaths.Resolve(@"C:\Users\ronny\OneDrive\Documents\RetroFS\test-images\ndfs\nd-disk1-75mb.ndfs");

        [Test]
        public void NdfsXatSerializer_SerializeToJson_Success()
        {
            // Arrange - Create sample PropertyBag
            var propertyBag = new Dictionary<string, object>
            {
                { "ndfs:ObjectName", "TESTFILE" },
                { "ndfs:Type", "PROG" },
                { "ndfs:UserName", "SYSTEM" },
                { "ndfs:UserIndex", 1 },
                { "ndfs:BytesInFile", 10240 },
                { "ndfs:PagesInFile", 5 },
                { "ndfs:AccessBits", 1279 },
                { "ndfs:CreatedDate", new DateTime(1985, 1, 1, 0, 0, 0, DateTimeKind.Utc) },
                { "ndfs:FriendList", new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 } }
            };

            // Act
            string json = NdfsXatSerializer.SerializeToJson(propertyBag);

            // Assert
            Assert.That(json, Is.Not.Null);
            Assert.That(json.Length, Is.GreaterThan(0));
            Assert.That(json, Does.Contain("ndfs:ObjectName"));
            Assert.That(json, Does.Contain("TESTFILE"));
            Assert.That(json, Does.Contain("ndfs:FriendList"));
            // Byte array should be base64 encoded
            Assert.That(json, Does.Contain("AQIDBAUGBwg="));

            Console.WriteLine("Serialized JSON:");
            Console.WriteLine(json);
        }

        [Test]
        public void NdfsXatSerializer_RoundTrip_PreservesData()
        {
            // Arrange
            var originalBag = new Dictionary<string, object>
            {
                { "ndfs:ObjectName", "TESTFILE" },
                { "ndfs:Type", "PROG" },
                { "ndfs:UserIndex", 42 },
                { "ndfs:BytesInFile", 10240 },
                { "ndfs:CreatedDate", new DateTime(2025, 10, 6, 12, 30, 0, DateTimeKind.Utc) },
                { "ndfs:FriendList", new byte[] { 0xFF, 0xAA, 0x55, 0x00 } }
            };

            // Act - Serialize then deserialize
            string json = NdfsXatSerializer.SerializeToJson(originalBag);
            var deserializedBag = NdfsXatSerializer.DeserializeFromJson(json);

            // Assert
            Assert.That(deserializedBag, Is.Not.Null);
            Assert.That(deserializedBag.Count, Is.EqualTo(originalBag.Count));

            // Check string values
            Assert.That(deserializedBag["ndfs:ObjectName"], Is.EqualTo("TESTFILE"));
            Assert.That(deserializedBag["ndfs:Type"], Is.EqualTo("PROG"));

            // Check numeric values
            Assert.That(Convert.ToInt32(deserializedBag["ndfs:UserIndex"]), Is.EqualTo(42));
            Assert.That(Convert.ToInt32(deserializedBag["ndfs:BytesInFile"]), Is.EqualTo(10240));

            // Check DateTime
            Assert.That(deserializedBag["ndfs:CreatedDate"], Is.TypeOf<DateTime>());

            // Check byte array
            Assert.That(deserializedBag["ndfs:FriendList"], Is.TypeOf<byte[]>());
            byte[] friendList = (byte[])deserializedBag["ndfs:FriendList"];
            Assert.That(friendList.Length, Is.EqualTo(4));
            Assert.That(friendList[0], Is.EqualTo(0xFF));
            Assert.That(friendList[1], Is.EqualTo(0xAA));
        }

        [Test]
        public void NdfsXatSerializer_GetXatFileName_RemovesExtension()
        {
            // Test simple extension removal
            Assert.That(NdfsXatSerializer.GetXatFileName("FILE.DAT"), Is.EqualTo("FILE.xat"));
            Assert.That(NdfsXatSerializer.GetXatFileName("TEST.PROG"), Is.EqualTo("TEST.xat"));
            Assert.That(NdfsXatSerializer.GetXatFileName("DATA.BIN"), Is.EqualTo("DATA.xat"));

            // Test no extension
            Assert.That(NdfsXatSerializer.GetXatFileName("NOEXT"), Is.EqualTo("NOEXT.xat"));

            // Test custom extension
            Assert.That(NdfsXatSerializer.GetXatFileName("FILE.DAT", ".meta"), Is.EqualTo("FILE.meta"));
        }

        [Test]
        public void NdfsXatSerializer_IsXatFile_DetectsCorrectly()
        {
            Assert.That(NdfsXatSerializer.IsXatFile("FILE.xat"), Is.True);
            Assert.That(NdfsXatSerializer.IsXatFile("FILE.XAT"), Is.True);
            Assert.That(NdfsXatSerializer.IsXatFile("TEST.DAT"), Is.False);
            Assert.That(NdfsXatSerializer.IsXatFile("NOEXTENSION"), Is.False);
        }

        [Test]
        public void NdfsXatSerializer_GetBaseFileName_RemovesXatExtension()
        {
            Assert.That(NdfsXatSerializer.GetBaseFileName("FILE.xat"), Is.EqualTo("FILE"));
            Assert.That(NdfsXatSerializer.GetBaseFileName("TEST.XAT"), Is.EqualTo("TEST"));
            Assert.That(NdfsXatSerializer.GetBaseFileName("DATA.DAT"), Is.EqualTo("DATA.DAT")); // Not .xat file
        }

        [Test]
        public void NdfsDriver_ImplementsIExtendedAttributeSupport()
        {
            // Arrange
            var driver = new NdfsDriver();

            // Act & Assert
            Assert.That(driver, Is.InstanceOf<IExtendedAttributeSupport>());

            // Check capabilities
            IExtendedAttributeSupport xatDriver = driver as IExtendedAttributeSupport;
            Assert.That(xatDriver, Is.Not.Null);

            var capabilities = xatDriver.GetCapabilities();
            Assert.That(capabilities, Is.Not.Null);
            Assert.That(capabilities.SupportsExtendedAttributes, Is.True);
            Assert.That(capabilities.SupportedPropertyKeys, Is.Not.Empty);
            Assert.That(capabilities.SupportedPropertyKeys.Length, Is.GreaterThan(20)); // NDFS has 23 fields
        }

        [Test]
        public void CommodoreDiskDriver_DoesNotImplementIExtendedAttributeSupport()
        {
            // Arrange
            var driver = new CommodoreDiskDriver();

            // Act & Assert
            Assert.That(driver, Is.Not.InstanceOf<IExtendedAttributeSupport>());
        }

        [Test]
        public void FileSystemCapabilities_SupportsProperty_WorksCorrectly()
        {
            // Arrange
            var capabilities = new FileSystemCapabilities
            {
                SupportsExtendedAttributes = true,
                SupportedPropertyKeys = new[] { "ndfs:ObjectName", "ndfs:Type", "ndfs:UserName" }
            };

            // Act & Assert
            Assert.That(capabilities.SupportsProperty("ndfs:ObjectName"), Is.True);
            Assert.That(capabilities.SupportsProperty("ndfs:Type"), Is.True);
            Assert.That(capabilities.SupportsProperty("ndfs:InvalidProperty"), Is.False);
        }

        [Test]
        public void FileSystemCapabilities_Basic_NoExtendedAttributes()
        {
            // Arrange
            var basicCaps = FileSystemCapabilities.Basic;

            // Act & Assert
            Assert.That(basicCaps.SupportsExtendedAttributes, Is.False);
            Assert.That(basicCaps.SupportedPropertyKeys, Is.Empty);
            Assert.That(basicCaps.SupportsProperty("anyProperty"), Is.False);
        }

        [Test]
        public void NdfsDriver_GetFileProperties_ReturnsPropertyBag()
        {
            // Arrange
            if (!File.Exists(TestImagePath1))
            {
                Assert.Ignore($"Test image not found: {TestImagePath1}");
                return;
            }

            var driver = new NdfsDriver();
            using (var stream = File.OpenRead(TestImagePath1))
            {
                driver.Mount(stream, true);
            }

            IExtendedAttributeSupport xatDriver = driver as IExtendedAttributeSupport;
            Assert.That(xatDriver, Is.Not.Null);

            // Find first file in filesystem
            var entries = driver.ListDirectory("/");
            Assert.That(entries.Length, Is.GreaterThan(0));

            string? firstFilePath = null;
            for (int i = 0; i < entries.Length; i++)
            {
                if (!entries[i].Metadata.IsDirectory)
                {
                    firstFilePath = entries[i].Name;
                    break;
                }
            }

            if (firstFilePath == null)
            {
                Assert.Ignore("No files found in test image");
                return;
            }

            // Act
            var propertyBag = xatDriver.GetFileProperties(firstFilePath);

            // Assert
            Assert.That(propertyBag, Is.Not.Null);
            Assert.That(propertyBag.Count, Is.GreaterThan(0));

            // Should contain NDFS-specific properties
            Assert.That(propertyBag.ContainsKey("ndfs:ObjectName"), Is.True);
            Assert.That(propertyBag.ContainsKey("ndfs:Type"), Is.True);
            Assert.That(propertyBag.ContainsKey("ndfs:UserName"), Is.True);

            Console.WriteLine($"PropertyBag for {firstFilePath}:");
            for (int i = 0; i < propertyBag.Count; i++)
            {
                var kvp = GetKeyValuePairAtIndex(propertyBag, i);
                Console.WriteLine($"  {kvp.Key} = {kvp.Value}");
            }

            driver.Dispose();
        }

        // Helper to get KeyValuePair at index (no LINQ)
        private KeyValuePair<TKey, TValue> GetKeyValuePairAtIndex<TKey, TValue>(
            IDictionary<TKey, TValue> dictionary, int index)
        {
            int currentIndex = 0;
            var enumerator = dictionary.GetEnumerator();
            while (enumerator.MoveNext())
            {
                if (currentIndex == index)
                    return new KeyValuePair<TKey, TValue>(enumerator.Current.Key, enumerator.Current.Value);
                currentIndex++;
            }
            throw new ArgumentOutOfRangeException(nameof(index));
        }
    }
}
