using System.Text.Json;
using System.Text.Json.Serialization;

namespace ShinkansenVault;

/// <summary>
/// On-disk vault (JSON). Only references, labels and timestamps are plaintext;
/// every value is AES-256-GCM sealed under the master key.
/// </summary>
public sealed class VaultFile
{
    public int Version { get; set; } = 1;
    public string VaultId { get; set; } = "";
    public string HelloKeyName { get; set; } = "";
    /// <summary>Base64 fixed per-vault challenge signed by the Hello key.</summary>
    public string Challenge { get; set; } = "";
    /// <summary>Base64 HKDF salt.</summary>
    public string KdfSalt { get; set; } = "";
    /// <summary>Base64 of DPAPI( AES-GCM(KEK, masterKey) ).</summary>
    public string WrappedMasterKey { get; set; } = "";
    public DateTimeOffset Created { get; set; }
    public List<VaultEntry> Entries { get; set; } = new();

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public byte[] MasterKeyAad() => System.Text.Encoding.UTF8.GetBytes("svault-mk-v1:" + VaultId);
    public byte[] EntryAad(string refPath) => System.Text.Encoding.UTF8.GetBytes("svault-entry-v1:" + VaultId + ":" + refPath);

    public byte[] MetaAad(string refPath) => EntryAad(refPath + "#meta");

    public VaultEntry? Find(VaultRef r) => Entries.FirstOrDefault(e => e.Ref == r.Path);

    public static VaultFile Load(string path)
    {
        if (!File.Exists(path))
            throw new VaultException($"No vault at {path}. Run 'svault init' first.");
        var v = JsonSerializer.Deserialize<VaultFile>(File.ReadAllText(path), Json)
                ?? throw new VaultException("Vault file is empty or corrupt.");
        if (v.Version != 1) throw new VaultException($"Unsupported vault version {v.Version}.");
        return v;
    }

    /// <summary>Atomic save: write a temp file next to the target, then replace.</summary>
    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
        File.Move(tmp, path, overwrite: true);
    }
}

public sealed class VaultEntry
{
    public string Ref { get; set; } = "";
    public string? Label { get; set; }
    public DateTimeOffset Created { get; set; }
    public DateTimeOffset Updated { get; set; }
    /// <summary>Base64 of nonce|ciphertext|tag.</summary>
    public string Data { get; set; } = "";
    /// <summary>Optional sealed JSON with login details (url, username, name, note) for Google sync. AAD: entry + "#meta".</summary>
    public string? Meta { get; set; }
    /// <summary>The value before the last rotate/import change (same sealing as <see cref="Data"/>), kept for undo.</summary>
    public string? Previous { get; set; }
    public DateTimeOffset? PreviousReplaced { get; set; }
}
