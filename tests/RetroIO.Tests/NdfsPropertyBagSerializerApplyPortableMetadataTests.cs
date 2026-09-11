//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using System.Collections.Generic;
using NUnit.Framework;
using RetroIO.Storage.Ndfs.Elements;
using RetroIO.Storage.Ndfs.PropertyBags;

namespace RetroIO.Tests
{
    /// <summary>
    /// Regression tests for NdfsPropertyBagSerializer.ApplyPortableMetadata — the function
    /// NdfsFileSystem.WriteFileWithProperties uses to restore metadata from a re-imported ".xat"
    /// sidecar onto a freshly-written ObjectEntry. Self-contained (no disk image fixture): builds
    /// a plain in-memory ObjectEntry and property bag directly, so these always run regardless of
    /// whether an external test image is present.
    /// </summary>
    [TestFixture]
    public class NdfsPropertyBagSerializerApplyPortableMetadataTests
    {
        /// <summary>
        /// Regression: a real exported .xat sidecar
        /// ("ndfs.date_created": 2372846803, "ndfs.last_read_date": 2439864995,
        ///  "ndfs.last_write_date": 2372855763) was found to only restore DateCreated on
        /// re-import — LastDateOpenedForRead/LastDateOpenedForWrite were silently dropped, because
        /// ApplyPortableMetadata never included them in its field whitelist. All three are
        /// equally portable historical timestamps (unlike structural/allocation fields such as
        /// PagesInFile/FilePointer/ObjectIndex, which correctly stay excluded) and must all
        /// round-trip.
        /// </summary>
        [Test]
        public void ApplyPortableMetadata_AllThreeDateFields_AreRestored()
        {
            // Arrange - the exact values from the bug report.
            var propertyBag = new Dictionary<string, object>
            {
                [NdfsPropertyBagKeys.DateCreated] = 2372846803u,
                [NdfsPropertyBagKeys.LastDateOpenedForRead] = 2439864995u,
                [NdfsPropertyBagKeys.LastDateOpenedForWrite] = 2372855763u,
            };

            var entry = new ObjectEntry
            {
                // Simulate what NdfsFileSystem.WriteFile already set before
                // ApplyPortableMetadata runs (see WriteFileWithProperties): a "just written now"
                // default that the sidecar's original values must override.
                DateCreated = 0,
                LastDateOpenedForRead = 0,
                LastDateOpenedForWrite = 999u
            };

            // Act
            NdfsPropertyBagSerializer.ApplyPortableMetadata(propertyBag, entry);

            // Assert
            Assert.That(entry.DateCreated, Is.EqualTo(2372846803u), "DateCreated must be restored");
            Assert.That(entry.LastDateOpenedForRead, Is.EqualTo(2439864995u),
                "LastDateOpenedForRead must be restored — this was silently dropped by the bug");
            Assert.That(entry.LastDateOpenedForWrite, Is.EqualTo(2372855763u),
                "LastDateOpenedForWrite must be restored — this was silently dropped by the bug");
        }

        [Test]
        public void ApplyPortableMetadata_AccessBits_StillRestored()
        {
            // Not a regression (already covered before this fix) — kept here to confirm the fix
            // for the two date fields didn't disturb the field that already worked.
            var propertyBag = new Dictionary<string, object>
            {
                [NdfsPropertyBagKeys.AccessBits] = 0x0155u,
            };
            var entry = new ObjectEntry { AccessBits = 0x03FF };

            NdfsPropertyBagSerializer.ApplyPortableMetadata(propertyBag, entry);

            Assert.That(entry.AccessBits, Is.EqualTo(0x0155u));
        }

        // ------------------------------------------------------------------------------------
        // Exhaustive field coverage. "AccessBits + one other field" was exactly the shape of
        // test that let LastDateOpenedForRead/LastDateOpenedForWrite go unrestored for an entire
        // release — a single representative example proves the mechanism works, not that every
        // field it claims to handle actually is. These two tests instead cover EVERY key
        // NdfsPropertyBagKeys.GetAllKeys() knows about, and fail loudly if a new key is ever
        // added without anyone deciding what ApplyPortableMetadata should do with it.
        // ------------------------------------------------------------------------------------

        /// <summary>Keys ApplyPortableMetadata restores onto the ObjectEntry.</summary>
        private static readonly string[] PortableKeys =
        {
            NdfsPropertyBagKeys.AccessBits,
            NdfsPropertyBagKeys.DateCreated,
            NdfsPropertyBagKeys.LastDateOpenedForRead,
            NdfsPropertyBagKeys.LastDateOpenedForWrite,
        };

        /// <summary>
        /// Structural/allocation ObjectEntry fields ApplyPortableMetadata must never touch (see
        /// its doc comment): the write that actually just happened owns these, not an imported bag.
        /// </summary>
        private static readonly string[] StructuralObjectEntryKeys =
        {
            NdfsPropertyBagKeys.Header,
            NdfsPropertyBagKeys.ObjectIndex,
            NdfsPropertyBagKeys.ObjectName,
            NdfsPropertyBagKeys.Type,
            NdfsPropertyBagKeys.UserName,
            NdfsPropertyBagKeys.UserIndex,
            NdfsPropertyBagKeys.ObjectEntryNextVersion,
            NdfsPropertyBagKeys.ObjectEntryPrevVersion,
            NdfsPropertyBagKeys.FileTypeFlags,
            NdfsPropertyBagKeys.DeviceNumber,
            NdfsPropertyBagKeys.UserIndexOfReservingUser,
            NdfsPropertyBagKeys.CurrentOpenCount,
            NdfsPropertyBagKeys.TotalOpenCount,
            NdfsPropertyBagKeys.PagesInFile,
            NdfsPropertyBagKeys.BytesInFile,
            NdfsPropertyBagKeys.FileType,
        };

        /// <summary>
        /// Keys that belong to UserEntry, or are purely computed/derived strings — out of scope
        /// for ApplyPortableMetadata(propertyBag, ObjectEntry), which only ever receives an
        /// ObjectEntry. Listed explicitly (not treated as "everything else") so
        /// AllNdfsPropertyBagKeys_AreExplicitlyCategorized fails if a new key shows up that nobody
        /// has actually looked at yet.
        /// </summary>
        private static readonly string[] OutOfScopeKeys =
        {
            NdfsPropertyBagKeys.UserFlags,
            NdfsPropertyBagKeys.EnterCount,
            NdfsPropertyBagKeys.Password,
            NdfsPropertyBagKeys.UserDateCreated,
            NdfsPropertyBagKeys.LastDateEntered,
            NdfsPropertyBagKeys.PagesReserved,
            NdfsPropertyBagKeys.PagesUsed,
            NdfsPropertyBagKeys.DirectoryIndex,
            NdfsPropertyBagKeys.DefaultFileAccess,
            NdfsPropertyBagKeys.Friends,
            NdfsPropertyBagKeys.AccessPermissionString,
            NdfsPropertyBagKeys.FileTypeString,
            NdfsPropertyBagKeys.IsVersioned,
            NdfsPropertyBagKeys.VersionCount,

            // Holes is supplied by the FILE SYSTEM, not by SerializeObjectEntry, so it is not part
            // of the bag this test builds from an ObjectEntry. An entry cannot know where the holes
            // are - they live in the file's index. NdfsFileSystem.AddSparseHoles fills it in on the
            // GetFileProperties and ReadFileWithProperties paths, and NdfsSparseHoleXatTests covers
            // it there.
            //
            // ApplyPortableMetadata must never restore it either: honouring a hole map means forcing
            // a hole at each listed page while writing, which only the write path can do, and
            // all-zero detection cannot tell a hole from a page of real zeros.
            NdfsPropertyBagKeys.Holes,
        };

        [Test]
        public void AllNdfsPropertyBagKeys_AreExplicitlyCategorized()
        {
            string[] allKeys = NdfsPropertyBagKeys.GetAllKeys();

            for (int i = 0; i < allKeys.Length; i++)
            {
                bool categorized = Contains(PortableKeys, allKeys[i]) ||
                                   Contains(StructuralObjectEntryKeys, allKeys[i]) ||
                                   Contains(OutOfScopeKeys, allKeys[i]);
                Assert.That(categorized, Is.True,
                    $"'{allKeys[i]}' is not categorized by this test. Decide whether " +
                    "ApplyPortableMetadata should restore it (add to PortableKeys) or not " +
                    "(add to StructuralObjectEntryKeys or OutOfScopeKeys), then add coverage in " +
                    "ApplyPortableMetadata_EveryObjectEntryField_OnlyPortableFieldsChange.");
            }

            // Reverse check: every key referenced above must still exist, catching a rename.
            AssertAllExistIn(PortableKeys, allKeys);
            AssertAllExistIn(StructuralObjectEntryKeys, allKeys);
            AssertAllExistIn(OutOfScopeKeys, allKeys);
        }

        [Test]
        public void ApplyPortableMetadata_EveryObjectEntryField_OnlyPortableFieldsChange()
        {
            // Baseline: a distinct, recognizable value for every ObjectEntry field.
            var entry = new ObjectEntry
            {
                Header = 1001u,
                ObjectIndex = 1002,
                ObjectName = "BASEOBJ",
                Type = "BASE",
                UserName = "BASEUSER",
                UserIndex = 1003,
                ObjectEntryNextVersion = 1004u,
                ObjectEntryPrevVersion = 1005u,
                AccessBits = 1006u,
                FileTypeFlags = FileTypes.IndexedFile,
                DeviceNumber = 1007u,
                UserIndexOfReservingUser = 1008u,
                CurrentOpenCount = 1009u,
                TotalOpenCount = 1010u,
                DateCreated = 1011u,
                LastDateOpenedForRead = 1012u,
                LastDateOpenedForWrite = 1013u,
                PagesInFile = 1014u,
                BytesInFile = 1015u,
                FileType = 1016
            };

            // A property bag with EVERY ObjectEntry-mapped key set to a value different from the
            // baseline above, so a field that's wrongly changed (or wrongly left alone) is
            // unambiguous either way.
            var propertyBag = new Dictionary<string, object>
            {
                [NdfsPropertyBagKeys.Header] = 2001u,
                [NdfsPropertyBagKeys.ObjectIndex] = 2002,
                [NdfsPropertyBagKeys.ObjectName] = "SENTINEL",
                [NdfsPropertyBagKeys.Type] = "SENT",
                [NdfsPropertyBagKeys.UserName] = "SENTINELUSER",
                [NdfsPropertyBagKeys.UserIndex] = 2003,
                [NdfsPropertyBagKeys.ObjectEntryNextVersion] = 2004u,
                [NdfsPropertyBagKeys.ObjectEntryPrevVersion] = 2005u,
                [NdfsPropertyBagKeys.AccessBits] = 2006u,
                [NdfsPropertyBagKeys.FileTypeFlags] = (ushort)FileTypes.ContiguousFile,
                [NdfsPropertyBagKeys.DeviceNumber] = 2008u,
                [NdfsPropertyBagKeys.UserIndexOfReservingUser] = 2009u,
                [NdfsPropertyBagKeys.CurrentOpenCount] = 2010u,
                [NdfsPropertyBagKeys.TotalOpenCount] = 2011u,
                [NdfsPropertyBagKeys.DateCreated] = 2012u,
                [NdfsPropertyBagKeys.LastDateOpenedForRead] = 2013u,
                [NdfsPropertyBagKeys.LastDateOpenedForWrite] = 2014u,
                [NdfsPropertyBagKeys.PagesInFile] = 2015u,
                [NdfsPropertyBagKeys.BytesInFile] = 2016u,
                [NdfsPropertyBagKeys.FileType] = 2017,
            };

            // Sanity: this test must cover exactly the portable + structural key sets, no more,
            // no less — if ApplyPortableMetadata's signature ever changes scope, this catches it.
            Assert.That(propertyBag.Count, Is.EqualTo(PortableKeys.Length + StructuralObjectEntryKeys.Length));

            // Act
            NdfsPropertyBagSerializer.ApplyPortableMetadata(propertyBag, entry);

            // Assert: portable fields changed to the sentinel value.
            Assert.That(entry.AccessBits, Is.EqualTo(2006u), "AccessBits must be restored");
            Assert.That(entry.DateCreated, Is.EqualTo(2012u), "DateCreated must be restored");
            Assert.That(entry.LastDateOpenedForRead, Is.EqualTo(2013u), "LastDateOpenedForRead must be restored");
            Assert.That(entry.LastDateOpenedForWrite, Is.EqualTo(2014u), "LastDateOpenedForWrite must be restored");

            // Assert: every structural/allocation field remains exactly at baseline.
            Assert.That(entry.Header, Is.EqualTo(1001u), "Header must not be touched");
            Assert.That(entry.ObjectIndex, Is.EqualTo(1002), "ObjectIndex must not be touched");
            Assert.That(entry.ObjectName, Is.EqualTo("BASEOBJ"), "ObjectName must not be touched");
            Assert.That(entry.Type, Is.EqualTo("BASE"), "Type must not be touched");
            Assert.That(entry.UserName, Is.EqualTo("BASEUSER"), "UserName must not be touched");
            Assert.That(entry.UserIndex, Is.EqualTo(1003), "UserIndex must not be touched");
            Assert.That(entry.ObjectEntryNextVersion, Is.EqualTo(1004u), "ObjectEntryNextVersion must not be touched");
            Assert.That(entry.ObjectEntryPrevVersion, Is.EqualTo(1005u), "ObjectEntryPrevVersion must not be touched");
            Assert.That(entry.FileTypeFlags, Is.EqualTo(FileTypes.IndexedFile), "FileTypeFlags must not be touched");
            Assert.That(entry.DeviceNumber, Is.EqualTo(1007u), "DeviceNumber must not be touched");
            Assert.That(entry.UserIndexOfReservingUser, Is.EqualTo(1008u), "UserIndexOfReservingUser must not be touched");
            Assert.That(entry.CurrentOpenCount, Is.EqualTo(1009u), "CurrentOpenCount must not be touched");
            Assert.That(entry.TotalOpenCount, Is.EqualTo(1010u), "TotalOpenCount must not be touched");
            Assert.That(entry.PagesInFile, Is.EqualTo(1014u), "PagesInFile must not be touched");
            Assert.That(entry.BytesInFile, Is.EqualTo(1015u), "BytesInFile must not be touched");
            Assert.That(entry.FileType, Is.EqualTo(1016), "FileType must not be touched");
        }

        private static bool Contains(string[] array, string value)
        {
            for (int i = 0; i < array.Length; i++)
            {
                if (array[i] == value)
                    return true;
            }
            return false;
        }

        private static void AssertAllExistIn(string[] subset, string[] all)
        {
            for (int i = 0; i < subset.Length; i++)
            {
                Assert.That(Contains(all, subset[i]), Is.True,
                    $"'{subset[i]}' no longer exists in NdfsPropertyBagKeys.GetAllKeys() — this test's key lists are stale");
            }
        }

        [Test]
        public void ApplyPortableMetadata_MissingKeys_LeavesFieldsUnchanged()
        {
            var propertyBag = new Dictionary<string, object>();
            var entry = new ObjectEntry
            {
                DateCreated = 111u,
                LastDateOpenedForRead = 222u,
                LastDateOpenedForWrite = 333u
            };

            NdfsPropertyBagSerializer.ApplyPortableMetadata(propertyBag, entry);

            Assert.That(entry.DateCreated, Is.EqualTo(111u));
            Assert.That(entry.LastDateOpenedForRead, Is.EqualTo(222u));
            Assert.That(entry.LastDateOpenedForWrite, Is.EqualTo(333u));
        }
    }
}
