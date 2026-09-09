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
