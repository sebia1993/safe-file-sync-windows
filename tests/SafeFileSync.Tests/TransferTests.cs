using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using SafeFileSync.Core;
using SafeFileSync.Infrastructure.Windows;
using Xunit;
namespace SafeFileSync.Tests;

public sealed class TransferTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "SafeFileSync-tests-" + Guid.NewGuid().ToString("N"));
    private string Source => Path.Combine(root, "source");
    private string Destination => Path.Combine(root, "destination");
    private string Storage => Path.Combine(root, "storage");
    public TransferTests() { Directory.CreateDirectory(Source); Directory.CreateDirectory(Destination); Directory.CreateDirectory(Storage); }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
    private void Seed()
    {
        Directory.CreateDirectory(Path.Combine(Source, "한글 폴더", "empty"));
        File.WriteAllText(Path.Combine(Source, "한글 폴더", "file.txt"), "important original\n");
        File.WriteAllBytes(Path.Combine(Source, "zero"), []);
        File.WriteAllBytes(Path.Combine(Source, "binary.dat"), RandomNumberGenerator.GetBytes(65537));
    }
    private Dictionary<string,string> Snapshot() => Directory.GetFiles(Source, "*", SearchOption.AllDirectories).ToDictionary(p => Path.GetRelativePath(Source,p), p => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))) + ":" + File.GetLastWriteTimeUtc(p).Ticks + ":" + File.GetCreationTimeUtc(p).Ticks + ":" + File.GetAttributes(p));
    private void AssertUnchanged(Dictionary<string,string> before) {
        var after = Snapshot(); Assert.Equal(before.Count, after.Count);
        foreach (var item in before) Assert.Equal(item.Value, after[item.Key]);
    }
    [Fact] public void ScannerIncludesEmptyDirectoriesAndSha256()
    {
        Seed(); var before = Snapshot();
        var entries = new FolderScanner().Scan(Source, VerificationMode.Sha256).ToArray();
        Assert.Equal(5, entries.Length); Assert.Equal(3, entries.Count(e => e.Kind == EntryKind.File));
        Assert.All(entries.Where(e => e.Kind == EntryKind.File), e => Assert.Equal(64, e.Hash!.Length)); AssertUnchanged(before);
    }
    [Fact] public void ScannerCancellationDoesNotBecomeAnEmptySuccess()
    {
        using var cts = new CancellationTokenSource(); cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => new FolderScanner().Scan(Source, VerificationMode.Quick, cts.Token).ToArray());
    }
    [Fact] public void LockedFileIsAnError()
    {
        Seed(); using var locked = new FileStream(Path.Combine(Source,"binary.dat"), FileMode.Open, FileAccess.Write, FileShare.None);
        Assert.Contains(new FolderScanner().Scan(Source,VerificationMode.Sha256), e => e.RelativePath == "binary.dat" && e.Kind == EntryKind.Error);
    }
    [Fact] public void DirectoryLeasePreventsRename()
    {
        using var lease = DirectoryLease.Acquire(Source);
        Assert.Throws<IOException>(() => Directory.Move(Source, Source + "-renamed"));
    }
    [Fact] public async Task CopyAndVerifyPreservesOriginalAndDestinationExtras()
    {
        Seed(); var before = Snapshot(); File.WriteAllText(Path.Combine(Destination,"extra.txt"),"keep");
        var result = await new TransferCoordinator(Storage).RunAsync(Source,Destination,VerificationMode.Sha256,ConflictPolicy.Preserve,true);
        Assert.Equal("Completed",result.Info.Status); Assert.True(result.Summary.AllSourceEntriesMatch); Assert.Equal(1,result.Summary.Extra);
        Assert.Equal("keep",File.ReadAllText(Path.Combine(Destination,"extra.txt"))); AssertUnchanged(before);
        Assert.True(File.Exists(result.ReportPath)); Assert.True(Directory.Exists(Path.Combine(Destination,"한글 폴더","empty")));
        var again = await new TransferCoordinator(Storage).RunAsync(Source,Destination,VerificationMode.Sha256,ConflictPolicy.Preserve,true,resumeId:result.Info.Id);
        Assert.Equal("Completed",again.Info.Status); AssertUnchanged(before);
    }
    [Fact] public async Task PreservePolicyReportsConflictWithoutOverwriting()
    {
        File.WriteAllText(Path.Combine(Source,"report.txt"),"original"); File.WriteAllText(Path.Combine(Destination,"report.txt"),"destination"); var before = Snapshot();
        var result = await new TransferCoordinator(Storage).RunAsync(Source,Destination,VerificationMode.Sha256,ConflictPolicy.Preserve,true);
        Assert.Equal("NeedsAttention",result.Info.Status); Assert.Equal("destination",File.ReadAllText(Path.Combine(Destination,"report.txt"))); AssertUnchanged(before);
    }
    [Fact] public async Task ReplacePolicyCommitsOnlyVerifiedCopy()
    {
        File.WriteAllText(Path.Combine(Source,"report.txt"),"original"); File.WriteAllText(Path.Combine(Destination,"report.txt"),"destination"); var before = Snapshot();
        var result = await new TransferCoordinator(Storage).RunAsync(Source,Destination,VerificationMode.Sha256,ConflictPolicy.ReplaceAfterVerification,true);
        Assert.Equal("Completed",result.Info.Status); Assert.Equal("original",File.ReadAllText(Path.Combine(Destination,"report.txt"))); AssertUnchanged(before);
    }
    [Fact] public async Task DestinationHardLinkToSourceIsBlocked()
    {
        Seed(); var before = Snapshot(); Assert.True(CreateHardLinkW(Path.Combine(Destination,"binary.dat"),Path.Combine(Source,"binary.dat"),IntPtr.Zero));
        await Assert.ThrowsAsync<IOException>(() => new TransferCoordinator(Storage).RunAsync(Source,Destination,VerificationMode.Sha256,ConflictPolicy.ReplaceAfterVerification,true)); AssertUnchanged(before);
    }
    [Fact] public void JunctionAncestorIsBlocked()
    {
        string link = Path.Combine(root,"alias");
        using var process = Process.Start(new ProcessStartInfo("cmd.exe") { Arguments = $"/c mklink /J \"{link}\" \"{Source}\"", CreateNoWindow=true, UseShellExecute=false })!; process.WaitForExit(); Assert.Equal(0,process.ExitCode);
        try { Assert.Throws<IOException>(() => RootSafetyLease.Acquire(link,Destination)); }
        finally { Directory.Delete(link); }
    }
    [Fact] public async Task CancellationIsPersistedAndResumable()
    {
        Seed(); using var cts = new CancellationTokenSource(); cts.Cancel(); var coordinator = new TransferCoordinator(Storage);
        await Assert.ThrowsAsync<OperationCanceledException>(() => coordinator.RunAsync(Source,Destination,VerificationMode.Sha256,ConflictPolicy.Preserve,true,token:cts.Token));
        var job = Assert.Single(coordinator.History()); Assert.Equal("Cancelled",job.Status);
        var result = await coordinator.RunAsync(Source,Destination,job.Mode,job.Conflicts,true,resumeId:job.Id); Assert.Equal("Completed",result.Info.Status);
    }
    [Fact] public async Task SourceContainingStorageIsRejectedBeforeCreatingDatabase()
    {
        string inside = Path.Combine(Source,"storage"); Directory.CreateDirectory(inside);
        Assert.Throws<ArgumentException>(() => new TransferWorkspace(Source,Destination,inside));
        Assert.Empty(Directory.EnumerateFileSystemEntries(inside)); await Task.CompletedTask;
    }
    [Fact] public void InterruptedSnapshotCannotBeCompared()
    {
        using var db = new JobStore(Path.Combine(Storage,"test.sqlite"));
        using var cts = new CancellationTokenSource();
        IEnumerable<ScanEntry> Interrupted() { yield return new("first",EntryKind.File); cts.Cancel(); yield return new("second",EntryKind.File); }
        Assert.Throws<OperationCanceledException>(() => db.SaveSnapshot("source",Interrupted(),cts.Token));
        Assert.Throws<InvalidOperationException>(() => db.Entries("source").ToArray());
    }
    [Fact] public void SqliteDoesNotMergeCaseCollisions()
    {
        using var db = new JobStore(Path.Combine(Storage,"test.sqlite"));
        Assert.Throws<IOException>(() => db.SaveSnapshot("source",[new("A",EntryKind.File),new("a",EntryKind.File)],default));
    }
    [Theory][InlineData(-1,true)][InlineData(0,false)][InlineData(1,false)][InlineData(7,false)][InlineData(8,true)][InlineData(16,true)]
    public void RobocopyExitCodesAreNotNormalProcessCodes(int code,bool failed) => Assert.Equal(failed,new CopyResult(code,"").Failed);
    [Fact] public void QuickMatchIsNotContentVerification()
    {
        var src = new ScanEntry("a",EntryKind.File,10,123,"AAA"); var dst = src with { Hash="BBB" };
        Assert.Equal(DifferenceKind.QuickMatch,EntryComparer.Compare(src,dst,VerificationMode.Quick).Kind);
        Assert.Equal(DifferenceKind.Different,EntryComparer.Compare(src,dst,VerificationMode.Sha256).Kind);
    }
    [Fact] public void MissingHashIsUnverified() => Assert.Equal(DifferenceKind.Unverified,EntryComparer.Compare(new("a",EntryKind.File),new("a",EntryKind.File),VerificationMode.Sha256).Kind);
    [Fact] public void ExclusionsAreNotSuccessfulExtras() => Assert.Equal(DifferenceKind.Unverified,EntryComparer.Compare(null,new("link",EntryKind.Excluded),VerificationMode.Quick).Kind);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] private static extern bool CreateHardLinkW(string name,string existing,IntPtr security);
}
