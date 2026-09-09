using SafeFileSync.Core;
using SafeFileSync.Infrastructure.Windows;
using Xunit;
namespace SafeFileSync.Tests;
public sealed class SafetyTests
{
    [Theory]
    [InlineData(@"C:\data", @"c:\DATA\")]
    [InlineData(@"C:\data", @"C:\data\child")]
    [InlineData(@"C:\data\child", @"C:\data")]
    [InlineData(@"C:\", @"C:\child")]
    [InlineData(@"\\server\share\data", @"\\SERVER\SHARE\data\child")]
    [InlineData(@"C:/data", @"C:\data")]
    public void OverlapRejected(string source, string destination) =>
        Assert.Throws<ArgumentException>(() => PathSafetyService.ValidatePair(source, destination));

    [Theory]
    [InlineData(@"C:\data", @"C:\database")]
    [InlineData(@"C:\data", @"D:\data")]
    [InlineData(@"\\server\share", @"\\server\share2")]
    [InlineData(@"C:\한글 폴더", @"\\example\share\한글 폴더")]
    public void SeparateRootsAccepted(string source, string destination) => PathSafetyService.ValidatePair(source, destination);

    [Theory]
    [InlineData("")][InlineData("relative")][InlineData(@"C:relative")]
    [InlineData(@"\rooted")][InlineData(@"\\server")]
    [InlineData(@"\\?\C:\data")][InlineData(@"\\.\C:\data")]
    [InlineData(@"C:\data\..\other")][InlineData(@"C:\data\.")]
    [InlineData(@"C:\data.")][InlineData(@"C:\data \child")]
    [InlineData(@"C:\data:stream")][InlineData(@"C:\NUL.txt")]
    [InlineData(@"C:\COM1")][InlineData(@"C:\LPT¹")]
    [InlineData(@"C:\a*\b")][InlineData(@"C:\a""b")]
    [InlineData(@"C:\a\\b")][InlineData("C:\\line\nbreak")]
    public void AmbiguousPathsRejected(string path) => Assert.Throws<ArgumentException>(() => PathSafetyService.Normalize(path));

    [Theory]
    [InlineData(FileOperation.Write)][InlineData(FileOperation.Delete)]
    [InlineData(FileOperation.Move)][InlineData(FileOperation.Rename)]
    [InlineData(FileOperation.Truncate)][InlineData(FileOperation.Create)]
    public void SourceMutationRejected(FileOperation operation)
    {
        var guard = new SourceProtectionGuard(@"C:\source");
        Assert.Throws<InvalidOperationException>(() => guard.Demand(@"c:\SOURCE\file", operation));
        Assert.Throws<InvalidOperationException>(() => guard.Demand(@"C:\source", operation));
        guard.Demand(@"C:\source2\file", operation);
    }
    [Fact] public void ReadPermitted() => new SourceProtectionGuard(@"C:\source").Demand(@"C:\source\file", FileOperation.Read);
    [Fact] public void UnknownOperationRejected() => Assert.Throws<ArgumentOutOfRangeException>(() => new SourceProtectionGuard(@"C:\source").Demand(@"D:\file", (FileOperation)100));

    [Theory]
    [InlineData("/MOV")][InlineData("/MOVE")][InlineData("/MIR")][InlineData("/PURGE")]
    [InlineData("/mov")][InlineData("/mir ")][InlineData("/LOG:C:\\source\\log")]
    [InlineData("/JOB:evil")][InlineData("/SAVE:evil")][InlineData("/B")]
    [InlineData("/E /MOVE")][InlineData("/MT:999")][InlineData("/M")]
    public void DangerousOrUnknownOptionsRejected(string option) => Assert.Throws<ArgumentException>(() => RobocopyArgumentBuilder.ValidateOptions([option]));

    [Fact] public void PreviewIsFixedAndHasSeparatedPathArguments()
    {
        var args = RobocopyArgumentBuilder.BuildPreview(@"C:\source folder", @"\\example\share\target folder");
        Assert.Equal(@"C:\source folder", args[0]);
        Assert.Equal(@"\\example\share\target folder", args[1]);
        Assert.Equal(new[] { "/E", "/Z", "/R:3", "/W:2", "/COPY:DAT", "/DCOPY:DAT", "/XJ" }, args.Skip(2));
        RobocopyArgumentBuilder.ValidateOptions(args.Skip(2));
    }
    [Fact] public void BuilderCannotBypassOverlapGuard() => Assert.Throws<ArgumentException>(() => RobocopyArgumentBuilder.BuildPreview(@"C:\source", @"C:\source\target"));
}
