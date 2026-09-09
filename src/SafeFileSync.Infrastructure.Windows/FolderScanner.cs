using System.ComponentModel;
using System.Security.Cryptography;
using SafeFileSync.Core;
namespace SafeFileSync.Infrastructure.Windows;
public sealed class FolderScanner
{
    public IEnumerable<ScanEntry> Scan(string root, VerificationMode mode, CancellationToken token = default, string? internalExcludedDirectory = null)
    {
        root = PathSafetyService.Normalize(root);
        var pending = new Stack<string>(); pending.Push(root);
        while (pending.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            var directory = pending.Pop();
            DirectoryLease? lease = null;
            ScanEntry? error = null;
            try { lease = DirectoryLease.Acquire(directory); }
            catch (Exception ex) when (IsScanError(ex)) { error = new(Relative(root, directory), EntryKind.Error, Detail: ex.Message); }
            if (error is not null) { yield return error; continue; }
            using (lease)
            {
                IEnumerator<string>? iterator = null;
                try { iterator = Directory.EnumerateFileSystemEntries(NativeFiles.Extended(directory)).GetEnumerator(); }
                catch (Exception ex) when (IsScanError(ex)) { error = new(Relative(root, directory), EntryKind.Error, Detail: ex.Message); }
                if (error is not null) { yield return error; continue; }
                using (iterator)
                {
                    while (true)
                    {
                        token.ThrowIfCancellationRequested();
                        string? path = null;
                        try { if (iterator!.MoveNext()) path = RemoveExtended(iterator.Current); }
                        catch (Exception ex) when (IsScanError(ex)) { error = new(Relative(root, directory), EntryKind.Error, Detail: ex.Message); }
                        if (error is not null) { yield return error; break; }
                        if (path is null) break;
                        if (internalExcludedDirectory is not null && Relative(root,path).Equals(internalExcludedDirectory,StringComparison.OrdinalIgnoreCase)) continue;
                        var entry = ReadEntry(root, path, mode, token);
                        yield return entry;
                        if (entry.Kind == EntryKind.Directory) pending.Push(path);
                    }
                }
            }
        }
    }
    private static ScanEntry ReadEntry(string root, string path, VerificationMode mode, CancellationToken token)
    {
        var relative = Relative(root, path);
        try {
            PathSafetyService.Normalize(path);
            var attributes = File.GetAttributes(NativeFiles.Extended(path));
            if (attributes.HasFlag(FileAttributes.ReparsePoint)) return new(relative, EntryKind.Excluded, Detail: "심볼릭 링크/Junction/Reparse point 제외");
            if (attributes.HasFlag(FileAttributes.Directory)) {
                using var lease = DirectoryLease.Acquire(path);
                return new(relative, EntryKind.Directory, Identity: lease.Identities[^1]);
            }
            using var stream = NativeFiles.OpenRead(path);
            var before = NativeFiles.Info(stream.SafeFileHandle);
            string? hash = null;
            if (mode == VerificationMode.Sha256) hash = Hash(stream, token);
            var after = NativeFiles.Info(stream.SafeFileHandle);
            if (before.Length != after.Length || before.WriteTicks != after.WriteTicks || before.Identity != after.Identity)
                return new(relative, EntryKind.Error, Detail: "검사 중 파일 변경 감지");
            return new(relative, EntryKind.File, after.Length, after.WriteTicks, hash, Identity: after.Identity, Links: after.Links);
        } catch (Exception ex) when (IsScanError(ex)) { return new(relative, EntryKind.Error, Detail: ex.Message); }
    }
    internal static string Hash(Stream stream, CancellationToken token)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(1024 * 1024);
        try {
            int count;
            while ((count = stream.Read(buffer)) != 0) { token.ThrowIfCancellationRequested(); hash.AppendData(buffer, 0, count); }
            token.ThrowIfCancellationRequested(); return Convert.ToHexString(hash.GetHashAndReset());
        } finally { System.Buffers.ArrayPool<byte>.Shared.Return(buffer); }
    }
    internal static bool IsScanError(Exception ex) => ex is IOException or UnauthorizedAccessException or Win32Exception or ArgumentException;
    private static string Relative(string root, string path) => path.Equals(root, StringComparison.OrdinalIgnoreCase) ? "." : Path.GetRelativePath(root, path);
    private static string RemoveExtended(string path) => path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase) ? @"\\" + path[8..] : path.StartsWith(@"\\?\") ? path[4..] : path;
}
