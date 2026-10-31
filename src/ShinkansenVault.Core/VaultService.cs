using System.Security.Cryptography;
using System.Text;

namespace ShinkansenVault;

/// <summary>Binding of an environment variable name to a vault reference for <c>svault run</c>.</summary>
public sealed record EnvBinding(string Name, VaultRef Ref);

/// <summary>
/// All vault operations. Platform pieces (Hello, DPAPI, dialogs, clipboard) come in through
/// interfaces so the logic is testable with fakes.
///
/// Key hierarchy:
///   masterKey (random 256 bit) seals every entry with AES-256-GCM.
///   KEK = HKDF-SHA256(HelloSign(challenge), salt).
///   WrappedMasterKey = DPAPI_CurrentUser( AES-GCM(KEK, masterKey) ).
/// There is deliberately no method that returns a secret value to the caller.
/// </summary>
public sealed partial class VaultService
{
    private readonly string _vaultPath;
    private readonly IHelloKey _hello;
    private readonly IKeyProtector _protector;
    private readonly IConsentPrompt _consent;
    private readonly AuditLog _audit;
    private readonly TextWriter _info;

    public VaultService(string vaultPath, IHelloKey hello, IKeyProtector protector, IConsentPrompt consent, AuditLog audit, TextWriter info)
    {
        _vaultPath = vaultPath;
        _hello = hello;
        _protector = protector;
        _consent = consent;
        _audit = audit;
        _info = info;
    }

    // ---------- init ----------

    public async Task InitAsync()
    {
        if (File.Exists(_vaultPath))
            throw new VaultException($"A vault already exists at {_vaultPath}.");

        var vaultId = Convert.ToHexString(VaultCrypto.RandomBytes(16)).ToLowerInvariant();
        var keyName = "ShinkansenVault-" + vaultId;
        var challenge = VaultCrypto.RandomBytes(32);
        var salt = VaultCrypto.RandomBytes(32);

        _info.WriteLine("Creating Windows Hello key (Windows Hello will ask for your PIN / fingerprint / face)...");
        await _hello.CreateKeyAsync(keyName);

        _info.WriteLine("Signing the vault challenge twice to verify the signature is deterministic...");
        byte[]? sig1 = null, sig2 = null, kek = null, masterKey = null, inner = null;
        try
        {
            sig1 = await _hello.SignAsync(keyName, challenge) ?? throw new VaultException("Windows Hello was cancelled.", ExitCodes.Denied);
            sig2 = await _hello.SignAsync(keyName, challenge) ?? throw new VaultException("Windows Hello was cancelled.", ExitCodes.Denied);
            if (!CryptographicOperations.FixedTimeEquals(sig1, sig2))
                throw new VaultException("Windows Hello signatures are not deterministic on this device; cannot derive a stable key. Vault NOT created.");

            var vault = new VaultFile
            {
                VaultId = vaultId,
                HelloKeyName = keyName,
                Challenge = Convert.ToBase64String(challenge),
                KdfSalt = Convert.ToBase64String(salt),
                Created = DateTimeOffset.UtcNow,
            };
            kek = VaultCrypto.DeriveKek(sig1, salt);
            masterKey = VaultCrypto.RandomBytes(VaultCrypto.KeySize);
            inner = VaultCrypto.Seal(kek, masterKey, vault.MasterKeyAad());
            vault.WrappedMasterKey = Convert.ToBase64String(_protector.Protect(inner));
            vault.Save(_vaultPath);
        }
        finally
        {
            VaultCrypto.Zero(sig1); VaultCrypto.Zero(sig2); VaultCrypto.Zero(kek);
            VaultCrypto.Zero(masterKey); VaultCrypto.Zero(inner);
        }
        _audit.Append(new AuditEntry(DateTimeOffset.UtcNow, "init", Array.Empty<string>(), _vaultPath, null, "approved"));
        _info.WriteLine($"Vault created: {_vaultPath}");
    }

    // ---------- add ----------

    public async Task AddAsync(VaultRef reference, string? label, bool replace, ISecretInput input, string? reason = null)
    {
        var vault = VaultFile.Load(_vaultPath);
        var existing = vault.Find(reference);
        if (existing != null && !replace)
            throw new VaultException($"{reference} already exists. Use --replace to overwrite it.");

        var shownReason = existing != null && reason != null ? "ЗАМЕНЯ съществуваща стойност / REPLACES existing value. " + reason : reason;
        var value = input.ReadSecret(reference.ToString(), label ?? existing?.Label, shownReason);
        if (value == null)
        {
            _audit.Append(new AuditEntry(DateTimeOffset.UtcNow, "add", new[] { reference.ToString() }, null, null, "denied", "input cancelled"));
            throw new VaultException("Cancelled.", ExitCodes.Denied);
        }
        byte[]? masterKey = null;
        try
        {
            if (value.Length == 0) throw new VaultException("Empty value; nothing stored.");
            // The input dialog was the human's explicit action; Windows Hello still has to confirm.
            masterKey = await UnlockAsync(vault, new ConsentRequest("add", new[] { reference.ToString() }, "vault", "store new value"), askConsent: false);
            var sealedValue = VaultCrypto.Seal(masterKey, value, vault.EntryAad(reference.Path));
            var now = DateTimeOffset.UtcNow;
            if (existing != null)
            {
                existing.Data = Convert.ToBase64String(sealedValue);
                existing.Updated = now;
                if (label != null) existing.Label = label;
            }
            else
            {
                vault.Entries.Add(new VaultEntry { Ref = reference.Path, Label = label, Created = now, Updated = now, Data = Convert.ToBase64String(sealedValue) });
            }
            vault.Save(_vaultPath);
        }
        finally
        {
            VaultCrypto.Zero(value);
            VaultCrypto.Zero(masterKey);
        }
        _info.WriteLine($"Stored {reference}.");
    }

    // ---------- list ----------

    public IReadOnlyList<(string Ref, string? Label)> List() =>
        VaultFile.Load(_vaultPath).Entries.OrderBy(e => e.Ref, StringComparer.Ordinal)
            .Select(e => (VaultRef.Scheme + e.Ref, e.Label)).ToList();

    // ---------- run ----------

    public async Task<int> RunAsync(IReadOnlyList<EnvBinding> bindings, string reason, IReadOnlyList<string> command, TextWriter stdout, TextWriter stderr, bool closeStdin = false)
    {
        if (bindings.Count == 0) throw new VaultException("At least one --env NAME=vault://ref is required.", ExitCodes.Usage);
        RequireReason(reason);
        if (command.Count == 0) throw new VaultException("No command given after '--'.", ExitCodes.Usage);

        var vault = VaultFile.Load(_vaultPath);
        var refs = bindings.Select(b => b.Ref).Distinct().ToList();
        var target = string.Join(" ", command.Select(QuoteForDisplay));
        var request = new ConsentRequest("run",
            bindings.Select(b => $"{b.Name} = {b.Ref}").ToList(), target, reason);

        var values = await DecryptAsync(vault, refs, request);
        var env = bindings.ToDictionary(b => b.Name, b => values[b.Ref.Path]);
        return await ProcessRunner.RunAsync(command, env, values.Values.ToList(), stdout, stderr, closeStdin);
    }

    // ---------- render ----------

    public async Task RenderAsync(string templatePath, string outputPath, string reason, TextWriter? info = null)
    {
        RequireReason(reason);
        if (!File.Exists(templatePath)) throw new VaultException($"Template not found: {templatePath}");
        var fullOutput = OutputPathGuard.Validate(outputPath, templatePath);
        var template = File.ReadAllText(templatePath);
        var refs = TemplateRenderer.FindRefs(template);
        if (refs.Count == 0) throw new VaultException("Template contains no {{vault://...}} placeholders.");

        var vault = VaultFile.Load(_vaultPath);
        var values = await DecryptAsync(vault, refs, new ConsentRequest("render", refs.Select(r => r.ToString()).ToList(), fullOutput, reason));
        var rendered = TemplateRenderer.Render(template, values);

        Directory.CreateDirectory(Path.GetDirectoryName(fullOutput)!);
        var tmp = fullOutput + ".svault-tmp";
        await File.WriteAllTextAsync(tmp, rendered, new UTF8Encoding(false));
        File.Move(tmp, fullOutput, overwrite: true);
        (info ?? _info).WriteLine($"Rendered {refs.Count} reference(s) into {fullOutput}. Content not shown.");
    }

    // ---------- clip ----------

    public async Task ClipAsync(VaultRef reference, string reason, IClipboard clipboard, TimeSpan duration)
    {
        RequireReason(reason);
        var vault = VaultFile.Load(_vaultPath);
        var values = await DecryptAsync(vault, new[] { reference },
            new ConsentRequest("clip", new[] { reference.ToString() }, $"clipboard ({duration.TotalSeconds:0} s)", reason));
        var value = values[reference.Path];
        clipboard.SetText(value);
        _info.WriteLine($"Copied {reference} to the clipboard. It will be cleared in {duration.TotalSeconds:0} s; keep this window open.");
        await Task.Delay(duration);
        clipboard.ClearIfEquals(value);
        _info.WriteLine("Clipboard cleared.");
    }

    // ---------- internals ----------

    private static void RequireReason(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
            throw new VaultException("--reason \"...\" is required and is shown to the human.", ExitCodes.Usage);
    }

    private static string QuoteForDisplay(string arg) =>
        arg.Length == 0 || arg.Any(char.IsWhiteSpace) ? "\"" + arg.Replace("\"", "\\\"") + "\"" : arg;

    /// <summary>Consent + Hello, then decrypt only the requested entries. Values exist as strings only for the caller's use.</summary>
    private async Task<Dictionary<string, string>> DecryptAsync(VaultFile vault, IReadOnlyList<VaultRef> refs, ConsentRequest request)
    {
        var missing = refs.Where(r => vault.Find(r) == null).Select(r => r.ToString()).ToList();
        if (missing.Count > 0)
        {
            _audit.Append(new AuditEntry(DateTimeOffset.UtcNow, request.Action, refs.Select(r => r.ToString()).ToList(), request.Target, request.Reason, "failed", "unknown reference"));
            throw new VaultException("Unknown reference(s): " + string.Join(", ", missing));
        }

        var masterKey = await UnlockAsync(vault, request, askConsent: true);
        var result = new Dictionary<string, string>();
        try
        {
            foreach (var r in refs)
            {
                var plain = VaultCrypto.Open(masterKey, Convert.FromBase64String(vault.Find(r)!.Data), vault.EntryAad(r.Path));
                try { result[r.Path] = Encoding.UTF8.GetString(plain); }
                finally { VaultCrypto.Zero(plain); }
            }
        }
        catch (CryptographicException)
        {
            throw new VaultException("An entry failed authentication (vault file was modified?).");
        }
        finally
        {
            VaultCrypto.Zero(masterKey);
        }
        return result;
    }

    private async Task<byte[]> UnlockAsync(VaultFile vault, ConsentRequest request, bool askConsent)
    {
        var refs = request.Refs;
        if (askConsent && !_consent.Confirm(request))
        {
            _audit.Append(new AuditEntry(DateTimeOffset.UtcNow, request.Action, refs, request.Target, request.Reason, "denied", "consent dialog"));
            throw new VaultException("Denied by the user.", ExitCodes.Denied);
        }

        var signature = await _hello.SignAsync(vault.HelloKeyName, Convert.FromBase64String(vault.Challenge));
        if (signature == null)
        {
            _audit.Append(new AuditEntry(DateTimeOffset.UtcNow, request.Action, refs, request.Target, request.Reason, "denied", "windows hello cancelled"));
            throw new VaultException("Windows Hello was cancelled.", ExitCodes.Denied);
        }

        byte[]? kek = null, inner = null;
        try
        {
            kek = VaultCrypto.DeriveKek(signature, Convert.FromBase64String(vault.KdfSalt));
            inner = _protector.Unprotect(Convert.FromBase64String(vault.WrappedMasterKey));
            var masterKey = VaultCrypto.Open(kek, inner, vault.MasterKeyAad());
            _audit.Append(new AuditEntry(DateTimeOffset.UtcNow, request.Action, refs, request.Target, request.Reason, "approved"));
            return masterKey;
        }
        catch (CryptographicException)
        {
            _audit.Append(new AuditEntry(DateTimeOffset.UtcNow, request.Action, refs, request.Target, request.Reason, "failed", "unlock failed"));
            throw new VaultException("Could not unlock the vault (Hello key or Windows user changed?).");
        }
        finally
        {
            VaultCrypto.Zero(signature); VaultCrypto.Zero(kek); VaultCrypto.Zero(inner);
        }
    }
}
