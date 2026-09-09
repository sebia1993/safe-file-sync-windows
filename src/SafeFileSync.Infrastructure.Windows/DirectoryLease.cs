using Microsoft.Win32.SafeHandles;
using SafeFileSync.Core;
namespace SafeFileSync.Infrastructure.Windows;

/// <summary>Pins each existing ancestor against rename/deletion; rejects reparse points.</summary>
public sealed class DirectoryLease : IDisposable
{
    private readonly List<SafeFileHandle> handles = [];
    public IReadOnlyList<string> Identities { get; private set; } = [];
    public string FinalPath { get; private set; } = "";
    public static DirectoryLease Acquire(string path)
    {
        path = PathSafetyService.Normalize(path);
        var lease = new DirectoryLease();
        try {
            var chain = new Stack<string>();
            for (string? current = path; current is not null; current = Directory.GetParent(current)?.FullName)
                chain.Push(current);
            var ids = new List<string>();
            foreach (var part in chain) {
                var handle = NativeFiles.OpenDirectory(part);
                lease.handles.Add(handle);
                ids.Add(NativeFiles.Info(handle).Identity);
            }
            lease.Identities = ids;
            lease.FinalPath = PathSafetyService.Normalize(NativeFiles.FinalPath(lease.handles[^1]));
            return lease;
        } catch { lease.Dispose(); throw; }
    }
    public void Dispose() { foreach (var handle in handles.AsEnumerable().Reverse()) handle.Dispose(); handles.Clear(); }
}

public sealed class RootSafetyLease : IDisposable
{
    private readonly DirectoryLease sourceLease, destinationLease;
    public string Source { get; }
    public string Destination { get; }
    private RootSafetyLease(string source, string destination, DirectoryLease src, DirectoryLease dst)
        => (Source, Destination, sourceLease, destinationLease) = (source, destination, src, dst);
    public static RootSafetyLease Acquire(string source, string destination)
    {
        PathSafetyService.ValidatePair(source, destination);
        var src = DirectoryLease.Acquire(source);
        try {
            var dst = DirectoryLease.Acquire(destination);
            try {
                PathSafetyService.ValidatePair(src.FinalPath, dst.FinalPath);
                if (dst.Identities.Contains(src.Identities[^1]) || src.Identities.Contains(dst.Identities[^1]))
                    throw new IOException("원본과 목적지가 물리적으로 겹칩니다.");
                return new(src.FinalPath, dst.FinalPath, src, dst);
            } catch { dst.Dispose(); throw; }
        } catch { src.Dispose(); throw; }
    }
    public void Dispose() { destinationLease.Dispose(); sourceLease.Dispose(); }
}
