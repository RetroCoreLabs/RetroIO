//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.IO;

namespace RetroIO.Protocols.Xmsg.Hosting.Tests
{
    /// <summary>
    /// A fresh folder under the system temp folder, deleted with everything in it on dispose.
    /// </summary>
    internal sealed class TempFolder : IDisposable
    {
        /// <summary>
        /// Creates the folder.
        /// </summary>
        public TempFolder()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "xmsg-hosting-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        /// <summary>
        /// Gets the full path of the folder.
        /// </summary>
        public string Path { get; }

        /// <summary>
        /// Deletes the folder and its contents.
        /// </summary>
        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
