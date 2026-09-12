using System.ComponentModel;
using SafeFileSync.Core;
using Xunit;

namespace SafeFileSync.Tests;

public sealed class DiagnosticCodeTests
{
    [Theory]
    [InlineData(DiagnosticCode.InvalidInput, "S01")]
    [InlineData(DiagnosticCode.PathOverlap, "S02")]
    [InlineData(DiagnosticCode.RecordLocation, "S03")]
    [InlineData(DiagnosticCode.AccessDenied, "S04")]
    [InlineData(DiagnosticCode.PathUnavailable, "S05")]
    [InlineData(DiagnosticCode.Network, "S06")]
    [InlineData(DiagnosticCode.FileInUse, "S07")]
    [InlineData(DiagnosticCode.InsufficientSpace, "S08")]
    [InlineData(DiagnosticCode.UnsafeLink, "S09")]
    [InlineData(DiagnosticCode.SourceChanged, "S10")]
    [InlineData(DiagnosticCode.CopyFailed, "S11")]
    [InlineData(DiagnosticCode.VerificationIncomplete, "S12")]
    [InlineData(DiagnosticCode.DestinationConflict, "S13")]
    [InlineData(DiagnosticCode.JobRecord, "S14")]
    [InlineData(DiagnosticCode.Cancelled, "S15")]
    [InlineData(DiagnosticCode.Io, "S16")]
    [InlineData(DiagnosticCode.Unknown, "S99")]
    public void PublicCodesRemainStableAndDescriptionsAreKorean(DiagnosticCode code, string expected)
    {
        Assert.Equal(expected, DiagnosticCodes.Format(code));
        Assert.Matches("[가-힣]", DiagnosticCodes.Description(code));
    }

    public static TheoryData<int, DiagnosticCode> NativeErrors => new()
    {
        { 5, DiagnosticCode.AccessDenied }, { 1314, DiagnosticCode.AccessDenied }, { 1326, DiagnosticCode.AccessDenied },
        { 53, DiagnosticCode.Network }, { 54, DiagnosticCode.Network }, { 59, DiagnosticCode.Network },
        { 64, DiagnosticCode.Network }, { 67, DiagnosticCode.Network }, { 121, DiagnosticCode.Network },
        { 1201, DiagnosticCode.Network }, { 1222, DiagnosticCode.Network }, { 1231, DiagnosticCode.Network },
        { 1232, DiagnosticCode.Network }, { 1236, DiagnosticCode.Network }, { 2250, DiagnosticCode.Network },
        { 2, DiagnosticCode.PathUnavailable }, { 3, DiagnosticCode.PathUnavailable },
        { 15, DiagnosticCode.PathUnavailable }, { 21, DiagnosticCode.PathUnavailable },
        { 32, DiagnosticCode.FileInUse }, { 33, DiagnosticCode.FileInUse },
        { 39, DiagnosticCode.InsufficientSpace }, { 112, DiagnosticCode.InsufficientSpace }
    };

    [Theory]
    [MemberData(nameof(NativeErrors))]
    public void NativeAndHresultFailuresUseTheSameBoundedCode(int nativeError, DiagnosticCode expected)
    {
        const string privateMessage = @"diagnostic-fixture-private: \\fixture-server\fixture-share\account-file.txt";
        var native = new Win32Exception(nativeError, privateMessage);
        var wrappedHresult = new IOException(privateMessage, unchecked((int)(0x80070000u | (uint)nativeError)));
        Assert.Equal(expected, DiagnosticCodes.FromException(native));
        Assert.Equal(expected, DiagnosticCodes.FromException(wrappedHresult));
        string visible = DiagnosticCodes.Format(DiagnosticCodes.FromException(native));
        Assert.Matches("^S(?:0[1-9]|1[0-6]|99)$", visible);
        Assert.DoesNotContain("fixture", visible, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(65535)]
    [InlineData(987654)]
    public void UnknownNativeErrorsNeverBecomeRawPublicCodes(int nativeError)
    {
        var error = new Win32Exception(nativeError, "arbitrary private failure");
        Assert.Equal(DiagnosticCode.Io, DiagnosticCodes.FromException(error));
        Assert.Equal("S16", DiagnosticCodes.Format(DiagnosticCodes.FromException(error)));
    }

    [Fact]
    public void StandardExceptionsAndWrappedCausesUseTheirTypes()
    {
        Assert.Equal(DiagnosticCode.InvalidInput, DiagnosticCodes.FromException(new ArgumentException("arbitrary")));
        Assert.Equal(DiagnosticCode.AccessDenied, DiagnosticCodes.FromException(new UnauthorizedAccessException("arbitrary")));
        Assert.Equal(DiagnosticCode.PathUnavailable, DiagnosticCodes.FromException(new FileNotFoundException("arbitrary", @"C:\fixture-private\missing.txt")));
        Assert.Equal(DiagnosticCode.PathUnavailable, DiagnosticCodes.FromException(new DirectoryNotFoundException("arbitrary")));
        Assert.Equal(DiagnosticCode.Cancelled, DiagnosticCodes.FromException(new OperationCanceledException("arbitrary")));
        Assert.Equal(DiagnosticCode.Cancelled, DiagnosticCodes.FromException(new TaskCanceledException("arbitrary")));
        Assert.Equal(DiagnosticCode.AccessDenied, DiagnosticCodes.FromException(new IOException("outer", new Exception("wrapper", new UnauthorizedAccessException("inner")))));
    }

    [Fact]
    public void SqliteAndJsonRecordFailuresHaveJobRecordCode()
    {
        var sqlite = new Microsoft.Data.Sqlite.SqliteException("fixture private database path", 11);
        Assert.Equal(DiagnosticCode.JobRecord, DiagnosticCodes.FromException(sqlite));
        Assert.Equal(DiagnosticCode.JobRecord, DiagnosticCodes.FromException(new IOException("fixture wrapper", sqlite)));
        Assert.Equal(DiagnosticCode.JobRecord, DiagnosticCodes.FromException(new System.Text.Json.JsonException("fixture corrupted manifest")));
        Assert.Equal("S14", DiagnosticCodes.Format(DiagnosticCodes.FromException(sqlite)));
    }

    [Fact]
    public void ArbitraryMessagesAndStringDataCannotSpoofOrLeakAnErrorCode()
    {
        const string sensitive = @"fixture-private-marker C:\fixture-user\source \\fixture-server\share S04 access denied network 1236 token=fixture-only";
        var io = new IOException(sensitive);
        io.Data["SafeFileSync.DiagnosticCode"] = DiagnosticCode.AccessDenied;
        io.Data["ErrorCode"] = "S06 " + sensitive;
        io.Data["NativeErrorCode"] = 32;
        Assert.Equal(DiagnosticCode.Io, DiagnosticCodes.FromException(io));
        Assert.Equal("S16", DiagnosticCodes.Format(DiagnosticCodes.FromException(io)));
        var unknown = new Exception(sensitive);
        unknown.Data["code"] = sensitive;
        Assert.Equal(DiagnosticCode.Unknown, DiagnosticCodes.FromException(unknown));
        Assert.Equal("S99", DiagnosticCodes.Format(DiagnosticCodes.FromException(unknown)));
        Assert.DoesNotContain("fixture-private-marker", DiagnosticCodes.Description(DiagnosticCodes.FromException(unknown)));
    }

    [Fact]
    public void TagPreservesExactExceptionAndOverridesItsInnerCause()
    {
        var original = new IOException("private fixture message", new UnauthorizedAccessException("inner"));
        original.Data["fixture-metadata"] = "keep";
        var tagged = DiagnosticCodes.Tag(original, DiagnosticCode.SourceChanged);
        Assert.Same(original, tagged);
        Assert.IsType<IOException>(tagged);
        Assert.Equal("private fixture message", tagged.Message);
        Assert.Equal("keep", tagged.Data["fixture-metadata"]);
        Assert.Equal(DiagnosticCode.SourceChanged, DiagnosticCodes.FromException(tagged));
        Assert.Equal("S10", DiagnosticCodes.Format(DiagnosticCodes.FromException(tagged)));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(65535)]
    [InlineData(int.MaxValue)]
    public void UnknownEnumValuesAlwaysNormalizeToTheFixedUnknownCode(int value)
    {
        var code = (DiagnosticCode)value;
        Assert.Equal("S99", DiagnosticCodes.Format(code));
        Assert.Equal(DiagnosticCodes.Description(DiagnosticCode.Unknown), DiagnosticCodes.Description(code));
        var original = new IOException("fixture private message");
        Assert.Same(original, DiagnosticCodes.Tag(original, code));
        Assert.Equal(DiagnosticCode.Unknown, DiagnosticCodes.FromException(original));
        Assert.Equal(DiagnosticCode.Unknown, DiagnosticCodes.Prefer(null, code));
        Assert.Equal(DiagnosticCode.SourceChanged, DiagnosticCodes.Prefer(code, DiagnosticCode.SourceChanged));
    }

    [Fact]
    public void AggregationIsIndependentOfSourceOrderAndKeepsSpecificCauses()
    {
        var codes = Enum.GetValues<DiagnosticCode>();
        foreach (var first in codes)
        {
            Assert.Equal(first, DiagnosticCodes.Prefer(null, first));
            Assert.Equal(first, DiagnosticCodes.Prefer(first, first));
            foreach (var second in codes)
                Assert.Equal(DiagnosticCodes.Prefer(first, second), DiagnosticCodes.Prefer(second, first));
        }
        foreach (var fallback in new[] { DiagnosticCode.Unknown, DiagnosticCode.Io, DiagnosticCode.VerificationIncomplete })
            Assert.Equal(DiagnosticCode.SourceChanged, DiagnosticCodes.Prefer(fallback, DiagnosticCode.SourceChanged));
    }
}
