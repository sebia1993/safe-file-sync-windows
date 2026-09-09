using System.Security.Cryptography;
using SafeFileSync.Core;
using SafeFileSync.Infrastructure.Windows;
using Xunit;
namespace SafeFileSync.Tests;
[Trait("Category","Smb")]
public sealed class SmbTests
{
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
            Assert.Throws<IOException>(() => RootSafetyLease.Acquire(Path.Combine(local,"source"),Path.Combine(uncRoot,id,"source")));
        } finally { Directory.Delete(local,true); }
    }
}
