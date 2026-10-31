using System.Security.Cryptography;

namespace ShinkansenVault;

/// <summary>
/// Best-effort removal of a plain-text password file: overwrite with random bytes, then delete.
/// On SSDs and synced folders (OneDrive, Nextcloud, Syncthing) old blocks or copies may survive,
/// so export files should go to a local, non-synced folder and live there only for minutes.
/// </summary>
public static class SecureFile
{
    public static void Wipe(string path)
    {
        if (!File.Exists(path)) return;
        var length = new FileInfo(path).Length;
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None))
        {
            var buffer = new byte[64 * 1024];
            long written = 0;
            while (written < length)
            {
                RandomNumberGenerator.Fill(buffer);
                int n = (int)Math.Min(buffer.Length, length - written);
                fs.Write(buffer, 0, n);
                written += n;
            }
            fs.Flush(flushToDisk: true);
            fs.SetLength(0);
        }
        File.Delete(path);
    }
}
