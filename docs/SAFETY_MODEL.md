# Safety model
The application must never intentionally mutate source data or metadata. Read-only file opens only.
Lexical validation rejects equal/nested roots in both directions, ambiguous segments, device paths, alternate streams, relative paths and reserved names.
Lexical validation alone does not establish filesystem identity. Aliased UNC shares, mapped drives, short names, reparse points, hard links and TOCTOU require a separate physical preflight and execution design. The prototype does not execute any file writes or Robocopy.
The guard is a policy helper, not an OS sandbox. Future mutations must go through guarded destination operations. Prefer source credentials with read-only permissions as defense in depth.
Preserve destination-only files. Default conflict policy and verified staging commit must be implemented before transfer. Never claim ACL/ADS or whole-tree identity from file-content checks alone.
Source scans and reports must distinguish unreadable/excluded/changed entries from verified entries. Cancellation or partial scanning cannot yield 100%.
Windows may update access metadata on reads; physical unchanged-source claims require precise scope and evidence.

