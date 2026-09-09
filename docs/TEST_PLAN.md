# Test plan

The Windows CI workflow is the automated authority. Local macOS builds and portable policy/SQLite tests are development aids only.

- SafetyTests: strict input paths, overlap, mutation operations and option allowlist.
- TransferTests: actual Windows handles/Robocopy, source hashes and metadata before/after, empty/Unicode files, locks, conflict policies, links, cancellation/recovery, changed source, same-size/time corruption, grouped small files and a streamed large file.
- PersistenceTests: 100,000 entries per snapshot, complete query and mismatch detection with streaming enumeration.
- SmbTests: isolated hosted-runner share, both transfer directions, replacement and local/UNC physical identity overlap.
- Smoke-App.ps1: published WPF process starts, creates a window and exits normally.
- Test-UI.ps1: real UI Automation against an isolated single EXE inputs folders, invokes compare/copy, checks content and quick-verification wording, and inspects both nested folder trees; captures screenshots.
- Package.ps1: portable ZIP, checksum/build metadata, extraction and extracted-executable startup.

Check current run results, not test names alone. A failure blocks merge/release until fixed. Actual company network, security products, physical disconnect and production-scale endurance remain field acceptance, documented in ACCEPTANCE.md.
