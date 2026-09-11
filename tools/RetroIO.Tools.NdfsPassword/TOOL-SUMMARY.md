# NDFS Password Tool - Complete

**Location**: `tools/RetroIO.Tools.NdfsPassword/`
**Binary**: `bin\Release\net9.0\ndfs-password.exe`
**Build Script**: `build.ps1` at the repository root (the old build-password-tool.bat is gone)

---

## Features Implemented

### 1. List Users
```bash
ndfs-password list disk.img
```

Shows:
- User index
- Username
- Reserved pages (quota)
- Used pages
- Password status (CLEARED or hex value)

### 2. Clear Password
```bash
ndfs-password clear disk.img SYSTEM
ndfs-password clear disk.img 0
```

Features:
- ✅ Accepts username or user index
- ✅ Shows old password (decimal, octal, hex)
- ✅ Shows new password (0)
- ✅ Saves changes to disk
- ✅ Displays confirmation

### 3. Get Instructions
```bash
ndfs-password instructions disk.img ADMIN
ndfs-password manual disk.img ADMIN
```

Generates:
- ✅ Exact FILE-SYSTEM-INVESTIGATOR commands
- ✅ Calculated page numbers (in octal)
- ✅ Calculated word offsets (in octal)
- ✅ Current password value
- ✅ Technical details section
- ✅ Step-by-step instructions

---

## Example Output

### List Command

```
╔════════════════════════════════════════════════════════════╗
║      NDFS Password Management Tool v1.0                   ║
║      RetroCore Labs - RetroFS Project                     ║
╚════════════════════════════════════════════════════════════╝

Found 1 user(s) in FLOPPY-360KB.img:

┌──────┬──────────────────┬──────────┬──────────┬────────────┐
│ Index│ Username         │ Reserved │ Used     │ Password   │
├──────┼──────────────────┼──────────┼──────────┼────────────┤
│    0 │ SYSTEM           │      148 │      148 │ CLEARED    │
└──────┴──────────────────┴──────────┴──────────┴────────────┘

Total: 1 user(s)
```

### Clear Command

```
✓ Password cleared for user 'SYSTEM'
  Old password:  5349 (decimal) = 012345 (octal) = 0x14E5 (hex)
  New password: 0 (cleared)
✓ Changes saved to disk.img
```

### Instructions Command

```
=== CLEAR PASSWORD: User 'SYSTEM' (Index 0) ===

FILE-SYSTEM-INVESTIGATOR Instructions:

3. Clear password to zero:
   CHANGE-PAGE
   Give page no. ( 0-110120 ): 230
   Ready:
   230/11
   <current_password_value>    <- Will be displayed
   000000                       <- Type this to clear
   .                            <- Type period to save

=== TECHNICAL DETAILS ===
User Data Block Page:     152 (octal: 230)
Password Word Offset:     9 (decimal) = 11 (octal)
Current Password Hash:    0 (decimal) = 0 (octal) = 0x0000 (hex)
```

---

## Build & Install

### Build

```powershell
# From project root
.\build-password-tool.bat

# Or manually
cd tools\NdfsPasswordTool
dotnet build -c Release
```

### Install (Optional)

Copy to a directory in your PATH:
```powershell
copy publish\win-x64\ndfs-password.exe <a directory on your PATH>
```

---

## All Commands

| Command | Description | Modifies Disk |
|---------|-------------|---------------|
| `list <image>` | List all users | No |
| `clear <image> <user>` | Clear password | **Yes** |
| `instructions <image> <user>` | Get manual steps | No |
| `manual <image> <user>` | Same as instructions | No |
| `help` | Show help | No |

---

## Use Cases

### 1. Quick Password Reset

```bash
ndfs-password clear production.img ADMIN
```

### 2. Generate Instructions for Help Desk

```bash
ndfs-password instructions production.img JOHNDOE > reset-johndoe.txt
```

### 3. Audit User Passwords

```bash
ndfs-password list production.img
```

Check which users have cleared passwords vs set passwords.

### 4. Batch Operations

```powershell
# Clear all passwords (use with caution!)
$users = @("USER1", "USER2", "USER3")
foreach ($user in $users) {
    .\ndfs-password.exe clear disk.img $user
}
```

---

## Safety Features

### Read-Only Mode

- `list` and `instructions` open disk read-only
- No accidental modifications

### Error Checking

- User not found → Error message
- File not found → Error message
- Access denied → Error message
- Invalid arguments → Help text

### Confirmation

- Shows old password before clearing
- Shows new password after clearing
- Displays filename being modified

---

## Security Recommendations

1. **Audit Logging**: Log when tool is used
   ```powershell
   $timestamp = Get-Date -Format "yyyy-MM-dd HH:mm:ss"
   "$timestamp - Cleared password for USER1" >> password-audit.log
   ```

2. **Access Control**: Restrict tool access to admins
   ```powershell
   icacls ndfs-password.exe /grant Administrators:F /deny Users:RX
   ```

3. **Backup First**: Always backup before using `clear`
   ```powershell
   copy disk.img disk.img.backup
   .\ndfs-password.exe clear disk.img SYSTEM
   ```

---

## File Structure

```
tools/NdfsPasswordTool/
├── RetroIO.Tools.NdfsPassword.csproj      # Project file
├── Program.cs                    # Main implementation
├── README.md                     # User guide
├── TOOL-SUMMARY.md              # This file
└── bin/Release/net9.0/
    └── ndfs-password.exe         # Compiled binary
```

---

## Test Results

### Build Status

```
Build succeeded.
    0 Warning(s)
    0 Error(s)
```

### Manual Tests

✅ List users on FLOPPY-360KB.img
✅ Generate instructions for SYSTEM user
✅ Shows correct page numbers (230 octal)
✅ Shows correct word offset (11 octal)
✅ Displays current password hash
✅ No LINQ usage (per requirements)

---

## Quick Start

```powershell
# 1. Build
.\build-password-tool.bat

# 2. List users
.\tools\NdfsPasswordTool\bin\Release\net9.0\ndfs-password.exe list disk.img

# 3. Get instructions
.\tools\NdfsPasswordTool\bin\Release\net9.0\ndfs-password.exe instructions disk.img ADMIN

# 4. Clear password (if needed)
.\tools\NdfsPasswordTool\bin\Release\net9.0\ndfs-password.exe clear disk.img ADMIN
```

---

**Tool Complete** - Professional NDFS password management! 🛠️

**Full Paths:**
- Tool: `tools/RetroIO.Tools.NdfsPassword/`
- Binary: `publish/win-x64/ndfs-password.exe` (published by build.ps1)
- Build: `build.ps1` at the repository root
