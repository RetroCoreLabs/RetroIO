//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// HackerCorp Labs — https://github.com/HackerCorpLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using RetroIO.Storage.Core;
using RetroIO.Storage.Local;
using RetroIO.Storage.Local.Drivers;

namespace RetroIO.Storage.Local.Tests
{
    /// <summary>
    /// Regression tests for the mount auto-detection: GetDetectionConfidences with NO explicit type
    /// list must score EVERY registered provider (not a hard-coded subset), so a valid D64 is
    /// confidently detected as Commodore. This is the bug where a 174,848-byte D64 showed
    /// "No confident detection" because only NDFS/V7/FFS were ever checked.
    /// </summary>
    [TestFixture]
    [Category("CBM")]
    public class CbmDetectionTests
    {
        private static string? FindCorpusDir()
        {
            DirectoryInfo? dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                string candidate = Path.Combine(dir.FullName, "demo", "test-images", "c64");
                if (Directory.Exists(candidate))
                {
                    return candidate;
                }
                dir = dir.Parent;
            }
            return null;
        }

        private static string? AnyD64()
        {
            string? corpus = FindCorpusDir();
            if (corpus == null)
            {
                return null;
            }
            string d64 = Path.Combine(corpus, "d64");
            if (!Directory.Exists(d64))
            {
                return null;
            }
            string[] files = Directory.GetFiles(d64, "*.d64");
            return files.Length > 0 ? files[0] : null;
        }

        // A factory with several RetroIO.Storage.Local providers registered (no NDFS/BSD here, but the point
        // is that auto-detect scores whatever is registered).
        private static DriverFactory MakeFactory()
        {
            DriverFactory factory = new DriverFactory();
            factory.Register(new CommodoreDiskDriverProvider());
            factory.Register(new T64DriverProvider());
            factory.Register(new PrgDriverProvider());
            factory.Register(new P00DriverProvider());
            factory.Register(new CrtDriverProvider());
            factory.Register(new TapDriverProvider());
            return factory;
        }

        [Test]
        public void GetDetectionConfidences_NoTypes_ScoresEveryRegisteredProvider()
        {
            string? path = AnyD64();
            if (path == null)
            {
                Assert.Ignore("no D64 fixture");
                return;
            }

            Dictionary<string, float> conf = MakeFactory().GetDetectionConfidences(path);

            // All six registered providers must be present (auto, not a hard-coded subset).
            Assert.That(conf.Count, Is.EqualTo(6), "every registered provider is scored");
            Assert.That(conf.ContainsKey("Commodore"), Is.True);
            Assert.That(conf.ContainsKey("T64"), Is.True);
            Assert.That(conf.ContainsKey("CRT"), Is.True);
            Assert.That(conf.ContainsKey("TAP"), Is.True);
        }

        [Test]
        public void GetDetectionConfidences_NoTypes_D64_IsConfidentlyCommodore()
        {
            string? path = AnyD64();
            if (path == null)
            {
                Assert.Ignore("no D64 fixture");
                return;
            }

            Dictionary<string, float> conf = MakeFactory().GetDetectionConfidences(path);

            Assert.That(conf["Commodore"], Is.GreaterThanOrEqualTo(0.8f),
                "a real 35-track D64 must be confidently Commodore (size + extension)");

            // Commodore must be the single highest scorer.
            string best = string.Empty;
            float bestScore = -1f;
            Dictionary<string, float>.Enumerator e = conf.GetEnumerator();
            while (e.MoveNext())
            {
                if (e.Current.Value > bestScore)
                {
                    bestScore = e.Current.Value;
                    best = e.Current.Key;
                }
            }
            Assert.That(best, Is.EqualTo("Commodore"), "Commodore is the top detection for a D64");
        }

        [Test]
        public void GetDetectionConfidences_ExplicitTypes_StillWorks()
        {
            string? path = AnyD64();
            if (path == null)
            {
                Assert.Ignore("no D64 fixture");
                return;
            }

            // Backward compatibility: an explicit list scores only those types.
            Dictionary<string, float> conf = MakeFactory().GetDetectionConfidences(path, "Commodore");
            Assert.That(conf.Count, Is.EqualTo(1));
            Assert.That(conf["Commodore"], Is.GreaterThanOrEqualTo(0.8f));
        }

        [Test]
        public void Detect_D64_ReturnsAMountableCommodoreDriver()
        {
            string? path = AnyD64();
            if (path == null)
            {
                Assert.Ignore("no D64 fixture");
                return;
            }

            IFileSystemDriver? driver = MakeFactory().Detect(path);
            Assert.That(driver, Is.Not.Null, "Detect must pick the Commodore driver for a D64");

            using FileStream stream = File.OpenRead(path);
            driver!.Mount(stream, readOnly: true);
            DirectoryEntry[] entries = driver.ListDirectory("/");
            Assert.That(entries, Is.Not.Null);
            driver.Dispose();
        }
    }
}
