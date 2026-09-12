using System.Security.Cryptography;
using SafeFileSync.Core;
using SafeFileSync.Infrastructure.Windows;
using Xunit;

namespace SafeFileSync.Tests;

public sealed class MultiSourceTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "SafeFileSync-multiple-" + Guid.NewGuid().ToString("N"));
    private string SourceA => Path.Combine(root, "source-one");
    private string SourceB => Path.Combine(root, "source-two");
    private string Destination => Path.Combine(root, "destination");
    private string Storage => Path.Combine(root, "records");
    private TransferSource[] Sources => [new(SourceA, "A"), new(SourceB, "B")];

    public MultiSourceTests()
    {
        foreach (string path in new[] { SourceA, SourceB, Destination, Storage }) Directory.CreateDirectory(path);
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }

    private void Seed()
    {
        File.WriteAllText(Path.Combine(SourceA, "same.txt"), "first source original");
        File.WriteAllText(Path.Combine(SourceB, "same.txt"), "second source original");
        Directory.CreateDirectory(Path.Combine(SourceA, "nested", "empty"));
        Directory.CreateDirectory(Path.Combine(SourceB, "nested", "empty"));
        File.WriteAllText(Path.Combine(SourceB, "nested", "한글.txt"), "nested second source");
    }

    private static Dictionary<string, string> Snapshot(params string[] folders)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string folder in folders)
        {
            foreach (string directory in Directory.EnumerateDirectories(folder, "*", SearchOption.AllDirectories).Prepend(folder))
                result.Add(directory, "directory:" + Directory.GetCreationTimeUtc(directory).Ticks + ":" + Directory.GetLastWriteTimeUtc(directory).Ticks + ":" + File.GetAttributes(directory));
            foreach (string file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
            {
                using var stream = File.OpenRead(file);
                result.Add(file, Convert.ToHexString(SHA256.HashData(stream)) + ":" + File.GetCreationTimeUtc(file).Ticks + ":" + File.GetLastWriteTimeUtc(file).Ticks + ":" + File.GetAttributes(file));
            }
        }
        return result;
    }

    private static void AssertUnchanged(Dictionary<string, string> before, params string[] folders)
    {
        var after = Snapshot(folders);
        Assert.Equal(before.Count, after.Count);
        foreach (var item in before) Assert.Equal(item.Value, after[item.Key]);
    }

    private static void AssertCompleted(JobView result)
    {
        using var db = new JobStore(result.Info.DatabasePath, true);
        Assert.True(result.Info.Status == "Completed", result.SourceCheck + " | " + string.Join("; ", db.Outcomes().Select(o => o.Path + ": " + o.Detail)));
    }

    [Fact]
    public async Task IndependentSourcesKeepSameNamesSeparateAndAggregateVerification()
    {
        Seed();
        File.WriteAllText(Path.Combine(Destination, "outside.txt"), "preserved root extra");
        Directory.CreateDirectory(Path.Combine(Destination, "A"));
        File.WriteAllText(Path.Combine(Destination, "A", "extra.txt"), "preserved source-folder extra");
        var before = Snapshot(SourceA, SourceB);

        var coordinator = new TransferCoordinator(Storage);
        var result = await coordinator.RunAsync(Sources, Destination, VerificationMode.Sha256, ConflictPolicy.Preserve, true);

        AssertCompleted(result);
        Assert.Equal("first source original", File.ReadAllText(Path.Combine(Destination, "A", "same.txt")));
        Assert.Equal("second source original", File.ReadAllText(Path.Combine(Destination, "B", "same.txt")));
        Assert.Equal("nested second source", File.ReadAllText(Path.Combine(Destination, "B", "nested", "한글.txt")));
        Assert.True(Directory.Exists(Path.Combine(Destination, "A", "nested", "empty")));
        Assert.True(Directory.Exists(Path.Combine(Destination, "B", "nested", "empty")));
        Assert.Equal("preserved root extra", File.ReadAllText(Path.Combine(Destination, "outside.txt")));
        Assert.Equal("preserved source-folder extra", File.ReadAllText(Path.Combine(Destination, "A", "extra.txt")));
        Assert.Equal(2, result.Summary.Extra);
        Assert.True(result.Summary.AllSourceEntriesMatch);
        Assert.False(result.Summary.TreesMatch);
        Assert.Equal(100d, result.TransferPercent);
        Assert.Equal(100d, result.HashPercent);
        Assert.Contains("PASS", result.SourceCheck);
        AssertUnchanged(before, SourceA, SourceB);

        using var db = new JobStore(result.Info.DatabasePath, true);
        var entries = db.Entries("source-after").ToArray();
        Assert.Equal(3, entries.Count(e => e.Kind == EntryKind.File));
        Assert.Contains(entries, e => e.RelativePath == "A" && e.Kind == EntryKind.Directory);
        Assert.Contains(entries, e => e.RelativePath == "B" && e.Kind == EntryKind.Directory);
        Assert.Contains(entries, e => e.RelativePath == @"A\same.txt");
        Assert.Contains(entries, e => e.RelativePath == @"B\same.txt");
        string report = File.ReadAllText(result.ReportPath!);
        Assert.Contains(@"A\same.txt", report);
        Assert.Contains(@"B\same.txt", report);
        Assert.Contains("outside.txt", report);
        var history = Assert.Single(coordinator.History());
        Assert.NotNull(history.Sources);
        Assert.Equal(Sources, history.Sources);
    }

    [Fact]
    public async Task EmptySourceRootsAreCreatedAsIndependentDestinationFolders()
    {
        var before = Snapshot(SourceA, SourceB);
        var result = await new TransferCoordinator(Storage).RunAsync(Sources, Destination, VerificationMode.Sha256, ConflictPolicy.Preserve, true);
        AssertCompleted(result);
        Assert.True(Directory.Exists(Path.Combine(Destination, "A")));
        Assert.True(Directory.Exists(Path.Combine(Destination, "B")));
        Assert.Equal(2, result.Summary.Matched);
        Assert.Empty(Directory.EnumerateFiles(Destination, "*", SearchOption.AllDirectories));
        AssertUnchanged(before, SourceA, SourceB);
    }

    [Fact]
    public async Task CompareIncludesAllSourceNamespacesWithoutCreatingDestinationFolders()
    {
        Seed();
        var before = Snapshot(SourceA, SourceB, Destination);
        var result = await new TransferCoordinator(Storage).RunAsync(Sources, Destination, VerificationMode.Sha256, ConflictPolicy.Preserve, false);
        Assert.Equal("Compared", result.Info.Status);
        Assert.Contains(result.Rows, row => row.RelativePath == @"A\same.txt" && row.Kind == DifferenceKind.Missing);
        Assert.Contains(result.Rows, row => row.RelativePath == @"B\same.txt" && row.Kind == DifferenceKind.Missing);
        Assert.False(Directory.Exists(Path.Combine(Destination, "A")));
        Assert.False(Directory.Exists(Path.Combine(Destination, "B")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Destination));
        AssertUnchanged(before, SourceA, SourceB, Destination);
    }

    [Fact]
    public async Task LaterSourceOverlappingDestinationIsRejectedBeforeAnyCreation()
    {
        Seed();
        File.WriteAllText(Path.Combine(Destination, "sentinel.txt"), "also a selected source");
        var before = Snapshot(SourceA, SourceB, Destination, Storage);
        TransferSource[] sources = [new(SourceA, "A"), new(Destination, "B")];
        await Assert.ThrowsAsync<ArgumentException>(() => new TransferCoordinator(Storage).RunAsync(sources, Destination, VerificationMode.Sha256, ConflictPolicy.ReplaceAfterVerification, true));
        Assert.False(Directory.Exists(Path.Combine(Storage, "SafeFileSync")));
        AssertUnchanged(before, SourceA, SourceB, Destination, Storage);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LaterSourceOverlappingActualRecordLocationIsRejectedBeforeAnyCreation(bool sourceIsActualStore)
    {
        Seed();
        string second = sourceIsActualStore ? Path.Combine(Storage, "SafeFileSync") : SourceB;
        if (sourceIsActualStore) Directory.CreateDirectory(second);
        string storageParent = sourceIsActualStore ? Storage : SourceB;
        File.WriteAllText(Path.Combine(second, "sentinel.txt"), "read-only later source");
        var before = Snapshot(SourceA, second, Destination);
        TransferSource[] sources = [new(SourceA, "A"), new(second, "B")];
        await Assert.ThrowsAsync<IOException>(() => new TransferCoordinator(storageParent).RunAsync(sources, Destination, VerificationMode.Sha256, ConflictPolicy.Preserve, true));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Destination));
        AssertUnchanged(before, SourceA, second, Destination);
        if (!sourceIsActualStore) Assert.False(Directory.Exists(Path.Combine(storageParent, "SafeFileSync")));
    }

    [Theory]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData(@"nested\folder")]
    [InlineData("nested/folder")]
    [InlineData("NUL")]
    [InlineData("stream:name")]
    [InlineData("trailing.")]
    [InlineData("trailing ")]
    [InlineData("wild*card")]
    public async Task InvalidDestinationFolderNamesCannotCreateRecordsOrDestinationContent(string alias)
    {
        Seed();
        var before = Snapshot(SourceA, SourceB, Destination, Storage);
        TransferSource[] sources = [new(SourceA, "A"), new(SourceB, alias)];
        await Assert.ThrowsAsync<ArgumentException>(() => new TransferCoordinator(Storage).RunAsync(sources, Destination, VerificationMode.Sha256, ConflictPolicy.Preserve, true));
        AssertUnchanged(before, SourceA, SourceB, Destination, Storage);
    }

    [Fact]
    public async Task CaseInsensitiveAliasCollisionIsRejectedBeforeAnyCreation()
    {
        Seed();
        var before = Snapshot(SourceA, SourceB, Destination, Storage);
        TransferSource[] sources = [new(SourceA, "Reports"), new(SourceB, "reports")];
        await Assert.ThrowsAsync<ArgumentException>(() => new TransferCoordinator(Storage).RunAsync(sources, Destination, VerificationMode.Sha256, ConflictPolicy.Preserve, true));
        AssertUnchanged(before, SourceA, SourceB, Destination, Storage);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DuplicateOrNestedSourceRootsAreRejectedBeforeAnyCreation(bool nested)
    {
        Seed();
        var before = Snapshot(SourceA, SourceB, Destination, Storage);
        string second = nested ? Path.Combine(SourceA, "nested") : SourceA;
        TransferSource[] sources = [new(SourceA, "A"), new(second, "B")];
        await Assert.ThrowsAsync<ArgumentException>(() => new TransferCoordinator(Storage).RunAsync(sources, Destination, VerificationMode.Sha256, ConflictPolicy.Preserve, true));
        AssertUnchanged(before, SourceA, SourceB, Destination, Storage);
    }

    [Theory]
    [InlineData("alias")]
    [InlineData("path")]
    [InlineData("missing")]
    public async Task ResumeRejectsChangedOrMissingSourceMappings(string change)
    {
        Seed();
        var coordinator = new TransferCoordinator(Storage);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => coordinator.RunAsync(Sources, Destination, VerificationMode.Sha256, ConflictPolicy.Preserve, true, token: cts.Token));
        var job = Assert.Single(coordinator.History());
        string alternative = Path.Combine(root, "alternative");
        Directory.CreateDirectory(alternative);
        TransferSource[] changed = change switch
        {
            "alias" => [new(SourceA, "A"), new(SourceB, "Renamed")],
            "path" => [new(SourceA, "A"), new(alternative, "B")],
            _ => [new(SourceA, "A")]
        };
        var before = Snapshot(SourceA, SourceB, Destination, alternative);
        await Assert.ThrowsAsync<IOException>(() => coordinator.RunAsync(changed, Destination, job.Mode, job.Conflicts, true, resumeId: job.Id));
        AssertUnchanged(before, SourceA, SourceB, Destination, alternative);
        Assert.Equal(Sources, Assert.Single(coordinator.History()).Sources);
        var resumed = await coordinator.RunAsync(Sources, Destination, job.Mode, job.Conflicts, true, resumeId: job.Id);
        AssertCompleted(resumed);
    }

    [Fact]
    public async Task ResumeKeepsOriginalManifestForChangesInLaterSourceWhileStopped()
    {
        Seed();
        var before = Snapshot(SourceA, SourceB);
        var coordinator = new TransferCoordinator(Storage);
        using var cts = new CancellationTokenSource();
        bool cancelledLaterSource = false;
        var progress = new ImmediateProgress(p =>
        {
            if (p.Phase == "임시 복사본 SHA-256 검증" && p.RelativePath == @"B\same.txt")
            {
                cancelledLaterSource = true;
                cts.Cancel();
            }
        });
        await Assert.ThrowsAsync<OperationCanceledException>(() => coordinator.RunAsync(Sources, Destination, VerificationMode.Sha256, ConflictPolicy.Preserve, true, progress, cts.Token));
        Assert.True(cancelledLaterSource);
        Assert.False(File.Exists(Path.Combine(Destination, "B", "same.txt")));
        AssertUnchanged(before, SourceA, SourceB);
        var job = Assert.Single(coordinator.History());
        Assert.Equal("Cancelled", job.Status);
        File.WriteAllText(Path.Combine(SourceB, "same.txt"), "second source externally changed during pause");
        var afterExternalChange = Snapshot(SourceA, SourceB);

        var result = await coordinator.RunAsync(Sources, Destination, job.Mode, job.Conflicts, true, resumeId: job.Id);
        Assert.Equal("NeedsAttention", result.Info.Status);
        Assert.DoesNotContain("PASS", result.SourceCheck);
        Assert.True(result.Summary.AllSourceEntriesMatch);
        Assert.Equal(100d, result.HashPercent);
        Assert.Equal("second source externally changed during pause", File.ReadAllText(Path.Combine(Destination, "B", "same.txt")));
        AssertUnchanged(afterExternalChange, SourceA, SourceB);
        using var db = new JobStore(result.Info.DatabasePath, true);
        Assert.NotEqual(db.Entries("source-original").Single(e => e.RelativePath == @"B\same.txt").Hash,
            db.Entries("source-after").Single(e => e.RelativePath == @"B\same.txt").Hash);
        Assert.Equal(db.Entries("source-original").Single(e => e.RelativePath == @"A\same.txt").Hash,
            db.Entries("source-after").Single(e => e.RelativePath == @"A\same.txt").Hash);
    }

    [Fact]
    public async Task FirstSourceMutationWhileCopyingLaterSourcePreventsSuccessfulVerification()
    {
        Seed();
        var secondBefore = Snapshot(SourceB);
        bool changed = false;
        var progress = new ImmediateProgress(p =>
        {
            if (!changed && p.Phase == "원본 내용 확인" && p.RelativePath == @"B\same.txt")
            {
                File.WriteAllText(Path.Combine(SourceA, "same.txt"), "first source externally changed during second source");
                changed = true;
            }
        });
        var result = await new TransferCoordinator(Storage).RunAsync(Sources, Destination, VerificationMode.Sha256, ConflictPolicy.Preserve, true, progress);
        Assert.True(changed);
        Assert.Equal("NeedsAttention", result.Info.Status);
        Assert.DoesNotContain("PASS", result.SourceCheck);
        Assert.Equal("first source externally changed during second source", File.ReadAllText(Path.Combine(SourceA, "same.txt")));
        Assert.Equal("first source original", File.ReadAllText(Path.Combine(Destination, "A", "same.txt")));
        Assert.Equal("second source original", File.ReadAllText(Path.Combine(Destination, "B", "same.txt")));
        Assert.Contains(result.Rows, row => row.RelativePath == @"A\same.txt" && row.Kind == DifferenceKind.Different);
        AssertUnchanged(secondBefore, SourceB);
    }

    [Fact]
    public async Task LockedFileInLaterSourceBlocksAllCopiesBeforeDestinationCreation()
    {
        Seed();
        var before = Snapshot(SourceA, SourceB, Destination);
        using (var locked = new FileStream(Path.Combine(SourceB, "same.txt"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            await Assert.ThrowsAsync<IOException>(() => new TransferCoordinator(Storage).RunAsync(Sources, Destination, VerificationMode.Sha256, ConflictPolicy.Preserve, true));
            Assert.Empty(Directory.EnumerateFileSystemEntries(Destination));
        }
        AssertUnchanged(before, SourceA, SourceB, Destination);
    }

    [Fact]
    public async Task FailedOnlyRetryUsesNamespacedPathsAndDoesNotCopyNewFiles()
    {
        File.WriteAllText(Path.Combine(SourceA, "same.txt"), "source A");
        File.WriteAllText(Path.Combine(SourceB, "same.txt"), "source B");
        Directory.CreateDirectory(Path.Combine(Destination, "A"));
        File.WriteAllText(Path.Combine(Destination, "A", "same.txt"), "preserve this conflict");
        var coordinator = new TransferCoordinator(Storage);
        var initial = await coordinator.RunAsync(Sources, Destination, VerificationMode.Sha256, ConflictPolicy.Preserve, true);
        Assert.Equal("NeedsAttention", initial.Info.Status);
        Assert.Equal("preserve this conflict", File.ReadAllText(Path.Combine(Destination, "A", "same.txt")));
        Assert.Equal("source B", File.ReadAllText(Path.Combine(Destination, "B", "same.txt")));
        File.Delete(Path.Combine(Destination, "A", "same.txt"));
        File.WriteAllText(Path.Combine(SourceA, "new.txt"), "new A");
        File.WriteAllText(Path.Combine(SourceB, "new.txt"), "new B");
        var before = Snapshot(SourceA, SourceB);

        var result = await coordinator.RunAsync(Sources, Destination, initial.Info.Mode, initial.Info.Conflicts, true, resumeId: initial.Info.Id, failedOnly: true);
        Assert.Equal("NeedsAttention", result.Info.Status);
        Assert.Equal("source A", File.ReadAllText(Path.Combine(Destination, "A", "same.txt")));
        Assert.Equal("source B", File.ReadAllText(Path.Combine(Destination, "B", "same.txt")));
        Assert.False(File.Exists(Path.Combine(Destination, "A", "new.txt")));
        Assert.False(File.Exists(Path.Combine(Destination, "B", "new.txt")));
        Assert.Equal(50d, result.HashPercent);
        AssertUnchanged(before, SourceA, SourceB);
    }

    private sealed class ImmediateProgress(Action<TransferProgress> callback) : IProgress<TransferProgress>
    {
        public void Report(TransferProgress value) => callback(value);
    }
}
