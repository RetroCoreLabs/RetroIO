# Changelog

All notable changes to RetroIO. One version per repo, so one entry covers every package.

## [Unreleased]

### Changed
- **SFTP mounts now check the server's host key and refuse an unknown one.** An existing `sftp://`
  mount that worked before stops working until you give it `?hostKey=SHA256:...` (the fingerprint
  from `ssh-keygen -lf`), or `?hostKeyCheck=accept-new` to remember the key on the first
  connection. `?hostKeyCheck=off` restores the old behaviour. See `RetroIO.Storage.Providers`.

### Added
- First import: the RetroFS, X25Emulator and NDInsight XMSG libraries, moved in by
  `Migration/tools/rename.py` and split into one package per project.

