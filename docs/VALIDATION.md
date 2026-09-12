# 검증 게이트 - 0.1.0-alpha.6

기록 설정 제거는 배포 전에 기존 Windows 워크플로에서 다음 검증을 통과해야 합니다.

- 기록 경로 입력·찾기·초기화·미리보기·위치 안내가 WPF 화면과 접근성 트리에 존재하지 않음.
- 앱 시작만으로 기록 폴더를 만들지 않고, 실제 비교·복사는 내부 기본 위치에 기록함.
- 시스템 드라이브 원본의 S03 차단 시 원본·목적지 안에 기록을 만들지 않으며, 화면·코드 복사에 내부 기록 경로가 나타나지 않음.
- 앱을 종료 후 다시 열어도 경로 설정 없이 이력을 불러오고, 원본 매핑을 복구하여 같은 작업을 재개함.
- 기존 ACL·원본 보호·다중 원본·오류코드·로컬/SMB·단일 EXE 및 ZIP 검증도 통과함.

위 목록은 필수 검증 항목이며 통과 결과가 아닙니다. 완료된 Windows 실행과 다운로드 ZIP·체크섬·태그 커밋 검증은 배포 응답에 기록합니다. 실제 사내 PC의 권한·EDR/DLP 검증은 별도입니다.

---

# 검증 게이트 - 0.1.0-alpha.5

새 기본 기록 위치는 배포 전에 기존 Windows 워크플로에서 다음 검증을 통과해야 합니다.

- UI와 실행 엔진의 기본 기준 폴더가 Windows 설치 드라이브의 루트로 일치하고, 실제 기록 경로 미리보기와 기본 위치 복원이 동작함.
- 앱 시작·이력 조회만으로 기록 폴더가 생기지 않으며, 모든 원본·목적지 겹침 검사 이후에만 생성함.
- 사용자 폴더 원본은 외부 기본 기록 위치를 사용하고, 시스템 드라이브 전체가 원본인 경우 S03으로 차단함.
- 신규 기본 기록 폴더의 제한된 DACL과 하위 파일 상속, 기존 폴더의 소유자·허용 권한 검사, 권한 거부 S04와 기존 ACL 미변경을 확인함.
- 기본 이력·재개 시 권한 검사 전에 DB를 읽지 않으며, 사용자 지정 위치와 이전 AppData 기록의 조회·재개를 유지함.
- 기존 원본 보호·다중 원본·오류코드·로컬/UNC/매핑 SMB·단일 EXE 및 패키지 검증도 통과함.

위 목록은 필수 검증 항목이며 통과 결과가 아닙니다. 완료된 Windows 실행과 다운로드 ZIP·체크섬·태그 커밋 검증은 배포 응답에 기록합니다. 실제 사내 PC의 드라이브 권한·EDR/DLP·네트워크 단절 검증은 별도입니다.

---

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
