# Development plan
Reconstructed from the referenced planning conversation; its original ZIP was not available.

0. Safety kernel: solution, source guard, conservative lexical paths, fixed argument preview, Windows tests.
1. Streaming folder scanner, manifest, source/destination comparison and directory entries.
2. WPF selection, side-by-side contents, cancellable preflight, capacity and write access checks.
3. Robocopy process coordinator, progress, bounded retry, cancellation, safe staging and commit.
4. Destination verification and source before/after manifest comparison; never equate exit code with verification.
5. Read-only SHA-256 verification and changed-during-read detection.
6. SQLite history, interrupted job recovery and failed-file retry; data outside source.
7. Windows packaging, release checksums, documentation and field acceptance.

Milestone 0 only is implemented. Copying remains disabled until physical identity checks (UNC aliases, mapped drives, reparse points, hard links and race conditions), safe staging, and source immutability integration tests are complete.
Estimate: 1 of 8 milestones (12.5% by milestone count; not an effort estimate).

