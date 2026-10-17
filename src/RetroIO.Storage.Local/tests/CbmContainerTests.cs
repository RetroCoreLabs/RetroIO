//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
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
    /// Detection + read tests for the single-program Commodore containers (PRG, P00) against the
    /// committed corpus under demo/test-images/c64.
    /// </summary>
    [TestFixture]
    [Category("CBM")]
    public class CbmContainerTests
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

        private static IEnumerable<string> Images(string sub, string pattern)
        {
            string? corpus = FindCorpusDir();
            if (corpus == null)
            {
                yield break;
            }
            string d = Path.Combine(corpus, sub);
            if (!Directory.Exists(d))
            {
                yield break;
            }
            string[] files = Directory.GetFiles(d, pattern);
            for (int i = 0; i < files.Length; i++)
            {
                yield return files[i];
            }
        }

        private static IEnumerable<string> PrgImages() => Images("prg", "*.prg");
        private static IEnumerable<string> P00Images() => Images("p00", "*.p00");
        private static IEnumerable<string> CrtImages() => Images("crt", "*.crt");
        private static IEnumerable<string> TapImages() => Images("tap", "*.tap");

        private static DriverFactory MakeFactory()
        {
            DriverFactory factory = new DriverFactory();
            factory.Register(new PrgDriverProvider());
            factory.Register(new P00DriverProvider());
            factory.Register(new CrtDriverProvider());
            factory.Register(new TapDriverProvider());
            return factory;
        }

        private static uint Be32(byte[] d, int o) => (uint)((d[o] << 24) | (d[o + 1] << 16) | (d[o + 2] << 8) | d[o + 3]);
        private static uint Le32(byte[] d, int o) => (uint)(d[o] | (d[o + 1] << 8) | (d[o + 2] << 16) | (d[o + 3] << 24));

        [TestCaseSource(nameof(PrgImages))]
        public void Prg_Detect_Mount_Read(string path)
        {
            IFileSystemDriver? driver = MakeFactory().Detect(path);
            Assert.That(driver, Is.Not.Null, "PRG should be detected");

            using FileStream stream = File.OpenRead(path);
            driver!.Mount(stream, readOnly: true);
            Assert.That(driver.FileSystemType, Is.EqualTo("PRG"), "a raw .prg mounts as PRG");

            DirectoryEntry[] entries = driver.ListDirectory("/");
            Assert.That(entries.Length, Is.EqualTo(1), "a PRG is a single-file container");

            byte[] content = driver.ReadFile(entries[0].Name);
            Assert.That(content, Is.EqualTo(File.ReadAllBytes(path)), "PRG content is the whole file (load addr + program)");
            driver.Dispose();
        }

        [TestCaseSource(nameof(P00Images))]
        public void P00_Detect_Mount_Read(string path)
        {
            IFileSystemDriver? driver = MakeFactory().Detect(path);
            Assert.That(driver, Is.Not.Null, "P00 should be detected by its signature");

            using FileStream stream = File.OpenRead(path);
            driver!.Mount(stream, readOnly: true);
            Assert.That(driver.FileSystemType, Is.EqualTo("P00"), "a .p00 mounts as P00 (signature recognised)");

            DirectoryEntry[] entries = driver.ListDirectory("/");
            Assert.That(entries.Length, Is.EqualTo(1));

            byte[] content = driver.ReadFile(entries[0].Name);
            byte[] raw = File.ReadAllBytes(path);
            byte[] expected = raw[0x1A..]; // payload after the 26-byte PC64 header
            Assert.That(content, Is.EqualTo(expected), "P00 payload is the bytes after the header");
            driver.Dispose();
        }

        [TestCaseSource(nameof(CrtImages))]
        public void Crt_Detect_Mount_ListAndExtractFirstChip(string path)
        {
            IFileSystemDriver? driver = MakeFactory().Detect(path);
            Assert.That(driver, Is.Not.Null, "CRT should be detected by its magic");

            using FileStream stream = File.OpenRead(path);
            driver!.Mount(stream, readOnly: true);
            Assert.That(driver.FileSystemType, Is.EqualTo("CRT"));

            DirectoryEntry[] entries = driver.ListDirectory("/");
            Assert.That(entries.Length, Is.GreaterThan(0), "a CRT has at least one CHIP packet");

            byte[] first = driver.ReadFile(entries[0].Name);
            Assert.That(first.Length, Is.GreaterThan(0));

            // Independently verify the first CHIP's ROM: header length (BE @0x10) -> first packet,
            // whose data starts +0x10 in.
            byte[] raw = File.ReadAllBytes(path);
            int headerOff = (int)Be32(raw, 0x10);
            int dataOff = headerOff + 0x10;
            byte[] expected = raw[dataOff..(dataOff + first.Length)];
            Assert.That(first, Is.EqualTo(expected), "first CHIP ROM is extracted correctly");
            driver.Dispose();
        }

        [TestCaseSource(nameof(TapImages))]
        public void Tap_Detect_Mount_ReadPulseData(string path)
        {
            IFileSystemDriver? driver = MakeFactory().Detect(path);
            Assert.That(driver, Is.Not.Null, "TAP should be detected by its magic");

            using FileStream stream = File.OpenRead(path);
            driver!.Mount(stream, readOnly: true);
            Assert.That(driver.FileSystemType, Is.EqualTo("TAP"));

            DirectoryEntry[] entries = driver.ListDirectory("/");
            Assert.That(entries.Length, Is.EqualTo(1), "a TAP is a single signal blob");

            byte[] content = driver.ReadFile(entries[0].Name);
            byte[] raw = File.ReadAllBytes(path);
            long declared = Le32(raw, 0x10);
            int available = raw.Length - 0x14;
            int len = (int)Math.Min(declared, available);
            byte[] expected = raw[0x14..(0x14 + len)];
            Assert.That(content, Is.EqualTo(expected), "pulse data is the bytes after the 0x14 header");
            driver.Dispose();
        }
    }
}
