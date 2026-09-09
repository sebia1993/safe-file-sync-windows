# Acceptance evidence required before production use

Automated Windows CI must cover:
- Normal and conflicting local copies, destination extras and empty directories.
- Source content, last-write/creation timestamps and attributes unchanged by successful/failed/retried transfers.
- Destination hardlinks, reparse ancestors, overlapping roots, unknown options and cancelled/incomplete snapshots.
- Same-size/time content corruption, source changes and interrupted staging recovery.
- Real loopback SMB copy and local/UNC identity overlap rejection.
- Large manifest and large-file checks; published WPF launch and clean shutdown.

Field acceptance (requires the actual Windows 11 PCs and network):
- Folder dialogs, UNC authentication with existing Windows credentials, errors and results readable in Korean.
- Company SMB share, real network unplug/reconnect, quota/full disk, permissions, EDR/DLP blocks.
- Long-running large data transfers, locked/open files and application shutdown/restart.
- Compare source/destination SHA-256 and review original timestamps/attributes independently.

Do not interpret hosted runner or loopback SMB success as completion of these field checks. No real company paths, logs or data should be committed to the public repository.
