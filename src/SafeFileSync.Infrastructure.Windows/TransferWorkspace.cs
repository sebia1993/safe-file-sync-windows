using System.ComponentModel;
using SafeFileSync.Core;
namespace SafeFileSync.Infrastructure.Windows;

/// <summary>Owns all directory pins for a job and every destination mutation.</summary>
public sealed class TransferWorkspace : IDisposable
{
    private readonly List<RootSafetyLease> roots = [];
    private readonly List<DirectoryLease> pins = [];
    private readonly HashSet<string> sourceIdentities = [];
    private readonly HashSet<string> pinnedDestinations = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<SourceProtectionGuard> guards = [];
    private readonly IReadOnlyList<TransferSource> mappings;
    public string Source => roots[0].Source;
    public string Destination => roots[0].Destination;
    public IReadOnlyList<TransferSource>? Sources { get; }
    public string StorageRoot { get; }
    public TransferWorkspace(string source, string destination, string storageParent)
        : this([new TransferSource(source, "")], destination, storageParent, false) { }
    public TransferWorkspace(IReadOnlyList<TransferSource> sources, string destination, string storageParent)
        : this(sources, destination, storageParent, true) { }
    internal TransferWorkspace(IReadOnlyList<TransferSource> sources, string destination, string storageParent, bool named, JobInfo? previous = null)
    {
        if (sources.Count == 0) throw new ArgumentException("원본 폴더를 하나 이상 추가하세요.");
        var requested = sources.Select(s => new TransferSource(PathSafetyService.Normalize(s.Path), s.FolderName)).ToArray();
        if (named) {
            var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var source in requested) {
                if (string.IsNullOrWhiteSpace(source.FolderName) || source.FolderName.IndexOfAny(['\\','/']) >= 0
                    || source.FolderName.StartsWith(".safefilesync-",StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("목적지 폴더 이름은 경로 구분자 없는 일반 폴더 이름이어야 합니다.");
                PathSafetyService.Normalize(@"C:\" + source.FolderName);
                if (!aliases.Add(source.FolderName)) throw new ArgumentException("목적지 폴더 이름이 중복됩니다. 서로 다른 이름으로 변경하세요.");
            }
        }
        try {
            // Resolve and pin every source before creating even the storage directory.
            foreach (var source in requested) roots.Add(RootSafetyLease.Acquire(source.Path, destination));
            for (int i = 0; i < roots.Count; i++)
                for (int j = i + 1; j < roots.Count; j++) {
                    using var pair = RootSafetyLease.Acquire(roots[i].Source, roots[j].Source);
                }
            mappings = Array.AsReadOnly(requested.Select((s,i) => new TransferSource(roots[i].Source,s.FolderName)).ToArray());
            Sources = named ? mappings : null;
            foreach (var source in mappings) guards.Add(new(source.Path));
            if (previous is not null && (!SameSources(previous,Source,Sources)
                || !previous.Destination.Equals(Destination,StringComparison.OrdinalIgnoreCase)))
                throw DiagnosticCodes.Tag(new IOException("작업 재개 시 원본 목록·목적지 폴더 이름·목적지를 변경할 수 없습니다."), DiagnosticCode.JobRecord);

            var storageLease = DirectoryLease.Acquire(storageParent); pins.Add(storageLease);
            using var dst = DirectoryLease.Acquire(Destination);
            StorageRoot = Path.Combine(storageLease.FinalPath,"SafeFileSync");
            foreach (var source in mappings) {
                using var src = DirectoryLease.Acquire(source.Path);
                if (storageLease.Identities.Contains(src.Identities[^1]) || storageLease.Identities.Contains(dst.Identities[^1])
                    || PathSafetyService.IsWithin(StorageRoot,source.Path) || PathSafetyService.IsWithin(source.Path,StorageRoot)
                    || PathSafetyService.IsWithin(StorageRoot,Destination) || PathSafetyService.IsWithin(Destination,StorageRoot))
                    throw DiagnosticCodes.Tag(new IOException($"작업 기록 위치({StorageRoot})가 원본 또는 목적지 안에 있습니다. 화면의 기록 위치를 양쪽 폴더 밖의 쓰기 가능한 폴더로 변경하세요."), DiagnosticCode.RecordLocation);
                if (Directory.Exists(StorageRoot)) {
                    CheckStoragePair(source.Path, StorageRoot);
                }
            }
            if (Directory.Exists(StorageRoot)) {
                CheckStoragePair(Destination, StorageRoot);
            }
            Demand(StorageRoot,FileOperation.Create);
            Directory.CreateDirectory(NativeFiles.Extended(StorageRoot)); pins.Add(DirectoryLease.Acquire(StorageRoot));
        } catch { Dispose(); throw; }
    }
    private static void CheckStoragePair(string root, string storage)
    {
        try { using var pair = RootSafetyLease.Acquire(root, storage); }
        catch (Exception ex) when (DiagnosticCodes.FromException(ex) == DiagnosticCode.PathOverlap) {
            DiagnosticCodes.Tag(ex, DiagnosticCode.RecordLocation); throw;
        }
    }
    internal static bool SameSources(JobInfo previous, string source, IReadOnlyList<TransferSource>? sources)
    {
        if (previous.Sources is null || sources is null)
            return previous.Sources is null && sources is null && previous.Source.Equals(source,StringComparison.OrdinalIgnoreCase);
        return previous.Sources.Count == sources.Count && previous.Sources.All(old => sources.Any(current =>
            old.Path.Equals(current.Path,StringComparison.OrdinalIgnoreCase) && old.FolderName.Equals(current.FolderName,StringComparison.OrdinalIgnoreCase)));
    }
    private void Demand(string path, FileOperation operation) { foreach (var guard in guards) guard.Demand(path,operation); }
    public string PathForSource(string relative)
    {
        if (Sources is null) return Combine(Source,relative);
        foreach (var source in mappings) {
            if (relative.Equals(source.FolderName,StringComparison.OrdinalIgnoreCase)) return source.Path;
            if (relative.StartsWith(source.FolderName + "\\",StringComparison.OrdinalIgnoreCase))
                return Combine(source.Path,relative[(source.FolderName.Length + 1)..]);
        }
        throw new ArgumentException("원본 목록에 없는 상대 경로입니다.");
    }
    public IEnumerable<ScanEntry> ScanSources(FolderScanner scanner, VerificationMode mode, CancellationToken token)
    {
        foreach (var source in mappings) {
            token.ThrowIfCancellationRequested();
            if (Sources is not null) {
                using var root = DirectoryLease.Acquire(source.Path);
                yield return new(source.FolderName,EntryKind.Directory,Identity:root.Identities[^1]);
            }
            foreach (var entry in scanner.Scan(source.Path,mode,token))
                yield return Sources is null ? entry : entry with {
                    RelativePath = entry.RelativePath == "." ? source.FolderName : source.FolderName + "\\" + entry.RelativePath
                };
        }
    }
    public void ProtectSourceTree(IEnumerable<ScanEntry> entries, CancellationToken token)
    {
        foreach (var source in mappings) {
            var root = DirectoryLease.Acquire(source.Path); sourceIdentities.Add(root.Identities[^1]); pins.Add(root);
        }
        foreach (var entry in entries) {
            token.ThrowIfCancellationRequested();
            if (entry.Kind == EntryKind.Error) throw DiagnosticCodes.Tag(new IOException("원본에 검사 불가 항목이 있습니다: " + entry.RelativePath + " · " + entry.Detail), entry.ErrorCode ?? DiagnosticCode.VerificationIncomplete);
            if (entry.Kind == EntryKind.Directory) {
                var lease = DirectoryLease.Acquire(PathForSource(entry.RelativePath)); pins.Add(lease); sourceIdentities.Add(lease.Identities[^1]);
                if (entry.Identity != lease.Identities[^1]) throw DiagnosticCodes.Tag(new IOException("원본 폴더가 스캔 후 변경되었습니다."), DiagnosticCode.SourceChanged);
            }
        }
        CheckDestinationDirectory(Destination);
    }
    public void CheckDestinationTree(IEnumerable<ScanEntry> entries, CancellationToken token)
    {
        foreach (var entry in entries) {
            token.ThrowIfCancellationRequested();
            if (entry.Kind is EntryKind.Error or EntryKind.Excluded || entry.Links > 1)
                throw DiagnosticCodes.Tag(new IOException("목적지에 오류·링크 또는 하드링크가 있습니다: " + entry.RelativePath + " · " + entry.Detail), entry.ErrorCode ?? (entry.Kind == EntryKind.Error ? DiagnosticCode.VerificationIncomplete : DiagnosticCode.UnsafeLink));
            if (entry.Kind == EntryKind.Directory) CheckDestinationDirectory(Combine(Destination, entry.RelativePath));
        }
    }
    private void CheckDestinationDirectory(string path, bool revalidate = false)
    {
        if (pinnedDestinations.Contains(path) && !revalidate) return;
        var lease = DirectoryLease.Acquire(path);
        if (lease.Identities.Any(sourceIdentities.Contains)) { lease.Dispose(); throw DiagnosticCodes.Tag(new IOException("목적지 별칭이 원본 영역을 가리킵니다."), DiagnosticCode.PathOverlap); }
        if (pinnedDestinations.Contains(path)) lease.Dispose();
        else { pins.Add(lease); pinnedDestinations.Add(path); }
    }
    public string EnsureDestinationDirectory(string relative)
    {
        var full = Combine(Destination, relative);
        Demand(full, FileOperation.Create);
        var current = Destination;
        foreach (var segment in Path.GetRelativePath(Destination, full).Split(Path.DirectorySeparatorChar)) {
            if (segment == ".") continue;
            current = Path.Combine(current, segment);
            Directory.CreateDirectory(NativeFiles.Extended(current)); CheckDestinationDirectory(current);
        }
        return full;
    }
    public void TestDestinationWrite(string stagingRelative)
    {
        string stage = EnsureDestinationDirectory(stagingRelative);
        string probe = Path.Combine(stage,".write-probe-" + Guid.NewGuid().ToString("N"));
        Demand(probe,FileOperation.Create); bool owned = false;
        try {
            using var stream = new FileStream(NativeFiles.Extended(probe),FileMode.CreateNew,FileAccess.Write,FileShare.None);
            owned = true; stream.WriteByte(0); stream.Flush(true);
        } finally {
            if (owned) { Demand(probe,FileOperation.Delete); File.Delete(NativeFiles.Extended(probe)); }
        }
    }
    public void CheckSpace(long required)
    {
        if (!NativeFiles.GetDiskFreeSpaceExW(NativeFiles.Extended(Destination), out var available, out _, out _))
            throw new Win32Exception(System.Runtime.InteropServices.Marshal.GetLastWin32Error(), "목적지 여유 공간 확인 실패");
        if ((ulong)Math.Max(0, required) > available) throw DiagnosticCodes.Tag(new IOException("검증용 임시 복사본을 위한 목적지 공간이 부족합니다."), DiagnosticCode.InsufficientSpace);
    }
    public void Commit(string stagedFile, string relative, ConflictPolicy conflicts)
    {
        string target = Combine(Destination, relative);
        Demand(target, FileOperation.Write); Demand(stagedFile, FileOperation.Move);
        if (!PathSafetyService.IsWithin(stagedFile, Destination)) throw DiagnosticCodes.Tag(new IOException("임시 파일이 목적지 밖에 있습니다."), DiagnosticCode.PathOverlap);
        CheckDestinationDirectory(Path.GetDirectoryName(target)!, true);
        CheckDestinationDirectory(Path.GetDirectoryName(stagedFile)!, true);
        if (Directory.Exists(target)) throw DiagnosticCodes.Tag(new IOException("목적지에 동일한 이름의 폴더가 있습니다."), DiagnosticCode.DestinationConflict);
        if (File.Exists(target)) {
            if (conflicts != ConflictPolicy.ReplaceAfterVerification) throw DiagnosticCodes.Tag(new IOException("기존 목적지 파일 보존 정책으로 교체하지 않았습니다."), DiagnosticCode.DestinationConflict);
            using (var existing = NativeFiles.OpenRead(target)) {
                if (NativeFiles.Info(existing.SafeFileHandle).Links != 1) throw DiagnosticCodes.Tag(new IOException("목적지 하드링크 교체 차단"), DiagnosticCode.UnsafeLink);
            }
            // Atomic replacement changes the directory entry, never opens the old target for writing.
            File.Replace(NativeFiles.Extended(stagedFile), NativeFiles.Extended(target), null);
        } else File.Move(NativeFiles.Extended(stagedFile), NativeFiles.Extended(target), false);
    }
    internal static string Combine(string root, string relative)
    {
        if (Path.IsPathRooted(relative)) throw new ArgumentException("상대 경로가 필요합니다.");
        string result = PathSafetyService.Normalize(Path.Combine(root, relative));
        if (!PathSafetyService.IsWithin(result, root)) throw new ArgumentException("선택한 폴더를 벗어나는 경로입니다.");
        return result;
    }
    public void Dispose() { foreach (var pin in pins.AsEnumerable().Reverse()) pin.Dispose(); foreach (var root in roots.AsEnumerable().Reverse()) root.Dispose(); }
}
