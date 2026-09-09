using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using SafeFileSync.Core;
namespace SafeFileSync.Infrastructure.Windows;

public sealed record CopyResult(int ExitCode, string Output)
{
    public bool Failed => ExitCode < 0 || ExitCode >= 8;
}
public sealed class RobocopyProcess
{
    public Task<CopyResult> CopyFileAsync(string sourceFile, string stagingDirectory,
        Action<double>? progress, CancellationToken token) => CopyFilesAsync([sourceFile], stagingDirectory, progress, token);
    public async Task<CopyResult> CopyFilesAsync(IReadOnlyList<string> sourceFiles, string stagingDirectory,
        Action<double>? progress, CancellationToken token)
    {
        if (sourceFiles.Count is < 1 or > 32) throw new ArgumentException("한 번에 1~32개의 파일이 필요합니다.");
        string parent = Path.GetDirectoryName(sourceFiles[0])!;
        PathSafetyService.ValidatePair(parent, stagingDirectory);
        foreach (var file in sourceFiles) {
            PathSafetyService.Normalize(file);
            if (!string.Equals(Path.GetDirectoryName(file), parent, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("묶음 내 파일은 같은 원본 폴더에 있어야 합니다.");
        }
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "robocopy.exe")) {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add(parent); start.ArgumentList.Add(stagingDirectory);
        foreach (var file in sourceFiles) start.ArgumentList.Add(Path.GetFileName(file));
        foreach (var arg in new[] { "/MT:8", "/Z", "/R:3", "/W:2", "/COPY:DAT", "/XJ", "/IS", "/IT", "/BYTES", "/NJH", "/NJS" }) start.ArgumentList.Add(arg);
        using var process = new Process { StartInfo = start };
        token.ThrowIfCancellationRequested();
        if (!process.Start()) throw new IOException("Robocopy를 시작하지 못했습니다.");
        var output = new StringBuilder(); var gate = new object();
        async Task Drain(StreamReader reader)
        {
            var buffer = new char[2048]; int count;
            while ((count = await reader.ReadAsync(buffer)) > 0) {
                string chunk = new(buffer, 0, count);
                lock (gate) {
                    output.Append(chunk); if (output.Length > 8192) output.Remove(0, output.Length - 8192);
                }
                foreach (Match match in Regex.Matches(chunk, @"(\d+(?:[.,]\d+)?)%"))
                    if (double.TryParse(match.Groups[1].Value.Replace(',', '.'), System.Globalization.CultureInfo.InvariantCulture, out var value)) progress?.Invoke(Math.Clamp(value, 0, 100));
            }
        }
        var stdout = Drain(process.StandardOutput); var stderr = Drain(process.StandardError);
        using var registration = token.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } });
        try { await process.WaitForExitAsync(CancellationToken.None); await Task.WhenAll(stdout, stderr); }
        finally { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } }
        token.ThrowIfCancellationRequested();
        return new(process.ExitCode, output.ToString());
    }
}
