using System.Text.RegularExpressions;
namespace SafeFileSync.Core;

/// <summary>Conservative lexical validation, not physical identity or authorization.</summary>
public static class PathSafetyService
{
    public static string Normalize(string input)
    {
        if (string.IsNullOrWhiteSpace(input) || input != input.Trim())
            throw new ArgumentException("경로를 입력하고 앞뒤 공백을 제거하세요.");
        var path = input.Replace('/', '\\');
        if (path.StartsWith(@"\\?\") || path.StartsWith(@"\\.\"))
            throw new ArgumentException("장치 및 확장 경로는 지원하지 않습니다.");
        bool unc = path.StartsWith(@"\\");
        if (!unc && !Regex.IsMatch(path, @"^[A-Za-z]:\\"))
            throw new ArgumentException("드라이브 절대 경로 또는 UNC 공유 경로가 필요합니다.");
        string tail = unc ? path[2..] : path[3..];
        var parts = tail.TrimEnd('\\').Split('\\');
        if (!unc && tail.Length == 0) return path[..3].ToUpperInvariant();
        if (unc && parts.Length < 2) throw new ArgumentException("UNC 서버와 공유 이름이 필요합니다.");
        foreach (var part in parts)
        {
            if (part.Length == 0 || part is "." or ".." || part.EndsWith('.') || part.EndsWith(' ')
                || part.Any(c => c < 32 || "<>:\"|?*".Contains(c))
                || Regex.IsMatch(part, @"^(CON|PRN|AUX|NUL|COM[1-9¹²³]|LPT[1-9¹²³])($|\.)", RegexOptions.IgnoreCase))
                throw new ArgumentException("모호하거나 지원하지 않는 경로 구성요소입니다.");
        }
        return unc ? @"\\" + string.Join('\\', parts) : char.ToUpperInvariant(path[0]) + @":\" + string.Join('\\', parts);
    }
    public static bool IsWithin(string candidate, string root)
    {
        candidate = Normalize(candidate); root = Normalize(root);
        return candidate.Equals(root, StringComparison.OrdinalIgnoreCase)
            || candidate.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);
    }
    public static void ValidatePair(string source, string destination)
    {
        if (IsWithin(destination, source) || IsWithin(source, destination))
            throw new ArgumentException("원본과 목적지는 같거나 서로 포함될 수 없습니다.");
    }
}

