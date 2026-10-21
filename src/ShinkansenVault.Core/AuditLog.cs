using System.Text;
using System.Text.Json;

namespace ShinkansenVault;

/// <summary>One audit line. Never contains secret values.</summary>
public sealed record AuditEntry(
    DateTimeOffset Time,
    string Action,
    IReadOnlyList<string> Refs,
    string? Target,
    string? Reason,
    string Decision,
    string? Detail = null);

/// <summary>Append-only JSONL audit log.</summary>
public sealed class AuditLog
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    public string Path { get; }

    public AuditLog(string path) => Path = path;

    public void Append(AuditEntry entry)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path))!);
        var line = JsonSerializer.Serialize(entry, Json) + "\n";
        using var fs = new FileStream(Path, FileMode.Append, FileAccess.Write, FileShare.Read);
        var bytes = Encoding.UTF8.GetBytes(line);
        fs.Write(bytes, 0, bytes.Length);
    }

    public IReadOnlyList<string> ReadLines(int? tail = null)
    {
        if (!File.Exists(Path)) return Array.Empty<string>();
        var lines = File.ReadAllLines(Path).Where(l => l.Length > 0).ToList();
        return tail is int n && n < lines.Count ? lines.Skip(lines.Count - n).ToList() : lines;
    }
}
