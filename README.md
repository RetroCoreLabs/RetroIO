# RetroIO

Storage, filesystems and comms libraries of the RetroCore family (formerly RetroFS, X25Emulator and the NDInsight XMSG library). See `E:\Dev\RCL\Migration\RetroIO code migration.md` while the move is in progress.

## Layout

| Folder | What is in it |
|---|---|
| `src/` | one folder per published package; inside each, `src/` is the code and `tests/` its own tests |
| `tests/` | test projects that span several packages and so belong to none of them |
| `tools/` | dev-only projects, never shipped |
| `eng/` | the shared MSBuild files (`eng/build/`), the package icon and the dev scripts |
| `DOC` | junction to the NDInsight XMSG research notes the protocol code cites; not RetroIO's own docs |
| `demo/test-images` | junction to the disk-image test corpus |

The two junctions are created by `eng/setup-test-data.ps1` and are in `.gitignore`.
