# Validation gates - 0.1.0-alpha.4

The existing Windows workflow gates publication with:
- Finite, stable three-character error codes; arbitrary exception messages, Data, unknown enum values and native error numbers cannot become clipboard payloads.
- Actual locked-file scanner/preflight propagation, storage/source overlap, preserved conflicts, source changes, incomplete scans and final destination verification failures.
- Successful and ordinary compare results have no stale error code; interrupted and resumed work retains the original safety rules.
- Standalone WPF shows and copies exactly S01/S03/S13, replaces earlier codes, clears codes after recovery, and preserves usable result/table/tree heights.
- The full existing source-protection, multi-source, local/UNC/mapped SMB and standalone package suites remain required.

These are required gates, not a claim that a pending run passed. The delivery response links the completed Windows run and records downloaded ZIP/checksum/tag identity verification. Actual company PCs, clipboard/DLP policies and physical network interruptions remain outside hosted CI.

---

# Validation gates - 0.1.0-alpha.3

New work uses a single job/manifest for named sources. The existing Windows workflow gates publication with:
- Multiple source folders with colliding relative filenames and empty roots; separate destination subfolders and aggregate SHA-256 results.
- All-source destination/storage overlap checks before creation; duplicate/nested sources and invalid/colliding destination names.
- Frozen source mapping on resume, cross-source cancellation/mutation evidence and namespaced failed-only retry.
- Multiple sources to a mapped SMB destination, alongside existing local/UNC coverage.
- WPF add/remove/edit source mappings, combined results, history restore, cancellation/resume and legacy single-source support.

These are required gates, not a claim that a pending run has passed. The delivery response links the completed run and records downloaded ZIP/checksum/tag identity verification. Actual company PCs, network policies and physical interruptions remain outside hosted Windows CI.

---

# Validation gates - 0.1.0-alpha.2

The release workflow runs the full Windows suite before publication. New regression gates cover:
- A synthetic user profile with AppData inside source: unsafe records rejected before writes, external records support copy/history/resume with unchanged source data/metadata.
- Storage within destination remains blocked.
- Real SMB mapping to a drive-letter root: profile copy with external records, source preservation and local/mapped physical-overlap rejection.
- Standalone WPF UI: unsafe records error, external location entry, correct DB/report location, history switching and resume.

A listed gate is not a claim that a pending run passed. Delivery reports the completed run and independent checksum/ZIP/commit checks. Actual Windows 11/company network/EDR/DLP acceptance remains separate (see ACCEPTANCE.md).

---

# Validation record — 0.1.0-alpha.1

Verified implementation: `5b8d9074fc69cf27d8129e39b0f2176f99d0038c`.
Windows run: https://github.com/sebia1993/safe-file-sync-windows/actions/runs/34346991634

- 91 unit/local Windows integration tests passed; 2 real loopback SMB tests passed, with no skipped tests.
- Source write/delete-deny ACL, unchanged source data/metadata, file/directory locks, physical overlap, junction/hardlink/dangling-link defenses passed.
- Long paths beyond 260 characters, Unicode/literal shell characters, overwrite/preserve/extras, corruption repair, cancellation/resume and immutable original manifest checks passed.
- Standalone EXE (without sidecar DLLs) launched and completed automated WPF compare/copy, tree navigation and second-instance rejection. UI screenshots are retained as the run's `ui-evidence` artifact.
- ZIP creation, SHA-256 checksum, embedded build identity and extracted executable launch/normal exit passed. The tagged release workflow repeats these gates before publication and checks tag/build identity.
- macOS cross-build passed separately; this is not Windows runtime evidence.

Synthetic scale measurements from the earlier equivalent benchmark fixtures:
https://github.com/sebia1993/safe-file-sync-windows/actions/runs/34342525484

| Fixture | Observed result |
|---|---|
| 100,000 source + 100,000 destination manifest entries | Persist/compare 4.59 s; 43.8 MiB managed memory at measurement point |
| 1,025 small files | Copy and SHA-256 verification 12.86 s |
| 512 MiB + 17 bytes | Copy and verification 5.56 s; fixture creation and independent hashes excluded |

These are hosted-runner measurements, not network throughput guarantees. The same scale fixtures also pass in the implementation run above.

The hosted runner was Windows Server 2025. Actual Windows 11 workstation, company SMB/EDR/DLP, physical disconnect, full-disk/quota and very long production transfer acceptance remain unverified. Follow ACCEPTANCE.md before production use. SHA-256 covers basic file data streams, not ACL/ADS or a point-in-time volume snapshot.

Release assets include `build-info.json` with the exact tagged commit and `SHA256SUMS`. Publication is a prerelease; final downloaded asset integrity and tag identity are independently checked during delivery.
