SafeFileSync Windows 11 x64용 첫 알파 버전입니다.

- 원본 읽기 전용 폴더 비교/복사, 로컬 및 SMB/UNC 경로
- 작업별 임시 복사와 SHA-256 검증 후 반영, 목적지 추가 파일 보존
- 빠른 검증과 전체 SHA-256 검증 구분, 원본 전후 검사
- 한국어 WPF 화면, 양쪽 폴더/차이 목록, 작업 이력과 재개/실패 항목 재시도
- SQLite manifest와 전체 HTML 보고서

ZIP에서 SafeFileSync.App.exe를 꺼내 실행하세요. .NET·SQLite가 포함된 단일 EXE이며 EXE만 별도로 옮겨 실행할 수 있습니다. SHA256SUMS로 다운로드 파일 무결성을 확인할 수 있습니다.

Windows CI, loopback SMB 및 자동 UI 검증 범위와 실제 사내 환경 검증은 구분합니다. 실행 파일은 아직 서명되지 않았으며 실제 사내 EDR/DLP·네트워크 단절·Windows 11 현장 수용 검증은 남아 있습니다. 빠른 검증은 내용의 완전한 일치를 증명하지 않으며 ACL/ADS 전체 복제는 지원하지 않습니다.
