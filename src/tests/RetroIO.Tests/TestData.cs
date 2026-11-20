//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.IO;

namespace RetroIO.Tests
{
    /// <summary>
    /// Finds the disk images and tools the tests read, without any machine-specific path in the code.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two kinds of test data exist:
    /// </para>
    /// <list type="bullet">
    ///   <item><description>
    ///   The downloaded test set (<c>src/eng/fetch-test-images</c> puts it in
    ///   <c>src/tests/demo/test-images</c>). <see cref="Demo"/> finds it by walking up from the test
    ///   binary to a <c>demo/test-images</c> folder, so it works on every machine with no setting.
    ///   </description></item>
    ///   <item><description>
    ///   Images and tools that only exist on some machines. Each lives in a folder named by an
    ///   environment variable, read by <see cref="FromVariable"/>. <c>src/eng/test-env.example.bat</c>
    ///   lists every variable; copy it to <c>src/eng/test-env.local.bat</c> (not in git), fill in the
    ///   folders on your machine, and run it in the console you run the tests from.
    ///   </description></item>
    /// </list>
    /// <para>
    /// When the data is not there, both return a path that does not exist and that names what is
    /// missing, so the test's own "file not found" check skips it with a message that says what to set.
    /// </para>
    /// </remarks>
    internal static class TestData
    {
        /// <summary>Folder holding the personal test-image collection (d81, dsk, vdk, img, ndfs, ...).</summary>
        public const string TestImagesVariable = "RETROIO_TEST_IMAGES";

        /// <summary>Folder holding the real ND and Sun hard-disk images (sun2-sunos-3.2.img, SUN2.img, 1325.img).</summary>
        public const string HddImagesVariable = "RETROIO_HDD_IMAGES";

        /// <summary>Folder holding the 2.11BSD images taken from tape (1.FromTape, 1b.FromTape).</summary>
        public const string BsdImagesVariable = "RETROIO_BSD_IMAGES";

        /// <summary>Folder holding loose disk images that belong to no collection (211bsd.img, sun2-s2.0-disk.img).</summary>
        public const string LooseImagesVariable = "RETROIO_LOOSE_IMAGES";

        /// <summary>The NDIX source tree, which holds the NDIX filesystem images.</summary>
        public const string NdixDirectoryVariable = "RETROIO_NDIX_DIR";

        /// <summary>
        /// Returns a path inside the folder an environment variable names.
        /// </summary>
        /// <param name="variable">
        /// The environment variable that names the folder.
        /// </param>
        /// <param name="relative">
        /// The file or folder inside it, with forward slashes.
        /// </param>
        /// <returns>
        /// The full path. When the variable is not set, a path starting with <c>$VARIABLE</c>, which
        /// does not exist and tells the reader what to set.
        /// </returns>
        public static string FromVariable(string variable, string relative)
        {
            string? root = Environment.GetEnvironmentVariable(variable);
            string tail = relative.Replace('/', Path.DirectorySeparatorChar);
            if (string.IsNullOrEmpty(root))
            {
                return Path.Combine("$" + variable + " is not set", tail);
            }

            return Path.Combine(root, tail);
        }

        /// <summary>
        /// Returns a path inside the downloaded test set.
        /// </summary>
        /// <param name="relative">
        /// The file or folder inside <c>demo/test-images</c>, with forward slashes.
        /// </param>
        /// <returns>
        /// The first match found walking up from the test binary. When none is found, a path that does
        /// not exist and says that the test set was not downloaded.
        /// </returns>
        public static string Demo(string relative)
        {
            string tail = Path.Combine("demo", "test-images", relative.Replace('/', Path.DirectorySeparatorChar));
            DirectoryInfo? dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                string candidate = Path.Combine(dir.FullName, tail);
                if (File.Exists(candidate) || Directory.Exists(candidate))
                {
                    return candidate;
                }

                dir = dir.Parent;
            }

            return Path.Combine("test set not downloaded - run src/eng/fetch-test-images", tail);
        }
    }
}
