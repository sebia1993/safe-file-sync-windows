using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
namespace SafeFileSync.Infrastructure.Windows;

internal static class NativeFiles
{
    [StructLayout(LayoutKind.Sequential)] internal struct Information
    {
        public uint Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME Creation, Access, Write;
        public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
        public readonly string Identity => $"{Volume:X8}:{IndexHigh:X8}{IndexLow:X8}";
        public readonly long Length => ((long)SizeHigh << 32) | SizeLow;
        public readonly long WriteTicks => DateTime.FromFileTimeUtc(((long)Write.dwHighDateTime << 32) | (uint)Write.dwLowDateTime).Ticks;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out Information info);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle handle, StringBuilder path, uint length, uint flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern bool GetDiskFreeSpaceExW(string directory, out ulong available, out ulong total, out ulong free);
    internal static string Extended(string path) => path.StartsWith(@"\\") ? @"\\?\UNC\" + path[2..] : @"\\?\" + path;
    internal static SafeFileHandle OpenDirectory(string path)
    {
        // OPEN_EXISTING, BACKUP_SEMANTICS, OPEN_REPARSE_POINT; no DELETE sharing pins the name.
        var handle = CreateFileW(Extended(path), 1, 3, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        if (handle.IsInvalid) { handle.Dispose(); throw new Win32Exception(Marshal.GetLastWin32Error(), "폴더 핸들을 열 수 없습니다: " + path); }
        try {
            var info = Info(handle);
            if ((info.Attributes & 0x400) != 0 || (info.Attributes & 0x10) == 0)
                throw new IOException("링크 또는 폴더가 아닌 경로는 허용하지 않습니다: " + path);
            return handle;
        } catch { handle.Dispose(); throw; }
    }
    internal static Information Info(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out var info)) throw new Win32Exception(Marshal.GetLastWin32Error());
        if (info.IndexHigh == 0 && info.IndexLow == 0) throw new IOException("파일 시스템에서 안정적인 파일 식별자를 제공하지 않습니다.");
        return info;
    }
    internal static string FinalPath(SafeFileHandle handle)
    {
        var buffer = new StringBuilder(32768);
        var count = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, 0);
        if (count == 0 || count >= buffer.Capacity) throw new Win32Exception(Marshal.GetLastWin32Error());
        var path = buffer.ToString();
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) return @"\\" + path[8..];
        return path.StartsWith(@"\\?\") ? path[4..] : path;
    }
    internal static FileStream OpenRead(string path)
    {
        // FileShare.Read forbids concurrent content writers and deletion while hashing/copying.
        var handle = CreateFileW(Extended(path), 0x80000000, 1, IntPtr.Zero, 3, 0x08200000, IntPtr.Zero);
        if (handle.IsInvalid) { var code = Marshal.GetLastWin32Error(); handle.Dispose(); throw new Win32Exception(code); }
        var stream = new FileStream(handle, FileAccess.Read, 64 * 1024);
        try {
            if ((Info(stream.SafeFileHandle).Attributes & 0x400) != 0) throw new IOException("파일 링크 제외: " + path);
            return stream;
        } catch { stream.Dispose(); throw; }
    }
}
