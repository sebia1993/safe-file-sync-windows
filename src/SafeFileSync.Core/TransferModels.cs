namespace SafeFileSync.Core;
public enum EntryKind { File, Directory, Excluded, Error }
public enum VerificationMode { Quick, Sha256 }
public enum DifferenceKind { QuickMatch, Verified, Missing, Extra, Different, TypeConflict, Unverified }
public enum ConflictPolicy { Preserve, ReplaceAfterVerification }
public sealed record ScanEntry(string RelativePath, EntryKind Kind, long Length = 0,
    long LastWriteUtcTicks = 0, string? Hash = null, string? Detail = null, string? Identity = null, uint Links = 1,
    DiagnosticCode? ErrorCode = null);
public sealed record Difference(string RelativePath, DifferenceKind Kind, ScanEntry? Source, ScanEntry? Destination);
public sealed record TransferProgress(string Phase, string RelativePath, long CompletedFiles = 0,
    long TotalFiles = 0, long CompletedBytes = 0, long TotalBytes = 0, string? Detail = null);
public sealed record TransferSource(string Path, string FolderName);
public sealed record JobInfo(string Id, string Source, string Destination, VerificationMode Mode,
    ConflictPolicy Conflicts, string Status, string DatabasePath, string StartedUtc,
    IReadOnlyList<TransferSource>? Sources = null);
public sealed record ComparisonSummary(long Matched, long Missing, long Different, long Extra, long Unverified)
{
    public long SourceEntries => Matched + Missing + Different + Unverified;
    public double? Percent => SourceEntries == 0 ? null : 100d * Matched / SourceEntries;
    public bool AllSourceEntriesMatch => Missing == 0 && Different == 0 && Unverified == 0;
    public bool TreesMatch => AllSourceEntriesMatch && Extra == 0;
}
public static class EntryComparer
{
    public static Difference Compare(ScanEntry? source, ScanEntry? destination, VerificationMode mode)
    {
        string path = source?.RelativePath ?? destination?.RelativePath ?? throw new ArgumentException("Empty comparison");
        DifferenceKind kind;
        if (source?.Kind is EntryKind.Error or EntryKind.Excluded || destination?.Kind is EntryKind.Error or EntryKind.Excluded)
            kind = DifferenceKind.Unverified;
        else if (source is null) kind = DifferenceKind.Extra;
        else if (destination is null) kind = DifferenceKind.Missing;
        else if (source.Kind != destination.Kind) kind = DifferenceKind.TypeConflict;
        else if (source.Kind == EntryKind.Directory) kind = DifferenceKind.Verified;
        else if (source.Length != destination.Length) kind = DifferenceKind.Different;
        else if (mode == VerificationMode.Sha256)
            kind = source.Hash is null || destination.Hash is null ? DifferenceKind.Unverified
                : source.Hash == destination.Hash ? DifferenceKind.Verified : DifferenceKind.Different;
        else kind = source.LastWriteUtcTicks == destination.LastWriteUtcTicks
            ? DifferenceKind.QuickMatch : DifferenceKind.Different;
        return new(path, kind, source, destination);
    }
}
