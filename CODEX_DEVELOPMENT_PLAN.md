# Development plan and completion gates

Reconstructed from the referenced planning conversation; its original ZIP was not available.
The implementation delivers a Windows 11 local/SMB folder transfer and verification application.

| Stage | Delivered implementation | Automated evidence |
|---|---|---|
| 0. Safety kernel | Strict paths, read-only source policy, physical directory leases, fixed arguments | Native overlap/link/lock tests and source write/delete-deny ACL fixture |
| 1. Scanner/manifest | Streaming SQLite snapshots, files/empty folders, exclusions, quick/hash comparison | 100,000 source + 100,000 destination manifest regression |
| 2. WPF/preflight | Korean paths/dialogs, trees/differences, capacity/write checks | Standalone EXE UI automation: compare, copy, trees, single-instance guard |
| 3. Copy engine | Restartable staging, batched Robocopy, SHA-256 commit, cancellation/retry | Conflicts/extras/long paths, 1,025 small files and 512 MiB file |
| 4. Automatic verification | Immutable initial source snapshot and final source/destination scans | Mutation during transfer and pause/resume regression |
| 5. SHA-256 | Read-only locked hashing and staged validation | Same-size/time corruption repair, large-file and loopback SMB checks |
| 6. Recovery | SQLite history, resume, failed-only retry, full HTML report | Interrupted snapshots, staged resume, retry selection, report beyond UI limit |
| 7. Delivery | Single EXE, ZIP/checksum/build identity, gated prerelease workflow | PR and release Windows workflow; final asset verification recorded in delivery response |

See docs/VALIDATION.md for actual run evidence. A listed test is not a claim that an unfinished run passed. No release while any required gate fails.

Actual Windows 11 PCs, company SMB, EDR/DLP, physical network interruptions and long production transfers require the separate field checklist in docs/ACCEPTANCE.md. Hosted CI cannot complete those checks.
