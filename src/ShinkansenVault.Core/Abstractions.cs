namespace ShinkansenVault;

/// <summary>Windows Hello key operations. The private key never leaves the TPM / Hello container.</summary>
public interface IHelloKey
{
    /// <summary>Creates a new Hello key with the given name. Shows the Windows Hello prompt.</summary>
    Task CreateKeyAsync(string keyName);

    /// <summary>
    /// Signs <paramref name="challenge"/> with the named Hello key (shows the Windows Hello prompt).
    /// Returns null if the user cancelled. RSA PKCS#1 v1.5 signatures are deterministic, so the
    /// same challenge always yields the same bytes.
    /// </summary>
    Task<byte[]?> SignAsync(string keyName, byte[] challenge);
}

/// <summary>Outer key wrapping layer (DPAPI CurrentUser on Windows).</summary>
public interface IKeyProtector
{
    byte[] Protect(byte[] data);
    byte[] Unprotect(byte[] data);
}

/// <summary>What the human sees in the consent dialog before Windows Hello is asked.</summary>
public sealed record ConsentRequest(string Action, IReadOnlyList<string> Refs, string Target, string Reason);

/// <summary>Shows the consent dialog. Returns true only on an explicit "Allow" by the human.</summary>
public interface IConsentPrompt
{
    bool Confirm(ConsentRequest request);
}

/// <summary>Reads a new secret value from the human (GUI dialog). Never from args or agent stdin.</summary>
public interface ISecretInput
{
    /// <summary>
    /// Returns the UTF-8 bytes of the value, or null if cancelled. Caller zeroes the array.
    /// <paramref name="reason"/> is shown when an agent asked for the secret (MCP vault_add).
    /// </summary>
    byte[]? ReadSecret(string reference, string? label, string? reason = null);
}

public interface IClipboard
{
    void SetText(string text);
    /// <summary>Clears the clipboard only if it still holds <paramref name="text"/>.</summary>
    void ClearIfEquals(string text);
}

/// <summary>
/// Shown while the human changes a password on a website (svault rotate). The new password is already
/// in the clipboard. Returns true only if the human confirms the website accepted it.
/// </summary>
public interface IRotationPrompt
{
    bool ConfirmChanged(string reference, string? site, Action copyNew, Action copyOld);
}

public sealed class VaultException : Exception
{
    public int ExitCode { get; }
    public VaultException(string message, int exitCode = 2) : base(message) => ExitCode = exitCode;
}

public static class ExitCodes
{
    public const int Usage = 64;
    public const int Error = 2;
    public const int Denied = 5;
}
