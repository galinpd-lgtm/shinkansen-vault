using System.Security.Cryptography;
using System.Text;

namespace ShinkansenVault;

/// <summary>Local password checks and generation. No network.</summary>
public static class PasswordCheck
{
    private static readonly HashSet<string> Common = new(StringComparer.OrdinalIgnoreCase)
    {
        "123456", "1234567", "12345678", "123456789", "1234567890", "password", "password1", "qwerty",
        "qwerty123", "abc123", "111111", "000000", "iloveyou", "admin", "welcome", "letmein", "parola",
        "parola123", "123123", "654321", "666666", "qwertyuiop", "asdfgh", "zxcvbnm",
    };

    /// <summary>Returns a short reason if the password is weak, otherwise null.</summary>
    public static string? WeakReason(string pw)
    {
        if (Common.Contains(pw)) return "common password";
        if (pw.Length < 12) return $"short ({pw.Length} chars)";
        if (pw.Distinct().Count() <= 3) return "too few distinct characters";
        int classes = (pw.Any(char.IsLower) ? 1 : 0) + (pw.Any(char.IsUpper) ? 1 : 0)
                    + (pw.Any(char.IsDigit) ? 1 : 0) + (pw.Any(c => !char.IsLetterOrDigit(c)) ? 1 : 0);
        if (classes < 3 && pw.Length < 16) return "needs 3 of: lower, upper, digit, symbol (or 16+ chars)";
        return null;
    }

    // No look-alike characters (0/O, 1/l/I); symbols that most sites accept.
    private const string Lower = "abcdefghijkmnopqrstuvwxyz";
    private const string Upper = "ABCDEFGHJKLMNPQRSTUVWXYZ";
    private const string Digits = "23456789";
    private const string Symbols = "!#%+-=?@_";

    /// <summary>Random password with at least one character of each class.</summary>
    public static string Generate(int length = 20)
    {
        if (length < 12) throw new ArgumentOutOfRangeException(nameof(length));
        var all = Lower + Upper + Digits + Symbols;
        var chars = new char[length];
        chars[0] = Pick(Lower); chars[1] = Pick(Upper); chars[2] = Pick(Digits); chars[3] = Pick(Symbols);
        for (int i = 4; i < length; i++) chars[i] = Pick(all);
        // Fisher-Yates shuffle with a CSPRNG.
        for (int i = length - 1; i > 0; i--)
        {
            int j = RandomNumberGenerator.GetInt32(i + 1);
            (chars[i], chars[j]) = (chars[j], chars[i]);
        }
        return new string(chars);
    }

    private static char Pick(string set) => set[RandomNumberGenerator.GetInt32(set.Length)];

    /// <summary>Hash used only in memory to find reused passwords.</summary>
    public static string Fingerprint(string pw) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(pw)));
}

/// <summary>Result of <c>svault check</c>. Contains references only, never values.</summary>
public sealed record CheckReport(
    int Total,
    IReadOnlyList<(string Ref, string Reason)> Weak,
    IReadOnlyList<IReadOnlyList<string>> ReusedGroups);

/// <summary>Result of <c>svault import-google</c>. References only.</summary>
public sealed record ImportReport(
    IReadOnlyList<string> Added,
    IReadOnlyList<string> Changed,
    int Unchanged,
    IReadOnlyList<string> OnlyInVault,
    int SkippedDuplicates);
