using System.Runtime.InteropServices;
using System.Text;

namespace PCAutobackup.Core;

public static class ExternalVolume
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeNameForVolumeMountPoint(string mountPoint, StringBuilder volumeName, uint bufferLength);

    public static string GetIdentity(string directory)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(directory))!;
        var buffer = new StringBuilder(1024);
        if (!GetVolumeNameForVolumeMountPoint(root, buffer, (uint)buffer.Capacity))
            throw new IOException("ไม่พบดิสก์ภายนอกหรืออ่านรหัสดิสก์ไม่ได้ กรุณาเสียบดิสก์ก่อนบันทึก");
        return buffer.ToString();
    }

    public static string ResolvePath(string configuredPath, string volumeId)
    {
        var oldRoot = Path.GetPathRoot(Path.GetFullPath(configuredPath))!;
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.IsReady && string.Equals(GetIdentity(drive.RootDirectory.FullName), volumeId, StringComparison.OrdinalIgnoreCase))
                    return Path.Combine(drive.RootDirectory.FullName, configuredPath[oldRoot.Length..]);
            }
            catch (IOException) { }
        }
        throw new IOException("ยังไม่พบดิสก์สำรองเดิม งานจะไม่เขียนลงดิสก์อื่นที่ใช้อักษรไดรฟ์เดียวกัน");
    }
}
