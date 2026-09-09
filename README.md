# SafeFileSync for Windows
Windows 11용 원본 보호 중심 폴더 복제·검증 도구. 로컬 및 사내 SMB/UNC 목적지를 목표로 합니다.

현재: **Milestone 0 안전 기반 프로토타입**. 실제 복사는 아직 제공하지 않습니다.

- C# / .NET 10 / WPF solution
- 원본 Write/Delete/Move/Rename/Truncate/Create 차단 정책
- 동일·중첩·모호한 경로 차단 (문자열 기준)
- 허용 목록 기반 Robocopy 인자 미리보기, 임의 옵션 금지
- Windows CI 단위 테스트와 WPF self-contained publish

## Build
```powershell
dotnet test SafeFileSync.slnx -c Release
dotnet run --project src/SafeFileSync.App
```

GitHub Actions artifact는 경로 검사 개발용입니다. 실제 복사, SMB 연결, 물리 경로 별칭, 권한, 대용량 전송, EDR/DLP, GUI 수동 테스트는 아직 검증하지 않았습니다.

전송 진행률, 원본→목적지 복제율, 원본 변경 여부, SHA-256 검증률은 별개로 구현합니다. 목적지 추가 파일은 보존합니다. 자격 증명을 저장하거나 보안제품을 우회하지 않습니다.

[개발 계획](CODEX_DEVELOPMENT_PLAN.md) · [안전 모델](docs/SAFETY_MODEL.md)
Robocopy 옵션 근거: [Microsoft Learn](https://learn.microsoft.com/en-us/windows-server/administration/windows-commands/robocopy).

