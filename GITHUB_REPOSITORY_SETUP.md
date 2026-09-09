# Repository setup

- Public repository: https://github.com/sebia1993/safe-file-sync-windows
- Default branch: main; development branches use codex/.
- License: MIT. Source and synthetic tests only; private work data stays local.
- Pull requests and main pushes run .github/workflows/windows.yml.
- Version tags v* run .github/workflows/release.yml. Release requires the complete Windows/SMB/UI/package workflow to succeed and the packaged version and commit to match the tag.
- First version is an alpha prerelease. Never create a production-ready claim from CI or a loopback share alone.
- Release payload: portable win-x64 ZIP, SHA256SUMS, build-info.json. Download and independently check SHA-256, ZIP integrity and the embedded commit after publication.
- Private vulnerability reporting is preferred when enabled; never post internal data in public issues.
