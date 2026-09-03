//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// HackerCorp Labs — https://github.com/HackerCorpLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using System.IO;
using System.Text;
using NUnit.Framework;
using RetroIO.Storage.Core;
using RetroIO.Storage.Ndfs.Creation;
using RetroIO.Storage.Ndfs.Drivers;

namespace RetroIO.Tests
{
    /// <summary>
    /// Regression tests for NDFS object name/type handling on write. NDFS stores a file as
    /// ObjectName + Type (4-char) and displays it as "NAME:TYPE" (colon separator). The bug:
    /// copying "fs.bpun" into an NDFS volume showed up as just "fs" — the ":BPUN" type was lost.
    /// These tests pin down whether the driver preserves the type in-memory and across a remount,
    /// for both the colon form ("FS:BPUN") and the period form ("fs.bpun").
    /// </summary>
    [TestFixture]
    public class NdfsFileTypeTests
    {
        private string _testDir = string.Empty;

        [SetUp]
        public void SetUp()
        {
            _testDir = Path.Combine(Path.GetTempPath(), "RetroFS_NdfsFileTypeTests");
            Directory.CreateDirectory(_testDir);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_testDir))
            {
                Directory.Delete(_testDir, true);
            }
        }

        private string MakeImage(string name)
        {
            string path = Path.Combine(_testDir, name);
            NdfsImageCreator.CreateImageFromTemplate(
                path,
                NdfsImageTemplate.Floppy12MB,
                directoryName: "TYPE-TEST",
                bootLoaderType: NdfsBootLoaderType.Floppy);
            return path;
        }

        // Find an entry whose Name matches (case-insensitive), no LINQ.
        private static DirectoryEntry? Find(DirectoryEntry[] entries, string name)
        {
            for (int i = 0; i < entries.Length; i++)
            {
                if (string.Equals(entries[i].Name, name, System.StringComparison.OrdinalIgnoreCase))
                {
                    return entries[i];
                }
            }
            return null;
        }

        [Test]
        public void WriteFile_ColonType_PreservesTypeInListingAndPersists()
        {
            string image = MakeImage("colon.img");
            byte[] data = Encoding.ASCII.GetBytes("hello bpun payload");

            using (FileStream stream = new FileStream(image, FileMode.Open, FileAccess.ReadWrite))
            {
                NdfsDriver driver = new NdfsDriver();
                driver.Mount(stream, readOnly: false);
                driver.CreateDirectory("/ALICE");

                driver.WriteFile("/ALICE/FS:BPUN", data);

                DirectoryEntry[] entries = driver.ListDirectory("/ALICE");
                DirectoryEntry? fs = Find(entries, "FS:BPUN");
                Assert.That(fs, Is.Not.Null, "in-memory listing must show FS:BPUN, not just FS");

                // The UI surfaces entry.Metadata.Name — it must ALSO carry the type, otherwise the
                // grid shows just "FS" (this is the exact bug that was reported).
                Assert.That(fs!.Metadata.Name, Is.EqualTo("FS:BPUN"),
                    "Metadata.Name (shown in the UI) must include the :TYPE");

                driver.Dispose();
            }

            // Persisted across remount?
            using (FileStream stream2 = new FileStream(image, FileMode.Open, FileAccess.Read))
            {
                NdfsDriver verify = new NdfsDriver();
                verify.Mount(stream2, readOnly: true);
                DirectoryEntry[] entries = verify.ListDirectory("/ALICE");
                Assert.That(Find(entries, "FS:BPUN"), Is.Not.Null, "FS:BPUN must persist with its type");
                verify.Dispose();
            }
        }

        [Test]
        public void WriteFile_PeriodType_StoredAsColonType()
        {
            // This is the actual copy scenario: a Windows file "fs.bpun" copied into NDFS.
            string image = MakeImage("period.img");
            byte[] data = Encoding.ASCII.GetBytes("hello bpun payload");

            using (FileStream stream = new FileStream(image, FileMode.Open, FileAccess.ReadWrite))
            {
                NdfsDriver driver = new NdfsDriver();
                driver.Mount(stream, readOnly: false);
                driver.CreateDirectory("/ALICE");

                driver.WriteFile("/ALICE/fs.bpun", data);

                DirectoryEntry[] entries = driver.ListDirectory("/ALICE");
                // NDFS should store it as FS + type BPUN, displayed FS:BPUN — NOT bare "FS".
                DirectoryEntry? fs = Find(entries, "FS:BPUN");
                Assert.That(fs, Is.Not.Null,
                    "a copied 'fs.bpun' must keep its type as FS:BPUN, not lose it to 'FS'");
                Assert.That(fs!.Metadata.Name, Is.EqualTo("FS:BPUN"),
                    "Metadata.Name (shown in the UI) must include the :TYPE for a copied 'fs.bpun'");

                driver.Dispose();
            }
        }
    }
}
