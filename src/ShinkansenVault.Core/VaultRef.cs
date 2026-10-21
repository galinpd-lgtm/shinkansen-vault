using System.Text.RegularExpressions;

namespace ShinkansenVault;

/// <summary>A reference like <c>vault://varnahub/cpanel</c>. Only the path part is stored.</summary>
public sealed partial record VaultRef(string Path)
{
    public const string Scheme = "vault://";

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]*(/[A-Za-z0-9][A-Za-z0-9._-]*)*$")]
    private static partial Regex PathRegex();

    public override string ToString() => Scheme + Path;

    /// <summary>Parses a reference. If <paramref name="requireScheme"/> is false, "vault://" is optional.</summary>
    public static VaultRef Parse(string text, bool requireScheme = true)
    {
        if (!TryParse(text, requireScheme, out var r))
            throw new VaultException($"Invalid reference '{text}'. Expected e.g. vault://project/name (letters, digits, . _ -).", ExitCodes.Usage);
        return r!;
    }

    public static bool TryParse(string? text, bool requireScheme, out VaultRef? result)
    {
        result = null;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var path = text.Trim();
        if (path.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase)) path = path[Scheme.Length..];
        else if (requireScheme) return false;
        if (path.Length > 200 || !PathRegex().IsMatch(path)) return false;
        result = new VaultRef(path);
        return true;
    }
}
