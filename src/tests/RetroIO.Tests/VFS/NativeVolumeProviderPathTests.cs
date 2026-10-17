//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.IO;
using NUnit.Framework;
using RetroIO.Storage.Core;
using RetroIO.Storage.Providers;

namespace RetroIO.Tests.VFS
{
    /// <summary>
    /// Tests that <see cref="NativeVolumeProvider"/> keeps every path inside the mounted folder.
    /// </summary>
    [TestFixture]
    [Category("Unit")]
    public class NativeVolumeProviderPathTests
    {
        private string _parent = null!;
        private string _root = null!;
        private string _sibling = null!;
        private NativeVolumeProvider _provider = null!;
        private UrnContext _context = null!;

        [SetUp]
        public void SetUp()
        {
            _parent = Path.Combine(Path.GetTempPath(), "retroio-native-path-" + Guid.NewGuid().ToString("N"));
            _root = Path.Combine(_parent, "root");
            // A folder whose name STARTS WITH the mount root's name: "root" and "rootX".
            _sibling = Path.Combine(_parent, "rootX");
            Directory.CreateDirectory(_root);
            Directory.CreateDirectory(_sibling);
            File.WriteAllText(Path.Combine(_root, "inside.txt"), "inside");
            File.WriteAllText(Path.Combine(_sibling, "secret.txt"), "outside the mount");

            _provider = new NativeVolumeProvider();
            _provider.Mount(_root, "vol1", readOnly: false);
            _context = new UrnContext { VolumeId = "vol1" };
        }

        [TearDown]
        public void TearDown()
        {
            _provider?.Dispose();
            try { Directory.Delete(_parent, recursive: true); } catch { /* best effort */ }
        }

        [Test]
        public void ReadFile_InsideTheMount_Works()
        {
            Assert.That(System.Text.Encoding.UTF8.GetString(_provider.ReadFile(_context, "/inside.txt")), Is.EqualTo("inside"));
        }

        [Test]
        public void ReadFile_IntoASiblingFolderWhoseNameStartsWithTheRoot_IsRefused()
        {
            // Regression: the root check was a plain StartsWith on the full path, so
            // ".../rootX/secret.txt" counted as being inside ".../root".
            Assert.Throws<UnauthorizedAccessException>(() => _provider.ReadFile(_context, "/../rootX/secret.txt"));
        }

        [Test]
        public void WriteFile_IntoASiblingFolderWhoseNameStartsWithTheRoot_IsRefused_AndNothingIsWritten()
        {
            string target = Path.Combine(_sibling, "new.txt");

            Assert.Throws<UnauthorizedAccessException>(() => _provider.WriteFile(_context, "/../rootX/new.txt", new byte[] { 1 }));
            Assert.That(File.Exists(target), Is.False);
        }

        [Test]
        public void ReadFile_WithDotDotAboveTheRoot_IsRefused()
        {
            Assert.Throws<UnauthorizedAccessException>(() => _provider.ReadFile(_context, "/../../etc/passwd"));
        }
    }
}
