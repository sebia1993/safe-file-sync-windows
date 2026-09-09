using System.Text.Json;
using SafeFileSync.Core;
namespace SafeFileSync.Infrastructure.Windows;

public sealed record JobView(JobInfo Info, ComparisonSummary Summary, IReadOnlyList<Difference> Rows, string SourceCheck, string? ReportPath);
public sealed class TransferCoordinator
{
    private readonly string storageParent;
    public TransferCoordinator(string? storageParent = null) => this.storageParent = storageParent ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    public async Task<JobView> RunAsync(string source, string destination, VerificationMode mode, ConflictPolicy conflicts,
        bool copy, IProgress<TransferProgress>? progress = null, CancellationToken token = default, string? resumeId = null)
    {
        if (!Enum.IsDefined(mode) || !Enum.IsDefined(conflicts)) throw new ArgumentException("지원하지 않는 작업 옵션");
        var id = resumeId is null ? Guid.NewGuid().ToString("N") : Guid.ParseExact(resumeId, "N").ToString("N");
        using var workspace = new TransferWorkspace(source, destination, storageParent);
        string databasePath = Path.Combine(workspace.StorageRoot, id + ".sqlite");
        if (resumeId is null) {
            using var fresh = new FileStream(databasePath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        } else {
            using var existing = NativeFiles.OpenRead(databasePath);
            if (NativeFiles.Info(existing.SafeFileHandle).Links != 1) throw new IOException("작업 DB 하드링크 차단");
        }
        using var db = new JobStore(databasePath);
        if (resumeId is not null) {
            var previous = db.Info;
            if (previous.Source != workspace.Source || previous.Destination != workspace.Destination || previous.Mode != mode || previous.Conflicts != conflicts)
                throw new IOException("작업 재개 시 원본·목적지·검증·교체 정책을 변경할 수 없습니다.");
        }
        db.Initialize(new(id, workspace.Source, workspace.Destination, mode, conflicts, "Scanning", databasePath, DateTime.UtcNow.ToString("O")));
        var scanner = new FolderScanner();
        string stageRelative = ".safefilesync-" + id;
        string sourceCheck = "검사 전";
        void Scan(string name, string root, bool destinationScan = false) {
            progress?.Report(new("스캔: " + name, root));
            // Only the exact staging directory owned by this job is excluded from destination comparison.
            var entries = scanner.Scan(root, mode, token).Where(e => !destinationScan || (e.RelativePath != stageRelative && !e.RelativePath.StartsWith(stageRelative + "\\", StringComparison.OrdinalIgnoreCase)));
            db.SaveSnapshot(name, entries, token, (n,b) => progress?.Report(new("스캔: " + name, root, n, CompletedBytes: b)));
        }
        try {
            Scan("source-before", workspace.Source);
            Scan("destination-before", workspace.Destination, true);
            if (!copy) { db.SetStatus("Compared"); return View(db, "source-before", "destination-before", "복사 전 비교", null); }
            db.SetStatus("Preflight");
            workspace.ProtectSourceTree(db.Entries("source-before"), token);
            workspace.CheckDestinationTree(db.Entries("destination-before"), token);
            // Reject a source collision with the internal staging namespace.
            if (db.Entries("source-before").Any(e => e.RelativePath.Split('\\')[0].Equals(stageRelative, StringComparison.OrdinalIgnoreCase)))
                throw new IOException("원본 이름과 작업 임시 영역이 충돌합니다.");
            var totals = db.Entries("source-before").Where(e => e.Kind == EntryKind.File).Aggregate((Files: 0L, Bytes: 0L), (v,e) => (v.Files + 1, checked(v.Bytes + e.Length)));
            // Conservative reserve: complete source payload plus 16 MiB. Existing destination files remain until commit.
            workspace.CheckSpace(checked(totals.Bytes + 16 * 1024 * 1024));
            workspace.EnsureDestinationDirectory(stageRelative);
            long done = 0, bytes = 0, failures = 0;
            var engine = new RobocopyProcess();
            db.SetStatus("Copying");
            foreach (var difference in db.Compare("source-before", "destination-before", mode)) {
                token.ThrowIfCancellationRequested();
                var entry = difference.Source;
                if (entry is null) continue;
                if (entry.Kind == EntryKind.Directory) {
                    try { workspace.EnsureDestinationDirectory(entry.RelativePath); db.Outcome(entry.RelativePath, "Directory"); }
                    catch (Exception ex) when (FolderScanner.IsScanError(ex)) { failures++; db.Outcome(entry.RelativePath, "Failed", ex.Message); }
                    continue;
                }
                if (difference.Kind is DifferenceKind.QuickMatch or DifferenceKind.Verified) {
                    done++; bytes += entry.Length; db.Outcome(entry.RelativePath, "Skipped"); continue;
                }
                try {
                    if (difference.Destination is not null && conflicts == ConflictPolicy.Preserve)
                        throw new IOException("목적지 기존 항목 보존: 교체 정책을 선택해야 복사할 수 있습니다.");
                    string sourceFile = TransferWorkspace.Combine(workspace.Source, entry.RelativePath);
                    using var sourceStream = NativeFiles.OpenRead(sourceFile);
                    var before = NativeFiles.Info(sourceStream.SafeFileHandle);
                    if (before.Length != entry.Length || before.WriteTicks != entry.LastWriteUtcTicks || before.Identity != entry.Identity)
                        throw new IOException("복사 전 원본 변경 감지");
                    var sourceHash = FolderScanner.Hash(sourceStream, token);
                    if (entry.Hash is not null && entry.Hash != sourceHash) throw new IOException("복사 전 원본 내용 변경 감지");
                    string parent = Path.GetDirectoryName(entry.RelativePath) ?? "";
                    workspace.EnsureDestinationDirectory(parent);
                    string stageDirectory = workspace.EnsureDestinationDirectory(Path.Combine(stageRelative, parent));
                    string stageFile = Path.Combine(stageDirectory, Path.GetFileName(sourceFile));
                    if (File.Exists(stageFile)) {
                        using var staged = NativeFiles.OpenRead(stageFile);
                        if (NativeFiles.Info(staged.SafeFileHandle).Links != 1) throw new IOException("임시 파일 하드링크 차단");
                    }
                    db.Outcome(entry.RelativePath, "Copying");
                    var result = await engine.CopyFileAsync(sourceFile, stageDirectory,
                        percent => progress?.Report(new("복사", entry.RelativePath, done, totals.Files, bytes, totals.Bytes, $"현재 파일 {percent:F1}%")), token);
                    if (result.Failed) throw new IOException($"Robocopy 종료 코드 {result.ExitCode}: {result.Output}");
                    progress?.Report(new("임시 복사본 SHA-256 검증", entry.RelativePath, done, totals.Files, bytes, totals.Bytes));
                    using (var staged = NativeFiles.OpenRead(stageFile)) {
                        var stageInfo = NativeFiles.Info(staged.SafeFileHandle);
                        if (stageInfo.Links != 1 || stageInfo.Length != entry.Length || FolderScanner.Hash(staged, token) != sourceHash)
                            throw new IOException("임시 복사본 검증 실패: 최종 파일에 반영하지 않았습니다.");
                    }
                    var after = NativeFiles.Info(sourceStream.SafeFileHandle);
                    if (before.Length != after.Length || before.WriteTicks != after.WriteTicks) throw new IOException("복사 중 원본 변경 감지");
                    token.ThrowIfCancellationRequested();
                    workspace.Commit(stageFile, entry.RelativePath, conflicts);
                    db.Outcome(entry.RelativePath, "CopiedAndVerified", $"Robocopy {result.ExitCode}; SHA-256 {sourceHash}");
                    done++; bytes += entry.Length;
                    progress?.Report(new("복사", entry.RelativePath, done, totals.Files, bytes, totals.Bytes));
                } catch (Exception ex) when (FolderScanner.IsScanError(ex)) { failures++; db.Outcome(entry.RelativePath, "Failed", ex.Message); }
            }
            db.SetStatus("Verifying");
            Scan("source-after", workspace.Source); Scan("destination-after", workspace.Destination, true);
            var unchanged = db.Summary("source-before", "source-after", mode);
            // Hash equality alone must not hide metadata or identity changes in the original snapshot.
            bool changedMetadata = db.Compare("source-before", "source-after", mode).Any(d => d.Source is not null && d.Destination is not null &&
                (d.Source.LastWriteUtcTicks != d.Destination.LastWriteUtcTicks || d.Source.Identity != d.Destination.Identity));
            sourceCheck = unchanged.TreesMatch && !changedMetadata ? "PASS (관찰한 파일·폴더 범위)" : "원본 변경 또는 검사 오류";
            db.Set("sourceCheck", sourceCheck);
            var final = db.Summary("source-after", "destination-after", mode);
            db.SetStatus(failures == 0 && unchanged.TreesMatch && !changedMetadata && final.AllSourceEntriesMatch ? "Completed" : "NeedsAttention");
            string report = ReportWriter.Write(db, "source-after", "destination-after", workspace.StorageRoot);
            db.Set("report", report);
            return View(db, "source-after", "destination-after", sourceCheck, report);
        } catch (OperationCanceledException) { db.SetStatus("Cancelled"); throw; }
        catch { db.SetStatus("Failed"); throw; }
    }
    private static JobView View(JobStore db, string source, string destination, string sourceCheck, string? report) =>
        new(db.Info, db.Summary(source, destination, db.Info.Mode), db.Compare(source, destination, db.Info.Mode).Take(1000).ToArray(), sourceCheck, report);
    public IReadOnlyList<JobInfo> History()
    {
        string root = Path.Combine(storageParent, "SafeFileSync");
        if (!Directory.Exists(root)) return [];
        var list = new List<JobInfo>();
        foreach (var path in Directory.EnumerateFiles(root, "*.sqlite").OrderByDescending(File.GetLastWriteTimeUtc).Take(100)) {
            try { using var db = new JobStore(path, true); list.Add(db.Info); }
            catch (Exception ex) when (ex is IOException or Microsoft.Data.Sqlite.SqliteException or JsonException) { }
        }
        return list;
    }
}
