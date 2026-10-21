using System.Runtime.InteropServices;
using Windows.Security.Credentials;
using Windows.Security.Cryptography;

namespace ShinkansenVault.Cli;

/// <summary>
/// Windows Hello via KeyCredentialManager. The RSA key lives in the TPM (or Hello software container);
/// RequestSignAsync shows the Hello prompt and returns a deterministic PKCS#1 v1.5 signature.
/// Same approach as KeePassXC's Windows Hello quick unlock.
/// </summary>
internal sealed class WindowsHelloKey : IHelloKey
{
    public async Task CreateKeyAsync(string keyName)
    {
        await EnsureSupportedAsync();
        using var focus = HelloFocus.Start();
        var result = await KeyCredentialManager.RequestCreateAsync(keyName, KeyCredentialCreationOption.FailIfExists);
        if (result.Status == KeyCredentialStatus.UserCanceled)
            throw new VaultException("Windows Hello was cancelled.", ExitCodes.Denied);
        if (result.Status != KeyCredentialStatus.Success)
            throw new VaultException($"Could not create Windows Hello key: {result.Status}.");
    }

    public async Task<byte[]?> SignAsync(string keyName, byte[] challenge)
    {
        await EnsureSupportedAsync();
        var open = await KeyCredentialManager.OpenAsync(keyName);
        if (open.Status != KeyCredentialStatus.Success)
            throw new VaultException($"Windows Hello key '{keyName}' not available: {open.Status}.");

        using var focus = HelloFocus.Start();
        var op = await open.Credential.RequestSignAsync(CryptographicBuffer.CreateFromByteArray(challenge));
        if (op.Status == KeyCredentialStatus.UserCanceled) return null;
        if (op.Status != KeyCredentialStatus.Success)
            throw new VaultException($"Windows Hello signing failed: {op.Status}.");
        CryptographicBuffer.CopyToByteArray(op.Result, out byte[] signature);
        return signature;
    }

    private static async Task EnsureSupportedAsync()
    {
        if (!await KeyCredentialManager.IsSupportedAsync())
            throw new VaultException("Windows Hello is not set up on this account. Set up a PIN in Settings > Accounts > Sign-in options.");
    }

    /// <summary>
    /// From a console app the Hello prompt can open behind other windows.
    /// This briefly polls for the Hello dialog and brings it to the foreground.
    /// </summary>
    private sealed class HelloFocus : IDisposable
    {
        private readonly CancellationTokenSource _cts = new();

        public static HelloFocus Start()
        {
            var f = new HelloFocus();
            _ = Task.Run(async () =>
            {
                for (int i = 0; i < 50 && !f._cts.IsCancellationRequested; i++)
                {
                    var hwnd = FindWindow("Credential Dialog Xaml Host", null);
                    if (hwnd != IntPtr.Zero) { SetForegroundWindow(hwnd); return; }
                    try { await Task.Delay(100, f._cts.Token); } catch (TaskCanceledException) { return; }
                }
            });
            return f;
        }

        public void Dispose() => _cts.Cancel();

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindow(string? className, string? windowName);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetForegroundWindow(IntPtr hWnd);
    }
}
