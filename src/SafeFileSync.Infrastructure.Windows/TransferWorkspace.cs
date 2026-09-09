using System.ComponentModel;
using SafeFileSync.Core;
namespace SafeFileSync.Infrastructure.Windows;

/// <summary>Owns all directory pins for a job and every destination mutation.</summary>
public sealed class TransferWorkspace : IDisposable
{
    private readonly RootSafetyLease roots;
    private readonly List<DirectoryLease> pins = [];
    private readonly HashSet<string> sourceIdentities = [];
    private readonly HashSet<string> pinnedDestinations = new(StringComparer.OrdinalIgnoreCase);
    private readonly SourceProtectionGuard guard;
    public string Source => roots.Source;
    public string Destination => roots.Destination;
    public string StorageRoot { get; }
    public TransferWorkspace(string source, string destination, string storageParent)
    {
        roots = RootSafetyLease.Acquire(source, destination); guard = new(Source);
        try {
            // Storage is local and separated from both selected trees before any DB/log write.
            using var storagePair = RootSafetyLease.Acquire(Source, storageParent);
            PathSafetyService.ValidatePair(Destination, storagePair.Destination);
            pins.Add(DirectoryLease.Acquire(storagePair.Destination));
            StorageRoot = Path.Combine(storagePair.Destination, "SafeFileSync");
            guard.Demand(StorageRoot, FileOperation.Create);
            if (Directory.Exists(StorageRoot)) {
                using var storageRootPair = RootSafetyLease.Acquire(Source, StorageRoot);
            }
            Directory.CreateDirectory(NativeFiles.Extended(StorageRoot)); pins.Add(DirectoryLease.Acquire(StorageRoot));
        } catch { Dispose(); throw; }
    }
    public void ProtectSourceTree(IEnumerable<ScanEntry> entries, CancellationToken token)
    {
        var root = DirectoryLease.Acquire(Source); sourceIdentities.Add(root.Identities[^1]); pins.Add(root);
        foreach (var entry in entries) {
            token.ThrowIfCancellationRequested();
            if (entry.Kind is EntryKind.Error or EntryKind.Excluded) throw new IOException("원본에 검사 불가 항목이 있습니다: " + entry.RelativePath);
            if (entry.Kind == EntryKind.Directory) {
                var lease = DirectoryLease.Acquire(Combine(Source, entry.RelativePath)); pins.Add(lease); sourceIdentities.Add(lease.Identities[^1]);
                if (entry.Identity != lease.Identities[^1]) throw new IOException("원본 폴더가 스캔 후 변경되었습니다.");
            }
        }
        CheckDestinationDirectory(Destination);
    }
    public void CheckDestinationTree(IEnumerable<ScanEntry> entries, CancellationToken token)
    {
        foreach (var entry in entries) {
            token.ThrowIfCancellationRequested();
            if (entry.Kind is EntryKind.Error or EntryKind.Excluded || entry.Links > 1)
                throw new IOException("목적지에 오류·링크 또는 하드링크가 있습니다: " + entry.RelativePath);
            if (entry.Kind == EntryKind.Directory) CheckDestinationDirectory(Combine(Destination, entry.RelativePath));
        }
    }
    private void CheckDestinationDirectory(string path)
    {
        if (pinnedDestinations.Contains(path)) return;
        var lease = DirectoryLease.Acquire(path);
        if (lease.Identities.Any(sourceIdentities.Contains)) { lease.Dispose(); throw new IOException("목적지 별칭이 원본 영역을 가리킵니다."); }
        pins.Add(lease); pinnedDestinations.Add(path);
    }
    public string EnsureDestinationDirectory(string relative)
    {
        var full = Combine(Destination, relative);
        guard.Demand(full, FileOperation.Create);
        var current = Destination;
        foreach (var segment in Path.GetRelativePath(Destination, full).Split(Path.DirectorySeparatorChar)) {
            if (segment == ".") continue;
            current = Path.Combine(current, segment);
            Directory.CreateDirectory(NativeFiles.Extended(current)); CheckDestinationDirectory(current);
        }
        return full;
    }
    public void CheckSpace(long required)
    {
        if (!NativeFiles.GetDiskFreeSpaceExW(NativeFiles.Extended(Destination), out var available, out _, out _))
            throw new Win32Exception(System.Runtime.InteropServices.Marshal.GetLastWin32Error(), "목적지 여유 공간 확인 실패");
        if ((ulong)Math.Max(0, required) > available) throw new IOException("검증용 임시 복사본을 위한 목적지 공간이 부족합니다.");
    }
    public void Commit(string stagedFile, string relative, ConflictPolicy conflicts)
    {
        string target = Combine(Destination, relative);
        guard.Demand(target, FileOperation.Write); guard.Demand(stagedFile, FileOperation.Move);
        if (!PathSafetyService.IsWithin(stagedFile, Destination)) throw new IOException("임시 파일이 목적지 밖에 있습니다.");
        if (Directory.Exists(target)) throw new IOException("목적지에 동일한 이름의 폴더가 있습니다.");
        if (File.Exists(target)) {
            if (conflicts != ConflictPolicy.ReplaceAfterVerification) throw new IOException("기존 목적지 파일 보존 정책으로 교체하지 않았습니다.");
            using (var existing = NativeFiles.OpenRead(target)) {
                if (NativeFiles.Info(existing.SafeFileHandle).Links != 1) throw new IOException("목적지 하드링크 교체 차단");
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
    public void Dispose() { foreach (var pin in pins.AsEnumerable().Reverse()) pin.Dispose(); roots.Dispose(); }
}
