using System.Text;

namespace ShinkansenVault;

/// <summary>
/// Streaming replacer: every occurrence of a secret value in the stream becomes "••••",
/// even when a value is split across read chunks. It holds back the last (maxLen - 1)
/// characters until more data arrives or <see cref="Flush"/> is called.
/// </summary>
public sealed class SecretMasker
{
    public const string Mask = "••••";
    private readonly string[] _secrets;
    private readonly int _holdBack;
    private readonly StringBuilder _buffer = new();

    public SecretMasker(IEnumerable<string> secrets)
    {
        // Longest first so a secret that contains another is masked as a whole.
        _secrets = secrets.Where(s => !string.IsNullOrEmpty(s)).Distinct()
            .OrderByDescending(s => s.Length).ToArray();
        _holdBack = _secrets.Length == 0 ? 0 : _secrets[0].Length - 1;
    }

    /// <summary>Adds output text and returns the part that is safe to emit now.</summary>
    public string Push(ReadOnlySpan<char> chunk)
    {
        _buffer.Append(chunk);
        ApplyMasks();
        int emit = _buffer.Length - _holdBack;
        if (emit <= 0) return "";
        var result = _buffer.ToString(0, emit);
        _buffer.Remove(0, emit);
        return result;
    }

    public string Flush()
    {
        ApplyMasks();
        var rest = _buffer.ToString();
        _buffer.Clear();
        return rest;
    }

    private void ApplyMasks()
    {
        foreach (var s in _secrets) _buffer.Replace(s, Mask);
    }

    /// <summary>One-shot convenience.</summary>
    public static string MaskAll(string text, IEnumerable<string> secrets)
    {
        var m = new SecretMasker(secrets);
        return m.Push(text) + m.Flush();
    }
}
