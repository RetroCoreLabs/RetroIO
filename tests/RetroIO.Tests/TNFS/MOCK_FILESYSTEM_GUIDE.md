# MockFileSystemBackend Usage Guide

## Overview

`MockFileSystemBackend` is an in-memory file system implementation used for unit testing TNFS server functionality without requiring actual disk access. This allows for fast, isolated, and repeatable tests.

## Why Use MockFileSystemBackend?

**Benefits:**
- ✅ **Fast**: No disk I/O overhead
- ✅ **Isolated**: Tests don't interfere with real filesystem
- ✅ **Repeatable**: Consistent test environment every time
- ✅ **Safe**: No risk of corrupting or deleting real files

**When to Use:**
- Unit tests that need filesystem operations
- Tests using `InMemoryTransport` (no network overhead)
- Integration tests that don't require external processes

## Basic Setup

### 1. Create Mock Filesystem

```csharp
using RetroIO.Server.Core.FileSystem;

// Create instance
var mockFS = new MockFileSystemBackend();
```

### 2. Add Directories

```csharp
// Add directories to the mock filesystem
mockFS.AddDirectory("/tmp/tnfs");
mockFS.AddDirectory("/tmp/tnfs/testdir");
mockFS.AddDirectory("/tmp/tnfs/subdir/nested");  // Creates parent dirs automatically
```

### 3. Add Files

```csharp
using System.Text;

// Add files with content
mockFS.AddFile("/tmp/tnfs/file1.txt", Encoding.ASCII.GetBytes("Hello World"));
mockFS.AddFile("/tmp/tnfs/file2.txt", Encoding.ASCII.GetBytes("Test content"));
mockFS.AddFile("/tmp/tnfs/testdir/nested.txt", Encoding.ASCII.GetBytes("Nested file"));

// Add binary files
byte[] binaryData = new byte[] { 0x01, 0x02, 0x03, 0x04 };
mockFS.AddFile("/tmp/tnfs/data.bin", binaryData);
```

### 4. Register with Dependency Injection

```csharp
services.AddSingleton<IFileSystemBackend>(mockFS);
```

## Complete Example: InMemory Transport Test

```csharp
using NUnit.Framework;
using RetroIO.Protocols.Tnfs.Server.Transport;
using RetroIO.Server.Core.FileSystem;
using System.Text;

[TestFixture]
public class MyTnfsTests
{
    private InMemoryTransport _transport = null!;
    private IHost _host = null!;
    private MockFileSystemBackend _mockFS = null!;

    [SetUp]
    public async Task Setup()
    {
        // 1. Create mock file system
        _mockFS = new MockFileSystemBackend();

        // 2. Populate with test data
        mockFS.AddDirectory("/tmp/tnfs");
        mockFS.AddDirectory("/tmp/tnfs/documents");
        mockFS.AddDirectory("/tmp/tnfs/images");

        mockFS.AddFile("/tmp/tnfs/readme.txt", Encoding.ASCII.GetBytes("Welcome!"));
        mockFS.AddFile("/tmp/tnfs/documents/doc1.txt", Encoding.ASCII.GetBytes("Document 1"));
        mockFS.AddFile("/tmp/tnfs/images/pic1.png", new byte[] { 0x89, 0x50, 0x4E, 0x47 });

        // 3. Create in-memory transport
        _transport = new InMemoryTransport();

        // 4. Build server with mock filesystem
        _host = Host.CreateDefaultBuilder()
            .ConfigureServices((context, services) =>
            {
                // Register mock filesystem
                services.AddSingleton<IFileSystemBackend>(_mockFS);

                // Register session manager
                services.AddSingleton<SessionManager>(sp =>
                    new SessionManager(
                        NullLogger<SessionManager>.Instance,
                        TimeSpan.FromMinutes(10),
                        maxSessionsPerIP: 8));

                // Register command handlers
                services.AddSingleton<SessionCommandHandler>(sp =>
                    new SessionCommandHandler(
                        sp.GetRequiredService<SessionManager>(),
                        NullLogger<SessionCommandHandler>.Instance,
                        "/tmp/tnfs",
                        sp.GetRequiredService<IFileSystemBackend>()));

                services.AddSingleton<DirectoryCommandHandler>(sp =>
                    new DirectoryCommandHandler(
                        sp.GetRequiredService<SessionManager>(),
                        NullLogger<DirectoryCommandHandler>.Instance,
                        sp.GetRequiredService<IFileSystemBackend>()));

                services.AddSingleton<FileCommandHandler>(sp =>
                    new FileCommandHandler(
                        sp.GetRequiredService<SessionManager>(),
                        NullLogger<FileCommandHandler>.Instance,
                        sp.GetRequiredService<IFileSystemBackend>()));

                // Register transport
                services.AddSingleton<ITransport>(_transport);
                services.AddHostedService<TnfsServerService>();
            })
            .Build();

        await _host.StartAsync();
    }

    [TearDown]
    public async Task TearDown()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    [Test]
    public async Task Test_ListDirectory()
    {
        // Test can now access mock filesystem through TNFS commands
        var mountReq = CreateMountRequest("/");
        await _transport.SendTestMessageAsync(mountReq);
        var mountResp = await _transport.ReceiveResponseAsync(TimeSpan.FromSeconds(5));

        Assert.That((TnfsReturnCode)mountResp.Header.Status, Is.EqualTo(TnfsReturnCode.SUCCESS));
        // ... rest of test
    }
}
```

## MockFileSystemBackend API Reference

### Directory Operations

```csharp
// Add directory
mockFS.AddDirectory("/path/to/dir");

// Check if directory exists
bool exists = mockFS.DirectoryExists("/path/to/dir");

// List directory contents
var entries = mockFS.ListDirectory("/path/to/dir");
```

### File Operations

```csharp
// Add file
mockFS.AddFile("/path/to/file.txt", Encoding.ASCII.GetBytes("content"));

// Check if file exists
bool exists = mockFS.FileExists("/path/to/file.txt");

// Read file
byte[] content = mockFS.ReadFile("/path/to/file.txt");

// Get file size
long size = mockFS.GetFileSize("/path/to/file.txt");

// Delete file
mockFS.DeleteFile("/path/to/file.txt");
```

### Path Operations

```csharp
// Combine paths
string fullPath = mockFS.CombinePath("/base", "relative/path");

// Get full path (normalized)
string normalized = mockFS.GetFullPath("/some/../path");
```

## Common Test Patterns

### Pattern 1: Directory Listing Test

```csharp
// Setup
mockFS.AddDirectory("/tmp/tnfs/test");
mockFS.AddFile("/tmp/tnfs/test/file1.txt", Encoding.ASCII.GetBytes("A"));
mockFS.AddFile("/tmp/tnfs/test/file2.txt", Encoding.ASCII.GetBytes("B"));
mockFS.AddFile("/tmp/tnfs/test/file3.txt", Encoding.ASCII.GetBytes("C"));

// Test OPENDIR/READDIR commands
// Verify all 3 files are returned
```

### Pattern 2: Nested Directory Structure

```csharp
// Setup hierarchical structure
mockFS.AddDirectory("/tmp/tnfs");
mockFS.AddDirectory("/tmp/tnfs/level1");
mockFS.AddDirectory("/tmp/tnfs/level1/level2");
mockFS.AddFile("/tmp/tnfs/level1/level2/deep.txt", Encoding.ASCII.GetBytes("Deep"));

// Test path traversal
```

### Pattern 3: Large Files

```csharp
// Create large file
byte[] largeContent = new byte[10 * 1024 * 1024]; // 10MB
new Random().NextBytes(largeContent);
mockFS.AddFile("/tmp/tnfs/large.bin", largeContent);

// Test chunked reading (LSEEK + READ)
```

### Pattern 4: Unicode/Special Characters

```csharp
// Test special filenames
mockFS.AddFile("/tmp/tnfs/café.txt", Encoding.UTF8.GetBytes("Content"));
mockFS.AddFile("/tmp/tnfs/测试.txt", Encoding.UTF8.GetBytes("Chinese"));
mockFS.AddDirectory("/tmp/tnfs/Ñoño");

// Verify TNFS handles encoding correctly
```

## Important Notes

### ⚠️ Path Normalization

The mock filesystem normalizes all paths to use forward slashes (`/`) and removes duplicate slashes:

```csharp
// These are equivalent:
mockFS.AddDirectory("/tmp/tnfs");
mockFS.AddDirectory("/tmp//tnfs");
mockFS.AddDirectory("\\tmp\\tnfs");  // Converted to /tmp/tnfs
```

### ⚠️ Root Directory

When configuring `SessionCommandHandler`, ensure the root directory exists in the mock:

```csharp
// If SessionCommandHandler root is "/tmp/tnfs"
mockFS.AddDirectory("/tmp/tnfs");  // MUST exist!

// When client mounts "/"
// Server checks: /tmp/tnfs + "/" = /tmp/tnfs (must exist)

// When client mounts "/subdir"
// Server checks: /tmp/tnfs + "/subdir" = /tmp/tnfs/subdir (must exist)
```

### ⚠️ Case Sensitivity

Mock filesystem is **case-sensitive**:

```csharp
mockFS.AddFile("/tmp/File.txt", data);
mockFS.FileExists("/tmp/file.txt");  // Returns FALSE
mockFS.FileExists("/tmp/File.txt");  // Returns TRUE
```

## Comparison: Mock vs Real Filesystem

| Feature | MockFileSystemBackend | LocalFileSystemBackend |
|---------|----------------------|------------------------|
| Speed | ⚡ Very Fast (in-memory) | 🐌 Slower (disk I/O) |
| Isolation | ✅ Isolated | ❌ Affects real disk |
| Setup | ✅ Programmatic | ❌ Requires mkdir/touch |
| Cleanup | ✅ Auto (GC) | ❌ Manual deletion |
| Parallelization | ✅ Perfect | ⚠️ May conflict |
| Real I/O bugs | ❌ Won't catch | ✅ Catches real issues |

## When to Use Real Filesystem

Use `LocalFileSystemBackend` with actual files when:

1. **Integration Testing**: Testing with external TNFS clients
2. **Performance Testing**: Measuring actual disk I/O performance
3. **Compatibility Testing**: Verifying behavior with real OS filesystem
4. **Edge Cases**: Testing filesystem-specific behavior (permissions, symlinks, etc.)

Example:
```csharp
// Real filesystem backend
services.AddSingleton<IFileSystemBackend, LocalFileSystemBackend>();

// Requires setup:
Directory.CreateDirectory("/tmp/tnfs");
File.WriteAllText("/tmp/tnfs/test.txt", "Real file");
```

## Best Practices

1. ✅ **Always create root directory**: Ensure SessionCommandHandler root exists
2. ✅ **Use descriptive paths**: `/tmp/tnfs/test-case-name/` helps debugging
3. ✅ **Cleanup in TearDown**: Not strictly needed (GC handles it) but good practice
4. ✅ **Test both empty and populated**: Test with 0 files and many files
5. ✅ **Test path edge cases**: Root `/`, deep nesting, special chars
6. ✅ **Verify error conditions**: Non-existent paths, invalid handles, etc.

## Troubleshooting

### Problem: MOUNT returns ENOENT

**Cause**: Root directory doesn't exist in mock filesystem

**Solution**:
```csharp
// If SessionCommandHandler root is "/tmp/tnfs"
mockFS.AddDirectory("/tmp/tnfs");  // Add this!
```

### Problem: OPENDIR returns ENOENT

**Cause**: Directory not added to mock, or path mismatch

**Solution**:
```csharp
// Verify exact path
mockFS.AddDirectory("/tmp/tnfs/testdir");  // Exact path
// NOT: mockFS.AddDirectory("testdir");     // Won't work
```

### Problem: Test passes locally, fails in CI

**Cause**: Using real filesystem instead of mock

**Solution**:
```csharp
// Ensure using mock
services.AddSingleton<IFileSystemBackend>(mockFS);  // Not LocalFileSystemBackend!
```

## See Also

- `TnfsInMemoryTransportTests.cs` - Complete working example
- `MockFileSystemBackend.cs` - Implementation details
- `IFileSystemBackend.cs` - Interface definition
- `LocalFileSystemBackend.cs` - Real filesystem implementation
