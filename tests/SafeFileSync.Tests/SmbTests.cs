using System.Security.Cryptography;
using SafeFileSync.Core;
using SafeFileSync.Infrastructure.Windows;
using Xunit;
namespace SafeFileSync.Tests;
[Trait("Category","Smb")]
public sealed class SmbTests
{
    [Fact] public async Task UserProfileToMappedDriveRootUsesExternalRecords()
    {
        string drive = Environment.GetEnvironmentVariable("SFS_SMB_DRIVE") ?? throw new InvalidOperationException("Mapped SMB fixture missing.");
        string sharedLocal = Environment.GetEnvironmentVariable("SFS_SMB_LOCAL")!;
        string fixture = Path.Combine(Path.GetTempPath(),"SfsProfile-" + Guid.NewGuid().ToString("N"));
        string source = Path.Combine(fixture,"profile"), storage = Path.Combine(fixture,"records"), appData = Path.Combine(source,"AppData","Local");
        Directory.CreateDirectory(appData); Directory.CreateDirectory(storage);
        try {
            string name = "profile-" + Guid.NewGuid().ToString("N") + ".txt";
            string file = Path.Combine(source,name); File.WriteAllText(file,"profile source unchanged");
            long time = File.GetLastWriteTimeUtc(file).Ticks;
            await Assert.ThrowsAsync<IOException>(() => new TransferCoordinator(appData).RunAsync(source,drive,VerificationMode.Sha256,ConflictPolicy.Preserve,true));
            Assert.False(Directory.Exists(Path.Combine(appData,"SafeFileSync")));
            var result = await new TransferCoordinator(storage).RunAsync(source,drive,VerificationMode.Sha256,ConflictPolicy.Preserve,true);
            using var db = new JobStore(result.Info.DatabasePath,true);
            Assert.True(result.Info.Status == "Completed",string.Join("; ",db.Outcomes().Select(o=>o.Detail)) + result.SourceCheck);
            Assert.Equal("profile source unchanged",File.ReadAllText(Path.Combine(drive,name)));
            Assert.Equal("profile source unchanged",File.ReadAllText(file)); Assert.Equal(time,File.GetLastWriteTimeUtc(file).Ticks);
            Assert.True(result.Summary.AllSourceEntriesMatch); Assert.Equal(100d,result.HashPercent);
            Assert.Throws<IOException>(() => RootSafetyLease.Acquire(sharedLocal,drive));
            Assert.Throws<IOException>(() => new TransferWorkspace(source,drive,sharedLocal));
            Assert.False(Directory.Exists(Path.Combine(sharedLocal,"SafeFileSync")));
        } finally { Directory.Delete(fixture,true); }
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task LocalAndUncTransferIsVerified(bool uncSource)
    {
        string localRoot = Environment.GetEnvironmentVariable("SFS_SMB_LOCAL") ?? throw new InvalidOperationException("Run with the Windows SMB fixture workflow.");
        string uncRoot = Environment.GetEnvironmentVariable("SFS_SMB_UNC") ?? throw new InvalidOperationException("SMB fixture missing.");
        string id = Guid.NewGuid().ToString("N"); string local = Path.Combine(localRoot,id);
        Directory.CreateDirectory(Path.Combine(local,"source")); Directory.CreateDirectory(Path.Combine(local,"dest")); Directory.CreateDirectory(Path.Combine(local,"storage"));
        try {
            string original = Path.Combine(local,"source","한글 data.bin");
            byte[] bytes = RandomNumberGenerator.GetBytes(1024 * 1024 + 31); File.WriteAllBytes(original,bytes);
            long time = File.GetLastWriteTimeUtc(original).Ticks;
            string src = Path.Combine(uncSource ? uncRoot : localRoot,id,"source");
            string dst = Path.Combine(uncSource ? localRoot : uncRoot,id,"dest");
            var result = await new TransferCoordinator(Path.Combine(local,"storage")).RunAsync(src,dst,VerificationMode.Sha256,ConflictPolicy.Preserve,true);
            using var db = new JobStore(result.Info.DatabasePath,true);
            Assert.True(result.Info.Status == "Completed", string.Join("; ",db.Outcomes().Select(o=>o.Detail)) + result.SourceCheck);
            Assert.Equal(bytes,File.ReadAllBytes(Path.Combine(local,"dest","한글 data.bin")));
            Assert.Equal(bytes,File.ReadAllBytes(original)); Assert.Equal(time,File.GetLastWriteTimeUtc(original).Ticks);
            File.WriteAllText(Path.Combine(local,"dest","한글 data.bin"),"damaged destination");
            var replaced = await new TransferCoordinator(Path.Combine(local,"storage")).RunAsync(src,dst,VerificationMode.Sha256,ConflictPolicy.ReplaceAfterVerification,true);
            using var replacedDb = new JobStore(replaced.Info.DatabasePath,true);
            Assert.True(replaced.Info.Status == "Completed",string.Join("; ",replacedDb.Outcomes().Select(o=>o.Detail)));
            Assert.Equal(bytes,File.ReadAllBytes(Path.Combine(local,"dest","한글 data.bin")));
            Assert.Equal(bytes,File.ReadAllBytes(original)); Assert.Equal(time,File.GetLastWriteTimeUtc(original).Ticks);
            Assert.Throws<IOException>(() => RootSafetyLease.Acquire(Path.Combine(local,"source"),Path.Combine(uncRoot,id,"source")));
        } finally { Directory.Delete(local,true); }
    }
}
