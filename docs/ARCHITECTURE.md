# Architecture

SafeFileSync.App is the Korean WPF UI. It permits one foreground job, cancels before closing and dispatches work off the UI thread. A per-session instance mutex prevents overlapping GUI instances.

SafeFileSync.Core owns immutable scan/difference/job models, lexical path validation and the source operation policy. Quick comparison and hash verification have distinct states.

SafeFileSync.Infrastructure.Windows owns native read handles and directory leases, streaming scanner, SQLite snapshots/outcomes, transfer workspace, fixed Robocopy argument builder/process controller, coordinator and HTML reporting.

Execution path: WPF -> coordinator -> physical workspace/source guard -> fixed argument builder -> system Robocopy -> staged SHA-256 verification -> destination commit -> source/destination rescan -> report.

Files are copied in same-directory groups of at most 32 with Robocopy /MT:8. Source read handles stay open for each group. Hash buffers are pooled, file buffers are bounded, SQLite stores the whole manifest and the UI displays a bounded subset. In-progress outcomes are committed before starting Robocopy; completed outcomes are committed at group boundaries. A crash is recovered by rescanning, not by trusting stale percentages.

Staging is on the destination volume so final rename/replacement stays on that volume. Every final file is committed independently; the whole tree is not transactional or a VSS snapshot. Existing destination files are not truncated as a fallback. Current credentials and security controls remain in effect.
