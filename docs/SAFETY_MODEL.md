# Safety model

## Invariant
The app never intentionally deletes, moves, renames, truncates, writes or creates anything inside the selected source. No source metadata setters exist. Source data opens request read access only. Reports and SQLite databases live under SafeFileSync in the user-selected existing parent directory (default: Windows system-drive root, normally C:\), after unchanged source/storage separation checks. The location selection is not persisted in the user profile; reselect a custom location after restart to load its history. Windows access-time behavior is outside the app's control; PASS refers to the observed manifest scope, not an OS snapshot.

기본 기록 위치는 UI와 실행 엔진이 같은 값으로 계산하며 실제 폴더는 보통 `C:\SafeFileSync`입니다. 시작·이력 조회에서는 폴더를 생성하지 않습니다. 작업 시작 시 모든 원본·목적지와 문자열·물리 경로 겹침을 확인한 뒤에만 기록 폴더를 만듭니다. 기본 위치의 신규 폴더는 상속을 차단한 DACL에 현재 사용자 SID·SYSTEM·Administrators의 FullControl을 지정하고 하위 파일·폴더에 상속합니다. 기존 기본 폴더는 소유자·허용 ACL을 검사한 뒤 이력·DB를 읽거나 재사용하며 기존 ACL을 수정하지 않습니다. 생성 권한이 없거나 기존 권한 검사를 통과하지 못하면 S04로 차단합니다. 직접 선택한 다른 기록 기준 폴더에는 기존 권한 정책을 유지합니다.

이전 AppData 기록의 자동 이동·삭제나 AppData로의 자동 복귀는 없습니다. 이전 위치를 직접 선택하면 해당 이력을 사용합니다. 시스템 드라이브 전체가 원본이면 기본 기록 위치도 원본에 포함되므로 다른 드라이브의 안전한 위치가 필요합니다.

## Paths and handles
Lexical validation rejects equal/nested roots in both directions, relative/device/extended inputs, alternate streams, ambiguous/reserved names. Internal native paths use the extended syntax for long paths.
Windows directory leases open each ancestor with OPEN_EXISTING and OPEN_REPARSE_POINT, reject reparse points, and hold real directory read access without delete sharing. Physical file identifiers and resolved names are checked for overlapping roots; source subdirectory identities are collected and compared against destination directory chains. Unsupported/zero file identifiers fail closed. File contents are opened with FileShare.Read to reject active content writers and deletion while hashing/copying.

This is not an OS sandbox and does not defend against a malicious administrator, kernel/filesystem bugs, a malicious SMB server or an actor with equivalent privileges replacing storage data between checks. Physical server alias behavior and unusual filesystems still require field validation. Destination reparse points and hardlinks are rejected. No ACL/DLP/EDR bypass or credential storage exists.

## Copy and commit
Named-source jobs persist the complete source-to-destination-folder mapping in one job. Every source root is resolved, checked against destination/storage and other sources, and pinned before any storage directory is created. Paths within the manifest are prefixed by a unique safe folder name; synthetic source-root directory entries carry physical identities. All actual reads map back to their source root; every mutation is checked against all source guards. Duplicate/nested roots and case-insensitive alias collisions are rejected. The first/final manifest spans all sources, so later processing cannot hide changes to an earlier source.

Resume loads the saved mapping before writable workspace creation and rejects added/removed/remapped sources. Older jobs without a Sources field keep their original single-source placement. Comparing a named-source job does not create destination children.

Only fixed file arguments are passed using ProcessStartInfo.ArgumentList to the system Robocopy executable, with no shell. /MOV, /MOVE, /MIR, /PURGE and arbitrary options are forbidden. Robocopy writes only to the job's `.safefilesync-<random job id>` destination directory. Existing final files remain until a staged copy passes size and SHA-256 checks against the locked source. Missing files use non-overwriting rename; explicitly allowed replacements use File.Replace. No fallback truncates a final target. Destination-only files remain untouched.

Staging is retained for interruption/retry. Only this job's exact staging subtree is omitted from its destination comparison. Other folders are never silently omitted. Existing source names colliding with the job staging namespace abort copying.

## What verification means
Quick mode compares path/type/size/last-write time and is not content proof. SHA-256 compares basic file data streams, not ACL/ADS or all NTFS metadata. Empty directories are included. Error/excluded/missing-hash entries cannot count as verified. Cancelled snapshots cannot be read as completed snapshots. The first completed source snapshot is retained across resume; differences against the final source snapshot and per-file transfer failures force NeedsAttention; Robocopy exit codes never determine verification success on their own.

Network filesystem calls may take time to return after cancellation. The app waits for the Robocopy child to terminate before releasing resources. No completed state is stored on cancellation.

Robocopy groups contain at most 32 files from one directory and use fixed /MT:8. Unicode logs are newly created in the separately pinned application storage directory with a no-delete-sharing handle. This avoids Robocopy log-path expansion failures for deeply nested payloads. Source reparse entries are excluded and remain unverified while regular files can proceed.

## Support codes
The support-code clipboard path accepts a typed finite category and emits one explicit literal (S01-S16 or S99). It never copies exception text, paths, native error numbers, IDs, hashes, timestamps or logs. Unknown persisted enum values normalize to S99. Domain tags preserve original exception types and local details; native classification uses known exception types and Windows-facility HRESULTs, never message matching. Scanner failures and per-file failures retain their category through preflight/final results. A failed scan is not itself proof of source mutation; changing destination data is not labeled as changing source data. Codes are diagnostic only and do not change completion or source-protection decisions. No diagnostic upload is added; local reports and logs still contain internal details.
