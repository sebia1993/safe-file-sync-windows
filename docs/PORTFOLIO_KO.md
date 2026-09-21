# SafeFileSync: 원본 보존과 복사 결과를 분리해서 검증하기

이 프로젝트는 **원본을 변경하지 않는 복사 과정, 검증 후 목적지 반영, 중단 후 재개**를 함께 다루는 Windows 데스크톱 도구입니다. 단순한 전송 완료율로 원본 보존이나 전체 내용 일치까지 판단하지 않습니다.

[README](../README.md)에서 사용 방법과 현재 기능을 먼저 확인할 수 있습니다. 아래 사례는 저장소의 구현과 합성 회귀 테스트를 연결하며, 실제 회사 파일이나 운영망 성과를 사용하지 않습니다.

## 원본 보호와 경로 경계

문자열상 다른 경로라도 원본의 하위 경로이거나 같은 물리 대상을 가리킬 수 있습니다. 기록 DB·로그 역시 원본과 분리되어야 하므로, 복사 명령 직전뿐 아니라 작업 공간 생성 전에 경계를 확인합니다.

- 구현: [SourceProtectionGuard](../src/SafeFileSync.Core/SourceProtectionGuard.cs)는 원본 영역의 읽기 외 작업을 거부합니다. [TransferWorkspace](../src/SafeFileSync.Infrastructure.Windows/TransferWorkspace.cs)와 [DirectoryLease](../src/SafeFileSync.Infrastructure.Windows/DirectoryLease.cs)는 실제 작업 경로·디렉터리 핸들을 다룹니다.
- 회귀: [SafetyTests](../tests/SafeFileSync.Tests/SafetyTests.cs)의 `SourceMutationRejected`, [TransferTests](../tests/SafeFileSync.Tests/TransferTests.cs)의 `SourceContainingStorageIsRejectedBeforeCreatingDatabase`, `DestinationHardLinkToSourceIsBlocked`를 확인할 수 있습니다.
- 다중 원본: [MultiSourceTests](../tests/SafeFileSync.Tests/MultiSourceTests.cs)의 `LaterSourceOverlappingDestinationIsRejectedBeforeAnyCreation`은 나중에 나열된 원본도 생성 전에 검사하는지 확인합니다.
- 한계: 앱 수준의 경계이며 악의적인 관리자·SMB 서버나 모든 파일시스템 별칭을 방어하는 OS 보안 경계가 아닙니다. 상세 조건은 [안전 모델](SAFETY_MODEL.md)을 따릅니다.

## 기존 파일과 내용 검증

예를 들어 원본과 목적지의 `report.txt`가 같은 크기·수정시간이더라도 내용은 다를 수 있습니다. 빠른 비교는 이를 내용 일치로 증명하지 못합니다. 새로 전송하는 파일은 목적지의 작업별 임시 영역에서 크기·SHA-256을 검증한 뒤 최종 위치에 반영합니다. 기존 목적지의 불일치 파일은 사용자가 교체 정책을 선택해야 교체합니다.

| 설계 선택 | 코드 | 실패 상황을 확인하는 회귀 테스트 |
|---|---|---|
| 기존 파일 보존과 검증 후 교체를 구분 | [TransferCoordinator](../src/SafeFileSync.Infrastructure.Windows/TransferCoordinator.cs), [TransferWorkspace](../src/SafeFileSync.Infrastructure.Windows/TransferWorkspace.cs) | [TransferTests](../tests/SafeFileSync.Tests/TransferTests.cs): `PreservePolicyReportsConflictWithoutOverwriting`, `ReplacePolicyCommitsOnlyVerifiedCopy` |
| 빠른 일치를 SHA-256 검증과 구분 | [TransferModels](../src/SafeFileSync.Core/TransferModels.cs) | [TransferTests](../tests/SafeFileSync.Tests/TransferTests.cs): `QuickMatchIsNotContentVerification`, `SameSizeTimeCorruptionIsRepairedByHashMode` |
| 취소된 임시 파일을 최종 파일로 게시하지 않음 | [TransferCoordinator](../src/SafeFileSync.Infrastructure.Windows/TransferCoordinator.cs) | [TransferTests](../tests/SafeFileSync.Tests/TransferTests.cs): `CancelDuringCopyNeverPublishesPartialFile` |
| 위험하거나 임의의 Robocopy 옵션을 차단 | [RobocopyArgumentBuilder](../src/SafeFileSync.Infrastructure.Windows/RobocopyArgumentBuilder.cs) | [SafetyTests](../tests/SafeFileSync.Tests/SafetyTests.cs): `DangerousOrUnknownOptionsRejected` |

SHA-256은 기본 파일 데이터 스트림을 비교합니다. ACL·ADS·NTFS 메타데이터 전체 복제나 폴더 전체의 원자적 반영을 뜻하지 않습니다. 목적지에만 있는 추가 파일도 삭제하지 않습니다.

## 중단과 재개

재개는 이전의 완료율부터 무조건 이어 쓰는 동작이 아닙니다. 저장된 원본 매핑과 정책을 유지하면서 현재 양쪽 폴더를 다시 검사하고, 최초 원본 manifest와의 차이도 남깁니다.

- 구현: [JobStore](../src/SafeFileSync.Infrastructure.Windows/JobStore.cs)의 snapshot·작업 이력과 [TransferCoordinator](../src/SafeFileSync.Infrastructure.Windows/TransferCoordinator.cs)의 재스캔·최종 상태 판정.
- 원본 변경: [TransferTests](../tests/SafeFileSync.Tests/TransferTests.cs)의 `ResumeDoesNotEraseEvidenceOfSourceChangesWhileStopped`는 중지 중 바뀐 내용을 재개 후 복사하더라도 `NeedsAttention`과 최초 기준의 차이를 유지하는 사례입니다.
- 매핑 고정: [MultiSourceTests](../tests/SafeFileSync.Tests/MultiSourceTests.cs)의 `ResumeRejectsChangedOrMissingSourceMappings`는 원본 추가·제거·재매핑을 차단합니다.
- 한계: 네트워크 파일시스템 호출이 반환하지 않으면 취소 반영이 늦어질 수 있습니다. 실제 네트워크 단절과 장시간 운용은 별도 확인이 필요합니다.

## 오류 설명과 데이터 보호

[DiagnosticCodes](../src/SafeFileSync.Core/DiagnosticCodes.cs)는 정해진 짧은 코드만 외부 문의에 사용하도록 구성합니다. [DiagnosticCodeTests](../tests/SafeFileSync.Tests/DiagnosticCodeTests.cs)의 `ArbitraryMessagesAndStringDataCannotSpoofOrLeakAnErrorCode`는 임의 오류 메시지나 문자열을 코드에 넣을 수 없는지 확인합니다.

코드 복사가 안전한 범위라는 설명은 상세 보고서·로그도 공개해도 된다는 뜻이 아닙니다. 로컬 보고서에는 내부 경로가 포함될 수 있습니다. [보안 제보 안내](../SECURITY.md)를 확인하십시오.

## 장비 없이 재현하기

Windows x64와 .NET 10 SDK에서 저장소 루트를 기준으로 실행합니다. 아래 테스트는 임시 합성 파일을 생성·변경·정리하며 실제 회사 폴더나 장비 계정이 필요하지 않습니다. 패키지 복원에는 개발 환경의 네트워크가 필요할 수 있습니다.

```powershell
dotnet test SafeFileSync.slnx -c Release --filter "FullyQualifiedName~QuickMatchIsNotContentVerification|FullyQualifiedName~SameSizeTimeCorruptionIsRepairedByHashMode|FullyQualifiedName~ResumeDoesNotEraseEvidenceOfSourceChangesWhileStopped"
```

예상 결과는 선택한 **3개 테스트 통과, 실패·건너뜀 0개**입니다. 이는 일부 사례의 재현이며 전체 Windows CI·SMB·패키지 검증 통과를 의미하지 않습니다.

전체 로컬·안전 회귀는 다음 명령을 사용합니다.

```powershell
dotnet test SafeFileSync.slnx -c Release --filter "Category!=Smb"
```

SMB 테스트는 [기존 Windows workflow](../.github/workflows/windows.yml)의 `smb` job이 만드는 격리된 loopback 공유와 매핑 드라이브를 사용합니다. 자동 검증은 이 workflow를 기준으로 하며 macOS에서의 빌드·검사를 Windows 실행 증거로 사용하지 않습니다.

## 검증 근거와 한계

아래는 **문서 정비 전 기준 커밋 `ea1620a2cad57f8b6d9e67f2268eb28bd026e933`**의 완료 기록입니다. 2026-09-21에 결과를 확인했으며, 실행일은 2026-09-12 UTC입니다. 이후 변경의 검증은 해당 변경 SHA의 실행 결과를 별도로 확인해야 합니다.

| 범위 | 공개 근거 | 확인한 결과 |
|---|---|---|
| Windows 로컬·안전 회귀, WPF 단일 EXE, ZIP 추출 smoke | [Windows safety checks / safety](https://github.com/sebia1993/safe-file-sync-windows/actions/runs/34696204135/job/103560009003) | 완료·성공 |
| 격리된 loopback SMB와 매핑 경로 | [Windows safety checks / smb](https://github.com/sebia1993/safe-file-sync-windows/actions/runs/34696204135/job/103560009118) | 완료·성공 |
| 실제 Windows 11·회사 SMB·EDR/DLP·물리 단절 | [현장 점검표](ACCEPTANCE.md) | 위 자동 검증으로 대체하지 않음 |

[검증 기록](VALIDATION.md)은 버전별 필수 게이트와 과거 완료 기록을 구분합니다. 게이트 목록이나 테스트 파일의 존재만으로 통과를 판단하지 않습니다. 최신 실행은 [Windows workflow](https://github.com/sebia1993/safe-file-sync-windows/actions/workflows/windows.yml)에서 대상 SHA와 함께 확인하십시오.

이 문서의 근거는 원본 보호·내용 검증·실패 복구의 구현과 자동 검증입니다. 실제 운영 파일의 무손실 보장, 업무 시간 절감률, 회사망 처리량 또는 현장 적용 완료를 주장하지 않습니다.
