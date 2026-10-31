using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace ShinkansenVault.Cli;

/// <summary>
/// Keeps the vault unlocked for the rest of this Windows sign-in, for every svault process (CLI and each
/// agent session's `svault mcp`), so the human approves once a day instead of on every use.
///
/// The master key is stored in session.bin next to the vault, sealed with DPAPI (CurrentUser) and bound to
///   - this sign-in: the logon session id of the Windows token changes on every sign-in, including after a
///     shutdown with Fast Startup (which keeps the kernel's uptime but signs the user out);
///   - this boot: boot time from the uptime counter, so a restart always locks;
///   - this vault: a fingerprint of the vault id and wrapped key.
/// Anything that does not match is wiped and the vault asks again. `svault lock` wipes it at once.
/// While unlocked, any program running as this Windows user can use the vault without a prompt.
/// </summary>
internal sealed class SessionUnlockCache : IUnlockCache
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("ShinkansenVault/session/v1");
    /// <summary>Clock corrections move the computed boot time a little; a restart moves it by much more.</summary>
    private const long BootToleranceSeconds = 300;

    private readonly string _path;

    public SessionUnlockCache(string home) => _path = Path.Combine(home, "session.bin");

    private sealed record Payload(string Fingerprint, string Logon, long Boot, string Key);

    public byte[]? TryGet(string vaultFingerprint)
    {
        if (!File.Exists(_path)) return null;
        byte[]? plain = null;
        try
        {
            plain = ProtectedData.Unprotect(File.ReadAllBytes(_path), Entropy, DataProtectionScope.CurrentUser);
            var p = JsonSerializer.Deserialize<Payload>(plain);
            if (p == null || p.Logon != LogonId() || Math.Abs(p.Boot - BootTime()) > BootToleranceSeconds)
            {
                Clear();                     // another sign-in or a restart: the old unlock is over
                return null;
            }
            return p.Fingerprint == vaultFingerprint ? Convert.FromBase64String(p.Key) : null;
        }
        catch (Exception e) when (e is CryptographicException or JsonException or IOException or FormatException
                                     or UnauthorizedAccessException)
        {
            Clear();
            return null;
        }
        finally
        {
            VaultCrypto.Zero(plain);
        }
    }

    public void Store(string vaultFingerprint, byte[] masterKey)
    {
        var plain = JsonSerializer.SerializeToUtf8Bytes(
            new Payload(vaultFingerprint, LogonId(), BootTime(), Convert.ToBase64String(masterKey)));
        var tmp = $"{_path}.{Environment.ProcessId}.tmp";   // two agent sessions may unlock at the same moment
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllBytes(tmp, ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser));
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or CryptographicException)
        {
            // Not kept: this use already succeeded, the next one simply asks again.
            try { File.Delete(tmp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        finally
        {
            VaultCrypto.Zero(plain);
        }
    }

    public bool Clear()
    {
        if (!File.Exists(_path)) return false;
        try
        {
            SecureFile.Wipe(_path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            File.Delete(_path);   // in use by another svault: deleting is enough, the content is sealed
        }
        return true;
    }

    private static long BootTime() =>
        DateTimeOffset.UtcNow.ToUnixTimeSeconds() - Environment.TickCount64 / 1000;

    /// <summary>The token's AuthenticationId: unique for each Windows sign-in until the next restart.</summary>
    private static string LogonId()
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (!GetTokenInformation(identity.Token, TokenStatisticsClass, out var stats, Marshal.SizeOf<TokenStatistics>(), out _))
            throw new IOException("Could not read the Windows logon session (error " + Marshal.GetLastWin32Error() + ").");
        return $"{stats.AuthenticationId.HighPart:x8}{stats.AuthenticationId.LowPart:x8}";
    }

    private const int TokenStatisticsClass = 10;

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenStatistics
    {
        public Luid TokenId;
        public Luid AuthenticationId;
        public long ExpirationTime;
        public int TokenType;
        public int ImpersonationLevel;
        public uint DynamicCharged;
        public uint DynamicAvailable;
        public uint GroupCount;
        public uint PrivilegeCount;
        public Luid ModifiedId;
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(IntPtr tokenHandle, int tokenInformationClass,
        out TokenStatistics tokenInformation, int tokenInformationLength, out int returnLength);
}
