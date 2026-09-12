using System.Security.AccessControl;
using System.Security.Principal;
using SafeFileSync.Core;

namespace SafeFileSync.Infrastructure.Windows;

public static class RecordStorageLocation
{
    // Use the Windows system drive, not the current directory or the user's profile drive.
    // Resolving a location must never create it: sources are not known yet during UI startup.
    public static string DefaultParent => Path.GetPathRoot(Environment.SystemDirectory)
        ?? throw new IOException("Windows 시스템 드라이브를 확인할 수 없습니다.");
    public static string RootFor(string parent) => Path.Combine(PathSafetyService.Normalize(parent), "SafeFileSync");
    public static bool IsDefaultParent(string parent) => PathSafetyService.Normalize(parent)
        .Equals(DefaultParent, StringComparison.OrdinalIgnoreCase);

    // Caller must validate source/destination separation and pin the existing parent first.
    public static void CreatePrivateDirectory(string path)
    {
        var user = CurrentUser();
        var security = new DirectorySecurity();
        security.SetOwner(user);
        security.SetAccessRuleProtection(true, false);
        foreach (var identity in TrustedIdentities(user))
            security.AddAccessRule(new FileSystemAccessRule(identity, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None, AccessControlType.Allow));
        // This API applies the DACL at creation and never modifies an existing directory's ACL.
        security.CreateDirectory(NativeFiles.Extended(path));
        ValidatePrivateDirectory(path);
    }

    public static void ValidatePrivateDirectory(string path)
    {
        var user = CurrentUser();
        var trusted = TrustedIdentities(user);
        var security = new DirectoryInfo(NativeFiles.Extended(path)).GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner);
        bool userControlsChildren = false;
        if (!security.AreAccessRulesProtected || security.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner || !trusted.Contains(owner))
            throw UntrustedDirectory();
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier))) {
            if (rule.AccessControlType != AccessControlType.Allow) continue;
            if (!trusted.Contains((SecurityIdentifier)rule.IdentityReference)) throw UntrustedDirectory();
            if (rule.IdentityReference.Equals(user) && (rule.FileSystemRights & FileSystemRights.FullControl) == FileSystemRights.FullControl
                && rule.InheritanceFlags.HasFlag(InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit)
                && rule.PropagationFlags == PropagationFlags.None) userControlsChildren = true;
        }
        if (!userControlsChildren) throw UntrustedDirectory();
    }

    private static SecurityIdentifier CurrentUser()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User ?? throw new UnauthorizedAccessException("현재 Windows 계정을 확인할 수 없습니다.");
    }
    private static HashSet<SecurityIdentifier> TrustedIdentities(SecurityIdentifier user) => [user,
        new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
        new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null)];
    private static UnauthorizedAccessException UntrustedDirectory() => new(
        "기본 기록 폴더를 현재 계정 전용으로 확인할 수 없습니다. 기록 위치를 다른 쓰기 가능한 폴더로 변경하세요.");
}
