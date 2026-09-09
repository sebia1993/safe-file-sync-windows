using System.Diagnostics;
using SafeFileSync.Core;
using SafeFileSync.Infrastructure.Windows;
using Xunit;
using Xunit.Abstractions;
namespace SafeFileSync.Tests;
public sealed class PersistenceTests(ITestOutputHelper output)
{
    [Fact] public void HundredThousandEntriesRemainQueryableWithoutMaterializingWholeTree()
    {
        string directory = Path.Combine(Path.GetTempPath(),"Sfs-manifest-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        try {
            using var db = new JobStore(Path.Combine(directory,"manifest.sqlite"));
            IEnumerable<ScanEntry> Entries(bool changed) {
                for (int i=0;i<100000;i++) yield return new($"folder/{i:D6}.dat",EntryKind.File,changed && i==99999 ? 2 : 1,123,"hash");
            }
            var timer = Stopwatch.StartNew();
            db.SaveSnapshot("source",Entries(false),default); db.SaveSnapshot("destination",Entries(true),default);
            var summary = db.Summary("source","destination",VerificationMode.Sha256);
            Assert.Equal(99999,summary.Matched); Assert.Equal(1,summary.Different); Assert.False(summary.AllSourceEntriesMatch);
            Assert.Equal(100000,db.Entries("source").LongCount());
            output.WriteLine($"100,000-entry source + destination persisted and compared in {timer.Elapsed.TotalSeconds:F2}s; managed memory {GC.GetTotalMemory(false)/1048576d:F1} MiB.");
        } finally { Directory.Delete(directory,true); }
    }
}
