using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using SafeFileSync.Core;
using SafeFileSync.Infrastructure.Windows;
using Xunit;

namespace SafeFileSync.Tests;

public sealed class RecordStorageLocationTests : IDisposable
{
    private readonly string fixture = Path.Combine(Path.GetTempPath(), "SafeFileSync-private-records-" + Guid.NewGuid().ToString("N"));
    private static SecurityIdentifier CurrentUser
    {
        get { using var identity = WindowsIdentity.GetCurrent(); return identity.User!; }
    }
    private static SecurityIdentifier SystemUser => new(WellKnownSidType.LocalSystemSid, null);
    private static SecurityIdentifier Administrators => new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private const InheritanceFlags Children = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
    private const AccessControlSections DescriptorSections = AccessControlSections.Access | AccessControlSections.Owner;

    public RecordStorageLocationTests() => Directory.CreateDirectory(fixture);
    public void Dispose() { if (Directory.Exists(fixture)) Directory.Delete(fixture, true); }

    private static DirectorySecurity PrivateFixtureAcl(FileSystemRights currentRights = FileSystemRights.FullControl, InheritanceFlags currentInheritance = Children)
    {
        var security = new DirectorySecurity();
        security.SetOwner(CurrentUser);
        security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new(CurrentUser, currentRights, currentInheritance, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new(SystemUser, FileSystemRights.FullControl, Children, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new(Administrators, FileSystemRights.FullControl, Children, PropagationFlags.None, AccessControlType.Allow));
        return security;
    }

    private static string Descriptor(DirectoryInfo directory) => directory.GetAccessControl(DescriptorSections).GetSecurityDescriptorSddlForm(DescriptorSections);

    private static void AssertAllowedTrusteesOnly(FileSystemSecurity security)
    {
        var trusted = new[] { CurrentUser.Value, SystemUser.Value, Administrators.Value };
        var owner = (SecurityIdentifier)security.GetOwner(typeof(SecurityIdentifier))!;
        Assert.Contains(owner.Value, trusted);
        var allowed = security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>()
            .Where(rule => rule.AccessControlType == AccessControlType.Allow).ToArray();
        Assert.NotEmpty(allowed);
        Assert.All(allowed, rule => Assert.Contains(rule.IdentityReference.Value, trusted));
        foreach (string identity in trusted)
            Assert.Contains(allowed, rule => rule.IdentityReference.Value == identity && (rule.FileSystemRights & FileSystemRights.FullControl) == FileSystemRights.FullControl);
    }

    private static void AssertAccessDenied(Action action)
    {
        var error = Assert.Throws<UnauthorizedAccessException>(action);
        Assert.Equal(DiagnosticCode.AccessDenied, DiagnosticCodes.FromException(error));
        Assert.Equal("S04", DiagnosticCodes.Format(DiagnosticCodes.FromException(error)));
    }

    [Fact]
    public void DefaultLocationUsesSystemDriveRootOutsideTheActiveUserProfile()
    {
        // Path calculations only: never create, enumerate or modify the real default record folder.
        string expected = Path.GetPathRoot(Environment.SystemDirectory)!;
        Assert.Equal(expected, RecordStorageLocation.DefaultParent, StringComparer.OrdinalIgnoreCase);
        string actual = RecordStorageLocation.RootFor(RecordStorageLocation.DefaultParent);
        Assert.Equal(Path.Combine(expected, "SafeFileSync"), actual, StringComparer.OrdinalIgnoreCase);
        Assert.True(RecordStorageLocation.IsDefaultParent(expected));
        Assert.True(RecordStorageLocation.IsDefaultParent(expected.ToLowerInvariant()));
        Assert.False(RecordStorageLocation.IsDefaultParent(fixture));
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        PathSafetyService.ValidatePair(profile, actual);
        PathSafetyService.ValidatePair(@"\\fixture-server\fixture-share", actual);
        Assert.Throws<ArgumentException>(() => PathSafetyService.ValidatePair(expected, actual));
    }

    [Fact]
    public void CustomRecordParentStillAppendsExactlyOneApplicationFolder()
    {
        string parent = Path.Combine(fixture, "custom-record-parent");
        Assert.Equal(Path.Combine(parent, "SafeFileSync"), RecordStorageLocation.RootFor(parent), StringComparer.OrdinalIgnoreCase);
        Assert.False(RecordStorageLocation.IsDefaultParent(parent));
        Assert.False(Directory.Exists(parent));
    }

    [Fact]
    public void NewlyCreatedPrivateDirectoryProtectsItsAclAndSqliteChildrenInheritTrustedAccessOnly()
    {
        string path = Path.Combine(fixture, "SafeFileSync");
        RecordStorageLocation.CreatePrivateDirectory(path);
        RecordStorageLocation.ValidatePrivateDirectory(path);
        var directory = new DirectoryInfo(path);
        var security = directory.GetAccessControl(DescriptorSections);
        Assert.True(security.AreAccessRulesProtected);
        AssertAllowedTrusteesOnly(security);
        Assert.Contains(security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>(),
            rule => rule.AccessControlType == AccessControlType.Allow && rule.IdentityReference.Value == CurrentUser.Value
                && !rule.IsInherited && rule.InheritanceFlags == Children && rule.PropagationFlags == PropagationFlags.None
                && (rule.FileSystemRights & FileSystemRights.FullControl) == FileSystemRights.FullControl);

        string child = Path.Combine(path, "child");
        Directory.CreateDirectory(child);
        File.WriteAllText(Path.Combine(child, "report.html"), "synthetic report fixture");
        using var db = new JobStore(Path.Combine(path, "fixture.sqlite"));
        db.Set("fixture", "synthetic record data");
        Assert.True(File.Exists(Path.Combine(path, "fixture.sqlite-wal")));
        AssertAllowedTrusteesOnly(new DirectoryInfo(child).GetAccessControl(DescriptorSections));
        foreach (string file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            AssertAllowedTrusteesOnly(new FileInfo(file).GetAccessControl(DescriptorSections));
        Assert.Equal("synthetic record data", db.Get("fixture"));
    }

    [Theory]
    [InlineData(WellKnownSidType.WorldSid)]
    [InlineData(WellKnownSidType.AuthenticatedUserSid)]
    [InlineData(WellKnownSidType.BuiltinUsersSid)]
    public void ExistingDirectoryGrantingOtherUsersReadAccessIsRejectedWithoutChangingAclOrContent(WellKnownSidType other)
    {
        var directory = Directory.CreateDirectory(Path.Combine(fixture, "existing-broad"));
        string marker = Path.Combine(directory.FullName, "existing.txt");
        File.WriteAllText(marker, "existing records must stay unchanged");
        var security = PrivateFixtureAcl();
        security.AddAccessRule(new(new SecurityIdentifier(other, null), FileSystemRights.ReadAndExecute, Children, PropagationFlags.None, AccessControlType.Allow));
        directory.SetAccessControl(security);
        string before = Descriptor(directory);
        long time = File.GetLastWriteTimeUtc(marker).Ticks;

        AssertAccessDenied(() => RecordStorageLocation.ValidatePrivateDirectory(directory.FullName));
        AssertAccessDenied(() => RecordStorageLocation.CreatePrivateDirectory(directory.FullName));
        Assert.Equal(before, Descriptor(directory));
        Assert.Equal("existing records must stay unchanged", File.ReadAllText(marker));
        Assert.Equal(time, File.GetLastWriteTimeUtc(marker).Ticks);
        Assert.Single(Directory.EnumerateFileSystemEntries(directory.FullName));
    }

    [Fact]
    public void ExistingProtectedPrivateDirectoryIsReusedWithoutAclOrContentChanges()
    {
        var directory = Directory.CreateDirectory(Path.Combine(fixture, "existing-private"));
        directory.SetAccessControl(PrivateFixtureAcl());
        string marker = Path.Combine(directory.FullName, "existing.txt");
        File.WriteAllText(marker, "existing private records");
        string before = Descriptor(directory);
        long time = File.GetLastWriteTimeUtc(marker).Ticks;

        RecordStorageLocation.ValidatePrivateDirectory(directory.FullName);
        RecordStorageLocation.CreatePrivateDirectory(directory.FullName);
        Assert.Equal(before, Descriptor(directory));
        Assert.Equal("existing private records", File.ReadAllText(marker));
        Assert.Equal(time, File.GetLastWriteTimeUtc(marker).Ticks);
        Assert.Single(Directory.EnumerateFileSystemEntries(directory.FullName));
    }

    [Fact]
    public void ExistingUnprotectedAclIsRejectedWithoutTryingToRepairIt()
    {
        var directory = Directory.CreateDirectory(Path.Combine(fixture, "unprotected"));
        var security = PrivateFixtureAcl();
        security.SetAccessRuleProtection(false, true);
        directory.SetAccessControl(security);
        string before = Descriptor(directory);
        Assert.False(directory.GetAccessControl().AreAccessRulesProtected);

        AssertAccessDenied(() => RecordStorageLocation.CreatePrivateDirectory(directory.FullName));
        Assert.Equal(before, Descriptor(directory));
        Assert.Empty(Directory.EnumerateFileSystemEntries(directory.FullName));
    }

    [Theory]
    [InlineData(FileSystemRights.ReadAndExecute, Children)]
    [InlineData(FileSystemRights.FullControl, InheritanceFlags.None)]
    public void CurrentUserRequiresFullControlForTheDirectoryAndInheritedChildren(FileSystemRights rights, InheritanceFlags inheritance)
    {
        var directory = Directory.CreateDirectory(Path.Combine(fixture, "incomplete-current-user"));
        var original = directory.GetAccessControl(DescriptorSections);
        directory.SetAccessControl(PrivateFixtureAcl(rights, inheritance));
        try
        {
            string before = Descriptor(directory);
            AssertAccessDenied(() => RecordStorageLocation.ValidatePrivateDirectory(directory.FullName));
            AssertAccessDenied(() => RecordStorageLocation.CreatePrivateDirectory(directory.FullName));
            Assert.Equal(before, Descriptor(directory));
        }
        finally { directory.SetAccessControl(original); }
    }

    [Fact]
    public void DeniedParentCreationReturnsAccessCodeWithoutCreatingOrChangingAnything()
    {
        var parent = Directory.CreateDirectory(Path.Combine(fixture, "denied-parent"));
        var original = parent.GetAccessControl(DescriptorSections);
        var denied = parent.GetAccessControl(DescriptorSections);
        denied.AddAccessRule(new(CurrentUser, FileSystemRights.CreateDirectories, InheritanceFlags.None, PropagationFlags.None, AccessControlType.Deny));
        parent.SetAccessControl(denied);
        string target = Path.Combine(parent.FullName, "SafeFileSync");
        try
        {
            string before = Descriptor(parent);
            AssertAccessDenied(() => RecordStorageLocation.CreatePrivateDirectory(target));
            Assert.False(Directory.Exists(target));
            Assert.Equal(before, Descriptor(parent));
            Assert.Empty(Directory.EnumerateFileSystemEntries(parent.FullName));
        }
        finally { parent.SetAccessControl(original); }
    }

    [GitHubHostedWindowsFact]
    public async Task HostedRunnerDefaultRecordsSupportProfileSourceHistoryAndResume()
    {
        // This is the sole test allowed to create the real default root, on an ephemeral hosted Windows runner only.
        Assert.True(OperatingSystem.IsWindows());
        Assert.Equal("true", Environment.GetEnvironmentVariable("GITHUB_ACTIONS"));
        Assert.Equal("github-hosted", Environment.GetEnvironmentVariable("RUNNER_ENVIRONMENT"));
        string defaultRoot = RecordStorageLocation.RootFor(RecordStorageLocation.DefaultParent);
        // Before entering the cleanup scope, prove no directory, file or reparse entry already exists.
        Assert.Throws<FileNotFoundException>(() => File.GetAttributes(defaultRoot));

        string profileTemp = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Temp");
        Assert.True(Directory.Exists(profileTemp));
        string source = Path.Combine(profileTemp, "SafeFileSync-hosted-source-" + Guid.NewGuid().ToString("N"));
        string destination = Path.Combine(fixture, "hosted-destination");
        bool attemptedDefaultCreation = false;
        try
        {
            Directory.CreateDirectory(source);
            Directory.CreateDirectory(destination);
            string original = Path.Combine(source, "payload.txt");
            File.WriteAllText(original, "ephemeral hosted-runner source fixture");
            string beforeHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(original)));
            long beforeTime = File.GetLastWriteTimeUtc(original).Ticks;
            var coordinator = new TransferCoordinator();
            Assert.Empty(coordinator.History());
            Assert.Throws<FileNotFoundException>(() => File.GetAttributes(defaultRoot));

            TransferSource[] sources = [new(source, "profile-source")];
            attemptedDefaultCreation = true;
            var copied = await coordinator.RunAsync(sources, destination, VerificationMode.Sha256, ConflictPolicy.Preserve, true);
            Assert.Equal("Completed", copied.Info.Status);
            Assert.Null(copied.ErrorCode);
            Assert.Equal(100d, copied.HashPercent);
            Assert.Equal(Path.Combine(defaultRoot, copied.Info.Id + ".sqlite"), copied.Info.DatabasePath, StringComparer.OrdinalIgnoreCase);
            RecordStorageLocation.ValidatePrivateDirectory(defaultRoot);
            AssertAllowedTrusteesOnly(new DirectoryInfo(defaultRoot).GetAccessControl(DescriptorSections));
            AssertAllowedTrusteesOnly(new FileInfo(copied.Info.DatabasePath).GetAccessControl(DescriptorSections));
            Assert.Equal(beforeHash, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(original))));
            Assert.Equal(beforeTime, File.GetLastWriteTimeUtc(original).Ticks);
            Assert.Equal(beforeHash, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(destination, "profile-source", "payload.txt")))));

            var reopened = new TransferCoordinator();
            var saved = Assert.Single(reopened.History());
            Assert.Equal(copied.Info.Id, saved.Id);
            Assert.Equal(sources, saved.Sources);
            var resumed = await reopened.RunAsync(sources, destination, saved.Mode, saved.Conflicts, true, resumeId: saved.Id);
            Assert.Equal("Completed", resumed.Info.Status);
            Assert.Null(resumed.ErrorCode);
            Assert.Equal(100d, resumed.HashPercent);
            Assert.Equal(copied.Info.DatabasePath, resumed.Info.DatabasePath, StringComparer.OrdinalIgnoreCase);
            Assert.Single(reopened.History());
            Assert.Equal(beforeHash, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(original))));
            Assert.Equal(beforeTime, File.GetLastWriteTimeUtc(original).Ticks);
        }
        finally
        {
            // Existing default roots fail before this scope; only this test's newly created hosted fixture is removed.
            if (attemptedDefaultCreation && Directory.Exists(defaultRoot)) Directory.Delete(defaultRoot, true);
            if (Directory.Exists(source)) Directory.Delete(source, true);
        }
    }
}

public sealed class GitHubHostedWindowsFactAttribute : FactAttribute
{
    public GitHubHostedWindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows()
            || Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true"
            || Environment.GetEnvironmentVariable("RUNNER_ENVIRONMENT") != "github-hosted")
            Skip = "Uses the real default record directory only on an ephemeral GitHub-hosted Windows runner.";
    }
}
