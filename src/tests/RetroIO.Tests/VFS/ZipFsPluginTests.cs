//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using RetroIO.Storage.Core.VFS.Plugins;

namespace RetroIO.Tests.VFS
{
    /// <summary>
    /// Tests for <see cref="ZipFsPlugin"/>: mount, unmount, mount again, dispose, and stream detection.
    /// </summary>
    [TestFixture]
    public class ZipFsPluginTests
    {
        /// <summary>
        /// A MemoryStream that remembers whether it was disposed, so a test can tell
        /// whether the plugin really released the stream it was given.
        /// </summary>
        private sealed class TrackingStream : MemoryStream
        {
            public TrackingStream(byte[] bytes) : base(bytes) { }

            public bool WasDisposed { get; private set; }

            protected override void Dispose(bool disposing)
            {
                WasDisposed = true;
                base.Dispose(disposing);
            }
        }

        // A stream that cannot seek, to exercise the CanHandleAsync contract.
        private sealed class NonSeekableStream : MemoryStream
        {
            public NonSeekableStream(byte[] bytes) : base(bytes) { }

            public override bool CanSeek => false;
        }

        // Builds a small ZIP in memory with one file in the root and one in a folder.
        private static byte[] MakeZip()
        {
            using MemoryStream ms = new MemoryStream();
            using (ZipArchive zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            {
                ZipArchiveEntry a = zip.CreateEntry("hello.txt");
                using (StreamWriter w = new StreamWriter(a.Open()))
                    w.Write("hi");
                ZipArchiveEntry b = zip.CreateEntry("dir/inner.txt");
                using (StreamWriter w = new StreamWriter(b.Open()))
                    w.Write("inside");
            }
            return ms.ToArray();
        }

        [Test]
        public async Task Unmount_ThenMountAgain_ClosesTheSecondStreamOnDispose()
        {
            // Regression: Unmount used to call Dispose, which latched _disposed, so the
            // next Dispose did nothing and the second archive and its stream were never closed.
            ZipFsPlugin plugin = new ZipFsPlugin();
            TrackingStream first = new TrackingStream(MakeZip());
            TrackingStream second = new TrackingStream(MakeZip());

            await plugin.MountAsync(first, CancellationToken.None);
            plugin.Unmount("zip-temp");
            Assert.That(first.WasDisposed, Is.True, "Unmount must close the stream it was mounted from");

            await plugin.MountAsync(second, CancellationToken.None);
            Assert.That(plugin.ReadFile(null!, "hello.txt"), Is.EqualTo(new byte[] { (byte)'h', (byte)'i' }));

            plugin.Dispose();
            Assert.That(second.WasDisposed, Is.True, "Dispose after a second mount must close the second stream");
        }

        [Test]
        public async Task Unmount_LeavesThePluginEmptyAndReusable()
        {
            ZipFsPlugin plugin = new ZipFsPlugin();
            await plugin.MountAsync(new TrackingStream(MakeZip()), CancellationToken.None);

            plugin.Unmount("zip-temp");

            Assert.That(plugin.ListMounts(), Is.Empty);
            Assert.That(plugin.GetMountInfo("zip-temp"), Is.Null);
            Assert.DoesNotThrowAsync(() => plugin.MountAsync(new TrackingStream(MakeZip()), CancellationToken.None));
            plugin.Dispose();
        }

        [Test]
        public async Task Dispose_CalledTwice_DoesNothingTheSecondTime()
        {
            ZipFsPlugin plugin = new ZipFsPlugin();
            TrackingStream stream = new TrackingStream(MakeZip());
            await plugin.MountAsync(stream, CancellationToken.None);

            plugin.Dispose();
            Assert.DoesNotThrow(() => plugin.Dispose());
            Assert.That(stream.WasDisposed, Is.True);
        }

        [Test]
        public void Unmount_WithAnotherVolumeId_Throws()
        {
            ZipFsPlugin plugin = new ZipFsPlugin();
            plugin.MountAsync(new TrackingStream(MakeZip()), CancellationToken.None).GetAwaiter().GetResult();

            Assert.Throws<ArgumentException>(() => plugin.Unmount("not-this-one"));
            plugin.Dispose();
        }

        [Test]
        public async Task CanHandleAsync_RecognisesTheZipSignature_AndRestoresPositionToZero()
        {
            ZipFsPlugin plugin = new ZipFsPlugin();
            using MemoryStream zip = new MemoryStream(MakeZip());
            using MemoryStream notZip = new MemoryStream(new byte[] { 1, 2, 3, 4, 5 });

            Assert.That(await plugin.CanHandleAsync(zip, CancellationToken.None), Is.True);
            Assert.That(zip.Position, Is.EqualTo(0));
            Assert.That(await plugin.CanHandleAsync(notZip, CancellationToken.None), Is.False);
        }

        [Test]
        public void CanHandleAsync_NonSeekableStream_ThrowsArgumentException()
        {
            // IExtFsPlugin.CanHandleAsync documents ArgumentException for a stream that cannot seek.
            ZipFsPlugin plugin = new ZipFsPlugin();
            using NonSeekableStream stream = new NonSeekableStream(MakeZip());

            Assert.ThrowsAsync<ArgumentException>(() => plugin.CanHandleAsync(stream, CancellationToken.None));
        }

        [Test]
        public async Task ListDirectory_ShowsTopLevelFileAndFolderOnce()
        {
            ZipFsPlugin plugin = new ZipFsPlugin();
            await plugin.MountAsync(new TrackingStream(MakeZip()), CancellationToken.None);

            Storage.Core.DirectoryEntry[] top = plugin.ListDirectory(null!, "");

            Assert.That(top.Length, Is.EqualTo(2));
            plugin.Dispose();
        }
    }
}
