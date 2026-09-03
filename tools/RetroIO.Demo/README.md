# RetroFS Demo Application

Interactive console application demonstrating RetroFS capabilities.

## Features

- **Mount D64 disk images** - Automatic format detection
- **Directory listing** - View all files with metadata (type, size, blocks)
- **File reading** - Extract files with hex dump display
- **Block access** - Read individual sectors (Track/Sector addressing)

## Usage

```bash
# Run with D64 file path
dotnet run --project demo/RetroIO.Demo.csproj path/to/disk.d64

# Or run interactively
dotnet run --project demo/RetroIO.Demo.csproj
```

## Example Session

```
╔═══════════════════════════════════════════════════════════════════════╗
║                           RETROFS DEMO                                ║
║              Accessing yesterday's data with today's standards       ║
╚═══════════════════════════════════════════════════════════════════════╝

Mounting: games.d64
  Type: D64
  Label: COMMODORE GAMES
  Block Access: Yes

╔═════════════════════════════════════════════════════════════════╗
║                        DIRECTORY LISTING                        ║
╠═════════════════════════════════════════════════════════════════╣
║  NAME                 TYPE    SIZE     BLOCKS                   ║
╠═════════════════════════════════════════════════════════════════╣
║  PITFALL             PRG      8382 B    34                      ║
║  ADVENTURE           PRG     12700 B    51                      ║
║  README              SEQ       508 B     3                      ║
╚═════════════════════════════════════════════════════════════════╝

Options:
  1. Read a file
  2. Read a block/sector
  3. Exit

Choose option:
```

## Supported Operations

### 1. Read File
- Enter filename from directory listing
- Displays file size and hex dump (first 256 bytes)
- Shows both hex values and ASCII representation

### 2. Read Block/Sector
- Enter track number (1-35)
- Enter sector number (0-20)
- Displays raw 256-byte sector data in hex dump format

## Requirements

- .NET 9.0 or later
- Valid D64 disk image file

## D64 Format Support

- ✅ 35-track (174,848 bytes)
- ✅ 40-track (196,608 bytes)
- ✅ 42-track (205,312 bytes)
- ✅ Images with error tables
- ✅ PETSCII to ASCII conversion
- ✅ All file types (PRG, SEQ, USR, REL, DEL)

## Architecture Demonstration

This demo showcases:
- **IProtocolAdapter** - LocalFsAdapter for disk images
- **IFileSystemDriver** - D64Driver with automatic detection
- **URN Context** - Volume-based addressing
- **Block Operations** - Zero-copy Span<byte> I/O
- **Property Bag Metadata** - Flexible filesystem-specific properties

## Next Steps

Try these operations:
1. Mount your own D64 images
2. List directory contents
3. Extract files for analysis
4. Read raw sectors for low-level inspection
5. Explore the block chaining mechanism (Track 18 for directory)
