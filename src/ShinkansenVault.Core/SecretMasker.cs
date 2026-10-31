using System.Text;

namespace ShinkansenVault;

/// <summary>
/// Streaming replacer: every occurrence of a secret value in the stream becomes "••••",
/// even when a value is split across read chunks. It holds back the last (maxLen - 1)
/// characters until more data arrives or <see cref="Flush"/> is called.
/// When no secret contains a line break, a line break is a safe cut: no secret can span it,
/// so every complete line is emitted at once. Line-based protocols (MCP over stdio,
/// JSON-RPC) then get each message without delay; only an unfinished last line is held back.
/// </summary>
public sealed class SecretMasker
{
    public const string Mask = "••••";
    /// <summary>Used when the output encoding cannot represent "•" (e.g. console code page 437).</summary>
    public const string AsciiMask = "****";
    private readonly string _mask;
    private readonly string[] _secrets;
    private readonly int _holdBack;
    private readonly bool _lineSafe;
    private readonly StringBuilder _buffer = new();

    public SecretMasker(IEnumerable<string> secrets, string mask = Mask)
    {
        _mask = mask;
        // Longest first so a secret that contains another is masked as a whole.
        _secrets = secrets.Where(s => !string.IsNullOrEmpty(s)).Distinct()
            .OrderByDescending(s => s.Length).ToArray();
        _holdBack = _secrets.Length == 0 ? 0 : _secrets[0].Length - 1;
        _lineSafe = _secrets.All(s => !s.Contains('\n'));
    }

    /// <summary>Adds output text and returns the part that is safe to emit now.</summary>
    public string Push(ReadOnlySpan<char> chunk)
    {
        _buffer.Append(chunk);
        ApplyMasks();
        int emit = _buffer.Length - _holdBack;
        if (_lineSafe)
        {
            // Everything up to and including the last line break is final:
            // a secret without a line break cannot start before it and end after it.
            for (int i = _buffer.Length - 1; i >= 0 && i >= emit; i--)
            {
                if (_buffer[i] == '\n') { emit = i + 1; break; }
            }
        }
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

    /// <summary>Masks every secret in a whole, already complete text (e.g. a command line for the audit log).</summary>
    public static string MaskText(string? text, IEnumerable<string> secrets, string mask = Mask)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        var m = new SecretMasker(secrets, mask);
        return m.Push(text) + m.Flush();
    }

    private void ApplyMasks()
    {
        foreach (var s in _secrets) _buffer.Replace(s, _mask);
    }

    /// <summary>Picks "••••" if <paramref name="encoding"/> can carry it, otherwise "****".</summary>
    public static string MaskFor(Encoding encoding)
    {
        try
        {
            return encoding.GetString(encoding.GetBytes(Mask)) == Mask ? Mask : AsciiMask;
        }
        catch (EncoderFallbackException)
        {
            return AsciiMask;
        }
    }

    /// <summary>One-shot convenience.</summary>
    public static string MaskAll(string text, IEnumerable<string> secrets)
    {
        var m = new SecretMasker(secrets);
        return m.Push(text) + m.Flush();
    }
}
