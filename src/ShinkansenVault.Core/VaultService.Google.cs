using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ShinkansenVault;

/// <summary>Login details kept (sealed) next to a password so it can be synced with Google Password Manager.</summary>
public sealed record LoginMeta(string Name, string Url, string Username, string Note, string Source);

/// <summary>
/// Google Password Manager sync, local checks and password rotation.
/// Like the rest of the service, nothing here returns a secret value to the caller:
/// reports contain references only, values go to a file the human chose or to the clipboard.
/// </summary>
public sealed partial class VaultService
{
    public const string GoogleSource = "google";

    // ---------- import from Google ----------

    /// <summary>
    /// Adds new logins, updates changed passwords (keeping the old value as Previous) and leaves
    /// logins that are only in the vault untouched. One consent + one Windows Hello for the whole file.
    /// </summary>
    public async Task<ImportReport> ImportGoogleAsync(IReadOnlyList<GoogleRow> rows, string sourceName)
    {
        if (rows.Count == 0) throw new VaultException("No passwords found in the file.");
        var vault = VaultFile.Load(_vaultPath);
        var request = new ConsentRequest("import-google",
            new[] { $"{rows.Count} записа / logins  →  vault://google/..." },
            "трезора / vault  ←  " + sourceName,
            "Внасяне от Google Password Manager / import from Google Password Manager");
        var masterKey = await UnlockAsync(vault, request, askConsent: true);

        var added = new List<string>();
        var changed = new List<string>();
        int unchanged = 0, duplicates = 0;
        var onlyInVault = new List<string>();
        try
        {
            var byKey = new Dictionary<string, (VaultEntry Entry, LoginMeta Meta)>();
            foreach (var e in vault.Entries.Where(e => e.Meta != null))
            {
                var meta = OpenMeta(vault, masterKey, e);
                if (meta != null) byKey.TryAdd(GoogleCsv.MatchKey(meta.Url, meta.Username), (e, meta));
            }

            var seen = new HashSet<string>();
            var now = DateTimeOffset.UtcNow;
            foreach (var row in rows)
            {
                var key = GoogleCsv.MatchKey(row.Url, row.Username);
                if (!seen.Add(key)) { duplicates++; continue; }
                var meta = new LoginMeta(row.Name, row.Url, row.Username, row.Note, GoogleSource);
                var pw = Encoding.UTF8.GetBytes(row.Password);
                try
                {
                    if (byKey.TryGetValue(key, out var hit))
                    {
                        var e = hit.Entry;
                        var old = VaultCrypto.Open(masterKey, Convert.FromBase64String(e.Data), vault.EntryAad(e.Ref));
                        try
                        {
                            if (CryptographicOperations.FixedTimeEquals(old, pw)) unchanged++;
                            else
                            {
                                e.Previous = e.Data;
                                e.PreviousReplaced = now;
                                e.Data = Convert.ToBase64String(VaultCrypto.Seal(masterKey, pw, vault.EntryAad(e.Ref)));
                                e.Updated = now;
                                changed.Add(VaultRef.Scheme + e.Ref);
                            }
                        }
                        finally { VaultCrypto.Zero(old); }
                        e.Meta = SealMeta(vault, masterKey, e.Ref, meta with { Source = hit.Meta.Source });
                    }
                    else
                    {
                        var path = UniquePath(vault, GoogleCsv.RefBase(row.Url, row.Name));
                        var entry = new VaultEntry
                        {
                            Ref = path,
                            Label = row.Name.Length > 0 ? row.Name : path["google/".Length..],
                            Created = now,
                            Updated = now,
                            Data = Convert.ToBase64String(VaultCrypto.Seal(masterKey, pw, vault.EntryAad(path))),
                        };
                        entry.Meta = SealMeta(vault, masterKey, path, meta);
                        vault.Entries.Add(entry);
                        byKey[key] = (entry, meta);
                        added.Add(VaultRef.Scheme + path);
                    }
                }
                finally { VaultCrypto.Zero(pw); }
            }

            onlyInVault = byKey.Where(kv => !seen.Contains(kv.Key) && kv.Value.Meta.Source == GoogleSource)
                .Select(kv => VaultRef.Scheme + kv.Value.Entry.Ref).OrderBy(x => x, StringComparer.Ordinal).ToList();
            vault.Save(_vaultPath);
        }
        catch (CryptographicException)
        {
            throw new VaultException("An entry failed authentication (vault file was modified?). Nothing was imported.");
        }
        finally
        {
            VaultCrypto.Zero(masterKey);
        }

        _audit.Append(new AuditEntry(DateTimeOffset.UtcNow, "import-google", Array.Empty<string>(), sourceName, request.Reason, "approved",
            $"added {added.Count}, changed {changed.Count}, unchanged {unchanged}, only in vault {onlyInVault.Count}, duplicates skipped {duplicates}"));
        return new ImportReport(added, changed, unchanged, onlyInVault, duplicates);
    }

    // ---------- export for Google ----------

    /// <summary>Writes every login that has URL details into a Google-format CSV. Returns the number of rows.</summary>
    public async Task<int> ExportGoogleAsync(string outputPath, string reason)
    {
        RequireReason(reason);
        var full = OutputPathGuard.Validate(outputPath, _vaultPath);
        var vault = VaultFile.Load(_vaultPath);
        var entries = vault.Entries.Where(e => e.Meta != null).OrderBy(e => e.Ref, StringComparer.Ordinal).ToList();
        if (entries.Count == 0) throw new VaultException("No logins with site details in the vault (import from Google first).");

        var request = new ConsentRequest("export-google",
            new[] { $"{entries.Count} записа / logins (паролите в явен вид! / passwords in plain text!)" },
            full, reason);
        var masterKey = await UnlockAsync(vault, request, askConsent: true);
        try
        {
            var rows = new List<GoogleRow>();
            foreach (var e in entries)
            {
                var meta = OpenMeta(vault, masterKey, e);
                if (meta == null || meta.Url.Length == 0) continue;
                var plain = VaultCrypto.Open(masterKey, Convert.FromBase64String(e.Data), vault.EntryAad(e.Ref));
                try { rows.Add(new GoogleRow(meta.Name, meta.Url, meta.Username, Encoding.UTF8.GetString(plain), meta.Note)); }
                finally { VaultCrypto.Zero(plain); }
            }
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            var tmp = full + ".svault-tmp";
            await File.WriteAllTextAsync(tmp, GoogleCsv.Write(rows), new UTF8Encoding(false));
            File.Move(tmp, full, overwrite: true);
            return rows.Count;
        }
        catch (CryptographicException)
        {
            throw new VaultException("An entry failed authentication (vault file was modified?).");
        }
        finally
        {
            VaultCrypto.Zero(masterKey);
        }
    }

    // ---------- check ----------

    /// <summary>Finds weak and reused passwords. The report has references only.</summary>
    public async Task<CheckReport> CheckAsync(string reason)
    {
        RequireReason(reason);
        var vault = VaultFile.Load(_vaultPath);
        if (vault.Entries.Count == 0) return new CheckReport(0, Array.Empty<(string, string)>(), Array.Empty<IReadOnlyList<string>>());
        var request = new ConsentRequest("check",
            new[] { $"всички {vault.Entries.Count} записа / all entries (резултат: само имена / result: names only)" },
            "локална проверка, без мрежа / local check, no network", reason);
        var masterKey = await UnlockAsync(vault, request, askConsent: true);
        var weak = new List<(string, string)>();
        var groups = new Dictionary<string, List<string>>();
        try
        {
            foreach (var e in vault.Entries.OrderBy(e => e.Ref, StringComparer.Ordinal))
            {
                var plain = VaultCrypto.Open(masterKey, Convert.FromBase64String(e.Data), vault.EntryAad(e.Ref));
                try
                {
                    var pw = Encoding.UTF8.GetString(plain);
                    var r = VaultRef.Scheme + e.Ref;
                    if (PasswordCheck.WeakReason(pw) is { } why) weak.Add((r, why));
                    var fp = PasswordCheck.Fingerprint(pw);
                    if (!groups.TryGetValue(fp, out var list)) groups[fp] = list = new List<string>();
                    list.Add(r);
                }
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
        var reused = groups.Values.Where(g => g.Count > 1).OrderByDescending(g => g.Count).Select(g => (IReadOnlyList<string>)g).ToList();
        return new CheckReport(vault.Entries.Count, weak, reused);
    }

    // ---------- rotate ----------

    /// <summary>
    /// Generates a strong new password, puts it in the clipboard for the human to paste on the website,
    /// and stores it only after the human confirms the website accepted it. The old value is kept as Previous.
    /// </summary>
    public async Task<bool> RotateAsync(VaultRef reference, string reason, IClipboard clipboard, IRotationPrompt prompt, int length = 20)
    {
        RequireReason(reason);
        var vault = VaultFile.Load(_vaultPath);
        var entry = vault.Find(reference) ?? throw new VaultException("Unknown reference: " + reference);
        var request = new ConsentRequest("rotate", new[] { reference.ToString() },
            "нова парола в клипборда → поставяте я на сайта / new password in clipboard → you paste it on the website", reason);
        var masterKey = await UnlockAsync(vault, request, askConsent: true);
        string? oldValue = null;
        var newValue = PasswordCheck.Generate(length);
        bool saved = false;
        try
        {
            var plain = VaultCrypto.Open(masterKey, Convert.FromBase64String(entry.Data), vault.EntryAad(entry.Ref));
            try { oldValue = Encoding.UTF8.GetString(plain); }
            finally { VaultCrypto.Zero(plain); }
            var site = OpenMeta(vault, masterKey, entry)?.Url;

            clipboard.SetText(newValue);
            bool confirmed;
            try
            {
                var old = oldValue;
                confirmed = prompt.ConfirmChanged(reference.ToString(), site, () => clipboard.SetText(newValue), () => clipboard.SetText(old));
            }
            finally
            {
                clipboard.ClearIfEquals(newValue);
                clipboard.ClearIfEquals(oldValue);
            }

            if (confirmed)
            {
                var bytes = Encoding.UTF8.GetBytes(newValue);
                try
                {
                    var now = DateTimeOffset.UtcNow;
                    entry.Previous = entry.Data;
                    entry.PreviousReplaced = now;
                    entry.Data = Convert.ToBase64String(VaultCrypto.Seal(masterKey, bytes, vault.EntryAad(entry.Ref)));
                    entry.Updated = now;
                    vault.Save(_vaultPath);
                    saved = true;
                }
                finally { VaultCrypto.Zero(bytes); }
            }
        }
        catch (CryptographicException)
        {
            throw new VaultException("The entry failed authentication (vault file was modified?).");
        }
        finally
        {
            VaultCrypto.Zero(masterKey);
        }
        _audit.Append(new AuditEntry(DateTimeOffset.UtcNow, "rotate", new[] { reference.ToString() }, "website", reason,
            saved ? "approved" : "denied", saved ? "new password saved" : "human did not confirm; old password kept"));
        return saved;
    }

    // ---------- helpers ----------

    private static readonly JsonSerializerOptions MetaJson = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private static string SealMeta(VaultFile vault, byte[] masterKey, string path, LoginMeta meta)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(meta, MetaJson);
        try { return Convert.ToBase64String(VaultCrypto.Seal(masterKey, bytes, vault.MetaAad(path))); }
        finally { VaultCrypto.Zero(bytes); }
    }

    private static LoginMeta? OpenMeta(VaultFile vault, byte[] masterKey, VaultEntry e)
    {
        if (e.Meta == null) return null;
        var bytes = VaultCrypto.Open(masterKey, Convert.FromBase64String(e.Meta), vault.MetaAad(e.Ref));
        try { return JsonSerializer.Deserialize<LoginMeta>(bytes, MetaJson); }
        finally { VaultCrypto.Zero(bytes); }
    }

    private static string UniquePath(VaultFile vault, string basePath)
    {
        if (vault.Entries.All(e => e.Ref != basePath)) return basePath;
        for (int i = 2; ; i++)
        {
            var p = $"{basePath}-{i}";
            if (vault.Entries.All(e => e.Ref != p)) return p;
        }
    }
}
