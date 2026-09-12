using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.AccessControl;
using System.Security.Principal;
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
    [Fact] public async Task UserProfileCopyCanUseExternalRecordsAndResumeFromThem()
    {
        Seed(); string appData = Path.Combine(Source,"AppData","Local"); Directory.CreateDirectory(appData);
        var before = Snapshot();
        var error = await Assert.ThrowsAsync<IOException>(() => new TransferCoordinator(appData).RunAsync(Source,Destination,VerificationMode.Sha256,ConflictPolicy.Preserve,true));
        Assert.Contains("기록 위치를",error.Message); Assert.False(Directory.Exists(Path.Combine(appData,"SafeFileSync")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Destination)); AssertUnchanged(before);
        var coordinator = new TransferCoordinator(Storage);
        var result = await coordinator.RunAsync(Source,Destination,VerificationMode.Sha256,ConflictPolicy.Preserve,true);
        AssertCompleted(result); AssertUnchanged(before); Assert.Single(coordinator.History());
        Assert.True(PathSafetyService.IsWithin(result.Info.DatabasePath,Storage)); Assert.True(PathSafetyService.IsWithin(result.ReportPath!,Storage));
        Assert.NotEmpty(Directory.GetFiles(Path.Combine(Storage,"SafeFileSync"),"*.log"));
        var resumed = await new TransferCoordinator(Storage).RunAsync(Source,Destination,VerificationMode.Sha256,ConflictPolicy.Preserve,true,resumeId:result.Info.Id);
        AssertCompleted(resumed); AssertUnchanged(before); Assert.Equal(result.Info.DatabasePath,resumed.Info.DatabasePath);
        Assert.Empty(new TransferCoordinator(appData).History());
    }
    [Fact] public void DestinationContainingRecordFolderRemainsBlockedBeforeCreation()
    {
        string inside = Path.Combine(Destination,"records"); Directory.CreateDirectory(inside);
        Assert.Throws<IOException>(() => new TransferWorkspace(Source,Destination,inside));
        Assert.False(Directory.Exists(Path.Combine(inside,"SafeFileSync")));
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
    [Fact] public void DirectoryLeaseAllowsChildRenameWhilePinningDirectoryItself()
    {
        File.WriteAllText(Path.Combine(Destination,"temporary"),"verified");
        using var lease = DirectoryLease.Acquire(Destination);
        File.Move(Path.Combine(Destination,"temporary"),Path.Combine(Destination,"final"));
        Assert.Equal("verified",File.ReadAllText(Path.Combine(Destination,"final")));
        Assert.Throws<IOException>(() => Directory.Move(Destination,Destination + "-renamed"));
    }
    [Fact] public async Task CopyAndVerifyPreservesOriginalAndDestinationExtras()
    {
        Seed(); var before = Snapshot(); File.WriteAllText(Path.Combine(Destination,"extra.txt"),"keep");
        var result = await new TransferCoordinator(Storage).RunAsync(Source,Destination,VerificationMode.Sha256,ConflictPolicy.Preserve,true);
        AssertCompleted(result); Assert.True(result.Summary.AllSourceEntriesMatch); Assert.Equal(1,result.Summary.Extra);
        Assert.Equal("keep",File.ReadAllText(Path.Combine(Destination,"extra.txt"))); AssertUnchanged(before);
        Assert.True(File.Exists(result.ReportPath)); Assert.True(Directory.Exists(Path.Combine(Destination,"한글 폴더","empty")));
        var again = await new TransferCoordinator(Storage).RunAsync(Source,Destination,VerificationMode.Sha256,ConflictPolicy.Preserve,true,resumeId:result.Info.Id);
        AssertCompleted(again); AssertUnchanged(before);
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
        AssertCompleted(result); Assert.Equal("original",File.ReadAllText(Path.Combine(Destination,"report.txt"))); AssertUnchanged(before);
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
        var result = await coordinator.RunAsync(Source,Destination,job.Mode,job.Conflicts,true,resumeId:job.Id); AssertCompleted(result);
    }
    [Fact] public async Task SourceContainingStorageIsRejectedBeforeCreatingDatabase()
    {
        string inside = Path.Combine(Source,"storage"); Directory.CreateDirectory(inside);
        Assert.Throws<IOException>(() => new TransferWorkspace(Source,Destination,inside));
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
    [Fact] public async Task ChangesBeforeFinalScanPreventSuccess()
    {
        Seed(); bool changed = false;
        var progress = new ImmediateProgress(p => {
            if (p.Phase == "스캔: source-after" && !changed) { File.WriteAllText(Path.Combine(Source,"new-after-copy.txt"),"external change"); changed = true; }
        });
        var result = await new TransferCoordinator(Storage).RunAsync(Source,Destination,VerificationMode.Sha256,ConflictPolicy.Preserve,true,progress);
        Assert.True(changed); Assert.Equal("NeedsAttention",result.Info.Status); Assert.DoesNotContain("PASS",result.SourceCheck);
    }
    [Fact] public async Task CancelDuringCopyNeverPublishesPartialFile()
    {
        File.WriteAllBytes(Path.Combine(Source,"cancel.dat"),RandomNumberGenerator.GetBytes(8 * 1024 * 1024)); var before = Snapshot();
        using var cts = new CancellationTokenSource();
        var progress = new ImmediateProgress(p => { if (p.Phase == "임시 복사본 SHA-256 검증") cts.Cancel(); });
        await Assert.ThrowsAsync<OperationCanceledException>(() => new TransferCoordinator(Storage).RunAsync(Source,Destination,VerificationMode.Sha256,ConflictPolicy.Preserve,true,progress,cts.Token));
        Assert.False(File.Exists(Path.Combine(Destination,"cancel.dat"))); AssertUnchanged(before);
        var job = Assert.Single(new TransferCoordinator(Storage).History());
        var resumed = await new TransferCoordinator(Storage).RunAsync(Source,Destination,job.Mode,job.Conflicts,true,resumeId:job.Id);
        AssertCompleted(resumed); AssertUnchanged(before);
    }
    [Fact] public async Task SameSizeTimeCorruptionIsRepairedByHashMode()
    {
        string src = Path.Combine(Source,"same.txt"), dst = Path.Combine(Destination,"same.txt");
        File.WriteAllText(src,"AAAA"); File.WriteAllText(dst,"BBBB"); File.SetLastWriteTimeUtc(dst,File.GetLastWriteTimeUtc(src));
        var result = await new TransferCoordinator(Storage).RunAsync(Source,Destination,VerificationMode.Sha256,ConflictPolicy.ReplaceAfterVerification,true);
        AssertCompleted(result); Assert.Equal("AAAA",File.ReadAllText(dst));
    }
    [Fact] public async Task ThousandSmallFilesCrossMultipleBatches()
    {
        for (int i=0;i<1025;i++) File.WriteAllText(Path.Combine(Source,$"file-{i:D4}.txt"),$"original {i}");
        var before = Snapshot(); var timer = Stopwatch.StartNew();
        var result = await new TransferCoordinator(Storage).RunAsync(Source,Destination,VerificationMode.Sha256,ConflictPolicy.Preserve,true);
        AssertCompleted(result); Assert.Equal(1025,result.Summary.Matched); Assert.Equal(1000,result.Rows.Count); AssertUnchanged(before);
        using var db = new JobStore(result.Info.DatabasePath,true); Assert.Equal(1025,db.Outcomes().Count());
        Assert.Contains("file-1024.txt",File.ReadAllText(result.ReportPath!));
        Console.WriteLine($"1,025 small files copied and SHA-256 verified in {timer.Elapsed.TotalSeconds:F2}s.");
    }
    [Fact] public async Task LargeFileIsStreamedAndVerified()
    {
        string path = Path.Combine(Source,"large.bin");
        using (var file = File.Create(path)) { file.SetLength(512L*1024*1024+17); file.Position=file.Length-4; file.Write([1,2,3,4]); }
        string Digest(string p) { using var file=File.OpenRead(p); return Convert.ToHexString(SHA256.HashData(file)); }
        var hash = Digest(path); var time = File.GetLastWriteTimeUtc(path); var timer=Stopwatch.StartNew();
        var result = await new TransferCoordinator(Storage).RunAsync(Source,Destination,VerificationMode.Sha256,ConflictPolicy.Preserve,true);
        AssertCompleted(result); Assert.Equal(hash,Digest(Path.Combine(Destination,"large.bin"))); Assert.Equal(hash,Digest(path)); Assert.Equal(time,File.GetLastWriteTimeUtc(path));
        Console.WriteLine($"512 MiB + 17 byte file copied and verified in {timer.Elapsed.TotalSeconds:F2}s.");
    }
    [Fact] public async Task FailedOnlyRetryDoesNotCopyNewUnrelatedFiles()
    {
        File.WriteAllText(Path.Combine(Source,"failed.txt"),"source"); File.WriteAllText(Path.Combine(Destination,"failed.txt"),"conflict");
        var coordinator = new TransferCoordinator(Storage);
        var initial = await coordinator.RunAsync(Source,Destination,VerificationMode.Sha256,ConflictPolicy.Preserve,true);
        Assert.Equal("NeedsAttention",initial.Info.Status);
        File.Delete(Path.Combine(Destination,"failed.txt")); File.WriteAllText(Path.Combine(Source,"new.txt"),"new work");
        var retried = await coordinator.RunAsync(Source,Destination,VerificationMode.Sha256,ConflictPolicy.Preserve,true,resumeId:initial.Info.Id,failedOnly:true);
        Assert.Equal("source",File.ReadAllText(Path.Combine(Destination,"failed.txt"))); Assert.False(File.Exists(Path.Combine(Destination,"new.txt")));
        Assert.Equal("NeedsAttention",retried.Info.Status); Assert.Equal(50,retried.TransferPercent); Assert.Equal(50,retried.HashPercent);
    }
    [Fact] public async Task StorageParentMayContainSourceWhenActualStorageIsSeparate()
    {
        Seed(); var result = await new TransferCoordinator(root).RunAsync(Source,Destination,VerificationMode.Quick,ConflictPolicy.Preserve,true);
        AssertCompleted(result); Assert.True(File.Exists(result.Info.DatabasePath));
        Assert.DoesNotContain(Directory.GetFiles(Source,"*",SearchOption.AllDirectories),p => p.EndsWith(".sqlite"));
    }
    [Fact] public async Task DanglingLinkInResumedStagingCannotCreateAFileInsideSource()
    {
        File.WriteAllText(Path.Combine(Source,"payload.txt"),"important");
        using var cts = new CancellationTokenSource(); cts.Cancel(); var coordinator = new TransferCoordinator(Storage);
        await Assert.ThrowsAsync<OperationCanceledException>(() => coordinator.RunAsync(Source,Destination,VerificationMode.Sha256,ConflictPolicy.Preserve,true,token:cts.Token));
        var job = Assert.Single(coordinator.History());
        var stage = Path.Combine(Destination,".safefilesync-" + job.Id); Directory.CreateDirectory(stage);
        string link = Path.Combine(stage,"payload.txt"), forbidden = Path.Combine(Source,"must-not-be-created.txt");
        File.CreateSymbolicLink(link,forbidden);
        try {
            var result = await coordinator.RunAsync(Source,Destination,job.Mode,job.Conflicts,true,resumeId:job.Id);
            Assert.Equal("NeedsAttention",result.Info.Status); Assert.False(File.Exists(forbidden)); Assert.False(File.Exists(Path.Combine(Destination,"payload.txt")));
            Assert.Equal("important",File.ReadAllText(Path.Combine(Source,"payload.txt")));
        } finally { File.Delete(link); }
    }
    [Fact] public async Task ReadOnlySourceFileCanBeCopiedWithoutChangingItsAttributes()
    {
        string file = Path.Combine(Source,"readonly.txt"); File.WriteAllText(file,"read-only source"); File.SetAttributes(file,FileAttributes.ReadOnly);
        try {
            var before = Snapshot(); var result = await new TransferCoordinator(Storage).RunAsync(Source,Destination,VerificationMode.Sha256,ConflictPolicy.Preserve,true);
            AssertCompleted(result); AssertUnchanged(before);
        } finally {
            File.SetAttributes(file,FileAttributes.Normal);
            string target=Path.Combine(Destination,"readonly.txt"); if (File.Exists(target)) File.SetAttributes(target,FileAttributes.Normal);
        }
    }
    [Fact] public void ExecutionArgumentsRejectWildcardsMixedDirectoriesAndOverlap()
    {
        Assert.Throws<ArgumentException>(() => RobocopyArgumentBuilder.BuildFiles([Path.Combine(Source,"*")],Destination));
        Assert.Throws<ArgumentException>(() => RobocopyArgumentBuilder.BuildFiles([Path.Combine(Source,"a"),Path.Combine(Destination,"b")],Storage));
        Assert.Throws<ArgumentException>(() => RobocopyArgumentBuilder.BuildFiles([Path.Combine(Source,"a")],Path.Combine(Source,"staging")));
        var arguments=RobocopyArgumentBuilder.BuildFiles([Path.Combine(Source,"한글 name.txt")],Destination);
        Assert.Equal("한글 name.txt",arguments[2]); Assert.Contains("/MT:8",arguments);
        Assert.DoesNotContain(arguments,a => new[]{"/MOV","/MOVE","/MIR","/PURGE"}.Contains(a));
    }
    [Fact] public async Task RobocopyOutputPreservesKoreanAndLiteralShellCharacters()
    {
        string name="한글 & (space).txt"; string file=Path.Combine(Source,name); File.WriteAllText(file,"source data"); var before=Snapshot();
        var result=await new RobocopyProcess().CopyFileAsync(file,Destination,null,default);
        Assert.False(result.Failed,result.Output); Assert.Contains(name,result.Output); Assert.Equal("source data",File.ReadAllText(Path.Combine(Destination,name))); AssertUnchanged(before);
    }
    [Fact] public async Task NestedSourceJunctionIsExcludedWithoutPreventingRegularFileCopy()
    {
        File.WriteAllText(Path.Combine(Source,"regular.txt"),"copy me");
        string outside=Path.Combine(root,"outside"), link=Path.Combine(Source,"link"); Directory.CreateDirectory(outside); File.WriteAllText(Path.Combine(outside,"secret.txt"),"leave unchanged");
        using var process=Process.Start(new ProcessStartInfo("cmd.exe") { Arguments=$"/c mklink /J \"{link}\" \"{outside}\"",UseShellExecute=false,CreateNoWindow=true })!; process.WaitForExit(); Assert.Equal(0,process.ExitCode);
        try {
            var result=await new TransferCoordinator(Storage).RunAsync(Source,Destination,VerificationMode.Sha256,ConflictPolicy.Preserve,true);
            Assert.Equal("NeedsAttention",result.Info.Status); Assert.Equal("copy me",File.ReadAllText(Path.Combine(Destination,"regular.txt")));
            Assert.False(Directory.Exists(Path.Combine(Destination,"link"))); Assert.Equal("leave unchanged",File.ReadAllText(Path.Combine(outside,"secret.txt")));
            Assert.True(result.Summary.Unverified>0); Assert.Null(result.HashPercent);
        } finally { Directory.Delete(link); }
    }
    [Fact] public async Task SourceWithExplicitWriteAndDeleteDenyAclStillCopiesReadOnly()
    {
        Seed(); var directory=new DirectoryInfo(Source); var locked=directory.GetAccessControl();
        var identity=WindowsIdentity.GetCurrent().User!;
        var denyRule = new FileSystemAccessRule(identity,FileSystemRights.Write | FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,PropagationFlags.None,AccessControlType.Deny);
        locked.AddAccessRule(denyRule);
        directory.SetAccessControl(locked);
        try {
            var before=Snapshot(); var acl=directory.GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access);
            var result=await new TransferCoordinator(Storage).RunAsync(Source,Destination,VerificationMode.Sha256,ConflictPolicy.Preserve,true);
            AssertCompleted(result); AssertUnchanged(before); Assert.Equal(acl,directory.GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access));
        } finally { locked.RemoveAccessRuleSpecific(denyRule); directory.SetAccessControl(locked); }
    }
    [Fact] public async Task PathsLongerThanLegacyMaxPathAreCopiedAndVerified()
    {
        string relative=Path.Combine(new string('a',80),new string('b',80),new string('c',80));
        string directory=Path.Combine(Source,relative); Directory.CreateDirectory(directory);
        string file=Path.Combine(directory,"payload.txt"); Assert.True(file.Length>260); File.WriteAllText(file,"long path source");
        var before=Snapshot(); var result=await new TransferCoordinator(Storage).RunAsync(Source,Destination,VerificationMode.Sha256,ConflictPolicy.Preserve,true);
        AssertCompleted(result); AssertUnchanged(before); Assert.Equal("long path source",File.ReadAllText(Path.Combine(Destination,relative,"payload.txt")));
    }
    [Fact] public async Task ResumeDoesNotEraseEvidenceOfSourceChangesWhileStopped()
    {
        string file=Path.Combine(Source,"payload.txt"); File.WriteAllText(file,"original data");
        using var cts=new CancellationTokenSource(); var coordinator=new TransferCoordinator(Storage);
        var progress=new ImmediateProgress(p => { if (p.Phase == "임시 복사본 SHA-256 검증") cts.Cancel(); });
        await Assert.ThrowsAsync<OperationCanceledException>(() => coordinator.RunAsync(Source,Destination,VerificationMode.Sha256,ConflictPolicy.Preserve,true,progress,cts.Token));
        var job=Assert.Single(coordinator.History()); File.WriteAllText(file,"externally changed during pause");
        var result=await coordinator.RunAsync(Source,Destination,job.Mode,job.Conflicts,true,resumeId:job.Id);
        Assert.Equal("NeedsAttention",result.Info.Status); Assert.DoesNotContain("PASS",result.SourceCheck);
        Assert.Equal("externally changed during pause",File.ReadAllText(Path.Combine(Destination,"payload.txt")));
        using var db=new JobStore(result.Info.DatabasePath,true);
        Assert.NotEqual(db.Entries("source-original").Single().Hash,db.Entries("source-after").Single().Hash);
    }
    private sealed class ImmediateProgress(Action<TransferProgress> callback) : IProgress<TransferProgress>
    {
        public void Report(TransferProgress value) => callback(value);
    }
    private static void AssertCompleted(JobView view) {
        using var db = new JobStore(view.Info.DatabasePath,true);
        string detail = view.SourceCheck + " | " + System.Text.Json.JsonSerializer.Serialize(view.Summary) + " | " + string.Join("; ",db.Outcomes().Select(o => o.Path + ": " + o.Status + ": " + o.Detail));
        Assert.True(view.Info.Status == "Completed", detail);
    }
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] private static extern bool CreateHardLinkW(string name,string existing,IntPtr security);
}
