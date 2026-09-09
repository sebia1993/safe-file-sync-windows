namespace SafeFileSync.Core;
public enum FileOperation { Read, Write, Delete, Move, Rename, Truncate, Create }
public sealed class SourceProtectionGuard
{
    private readonly string source;
    public SourceProtectionGuard(string sourceRoot) => source = PathSafetyService.Normalize(sourceRoot);
    public void Demand(string path, FileOperation operation)
    {
        if (!Enum.IsDefined(operation)) throw new ArgumentOutOfRangeException(nameof(operation));
        if (PathSafetyService.IsWithin(path, source) && operation != FileOperation.Read)
            throw new InvalidOperationException("원본 영역에는 읽기만 허용됩니다.");
    }
}

