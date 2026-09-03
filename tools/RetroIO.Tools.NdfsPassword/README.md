# NDFS Password Management Tool

Command-line tool for managing NDFS user passwords.

## Features

- ✅ List all users with password status
- ✅ Clear user passwords programmatically
- ✅ Generate FILE-SYSTEM-INVESTIGATOR instructions
- ✅ Shows old password values (decimal/octal/hex)
- ✅ Access by username or user index

## Installation

```powershell
cd tools\NdfsPasswordTool
dotnet build -c Release
```

Binary will be in `bin\Release\net9.0\ndfs-password.exe`

## Usage

### List Users

```bash
ndfs-password list disk.img
```

Output:
```
Found 3 user(s) in disk.img:

┌──────┬──────────────────┬──────────┬──────────┬────────────┐
│ Index│ Username         │ Reserved │ Used     │ Password   │
├──────┼──────────────────┼──────────┼──────────┼────────────┤
│    0 │ SYSTEM           │     1000 │      500 │ 0x14E5     │
│    1 │ ADMIN            │     2000 │     1200 │ CLEARED    │
│    2 │ USER1            │      500 │      100 │ 0x0042     │
└──────┴──────────────────┴──────────┴──────────┴────────────┘
```

### Clear Password

Clear by username:
```bash
ndfs-password clear disk.img SYSTEM
```

Clear by user index:
```bash
ndfs-password clear disk.img 0
```

Output:
```
✓ Password cleared for user 'SYSTEM'
  Old password:  5349 (decimal) = 012345 (octal) = 0x14E5 (hex)
  New password: 0 (cleared)
✓ Changes saved to disk.img
```

### Get Manual Instructions

```bash
ndfs-password instructions disk.img ADMIN
```

Or save to file:
```bash
ndfs-password instructions disk.img ADMIN > clear-admin.txt
```

Output includes exact CHANGE-PAGE commands with calculated page numbers and offsets.

## Commands

| Command | Description | Modifies Disk? |
|---------|-------------|----------------|
| `list <image>` | List all users | No |
| `clear <image> <user>` | Clear password programmatically | **Yes** |
| `instructions <image> <user>` | Get manual instructions | No |
| `manual <image> <user>` | Same as instructions | No |
| `help` | Show help | No |

## Examples

```bash
# List users
ndfs-password list C:\ndfs\disk.img

# Clear password for SYSTEM user
ndfs-password clear C:\ndfs\disk.img SYSTEM

# Clear password for user index 5
ndfs-password clear C:\ndfs\disk.img 5

# Get manual instructions
ndfs-password instructions C:\ndfs\disk.img ADMIN

# Save instructions to file
ndfs-password manual C:\ndfs\disk.img ADMIN > instructions.txt
```

## Security Notes

- **Audit Trail**: Log when this tool is used
- **Access Control**: Restrict who can run this tool
- **Backup First**: Always backup before using `clear` command
- **Read-Only**: Use `instructions` when you don't want to modify the disk

## Error Messages

| Error | Cause | Solution |
|-------|-------|----------|
| File not found | Image path is wrong | Check file path |
| User not found | Invalid username/index | Use `list` to see users |
| Access denied | File is read-only | Check file permissions |
| Failed to clear | Disk mounted read-only | Ensure write access |

## Build & Run

```powershell
# Build
dotnet build -c Release

# Run
.\bin\Release\net9.0\ndfs-password.exe list disk.img
```

Or use from project root:
```powershell
dotnet run --project tools\NdfsPasswordTool -- list disk.img
```

## Exit Codes

- `0` - Success
- `1` - Error (see error message)

## Requirements

- .NET 9.0 or later
- Windows/Linux/macOS

## License

MIT License - HackerCorp Labs
