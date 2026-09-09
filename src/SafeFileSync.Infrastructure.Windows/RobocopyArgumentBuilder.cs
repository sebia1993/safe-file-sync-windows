using SafeFileSync.Core;
namespace SafeFileSync.Infrastructure.Windows;
public static class RobocopyArgumentBuilder
{
    private static readonly string[] FolderOptions = ["/E", "/Z", "/R:3", "/W:2", "/COPY:DAT", "/DCOPY:DAT", "/XJ"];
    private static readonly string[] FileOptions = ["/MT:8", "/Z", "/R:3", "/W:2", "/COPY:DAT", "/XJ", "/IS", "/IT", "/BYTES", "/NJH", "/NJS"];
    public static IReadOnlyList<string> BuildPreview(string source, string destination)
    {
        PathSafetyService.ValidatePair(source, destination);
        return Array.AsReadOnly(new[] { PathSafetyService.Normalize(source), PathSafetyService.Normalize(destination) }.Concat(FolderOptions).ToArray());
    }
    public static IReadOnlyList<string> BuildFiles(IReadOnlyList<string> sourceFiles, string stagingDirectory)
    {
        if (sourceFiles.Count is < 1 or > 32) throw new ArgumentException("한 번에 1~32개의 파일이 필요합니다.");
        string parent = Path.GetDirectoryName(PathSafetyService.Normalize(sourceFiles[0]))!;
        stagingDirectory = PathSafetyService.Normalize(stagingDirectory);
        PathSafetyService.ValidatePair(parent,stagingDirectory);
        new SourceProtectionGuard(parent).Demand(stagingDirectory,FileOperation.Create);
        var args = new List<string> { parent,stagingDirectory };
        foreach (var file in sourceFiles) {
            string normalized = PathSafetyService.Normalize(file);
            if (!string.Equals(Path.GetDirectoryName(normalized),parent,StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("묶음 내 파일은 같은 원본 폴더에 있어야 합니다.");
            args.Add(Path.GetFileName(normalized));
        }
        ValidateOptions(FileOptions); args.AddRange(FileOptions);
        if (args.Sum(a => a.Length + 3) > 30000) throw new ArgumentException("Robocopy 명령줄 길이 제한을 초과합니다.");
        return args.AsReadOnly();
    }
    public static void ValidateOptions(IEnumerable<string> options)
    {
        foreach (var option in options)
            if (!FolderOptions.Contains(option,StringComparer.OrdinalIgnoreCase) && !FileOptions.Contains(option,StringComparer.OrdinalIgnoreCase))
                throw new ArgumentException("허용되지 않은 Robocopy 옵션입니다.");
    }
}
