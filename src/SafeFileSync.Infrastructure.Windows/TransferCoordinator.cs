using System.Text.Json;
using SafeFileSync.Core;
namespace SafeFileSync.Infrastructure.Windows;

public sealed record JobView(JobInfo Info, ComparisonSummary Summary, IReadOnlyList<Difference> Rows, string SourceCheck, string? ReportPath, double? TransferPercent, double? HashPercent);
public sealed class TransferCoordinator
{
    private readonly string storageParent;
    public TransferCoordinator(string? storageParent = null) => this.storageParent = storageParent ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    public async Task<JobView> RunAsync(string source, string destination, VerificationMode mode, ConflictPolicy conflicts,
        bool copy, IProgress<TransferProgress>? progress = null, CancellationToken token = default, string? resumeId = null, bool failedOnly = false)
    {
        if (failedOnly && resumeId is null) throw new ArgumentException("실패 항목 재시도는 기존 작업에서만 가능합니다.");
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
            if (!previous.Source.Equals(workspace.Source,StringComparison.OrdinalIgnoreCase) || !previous.Destination.Equals(workspace.Destination,StringComparison.OrdinalIgnoreCase) || previous.Mode != mode || previous.Conflicts != conflicts)
                throw new IOException("작업 재개 시 원본·목적지·검증·교체 정책을 변경할 수 없습니다.");
        }
        var retryPaths = failedOnly ? db.Outcomes().Where(o => o.Status is "Failed" or "Copying").Select(o => o.Path).ToHashSet(StringComparer.OrdinalIgnoreCase) : null;
        db.Initialize(new(id, workspace.Source, workspace.Destination, mode, conflicts, "Scanning", databasePath, DateTime.UtcNow.ToString("O")));
        var scanner = new FolderScanner();
        string stageRelative = ".safefilesync-" + id;
        string sourceCheck = "검사 전";
        void Scan(string name, string root, bool destinationScan = false) {
            progress?.Report(new("스캔: " + name, root));
            // Only the exact staging directory owned by this job is excluded from destination comparison.
            var entries = scanner.Scan(root, mode, token, destinationScan ? stageRelative : null);
            db.SaveSnapshot(name, entries, token, (n,b) => progress?.Report(new("스캔: " + name, root, n, CompletedBytes: b)));
        }
        try {
            Scan("source-before", workspace.Source);
            db.PreserveOriginalSnapshot("source-before","source-original");
            Scan("destination-before", workspace.Destination, true);
            if (!copy) { db.SetStatus("Compared"); return View(db, "source-before", "destination-before", "복사 전 비교", null); }
            db.SetStatus("Preflight");
            workspace.ProtectSourceTree(db.Entries("source-before"), token);
            workspace.CheckDestinationTree(db.Entries("destination-before"), token);
            // Reject a source collision with the internal staging namespace.
            if (db.Entries("source-before").Any(e => e.RelativePath.Split('\\')[0].Equals(stageRelative, StringComparison.OrdinalIgnoreCase)))
                throw new IOException("원본 이름과 작업 임시 영역이 충돌합니다.");
            var totals = db.Entries("source-before").Where(e => e.Kind == EntryKind.File).Aggregate((Files: 0L, Bytes: 0L), (v,e) => (v.Files + 1, checked(v.Bytes + e.Length)));
            // Reserve pending payload; already matched files and preserved conflicts need no staging space.
            var pending = db.Compare("source-before","destination-before",mode)
                .Where(d => d.Source?.Kind == EntryKind.File && d.Kind is not (DifferenceKind.QuickMatch or DifferenceKind.Verified)
                    && (d.Destination is null || conflicts == ConflictPolicy.ReplaceAfterVerification)
                    && (retryPaths is null || retryPaths.Contains(d.RelativePath)))
                .Aggregate((Bytes:0L,Files:0L),(sum,d) => (checked(sum.Bytes + d.Source!.Length),sum.Files+1));
            if (pending.Files > 0) {
                progress?.Report(new("목적지 공간·쓰기 권한 검사",workspace.Destination));
                workspace.CheckSpace(checked(pending.Bytes + 16 * 1024 * 1024));
                workspace.TestDestinationWrite(stageRelative);
            }
            long done = 0, bytes = 0, failures = 0;
            var engine = new RobocopyProcess();
            db.SetStatus("Copying");
            var batch = new List<ScanEntry>();
            string batchParent = "";
            async Task Flush()
            {
                if (batch.Count == 0) return;
                var prepared = new List<(ScanEntry Entry, FileStream Source, string Hash, string Stage, bool NeedsCopy)>();
                try {
                    foreach (var entry in batch) {
                        FileStream? stream = null;
                        try {
                            token.ThrowIfCancellationRequested();
                            string sourceFile = TransferWorkspace.Combine(workspace.Source, entry.RelativePath);
                            stream = NativeFiles.OpenRead(sourceFile);
                            var before = NativeFiles.Info(stream.SafeFileHandle);
                            if (before.Length != entry.Length || before.WriteTicks != entry.LastWriteUtcTicks || before.Identity != entry.Identity)
                                throw new IOException("복사 전 원본 변경 감지");
                            progress?.Report(new("원본 내용 확인",entry.RelativePath,done,totals.Files,bytes,totals.Bytes));
                            string hash = FolderScanner.Hash(stream, token);
                            if (entry.Hash is not null && entry.Hash != hash) throw new IOException("복사 전 원본 내용 변경 감지");
                            workspace.EnsureDestinationDirectory(batchParent);
                            string stageDirectory = workspace.EnsureDestinationDirectory(Path.Combine(stageRelative, batchParent));
                            string stageFile = Path.Combine(stageDirectory, Path.GetFileName(sourceFile));
                            bool needsCopy = true;
                            FileAttributes? stageAttributes = null;
                            try { stageAttributes = File.GetAttributes(NativeFiles.Extended(stageFile)); }
                            catch (FileNotFoundException) { }
                            catch (DirectoryNotFoundException) { }
                            if (stageAttributes is not null) {
                                if (stageAttributes.Value.HasFlag(FileAttributes.ReparsePoint) || stageAttributes.Value.HasFlag(FileAttributes.Directory))
                                    throw new IOException("임시 경로 링크/폴더 차단");
                                using var staged = NativeFiles.OpenRead(stageFile);
                                var stageInfo = NativeFiles.Info(staged.SafeFileHandle);
                                if (stageInfo.Links != 1) throw new IOException("임시 파일 하드링크 차단");
                                if (stageInfo.Length == entry.Length && stageInfo.WriteTicks == entry.LastWriteUtcTicks && FolderScanner.Hash(staged, token) == hash) needsCopy = false;
                            }
                            prepared.Add((entry, stream, hash, stageFile, needsCopy)); stream = null;
                            db.Outcome(entry.RelativePath, "Copying");
                        } catch (Exception ex) when (FolderScanner.IsScanError(ex)) { failures++; db.Outcome(entry.RelativePath, "Failed", ex.Message); }
                        finally { stream?.Dispose(); }
                    }
                    db.FlushOutcomes(); // Persist in-progress files before starting the child process.
                    var required = prepared.Where(p => p.NeedsCopy).ToArray();
                    CopyResult result = new(0, "검증된 임시 복사본 재사용");
                    if (required.Length > 0) {
                        result = await engine.CopyFilesAsync(required.Select(p => TransferWorkspace.Combine(workspace.Source,p.Entry.RelativePath)).ToArray(), Path.GetDirectoryName(required[0].Stage)!,
                            percent => progress?.Report(new("복사", batchParent + $" ({required.Length}개 묶음)", done, totals.Files, bytes, totals.Bytes, $"현재 Robocopy 파일 {percent:F1}%")), token);
                    }
                    foreach (var item in prepared) {
                        try {
                            token.ThrowIfCancellationRequested();
                            if (item.NeedsCopy && result.Failed) throw new IOException($"Robocopy 종료 코드 {result.ExitCode}: {result.Output}");
                            progress?.Report(new("임시 복사본 SHA-256 검증", item.Entry.RelativePath, done, totals.Files, bytes, totals.Bytes));
                            using (var staged = NativeFiles.OpenRead(item.Stage)) {
                                var info = NativeFiles.Info(staged.SafeFileHandle);
                                if (info.Links != 1 || info.Length != item.Entry.Length || FolderScanner.Hash(staged, token) != item.Hash)
                                    throw new IOException("임시 복사본 검증 실패: 최종 파일에 반영하지 않았습니다.");
                            }
                            var after = NativeFiles.Info(item.Source.SafeFileHandle);
                            if (after.Length != item.Entry.Length || after.WriteTicks != item.Entry.LastWriteUtcTicks)
                                throw new IOException("복사 중 원본 변경 감지");
                            token.ThrowIfCancellationRequested();
                            workspace.Commit(item.Stage, item.Entry.RelativePath, conflicts);
                            db.Outcome(item.Entry.RelativePath, "CopiedAndVerified", $"Robocopy {result.ExitCode}; SHA-256 {item.Hash}");
                            done++; bytes += item.Entry.Length;
                            progress?.Report(new("복사", item.Entry.RelativePath, done, totals.Files, bytes, totals.Bytes));
                        } catch (Exception ex) when (FolderScanner.IsScanError(ex)) { failures++; db.Outcome(item.Entry.RelativePath, "Failed", ex.Message); }
                    }
                } finally { foreach (var item in prepared) item.Source.Dispose(); batch.Clear(); db.FlushOutcomes(); }
            }
            foreach (var difference in db.Compare("source-before", "destination-before", mode)) {
                token.ThrowIfCancellationRequested();
                var entry = difference.Source;
                if (entry is null) continue;
                if (entry.Kind == EntryKind.Excluded) { failures++; db.Outcome(entry.RelativePath,"Excluded",entry.Detail ?? "링크 제외"); continue; }
                if (retryPaths is not null && !retryPaths.Contains(entry.RelativePath) && difference.Kind is not (DifferenceKind.QuickMatch or DifferenceKind.Verified)) continue;
                if (entry.Kind == EntryKind.Directory) {
                    await Flush();
                    try { workspace.EnsureDestinationDirectory(entry.RelativePath); db.Outcome(entry.RelativePath, "Directory"); }
                    catch (Exception ex) when (FolderScanner.IsScanError(ex)) { failures++; db.Outcome(entry.RelativePath, "Failed", ex.Message); }
                    continue;
                }
                if (difference.Kind is DifferenceKind.QuickMatch or DifferenceKind.Verified) {
                    done++; bytes += entry.Length; db.Outcome(entry.RelativePath, "Skipped"); continue;
                }
                if (difference.Destination is not null && conflicts == ConflictPolicy.Preserve) {
                    failures++; db.Outcome(entry.RelativePath, "Failed", "목적지 기존 항목 보존: 교체 정책이 선택되지 않았습니다."); continue;
                }
                string parent = Path.GetDirectoryName(entry.RelativePath) ?? "";
                if (batch.Count == 32 || batchParent != parent || batch.Sum(e => e.RelativePath.Length) + entry.RelativePath.Length > 20000) await Flush();
                batchParent = parent; batch.Add(entry);
            }
            await Flush();
            db.Set("transferPercent", (totals.Files == 0 ? 100d : 100d * done / totals.Files).ToString(System.Globalization.CultureInfo.InvariantCulture));
            db.SetStatus("Verifying");
            Scan("source-after", workspace.Source); Scan("destination-after", workspace.Destination, true);
            var unchanged = db.Summary("source-original", "source-after", mode);
            // Hash equality alone must not hide metadata or identity changes in the original snapshot.
            bool changedMetadata = db.Compare("source-original", "source-after", mode).Any(d => d.Source is not null && d.Destination is not null &&
                (d.Source.LastWriteUtcTicks != d.Destination.LastWriteUtcTicks || d.Source.Identity != d.Destination.Identity));
            sourceCheck = unchanged.TreesMatch && !changedMetadata ? "PASS (최초 스캔 이후 관찰 범위)" : "원본 변경 또는 검사 오류";
            db.Set("sourceCheck", sourceCheck);
            var final = db.Summary("source-after", "destination-after", mode);
            db.SetStatus(failures == 0 && unchanged.TreesMatch && !changedMetadata && final.AllSourceEntriesMatch ? "Completed" : "NeedsAttention");
            string report = ReportWriter.Write(db, "source-after", "destination-after", workspace.StorageRoot);
            db.Set("report", report);
            return View(db, "source-after", "destination-after", sourceCheck, report);
        } catch (OperationCanceledException) { db.SetStatus("Cancelled"); throw; }
        catch (Exception ex) { db.Set("error",ex.Message); db.SetStatus("Failed"); throw; }
    }
    private static JobView View(JobStore db, string source, string destination, string sourceCheck, string? report)
    {
        long files = 0, hashes = 0;
        foreach (var row in db.Compare(source,destination,db.Info.Mode)) {
            if (row.Source?.Kind == EntryKind.File) { files++; if (row.Kind == DifferenceKind.Verified) hashes++; }
        }
        double? transfer = double.TryParse(db.Get("transferPercent"),System.Globalization.CultureInfo.InvariantCulture,out var value) ? value : null;
        double? hashPercent = db.Info.Mode == VerificationMode.Sha256 && files > 0 && db.Summary(source,destination,db.Info.Mode).Unverified == 0 ? 100d * hashes / files : null;
        var rows = db.Compare(source,destination,db.Info.Mode).Where(d => d.Kind is not (DifferenceKind.QuickMatch or DifferenceKind.Verified))
            .Concat(db.Compare(source,destination,db.Info.Mode).Where(d => d.Kind is DifferenceKind.QuickMatch or DifferenceKind.Verified)).Take(1000).ToArray();
        return new(db.Info,db.Summary(source,destination,db.Info.Mode),rows,sourceCheck,report,transfer,hashPercent);
    }
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
