# Development plan and completion gates

Reconstructed from the referenced planning conversation; its original ZIP was not available.
The actual objective is a usable Windows 11 local/SMB folder transfer and verification application, not just a safety prototype.

| Stage | Implementation | Remaining evidence/work |
|---|---|---|
| 0. Safety kernel | Strict lexical paths, source mutation policy, Windows directory leases, fixed Robocopy arguments | Expanded native integration suite must pass |
| 1. Scanner/manifest | Streaming scan into SQLite, files/empty directories, error/exclusion states, quick/hash comparison | Large-tree performance and network error coverage |
| 2. WPF/preflight | Folder dialogs/UNC input, side-by-side trees, differences, capacity check, policy selection | Packaged launch smoke, GUI acceptance, useful failure browsing |
| 3. Copy engine | Restartable Robocopy to job staging, per-file verification/commit, cancellation, bounded retries | Resolve Windows integration regressions; benchmark and optimize many-small-file performance |
| 4. Automatic verification | Source-before/after snapshots, destination rescan, separate status/percent | Mutation/cancellation integration gates |
| 5. SHA-256 | Read-only locked hashing and staged content validation | Large-file and loopback SMB evidence |
| 6. Recovery | SQLite jobs/snapshots/outcomes, history, resume by re-scan, HTML report | Dedicated failed-only retry, large manifest regression, report completeness |
| 7. Delivery | Windows build/publish CI | Tested ZIP/checksum release, tagged SHA verification, documentation and field checklist |

No release while safety tests fail. CI/loopback tests do not prove company SMB, EDR/DLP, actual network interruptions or interactive field usability. Those limits must remain explicit.
