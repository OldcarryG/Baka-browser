using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Baka;

/// Uses Windows Encrypting File System for the browser's local data directory, including the WebView
/// profile, history, bookmarks, and settings. EFS is transparent to the signed-in Windows account
/// and encrypts future files created under the protected directory as well.
static class ProfileProtection
{
    [DllImport("advapi32.dll", EntryPoint = "EncryptFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool EncryptFile(string path);

    [DllImport("advapi32.dll", EntryPoint = "DecryptFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool DecryptFile(string path, uint reserved);

    public static bool Apply(string path, bool enabled, out string error)
    {
        error = "";
        try
        {
            Directory.CreateDirectory(path);
            var fullPath = Path.GetFullPath(path);
            var ok = enabled ? EncryptFile(fullPath) : DecryptFile(fullPath, 0);
            if (!ok)
            {
                error = new Win32Exception(Marshal.GetLastWin32Error()).Message;
                return false;
            }
            var encrypted = (File.GetAttributes(fullPath) & FileAttributes.Encrypted) != 0;
            if (encrypted == enabled) return true;
            error = enabled
                ? "Windows returned success, but the data directory is not marked encrypted. Check for read-only files."
                : "Windows returned success, but the data directory is still marked encrypted.";
            return false;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }
}
