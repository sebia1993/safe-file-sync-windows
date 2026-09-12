using System.ComponentModel;
using System.Data.Common;
using System.Text.Json;

namespace SafeFileSync.Core;

// Stable, finite support vocabulary. Never derive a support code from user data or message text.
public enum DiagnosticCode
{
    InvalidInput = 1, PathOverlap = 2, RecordLocation = 3, AccessDenied = 4,
    PathUnavailable = 5, Network = 6, FileInUse = 7, InsufficientSpace = 8,
    UnsafeLink = 9, SourceChanged = 10, CopyFailed = 11, VerificationIncomplete = 12,
    DestinationConflict = 13, JobRecord = 14, Cancelled = 15, Io = 16, Unknown = 99
}

public static class DiagnosticCodes
{
    private static readonly object TagKey = new();
    private static readonly DiagnosticCode[] Priority = [
        DiagnosticCode.SourceChanged, DiagnosticCode.RecordLocation, DiagnosticCode.PathOverlap,
        DiagnosticCode.UnsafeLink, DiagnosticCode.InvalidInput, DiagnosticCode.AccessDenied,
        DiagnosticCode.Network, DiagnosticCode.PathUnavailable, DiagnosticCode.FileInUse,
        DiagnosticCode.InsufficientSpace, DiagnosticCode.JobRecord, DiagnosticCode.DestinationConflict,
        DiagnosticCode.CopyFailed, DiagnosticCode.Cancelled, DiagnosticCode.Io,
        DiagnosticCode.VerificationIncomplete, DiagnosticCode.Unknown
    ];

    public static DiagnosticCode Normalize(DiagnosticCode code) => Enum.IsDefined(code) ? code : DiagnosticCode.Unknown;

    // Explicit literals are the only possible clipboard output, including for corrupted stored enum values.
    public static string Format(DiagnosticCode code) => code switch {
        DiagnosticCode.InvalidInput => "S01", DiagnosticCode.PathOverlap => "S02",
        DiagnosticCode.RecordLocation => "S03", DiagnosticCode.AccessDenied => "S04",
        DiagnosticCode.PathUnavailable => "S05", DiagnosticCode.Network => "S06",
        DiagnosticCode.FileInUse => "S07", DiagnosticCode.InsufficientSpace => "S08",
        DiagnosticCode.UnsafeLink => "S09", DiagnosticCode.SourceChanged => "S10",
        DiagnosticCode.CopyFailed => "S11", DiagnosticCode.VerificationIncomplete => "S12",
        DiagnosticCode.DestinationConflict => "S13", DiagnosticCode.JobRecord => "S14",
        DiagnosticCode.Cancelled => "S15", DiagnosticCode.Io => "S16", _ => "S99"
    };

    public static string Description(DiagnosticCode code) => code switch {
        DiagnosticCode.InvalidInput => "경로·폴더 이름·작업 옵션을 확인하세요.",
        DiagnosticCode.PathOverlap => "원본끼리 또는 원본과 목적지가 겹칩니다.",
        DiagnosticCode.RecordLocation => "기록 위치를 모든 원본과 목적지 밖으로 변경하세요.",
        DiagnosticCode.AccessDenied => "접근 권한 또는 인증을 확인하세요.",
        DiagnosticCode.PathUnavailable => "경로·드라이브가 존재하고 연결되어 있는지 확인하세요.",
        DiagnosticCode.Network => "네트워크 공유 연결 상태를 확인하세요.",
        DiagnosticCode.FileInUse => "파일·폴더를 사용하는 프로그램이 있는지 확인하세요.",
        DiagnosticCode.InsufficientSpace => "저장 공간이 부족합니다.",
        DiagnosticCode.UnsafeLink => "링크 또는 안전하게 확인할 수 없는 파일 시스템 항목입니다.",
        DiagnosticCode.SourceChanged => "작업 중 원본 변경이 감지되었습니다.",
        DiagnosticCode.CopyFailed => "복사 도구가 파일 전송을 완료하지 못했습니다.",
        DiagnosticCode.VerificationIncomplete => "복사본 일치 여부를 모두 검증하지 못했습니다.",
        DiagnosticCode.DestinationConflict => "기존 목적지 항목과 충돌하여 복사하지 못했습니다.",
        DiagnosticCode.JobRecord => "작업 기록을 읽거나 저장할 수 없거나 재개 조건이 다릅니다.",
        DiagnosticCode.Cancelled => "작업이 중지되어 전송·검증이 완료되지 않았습니다.",
        DiagnosticCode.Io => "파일 시스템 입출력 작업을 완료하지 못했습니다.",
        _ => "분류되지 않은 오류로 작업을 완료하지 못했습니다."
    };

    // Retain the original exception type and local detail for existing safety callers.
    public static T Tag<T>(T exception, DiagnosticCode code) where T : Exception
    {
        exception.Data[TagKey] = Normalize(code);
        return exception;
    }

    public static DiagnosticCode Prefer(DiagnosticCode? current, DiagnosticCode candidate)
    {
        candidate = Normalize(candidate);
        if (current is null) return candidate;
        var previous = Normalize(current.Value);
        return Array.IndexOf(Priority, previous) <= Array.IndexOf(Priority, candidate) ? previous : candidate;
    }

    public static DiagnosticCode FromException(Exception exception)
    {
        var fallback = DiagnosticCode.Unknown;
        int depth = 0;
        // Bounded traversal: messages, stack traces, paths and unrelated Data are never inspected.
        for (Exception? current = exception; current is not null; current = current.InnerException) {
            if (current.Data[TagKey] is DiagnosticCode tagged) return Normalize(tagged);
            if (current is OperationCanceledException) return DiagnosticCode.Cancelled;
            if (current is UnauthorizedAccessException) return DiagnosticCode.AccessDenied;
            if (current is FileNotFoundException or DirectoryNotFoundException or DriveNotFoundException) return DiagnosticCode.PathUnavailable;
            if (current is ArgumentException or FormatException or PathTooLongException) return DiagnosticCode.InvalidInput;
            if (current is DbException or JsonException) return DiagnosticCode.JobRecord;
            if (current is Win32Exception native) return FromWindowsError(native.NativeErrorCode);
            if (current is IOException) {
                if ((current.HResult & 0xffff0000L) == 0x80070000L) return FromWindowsError(current.HResult & 0xffff);
                fallback = DiagnosticCode.Io;
            }
            if (++depth == 16) break;
        }
        return fallback;
    }

    private static DiagnosticCode FromWindowsError(int code) => code switch {
        5 or 65 or 86 or 1314 or 1326 or 1385 or 1909 => DiagnosticCode.AccessDenied,
        2 or 3 or 15 or 21 => DiagnosticCode.PathUnavailable,
        53 or 54 or 59 or 64 or 67 or 1201 or 1222 or 1231 or 1232 or 1236 or 2250 => DiagnosticCode.Network,
        // ERROR_SEM_TIMEOUT does not establish a network cause (WinError.h / Microsoft system error codes).
        121 => DiagnosticCode.Io,
        32 or 33 => DiagnosticCode.FileInUse,
        39 or 112 => DiagnosticCode.InsufficientSpace,
        80 or 183 => DiagnosticCode.DestinationConflict,
        123 or 206 => DiagnosticCode.InvalidInput,
        _ => DiagnosticCode.Io
    };
}
