# Contributing

Read AGENTS.md, CODEX_DEVELOPMENT_PLAN.md and docs/SAFETY_MODEL.md before changes. Source protection is an invariant, not a configurable option. Do not weaken checks to pass tests or add arbitrary Robocopy switches.

Use synthetic fixtures only. Never commit actual company paths, names, credentials, data or logs. Run the relevant tests and the Windows workflow. Record native Windows, loopback SMB, GUI/package and actual field evidence separately.

Changes to mutations, path handling, verification or recovery need regression tests that demonstrate the failure being prevented. Keep tests that found previous safety regressions. Open a PR with behavior, safety impact and verification evidence.
