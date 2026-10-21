using System.Text.RegularExpressions;

namespace ShinkansenVault;

/// <summary>Replaces <c>{{vault://ref}}</c> placeholders in a template.</summary>
public static partial class TemplateRenderer
{
    [GeneratedRegex(@"\{\{\s*vault://([A-Za-z0-9][A-Za-z0-9._/-]*)\s*\}\}")]
    private static partial Regex Placeholder();

    public static IReadOnlyList<VaultRef> FindRefs(string template) =>
        Placeholder().Matches(template)
            .Select(m => VaultRef.Parse(VaultRef.Scheme + m.Groups[1].Value))
            .Distinct().ToList();

    public static string Render(string template, IReadOnlyDictionary<string, string> valuesByPath) =>
        Placeholder().Replace(template, m => valuesByPath[m.Groups[1].Value]);
}

/// <summary>Rejects output targets that would print to a console or device instead of a file.</summary>
public static class OutputPathGuard
{
    private static readonly HashSet<string> Devices = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    public static string Validate(string output, string template)
    {
        if (string.IsNullOrWhiteSpace(output) || output == "-")
            throw new VaultException("Output must be a file path (stdout is not allowed).", ExitCodes.Usage);
        var trimmed = output.Trim();
        if (trimmed.StartsWith(@"\\.\") || trimmed.StartsWith(@"\\?\") || trimmed.StartsWith("/dev/") || trimmed.StartsWith("/proc/"))
            throw new VaultException("Output must be a regular file, not a device.", ExitCodes.Usage);
        var name = Path.GetFileNameWithoutExtension(trimmed.TrimEnd(':'));
        if (Devices.Contains(name) || Devices.Contains(Path.GetFileName(trimmed).TrimEnd(':')))
            throw new VaultException("Output must be a regular file, not a device name.", ExitCodes.Usage);
        var full = Path.GetFullPath(trimmed);
        if (string.Equals(full, Path.GetFullPath(template), StringComparison.OrdinalIgnoreCase))
            throw new VaultException("Output must differ from the template.", ExitCodes.Usage);
        if (Directory.Exists(full))
            throw new VaultException("Output is a directory.", ExitCodes.Usage);
        return full;
    }
}
