# D64 Driver Implementation TODO

## Current Status

### ✅ Implemented (Read-Only)
- [x] Mount D64 disk images (35/40/42 track variants)
- [x] Read file data following track/sector chains
- [x] List directory contents
- [x] Get file metadata (size, type, blocks, locked status)
- [x] Read blocks (track/sector addressing)
- [x] Get block device info
- [x] Parse disk header and volume label
- [x] PETSCII to ASCII conversion
- [x] Support for all file types (PRG, SEQ, USR, REL, DEL)
- [x] URN identifier generation
- [x] Numeric file indexing (#1, #2, #3)

### ❌ Not Implemented (Write Operations)

#### BAM (Block Availability Map) Management
- [ ] Parse BAM structure from Track 18, Sector 0
- [ ] Read BAM to determine free/used blocks
- [ ] Find next available free block
- [ ] Allocate block (mark as used in BAM)
- [ ] Free block (mark as available in BAM)
- [ ] Write BAM back to disk
- [ ] Get free block count per track
- [ ] Validate BAM integrity
- [ ] Handle track 18 special case (directory track)

**Priority:** HIGH - Required for all write operations

**Test Requirements:**
```csharp
[Test] BAM_ParseFromDisk()
[Test] BAM_FindFreeBlock()
[Test] BAM_AllocateBlock()
[Test] BAM_FreeBlock()
[Test] BAM_WriteToD

isk()
[Test] BAM_GetFreeBlockCount()
[Test] BAM_Track18_NeverAllocated()  // Directory track protection
```

#### Directory Entry Management
- [ ] Find free directory entry slot
- [ ] Add new directory entry
- [ ] Update existing directory entry
- [ ] Delete directory entry (mark type = 0)
- [ ] Chain new directory sector when full
- [ ] Validate filename constraints
- [ ] Handle duplicate filenames
- [ ] Support all file type flags
- [ ] Update file locked status

**Priority:** HIGH - Required for file creation/deletion

**Test Requirements:**
```csharp
[Test] Directory_FindFreeSlot()
[Test] Directory_AddEntry()
[Test] Directory_UpdateEntry()
[Test] Directory_DeleteEntry()
[Test] Directory_AllocateNewSector()
[Test] Directory_ValidateFilename()
[Test] Directory_HandleDuplicates()
```

#### File Writing
- [ ] Allocate sectors for new file
- [ ] Build track/sector chain
- [ ] Write file data to sectors
- [ ] Handle last sector (partial data)
- [ ] Update directory with start track/sector
- [ ] Update directory with block count
- [ ] Support sequential write
- [ ] Handle disk full condition
- [ ] Preserve file type (PRG, SEQ, etc.)
- [ ] Support write to locked file (error)

**Priority:** HIGH - Core write functionality

**Test Requirements:**
```csharp
[Test] WriteFile_SmallFile_SingleSector()
[Test] WriteFile_LargeFile_MultiSector()
[Test] WriteFile_ExactBlock_254Bytes()
[Test] WriteFile_MaxSize()
[Test] WriteFile_DiskFull_ThrowsException()
[Test] WriteFile_PreserveFileType()
[Test] WriteFile_CorrectBlockCount()
```

#### File Overwriting
- [ ] Free old file sectors (update BAM)
- [ ] Allocate new sectors
- [ ] Reuse directory entry
- [ ] Handle size increase (more blocks)
- [ ] Handle size decrease (fewer blocks)
- [ ] Maintain filename and type
- [ ] Update block count
- [ ] Handle locked file (error)

**Priority:** MEDIUM - Extends write functionality

**Test Requirements:**
```csharp
[Test] WriteFile_Overwrite_SameSize()
[Test] WriteFile_Overwrite_Larger()
[Test] WriteFile_Overwrite_Smaller()
[Test] WriteFile_Overwrite_LockedFile_Throws()
```

#### File Deletion
- [ ] Find directory entry
- [ ] Mark entry as deleted (type = 0)
- [ ] Free all file sectors in BAM
- [ ] Handle locked file (error)
- [ ] Update free block counts
- [ ] Preserve other files
- [ ] Handle non-existent file

**Priority:** MEDIUM - Completes basic file operations

**Test Requirements:**
```csharp
[Test] DeleteFile_Standard()
[Test] DeleteFile_LockedFile_Throws()
[Test] DeleteFile_NonExistent_Throws()
[Test] DeleteFile_FreesBlocks()
[Test] DeleteFile_PreservesOtherFiles()
```

#### Block-Level Write
- [ ] Write block to track/sector
- [ ] Validate track/sector address
- [ ] Handle read-only mount (error)
- [ ] Update BAM sector
- [ ] Update directory sector
- [ ] Support partial block write

**Priority:** LOW - Advanced feature

**Test Requirements:**
```csharp
[Test] WriteBlock_Standard()
[Test] WriteBlock_BAMSector()
[Test] WriteBlock_DirectorySector()
[Test] WriteBlock_InvalidAddress_Throws()
[Test] WriteBlock_ReadOnly_Throws()
```

#### File Renaming
- [ ] Find directory entry
- [ ] Update filename
- [ ] Preserve all other metadata
- [ ] Validate new filename
- [ ] Handle locked file (error)
- [ ] Check for duplicate names

**Priority:** LOW - Nice to have

**Test Requirements:**
```csharp
[Test] RenameFile_Standard()
[Test] RenameFile_LockedFile_Throws()
[Test] RenameFile_Duplicate_Throws()
[Test] RenameFile_PreservesMetadata()
```

#### File Locking
- [ ] Set file lock flag (bit 6 in type byte)
- [ ] Clear file lock flag
- [ ] Enforce lock on write operations
- [ ] Enforce lock on delete operations

**Priority:** LOW - Adds protection

**Test Requirements:**
```csharp
[Test] LockFile()
[Test] UnlockFile()
[Test] LockedFile_CannotWrite()
[Test] LockedFile_CannotDelete()
```

#### Format/Initialize
- [ ] Create empty D64 image (35/40/42 tracks)
- [ ] Initialize BAM
- [ ] Create directory structure
- [ ] Write disk header
- [ ] Set volume label
- [ ] Set disk ID

**Priority:** LOW - Advanced feature

**Test Requirements:**
```csharp
[Test] Format_35Track()
[Test] Format_40Track()
[Test] Format_42Track()
[Test] Format_SetVolumeLabel()
[Test] Format_CreateValidBAM()
```

### ❌ Not Supported (By Design)

#### Features Not in D64 Format
- [ ] ~~Subdirectories~~ (D64 has no subdirectory support)
- [ ] ~~Timestamps~~ (D64 has no timestamp support)
- [ ] ~~Permissions~~ (Only lock flag supported)
- [ ] ~~Extended attributes~~ (Not in D64 format)
- [ ] ~~Long filenames~~ (16 character limit)
- [ ] ~~Unicode filenames~~ (PETSCII only)
- [ ] ~~Symbolic links~~ (Not supported)
- [ ] ~~Hard links~~ (Not supported)
- [ ] ~~Compression~~ (Not supported)
- [ ] ~~Encryption~~ (Not supported)

## Implementation Order (Recommended)

### Phase 1: Core Write Support (MVP)
**Goal:** Create new files and read them back

1. [ ] Implement BAM parsing and management
2. [ ] Implement directory entry creation
3. [ ] Implement basic file writing (new files only)
4. [ ] Add integration tests (write → read verification)
5. [ ] Update `CanWrite` to return `!_readOnly`
6. [ ] Update `Capabilities` to include write flags

**Estimated Effort:** 4-6 hours
**Test Count:** ~20 tests

### Phase 2: File Management
**Goal:** Full file lifecycle management

1. [ ] Implement file deletion
2. [ ] Implement file overwriting
3. [ ] Implement disk full handling
4. [ ] Add comprehensive error handling
5. [ ] Add integration tests

**Estimated Effort:** 3-4 hours
**Test Count:** ~15 tests

### Phase 3: Advanced Features
**Goal:** Complete D64 write support

1. [ ] Implement file renaming
2. [ ] Implement file locking
3. [ ] Implement block-level write
4. [ ] Implement format/initialize
5. [ ] Add performance tests

**Estimated Effort:** 3-4 hours
**Test Count:** ~10 tests

## Capabilities Flags (Current vs. Target)

### Current Capabilities
```csharp
DriverCapabilities.Read |
DriverCapabilities.ListDirectory |
DriverCapabilities.FileTypes |
DriverCapabilities.PetsciiEncoding |
DriverCapabilities.FileLocking |  // Read-only (can detect locked files)
DriverCapabilities.VolumeLabel |
DriverCapabilities.BlockRead |
DriverCapabilities.BlockAllocationMap  // Read-only
```

### Target Capabilities (After Full Implementation)
```csharp
DriverCapabilities.Read |
DriverCapabilities.Write |          // NEW
DriverCapabilities.Delete |         // NEW
DriverCapabilities.Rename |         // NEW (Phase 3)
DriverCapabilities.ListDirectory |
DriverCapabilities.FileTypes |
DriverCapabilities.PetsciiEncoding |
DriverCapabilities.FileLocking |    // Read/Write
DriverCapabilities.VolumeLabel |
DriverCapabilities.BlockRead |
DriverCapabilities.BlockWrite |     // NEW (Phase 3)
DriverCapabilities.Format |         // NEW (Phase 3)
DriverCapabilities.BlockAllocationMap  // Read/Write
```

## Code Changes Required

### 1. Update CanWrite Property
```csharp
// Current
public bool CanWrite => false;

// Target
public bool CanWrite => !_readOnly;
```

### 2. Update Capabilities Property
```csharp
// Uncomment write capabilities when implemented
if (!_readOnly)
{
    caps |= DriverCapabilities.Write |
            DriverCapabilities.Delete |
            DriverCapabilities.Rename |
            DriverCapabilities.BlockWrite |
            DriverCapabilities.Format;
}
```

### 3. Implement Write Methods
```csharp
public void WriteFile(string path, byte[] data)
{
    // Remove: throw new NotImplementedException(...);
    // Add: Full implementation
}

public void DeleteFile(string path)
{
    // Remove: throw new NotImplementedException(...);
    // Add: Full implementation
}

public void WriteBlock(BlockAddress address, ReadOnlySpan<byte> data)
{
    // Remove: throw new NotImplementedException(...);
    // Add: Full implementation
}
```

### 4. Add New Methods (Internal)
```csharp
private BAM ParseBAM()
private void UpdateBAM(BAM bam)
private BlockAddress FindFreeBlock()
private void AllocateBlock(BlockAddress address)
private void FreeBlock(BlockAddress address)
private DirectoryEntry? FindFreeDirectorySlot()
private void AddDirectoryEntry(DirectoryEntry entry)
private void UpdateDirectoryEntry(DirectoryEntry entry)
private void DeleteDirectoryEntry(string fileName)
```

## Testing Strategy

### Unit Tests
- Test individual methods in isolation
- Mock dependencies (BAM, directory)
- Fast execution (< 1 second per test)
- Location: `tests/RetroIO.Tests/D64DriverWriteTests.cs`

### Integration Tests
- Test full workflows with real disk images
- Verify data persistence
- May be slower (1-5 seconds per test)
- Location: `tests/RetroIO.Tests/D64DriverIntegrationTests.cs`

### Test Disk Images Needed
1. Empty formatted disk
2. Partially full disk (50%)
3. Nearly full disk (95%)
4. Disk with locked files
5. Disk with all file types

## Documentation Updates Required

### Files to Update
- [ ] `docs/D64-Write-Testing-Plan.md` - Update with implementation status
- [ ] `docs/TEST-RESULTS.md` - Add write operation test results
- [ ] `README.md` - Update feature list
- [ ] `QUICKSTART.md` - Add write examples

### API Documentation
- [ ] Update XML comments for implemented methods
- [ ] Add code examples for write operations
- [ ] Document error conditions and exceptions

## Performance Targets

- **Write small file** (< 1KB): < 10ms
- **Write large file** (> 100KB): < 100ms
- **Delete file**: < 5ms
- **List directory** (after writes): < 20ms
- **Format disk**: < 50ms

## Compatibility Testing

### Verification Tools
- [ ] VICE emulator (load written files)
- [ ] CCS64 emulator
- [ ] dirmaster (directory editor)
- [ ] c1541 command-line tool

### Test Cases
- [ ] Write file, load in VICE
- [ ] Create multiple files, verify in dirmaster
- [ ] Delete files, verify BAM updates
- [ ] Fill disk, verify no corruption

## Known Limitations

1. **No subdirectories** - D64 format limitation
2. **16-character filenames** - PETSCII limitation
3. **No timestamps** - D64 format limitation
4. **Fixed disk sizes** - 35/40/42 tracks only
5. **No compression** - D64 format limitation

## References

- **D64 Specification:** https://ist.uwaterloo.ca/~schepers/formats/D64.TXT
- **VICE Source:** https://sourceforge.net/projects/vice-emu/
- **Commodore 1541 Manual:** `Doc/Machine/C64/`

## Progress Tracking

**Last Updated:** 2025-10-01

**Overall Progress:**
- Phase 1 (Core Write): 0% (NOT STARTED)
- Phase 2 (File Management): 0% (NOT STARTED)
- Phase 3 (Advanced): 0% (NOT STARTED)

**Next Action:** Begin Phase 1 - Implement BAM management
