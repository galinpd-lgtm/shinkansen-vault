using System.Security.Cryptography;
using System.Text;

namespace ShinkansenVault.Tests;

/// <summary>Deterministic stand-in for Windows Hello: HMAC with a per-key random secret.</summary>
public sealed class FakeHello : IHelloKey
{
    private readonly Dictionary<string, byte[]> _keys = new();
    public bool Cancel { get; set; }
    public int SignCount { get; private set; }

    public Task CreateKeyAsync(string keyName)
    {
        _keys[keyName] = RandomNumberGenerator.GetBytes(32);
        return Task.CompletedTask;
    }

    public Task<byte[]?> SignAsync(string keyName, byte[] challenge)
    {
        SignCount++;
        if (Cancel) return Task.FromResult<byte[]?>(null);
        return Task.FromResult<byte[]?>(HMACSHA256.HashData(_keys[keyName], challenge));
    }

    /// <summary>Simulates the Hello key being replaced (e.g. PIN reset that wiped keys).</summary>
    public void ReplaceAllKeys()
    {
        foreach (var k in _keys.Keys.ToList()) _keys[k] = RandomNumberGenerator.GetBytes(32);
    }
}

/// <summary>Stand-in for DPAPI: AES-GCM under a fixed per-instance key.</summary>
public sealed class FakeProtector : IKeyProtector
{
    private readonly byte[] _key = RandomNumberGenerator.GetBytes(32);
    public byte[] Protect(byte[] data) => VaultCrypto.Seal(_key, data, default);
    public byte[] Unprotect(byte[] data) => VaultCrypto.Open(_key, data, default);
}

public sealed class FakeConsent : IConsentPrompt
{
    public bool Answer { get; set; } = true;
    public List<ConsentRequest> Requests { get; } = new();
    public bool Confirm(ConsentRequest request)
    {
        Requests.Add(request);
        return Answer;
    }
}

public sealed class FakeInput : ISecretInput
{
    private readonly string? _value;
    public FakeInput(string? value) => _value = value;
    public string? LastReason { get; private set; }
    public byte[]? ReadSecret(string reference, string? label, string? reason = null)
    {
        LastReason = reason;
        return _value == null ? null : Encoding.UTF8.GetBytes(_value);
    }
}

public sealed class FakeClipboard : IClipboard
{
    public string? Content { get; private set; }
    public List<string?> History { get; } = new();
    public void SetText(string text) { Content = text; History.Add(text); }
    public void ClearIfEquals(string text) { if (Content == text) Content = null; }
}

/// <summary>A temp vault folder plus a service wired with fakes.</summary>
public sealed class TestVault : IDisposable
{
    public string Dir { get; } = Path.Combine(Path.GetTempPath(), "svault-test-" + Guid.NewGuid().ToString("N"));
    public FakeHello Hello { get; } = new();
    public FakeProtector Protector { get; } = new();
    public FakeConsent Consent { get; } = new();
    public StringWriter Info { get; } = new();
    public AuditLog Audit { get; }
    public VaultService Service { get; }
    public string VaultPath => Path.Combine(Dir, "vault.svlt");

    public TestVault()
    {
        Directory.CreateDirectory(Dir);
        Audit = new AuditLog(Path.Combine(Dir, "audit.jsonl"));
        Service = new VaultService(VaultPath, Hello, Protector, Consent, Audit, Info);
    }

    public string AllFilesText() =>
        string.Join("\n", Directory.GetFiles(Dir).Select(File.ReadAllText)) + Info;

    public void Dispose()
    {
        try { Directory.Delete(Dir, true); } catch { /* best effort */ }
    }
}
