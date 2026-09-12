using SafeFileSync.Core;
using SafeFileSync.Infrastructure.Windows;
using Xunit;

namespace SafeFileSync.Tests;

public sealed class DiagnosticIntegrationTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "SafeFileSync-diagnostics-" + Guid.NewGuid().ToString("N"));
    private string Source => Path.Combine(root, "source");
    private string Destination => Path.Combine(root, "destination");
    private string Storage => Path.Combine(root, "records");
    private string Original => Path.Combine(Source, "payload.txt");

    public DiagnosticIntegrationTests()
    {
        foreach (string path in new[] { Source, Destination, Storage }) Directory.CreateDirectory(path);
        File.WriteAllText(Original, "read-only source fixture");
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }

    [Fact]
    public async Task StorageOverlapHasActionableCodeBeforeCreatingRecordsOrDestinationFiles()
    {
        string storageParent = Path.Combine(Source, "AppData", "Local");
        Directory.CreateDirectory(storageParent);
        long time = File.GetLastWriteTimeUtc(Original).Ticks;
        var error = await Assert.ThrowsAsync<IOException>(() => new TransferCoordinator(storageParent).RunAsync(Source, Destination, VerificationMode.Sha256, ConflictPolicy.Preserve, true));
        Assert.Equal(DiagnosticCode.RecordLocation, DiagnosticCodes.FromException(error));
        Assert.Equal("S03", DiagnosticCodes.Format(DiagnosticCodes.FromException(error)));
        Assert.False(Directory.Exists(Path.Combine(storageParent, "SafeFileSync")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Destination));
        Assert.Equal("read-only source fixture", File.ReadAllText(Original));
        Assert.Equal(time, File.GetLastWriteTimeUtc(Original).Ticks);
    }

    [Fact]
    public async Task SourceDestinationOverlapHasSafetyCodeAndCannotCreateRecords()
    {
        var error = await Assert.ThrowsAsync<ArgumentException>(() => new TransferCoordinator(Storage).RunAsync(Source, Source, VerificationMode.Sha256, ConflictPolicy.Preserve, true));
        Assert.Equal(DiagnosticCode.PathOverlap, DiagnosticCodes.FromException(error));
        Assert.False(Directory.Exists(Path.Combine(Storage, "SafeFileSync")));
        Assert.Equal("read-only source fixture", File.ReadAllText(Original));
        Assert.Single(Directory.EnumerateFileSystemEntries(Source));
    }

    [Fact]
    public async Task NativeSharingViolationSurvivesScannerAndPreflightPropagation()
    {
        using (var locked = new FileStream(Original, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var entry = Assert.Single(new FolderScanner().Scan(Source, VerificationMode.Sha256));
            Assert.Equal(EntryKind.Error, entry.Kind);
            Assert.Equal(DiagnosticCode.FileInUse, entry.ErrorCode);
            var error = await Assert.ThrowsAsync<IOException>(() => new TransferCoordinator(Storage).RunAsync(Source, Destination, VerificationMode.Sha256, ConflictPolicy.Preserve, true));
            Assert.Equal(DiagnosticCode.FileInUse, DiagnosticCodes.FromException(error));
            Assert.Empty(Directory.EnumerateFileSystemEntries(Destination));
        }
        Assert.Equal("read-only source fixture", File.ReadAllText(Original));
    }

    [Fact]
    public async Task PreservedDestinationConflictHasCodeAndNextSuccessfulJobHasNoStaleCode()
    {
        string target = Path.Combine(Destination, "payload.txt");
        File.WriteAllText(target, "preserve existing destination");
        var coordinator = new TransferCoordinator(Storage);
        var conflict = await coordinator.RunAsync(Source, Destination, VerificationMode.Sha256, ConflictPolicy.Preserve, true);
        Assert.Equal("NeedsAttention", conflict.Info.Status);
        Assert.Equal(DiagnosticCode.DestinationConflict, conflict.ErrorCode);
        Assert.Equal("preserve existing destination", File.ReadAllText(target));
        Assert.Equal("read-only source fixture", File.ReadAllText(Original));

        File.Delete(target);
        var success = await coordinator.RunAsync(Source, Destination, VerificationMode.Sha256, ConflictPolicy.Preserve, true);
        Assert.Equal("Completed", success.Info.Status);
        Assert.Null(success.ErrorCode);
        Assert.Equal(100d, success.HashPercent);
        Assert.Equal("read-only source fixture", File.ReadAllText(target));
    }

    [Fact]
    public async Task MissingFinalDestinationIsVerificationIncompleteWithoutClaimingSourceChange()
    {
        string target = Path.Combine(Destination, "payload.txt");
        bool removed = false;
        var progress = new ImmediateProgress(p =>
        {
            if (p.Phase == "스캔: destination-after" && !removed)
            {
                Assert.True(File.Exists(target));
                File.Delete(target);
                removed = true;
            }
        });
        var result = await new TransferCoordinator(Storage).RunAsync(Source, Destination, VerificationMode.Sha256, ConflictPolicy.Preserve, true, progress);
        Assert.True(removed);
        Assert.Equal("NeedsAttention", result.Info.Status);
        Assert.Equal(DiagnosticCode.VerificationIncomplete, result.ErrorCode);
        Assert.Contains("PASS", result.SourceCheck);
        Assert.Equal(1, result.Summary.Missing);
        Assert.Equal("read-only source fixture", File.ReadAllText(Original));
    }

    [Fact]
    public async Task ActualSourceMutationHasSourceChangedCode()
    {
        bool changed = false;
        var progress = new ImmediateProgress(p =>
        {
            if (p.Phase == "스캔: source-after" && !changed)
            {
                File.WriteAllText(Original, "external fixture mutation after copy");
                changed = true;
            }
        });
        var result = await new TransferCoordinator(Storage).RunAsync(Source, Destination, VerificationMode.Sha256, ConflictPolicy.Preserve, true, progress);
        Assert.True(changed);
        Assert.Equal("NeedsAttention", result.Info.Status);
        Assert.Equal(DiagnosticCode.SourceChanged, result.ErrorCode);
        Assert.DoesNotContain("PASS", result.SourceCheck);
        Assert.Equal("read-only source fixture", File.ReadAllText(Path.Combine(Destination, "payload.txt")));
    }

    [Fact]
    public async Task FinalSourceScanFailureRetainsItsCauseInsteadOfClaimingSourceChanged()
    {
        FileStream? locked = null;
        var progress = new ImmediateProgress(p =>
        {
            if (p.Phase == "스캔: source-after" && locked is null)
                locked = new FileStream(Original, FileMode.Open, FileAccess.Read, FileShare.None);
        });
        try
        {
            var result = await new TransferCoordinator(Storage).RunAsync(Source, Destination, VerificationMode.Sha256, ConflictPolicy.Preserve, true, progress);
            Assert.NotNull(locked);
            Assert.Equal("NeedsAttention", result.Info.Status);
            Assert.Equal(DiagnosticCode.FileInUse, result.ErrorCode);
            Assert.True(result.Summary.Unverified > 0);
            Assert.Null(result.HashPercent);
        }
        finally { locked?.Dispose(); }
        Assert.Equal("read-only source fixture", File.ReadAllText(Original));
        Assert.Equal("read-only source fixture", File.ReadAllText(Path.Combine(Destination, "payload.txt")));
    }

    [Fact]
    public async Task ObservedSourceChangeIsNotHiddenByAnUnrelatedFinalScanFailure()
    {
        string other = Path.Combine(Source, "other.txt");
        File.WriteAllText(other, "unchanged but temporarily locked");
        FileStream? locked = null;
        var progress = new ImmediateProgress(p =>
        {
            if (p.Phase == "스캔: source-after" && locked is null)
            {
                File.WriteAllText(Original, "observed external mutation");
                locked = new FileStream(other, FileMode.Open, FileAccess.Read, FileShare.None);
            }
        });
        try
        {
            var result = await new TransferCoordinator(Storage).RunAsync(Source, Destination, VerificationMode.Sha256, ConflictPolicy.Preserve, true, progress);
            Assert.NotNull(locked);
            Assert.Equal("NeedsAttention", result.Info.Status);
            Assert.Equal(DiagnosticCode.SourceChanged, result.ErrorCode);
            Assert.True(result.Summary.Unverified > 0);
            Assert.True(result.Summary.Different > 0);
            Assert.DoesNotContain("PASS", result.SourceCheck);
        }
        finally { locked?.Dispose(); }
        Assert.Equal("observed external mutation", File.ReadAllText(Original));
        Assert.Equal("unchanged but temporarily locked", File.ReadAllText(other));
    }

    [Fact]
    public async Task CompletedQuickCopyWithDestinationExtrasHasNoFailureCode()
    {
        File.WriteAllText(Path.Combine(Destination, "extra.txt"), "preserved extra");
        var result = await new TransferCoordinator(Storage).RunAsync(Source, Destination, VerificationMode.Quick, ConflictPolicy.Preserve, true);
        Assert.Equal("Completed", result.Info.Status);
        Assert.Null(result.ErrorCode);
        Assert.Null(result.HashPercent);
        Assert.Equal(1, result.Summary.Extra);
        Assert.Equal("preserved extra", File.ReadAllText(Path.Combine(Destination, "extra.txt")));
        Assert.Equal("read-only source fixture", File.ReadAllText(Original));
    }

    [Fact]
    public async Task OrdinaryCompareDifferencesDoNotBecomeFailureCodes()
    {
        var result = await new TransferCoordinator(Storage).RunAsync(Source, Destination, VerificationMode.Sha256, ConflictPolicy.Preserve, false);
        Assert.Equal("Compared", result.Info.Status);
        Assert.Equal(1, result.Summary.Missing);
        Assert.Null(result.ErrorCode);
        Assert.Empty(Directory.EnumerateFileSystemEntries(Destination));
    }

    private sealed class ImmediateProgress(Action<TransferProgress> callback) : IProgress<TransferProgress>
    {
        public void Report(TransferProgress value) => callback(value);
    }
}
