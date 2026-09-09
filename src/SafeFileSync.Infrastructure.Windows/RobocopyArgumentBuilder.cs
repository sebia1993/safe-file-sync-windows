using SafeFileSync.Core;
namespace SafeFileSync.Infrastructure.Windows;
/// <summary>Preview only. No execution API exists in Milestone 0.</summary>
public static class RobocopyArgumentBuilder
{
    private static readonly string[] Allowed = ["/E", "/Z", "/R:3", "/W:2", "/COPY:DAT", "/DCOPY:DAT", "/XJ"];
    public static IReadOnlyList<string> BuildPreview(string source, string destination)
    {
        PathSafetyService.ValidatePair(source, destination);
        return Array.AsReadOnly(new[] { PathSafetyService.Normalize(source), PathSafetyService.Normalize(destination) }.Concat(Allowed).ToArray());
    }
    public static void ValidateOptions(IEnumerable<string> options)
    {
        foreach (var option in options)
            if (!Allowed.Contains(option, StringComparer.OrdinalIgnoreCase))
                throw new ArgumentException("허용되지 않은 Robocopy 옵션입니다.");
    }
}

