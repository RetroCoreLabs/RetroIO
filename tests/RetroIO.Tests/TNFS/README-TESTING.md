# TNFS Test Configuration Guide

All TNFS tests now use a centralized configuration in `TnfsTestConfig.cs`. This ensures consistent server addresses and ports across all test files.

## Test Categories

### 1. **Manual Tests** (External C TNFSD Server)

Tests that require a **manually started C TNFS server** running on a remote machine:

- **Server Address**: `TnfsTestConfig.REMOTE_SERVER_ADDRESS` = `192.168.1.180`
- **Server Port**: `TnfsTestConfig.REMOTE_SERVER_PORT` = `16384`
- **Root Directory**: `TnfsTestConfig.REMOTE_SERVER_ROOT` = `/tmp/tnfs`

**Test Files**:
- `TnfsClientIntegrationTests.cs` - High-level client API tests
- `TnfsDirectoryTests.cs` - Directory operations tests
- `TnfsServerManualTests.cs` - Manual server validation tests
- `TnfsT cpProtocolTests.cs` - TCP transport tests

**Running Manual Tests**:
```bash
# On remote server (192.168.1.180), ensure tnfsd is running:
tnfsd /tmp/tnfs --port 16384 --tcp

# Ensure test files exist on the remote server:
/tmp/tnfs/test/file1.txt
/tmp/tnfs/test/file2.txt
/tmp/tnfs/test/subdir/nested.txt
/tmp/tnfs/writable/ (writable directory)

# Run manual tests:
dotnet test --filter "Category=Manual"
```

### 2. **Automated Tests** (Self-Hosted .NET TNFS Server)

Tests that start their own .NET TNFS server automatically:

- **UDP Port**: `TnfsTestConfig.AUTOMATED_TEST_PORT_UDP` = `16666`
- **TCP Port**: `TnfsTestConfig.AUTOMATED_TEST_PORT_TCP` = `16667`
- **Root Directory**: `TnfsTestConfig.AUTOMATED_TEST_ROOT` = `/tmp/tnfs-integration-test`

**Test Files**:
- `TnfsIntegrationTests.cs` - UDP integration tests
- `TnfsTcpIntegrationTests.cs` - TCP integration tests
- `TnfsConcurrentTransportTests.cs` - UDP+TCP concurrent tests

**Running Automated Tests**:
```bash
# No manual setup required - tests create their own server and test files
dotnet test --filter "Category=Integration"
```

### 3. **Unit Tests** (In-Memory/Mock)

Tests that use in-memory transport or mock file systems (no network):

- `TnfsInMemoryTransportTests.cs` - In-memory transport tests
- `TnfsDirectoryMockTests.cs` - Mock file system tests

**Running Unit Tests**:
```bash
dotnet test --filter "Category=Unit"
```

## Changing Configuration

To change the remote server address or ports, edit `TnfsTestConfig.cs`:

```csharp
public static class TnfsTestConfig
{
    // Remote C TNFS server
    public const string REMOTE_SERVER_ADDRESS = "192.168.1.180"; // ← Change this
    public const int REMOTE_SERVER_PORT = 16384;

    // Automated test ports (avoid conflicts)
    public const int AUTOMATED_TEST_PORT_UDP = 16666;
    public const int AUTOMATED_TEST_PORT_TCP = 16667;
}
```

## Quick Test Commands

```bash
# Run all unit tests (fast, no network)
dotnet test --filter "Category=Unit"

# Run all automated integration tests (self-hosted servers)
dotnet test --filter "Category=Integration"

# Run all manual tests (requires external C server)
dotnet test --filter "Category=Manual"

# Run specific test class
dotnet test --filter "ClassName=TnfsInMemoryTransportTests"

# Run tests by category and type
dotnet test --filter "Category=Unit&Category=Mock"
```

## Port Usage Summary

| Port  | Purpose                                    | Test Type  |
|-------|--------------------------------------------|------------|
| 16384 | Remote C TNFSD server                      | Manual     |
| 16666 | Automated UDP integration tests            | Automated  |
| 16667 | Automated TCP integration tests            | Automated  |

## Test File Locations

### Remote Server (`192.168.1.180:/tmp/tnfs/`)
Manual tests expect this structure on the **remote machine**:
```
/tmp/tnfs/
├── test/
│   ├── file1.txt
│   ├── file2.txt
│   └── subdir/
│       └── nested.txt
└── writable/
```

### Automated Tests
These directories are created/destroyed automatically:
- `/tmp/tnfs-integration-test/` - Integration tests
- `/tmp/tnfs_tcp_test/` - TCP integration tests
- `/tmp/tnfs_concurrent_test/` - Concurrent transport tests
