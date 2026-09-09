# Validation record

Development is still in progress; this file will be finalized against the release commit and artifacts.

Verified intermediate Windows run: https://github.com/sebia1993/safe-file-sync-windows/actions/runs/34342525484
- 81 local/unit/integration tests passed; 2 loopback SMB tests passed; WPF process launch and normal exit passed.
- 100,000 source + 100,000 destination manifest entries persisted and compared: 4.59 s, 43.8 MiB managed memory at the measurement point.
- 1,025 small files copied and SHA-256 verified: 12.86 s.
- 512 MiB + 17 byte file copied and verified: 5.56 s (additional fixture creation and independent hashes are outside this timer).
These are synthetic hosted-runner measurements, not network throughput guarantees.

Additional GUI interaction, staging-link regressions, failed-only retry, current commit checks and final ZIP verification remain to be recorded.

Field limitations: actual Windows 11 workstation, company SMB/EDR/DLP, physical disconnect and very long production transfers are not verified here. See ACCEPTANCE.md.
