//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen 
// HackerCorp Labs — https://github.com/HackerCorpLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using System;
using NUnit.Framework;
using RetroIO.Storage.Core;

namespace RetroIO.Tests
{
    /// <summary>
    /// Tests for PathNormalizer class - RFC 3986 path normalization and security validation.
    /// </summary>
    [TestFixture]
    public class PathNormalizerTests
    {
        #region Dot-Segment Removal Tests (RFC 3986 Section 6.2.2)

        [Test]
        public void Normalize_RemovesCurrentDirectoryDots()
        {
            Assert.That(PathNormalizer.Normalize("/a/./b"), Is.EqualTo("/a/b"));
            Assert.That(PathNormalizer.Normalize("/a/b/./c"), Is.EqualTo("/a/b/c"));
            Assert.That(PathNormalizer.Normalize("/a/././b"), Is.EqualTo("/a/b"));
        }

        [Test]
        public void Normalize_RemovesParentDirectoryDots()
        {
            Assert.That(PathNormalizer.Normalize("/a/b/../c"), Is.EqualTo("/a/c"));
            Assert.That(PathNormalizer.Normalize("/a/b/c/.."), Is.EqualTo("/a/b"));
            Assert.That(PathNormalizer.Normalize("/a/.."), Is.EqualTo("/"));
        }

        [Test]
        public void Normalize_RFC3986_ExamplePaths()
        {
            // Examples from RFC 3986 Section 5.4
            Assert.That(PathNormalizer.Normalize("/a/b/c/./../../g"), Is.EqualTo("/a/g"));
            Assert.That(PathNormalizer.Normalize("/mid/content=5/../6"), Is.EqualTo("/mid/6"));
        }

        [Test]
        public void Normalize_ComplexDotSegments()
        {
            Assert.That(PathNormalizer.Normalize("/a/b/c/../../d/file.txt"), Is.EqualTo("/a/d/file.txt"));
            Assert.That(PathNormalizer.Normalize("/a/b/c/../../../file.txt"), Is.EqualTo("/file.txt"));
        }

        [Test]
        public void Normalize_TrailingDots()
        {
            Assert.That(PathNormalizer.Normalize("/a/b/."), Is.EqualTo("/a/b"));
            Assert.That(PathNormalizer.Normalize("/a/b/.."), Is.EqualTo("/a"));
            Assert.That(PathNormalizer.Normalize("/a/b/c/.."), Is.EqualTo("/a/b"));
        }

        #endregion

        #region Root Escape Prevention Tests (SECURITY CRITICAL)

        [Test]
        public void Normalize_CannotEscapeRoot_SingleDotDot()
        {
            // Attempting to go above root should stay at root
            Assert.That(PathNormalizer.Normalize("/.."), Is.EqualTo("/"));
        }

        [Test]
        public void Normalize_CannotEscapeRoot_MultipleDotDot()
        {
            Assert.That(PathNormalizer.Normalize("/../.."), Is.EqualTo("/"));
            Assert.That(PathNormalizer.Normalize("/../../.."), Is.EqualTo("/"));
        }

        [Test]
        public void Normalize_CannotEscapeRoot_WithPath()
        {
            // Even with path after, cannot escape root
            Assert.That(PathNormalizer.Normalize("/../../../etc/passwd"), Is.EqualTo("/etc/passwd"));
            Assert.That(PathNormalizer.Normalize("/game/../../../secret"), Is.EqualTo("/secret"));
        }

        [Test]
        public void Normalize_DirectoryTraversalAttack_Blocked()
        {
            // Common directory traversal attack patterns
            Assert.That(PathNormalizer.Normalize("/../../../etc/passwd"), Is.EqualTo("/etc/passwd"));
            Assert.That(PathNormalizer.Normalize("/../../../windows/system32/config"), Is.EqualTo("/windows/system32/config"));
            Assert.That(PathNormalizer.Normalize("/game/../../../secrets.txt"), Is.EqualTo("/secrets.txt"));
        }

        #endregion

        #region Null Byte Injection Tests (SECURITY CRITICAL)

        [Test]
        public void Normalize_RejectsNullByte_AtEnd()
        {
            ArgumentException? ex = Assert.Throws<ArgumentException>(() =>
                PathNormalizer.Normalize("/file.txt\0")
            );
            Assert.That(ex?.Message, Does.Contain("null byte"));
        }

        [Test]
        public void Normalize_RejectsNullByte_InMiddle()
        {
            ArgumentException? ex = Assert.Throws<ArgumentException>(() =>
                PathNormalizer.Normalize("/file\0.txt")
            );
            Assert.That(ex?.Message, Does.Contain("null byte"));
        }

        [Test]
        public void Normalize_RejectsNullByte_WithTraversal()
        {
            ArgumentException? ex = Assert.Throws<ArgumentException>(() =>
                PathNormalizer.Normalize("/data\0/../../../etc/passwd")
            );
            Assert.That(ex?.Message, Does.Contain("null byte"));
        }

        #endregion

        #region Backslash Conversion Tests

        [Test]
        public void Normalize_ConvertsBackslashesToForwardSlashes()
        {
            Assert.That(PathNormalizer.Normalize("\\a\\b\\c"), Is.EqualTo("/a/b/c"));
            Assert.That(PathNormalizer.Normalize("\\games\\c64\\file.prg"), Is.EqualTo("/games/c64/file.prg"));
        }

        [Test]
        public void Normalize_BackslashTraversal_Converted()
        {
            // Windows-style path traversal converted then normalized
            Assert.That(PathNormalizer.Normalize("/game\\..\\..\\..\\secrets.txt"), Is.EqualTo("/secrets.txt"));
        }

        [Test]
        public void Normalize_MixedSlashes()
        {
            Assert.That(PathNormalizer.Normalize("/a\\b/c"), Is.EqualTo("/a/b/c"));
            Assert.That(PathNormalizer.Normalize("\\a/b\\c"), Is.EqualTo("/a/b/c"));
        }

        #endregion

        #region Relative Reference Resolution Tests (RFC 3986 Section 5.2)

        [Test]
        public void ResolveRelative_SimpleFilename()
        {
            string result = PathNormalizer.ResolveRelative("/a/b/c/d", "g");
            Assert.That(result, Is.EqualTo("/a/b/c/g"));
        }

        [Test]
        public void ResolveRelative_CurrentDirectory()
        {
            string result = PathNormalizer.ResolveRelative("/a/b/c/d", "./g");
            Assert.That(result, Is.EqualTo("/a/b/c/g"));
        }

        [Test]
        public void ResolveRelative_ParentDirectory()
        {
            string result = PathNormalizer.ResolveRelative("/a/b/c/d", "../g");
            Assert.That(result, Is.EqualTo("/a/b/g"));
        }

        [Test]
        public void ResolveRelative_MultipleParents()
        {
            string result = PathNormalizer.ResolveRelative("/a/b/c/d", "../../g");
            Assert.That(result, Is.EqualTo("/a/g"));
        }

        [Test]
        public void ResolveRelative_AbsoluteReference()
        {
            // Absolute reference ignores base
            string result = PathNormalizer.ResolveRelative("/a/b/c/d", "/x/y/z");
            Assert.That(result, Is.EqualTo("/x/y/z"));
        }

        [Test]
        public void ResolveRelative_RFC3986_Examples()
        {
            // Examples from RFC 3986 Section 5.4
            string basePath = "/a/b/c/d";

            Assert.That(PathNormalizer.ResolveRelative(basePath, "g"), Is.EqualTo("/a/b/c/g"));
            Assert.That(PathNormalizer.ResolveRelative(basePath, "./g"), Is.EqualTo("/a/b/c/g"));
            // Note: Trailing slash in relative reference gets normalized away
            Assert.That(PathNormalizer.ResolveRelative(basePath, "g/"), Is.EqualTo("/a/b/c/g"));
            Assert.That(PathNormalizer.ResolveRelative(basePath, "/g"), Is.EqualTo("/g"));
            Assert.That(PathNormalizer.ResolveRelative(basePath, "../g"), Is.EqualTo("/a/b/g"));
            Assert.That(PathNormalizer.ResolveRelative(basePath, "../.."), Is.EqualTo("/a"));
        }

        #endregion

        #region Security Validation Tests

        [Test]
        public void IsSafePath_ValidPaths()
        {
            Assert.That(PathNormalizer.IsSafePath("/"), Is.True);
            Assert.That(PathNormalizer.IsSafePath("/a/b/c"), Is.True);
            Assert.That(PathNormalizer.IsSafePath("/games/c64/pitfall.prg"), Is.True);
        }

        [Test]
        public void IsSafePath_RejectsNonAbsolutePaths()
        {
            Assert.That(PathNormalizer.IsSafePath("relative/path"), Is.False);
            Assert.That(PathNormalizer.IsSafePath("file.txt"), Is.False);
        }

        [Test]
        public void IsSafePath_RejectsNullBytes()
        {
            Assert.That(PathNormalizer.IsSafePath("/file\0.txt"), Is.False);
            Assert.That(PathNormalizer.IsSafePath("/data\0"), Is.False);
        }

        [Test]
        public void IsSafePath_RejectsControlCharacters()
        {
            Assert.That(PathNormalizer.IsSafePath("/file\r\n.txt"), Is.False);
            Assert.That(PathNormalizer.IsSafePath("/data\x01\x02"), Is.False);
            Assert.That(PathNormalizer.IsSafePath("/file\t.txt"), Is.False); // Tab is control char
        }

        [Test]
        public void IsSafePath_RejectsSuspiciousPatterns()
        {
            // These patterns should not exist after normalization
            Assert.That(PathNormalizer.IsSafePath("/a/../b"), Is.False);     // Contains ..
            Assert.That(PathNormalizer.IsSafePath("/a/./b"), Is.False);      // Contains ./
            Assert.That(PathNormalizer.IsSafePath("/a//b"), Is.False);       // Contains //
        }

        [Test]
        public void IsSafePath_EmptyOrNull()
        {
            Assert.That(PathNormalizer.IsSafePath(""), Is.False);
            Assert.That(PathNormalizer.IsSafePath(null!), Is.False);
        }

        #endregion

        #region Path Manipulation Tests

        [Test]
        public void GetParentDirectory_StandardPaths()
        {
            Assert.That(PathNormalizer.GetParentDirectory("/a/b/c"), Is.EqualTo("/a/b"));
            Assert.That(PathNormalizer.GetParentDirectory("/a/b"), Is.EqualTo("/a"));
            Assert.That(PathNormalizer.GetParentDirectory("/a"), Is.EqualTo("/"));
        }

        [Test]
        public void GetParentDirectory_Root()
        {
            Assert.That(PathNormalizer.GetParentDirectory("/"), Is.EqualTo("/"));
        }

        [Test]
        public void GetParentDirectory_WithFilename()
        {
            Assert.That(PathNormalizer.GetParentDirectory("/a/b/c/file.txt"), Is.EqualTo("/a/b/c"));
            Assert.That(PathNormalizer.GetParentDirectory("/games/c64/pitfall.prg"), Is.EqualTo("/games/c64"));
        }

        [Test]
        public void GetFileName_StandardPaths()
        {
            Assert.That(PathNormalizer.GetFileName("/a/b/c/file.txt"), Is.EqualTo("file.txt"));
            Assert.That(PathNormalizer.GetFileName("/games/c64/pitfall.prg"), Is.EqualTo("pitfall.prg"));
        }

        [Test]
        public void GetFileName_RootAndDirectories()
        {
            Assert.That(PathNormalizer.GetFileName("/"), Is.EqualTo(""));
            // Note: PathNormalizer.Normalize removes trailing slashes, so "/a/b/c/" becomes "/a/b/c"
            // GetFileName then returns "c" as the filename
            // If you want directory behavior, check if path ends with / before normalizing
            Assert.That(PathNormalizer.GetFileName("/a/b/c/"), Is.EqualTo("c"));
        }

        [Test]
        public void GetFileName_SingleLevel()
        {
            Assert.That(PathNormalizer.GetFileName("/file.txt"), Is.EqualTo("file.txt"));
        }

        #endregion

        #region Edge Cases and Special Scenarios

        [Test]
        public void Normalize_EmptyPath()
        {
            Assert.That(PathNormalizer.Normalize(""), Is.EqualTo("/"));
            Assert.That(PathNormalizer.Normalize(null!), Is.EqualTo("/"));
        }

        [Test]
        public void Normalize_RootPath()
        {
            Assert.That(PathNormalizer.Normalize("/"), Is.EqualTo("/"));
        }

        [Test]
        public void Normalize_AddsLeadingSlash()
        {
            Assert.That(PathNormalizer.Normalize("a/b/c"), Is.EqualTo("/a/b/c"));
        }

        [Test]
        public void Normalize_MultipleSlashes()
        {
            Assert.That(PathNormalizer.Normalize("/a//b///c"), Is.EqualTo("/a/b/c"));
        }

        [Test]
        public void Normalize_OnlyDots()
        {
            Assert.That(PathNormalizer.Normalize("/."), Is.EqualTo("/"));
            Assert.That(PathNormalizer.Normalize("/./.."), Is.EqualTo("/"));
        }

        [Test]
        public void Normalize_PreservesFilename()
        {
            Assert.That(PathNormalizer.Normalize("/games/./c64/../c64/pitfall.prg"),
                Is.EqualTo("/games/c64/pitfall.prg"));
        }

        #endregion

        #region Real-World RetroFS Scenarios

        [Test]
        public void Normalize_D64_FilePaths()
        {
            // D64 images typically use simple paths
            Assert.That(PathNormalizer.Normalize("/CARMEN SANDIEGO"), Is.EqualTo("/CARMEN SANDIEGO"));
            Assert.That(PathNormalizer.Normalize("/./WHERE IN THE"), Is.EqualTo("/WHERE IN THE"));
        }

        [Test]
        public void Normalize_HFS_NestedDirectories()
        {
            // HFS (Mac) supports subdirectories
            Assert.That(PathNormalizer.Normalize("/System/./Finder"), Is.EqualTo("/System/Finder"));
            Assert.That(PathNormalizer.Normalize("/Applications/Utilities/../Games/Lemmings"),
                Is.EqualTo("/Applications/Games/Lemmings"));
        }

        [Test]
        public void Normalize_ProDOS_Paths()
        {
            // ProDOS (Apple II) directory structure
            Assert.That(PathNormalizer.Normalize("/BASIC.SYSTEM"), Is.EqualTo("/BASIC.SYSTEM"));
            Assert.That(PathNormalizer.Normalize("/GAMES/../UTILITIES/COPY.II"),
                Is.EqualTo("/UTILITIES/COPY.II"));
        }

        [Test]
        public void ResolveRelative_CrossDirectoryAccess()
        {
            // User navigating between directories
            // RFC 3986: If base doesn't end with /, remove last segment (treat as file)
            // To navigate from directory, use trailing slash in base
            string current = "/games/c64/file.prg";  // Treat c64 as directory by including a file
            string relative = "../amiga/lemmings.adf";
            string result = PathNormalizer.ResolveRelative(current, relative);
            Assert.That(result, Is.EqualTo("/games/amiga/lemmings.adf"));
        }

        #endregion

        #region Security Attack Scenarios

        [Test]
        public void SecurityTest_EncodedDotSegmentBypass()
        {
            // After percent-decoding, these become .. which must be normalized
            string decoded = "/../.."; // Already decoded from %2e%2e
            Assert.That(PathNormalizer.Normalize(decoded), Is.EqualTo("/"));
        }

        [Test]
        public void SecurityTest_ComplexTraversalAttempt()
        {
            string attack = "/game/./save/../../../../../../etc/passwd";
            string normalized = PathNormalizer.Normalize(attack);
            Assert.That(normalized, Is.EqualTo("/etc/passwd"));
            // Still can't escape volume root - this is /etc/passwd WITHIN the volume
        }

        [Test]
        public void SecurityTest_BackslashWindowsTraversal()
        {
            string attack = "/game\\..\\..\\..\\windows\\system32\\config";
            string normalized = PathNormalizer.Normalize(attack);
            Assert.That(normalized, Is.EqualTo("/windows/system32/config"));
            // Backslashes converted, then normalized (cannot escape root)
        }

        [Test]
        public void SecurityTest_NormalizedPathIsSafe()
        {
            string path = "/a/b/c/./../../g";
            string normalized = PathNormalizer.Normalize(path);
            Assert.That(PathNormalizer.IsSafePath(normalized), Is.True);
        }

        [Test]
        public void SecurityTest_IntegrationFlow()
        {
            // Simulate full security flow: decode → normalize → validate

            // 1. Input (already percent-decoded)
            string input = "/game/../../../etc/passwd";

            // 2. Normalize
            string normalized = PathNormalizer.Normalize(input);

            // 3. Validate
            Assert.That(PathNormalizer.IsSafePath(normalized), Is.True);

            // 4. Result cannot escape root
            Assert.That(normalized, Is.EqualTo("/etc/passwd"));
        }

        #endregion

        #region Performance Tests

        [Test]
        public void Performance_NormalizeSimplePath()
        {
            // Should be fast for paths without dot-segments
            string path = "/a/b/c/d/e/f/g/h/i/j/file.txt";
            string result = PathNormalizer.Normalize(path);
            Assert.That(result, Is.EqualTo(path));
        }

        [Test]
        public void Performance_NormalizeManyDotSegments()
        {
            // Should handle many dot-segments efficiently
            string path = "/a/./b/./c/./d/./e/./f/./g/./h";
            string result = PathNormalizer.Normalize(path);
            Assert.That(result, Is.EqualTo("/a/b/c/d/e/f/g/h"));
        }

        [Test]
        public void Performance_ResolveRelativeDeep()
        {
            string basePath = "/a/b/c/d/e/f/g/h/i/j";
            string reference = "../../../../../../../../../x";
            string result = PathNormalizer.ResolveRelative(basePath, reference);
            Assert.That(result, Is.EqualTo("/x")); // Cannot go above root
        }

        #endregion
    }
}
