//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen 
// HackerCorp Labs — https://github.com/HackerCorpLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using System;
using System.IO;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using RetroIO.Server.Core.FileSystem;

namespace RetroIO.Tests.TNFS;

/// <summary>
/// Debug test to identify the exact file read failure.
/// </summary>
[TestFixture]
[Category("Debug")]
public class TnfsFileReadDebugTest
{
    [Test]
    public void Debug_LocalFileSystemBackend_OpenAndRead()
    {
        // Create test file
        string testRoot = Path.Combine(Path.GetTempPath(), "tnfs_debug_test");
        Directory.CreateDirectory(testRoot);
        string testFile = Path.Combine(testRoot, "file1.txt");
        File.WriteAllText(testFile, "File 1 Content");

        TestContext.Out.WriteLine($"Created test file: {testFile}");
        TestContext.Out.WriteLine($"File exists: {File.Exists(testFile)}");

        try
        {
            // Test LocalFileSystemBackend
            var backend = new LocalFileSystemBackend();

            // Test file exists
            bool exists = backend.FileExists(testFile);
            TestContext.Out.WriteLine($"Backend.FileExists: {exists}");

            // Test open file
            FileStream fs = backend.OpenFile(testFile, FileMode.Open, FileAccess.Read);
            TestContext.Out.WriteLine($"File opened successfully");
            TestContext.Out.WriteLine($"FileStream.CanRead: {fs.CanRead}");
            TestContext.Out.WriteLine($"FileStream.Length: {fs.Length}");
            TestContext.Out.WriteLine($"FileStream.Position: {fs.Position}");
            TestContext.Out.WriteLine($"FileStream.Name: {fs.Name}");

            // Test read
            byte[] buffer = new byte[1024];
            int bytesRead = fs.Read(buffer, 0, buffer.Length);
            TestContext.Out.WriteLine($"Bytes read: {bytesRead}");

            string content = System.Text.Encoding.ASCII.GetString(buffer, 0, bytesRead);
            TestContext.Out.WriteLine($"Content: {content}");

            Assert.That(content, Is.EqualTo("File 1 Content"));

            fs.Dispose();
            TestContext.Out.WriteLine("✅ Direct file read successful");
        }
        finally
        {
            if (Directory.Exists(testRoot))
            {
                Directory.Delete(testRoot, recursive: true);
            }
        }
    }
}
