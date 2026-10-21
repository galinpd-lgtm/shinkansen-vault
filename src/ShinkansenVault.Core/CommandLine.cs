using System.Text.RegularExpressions;

namespace ShinkansenVault;

/// <summary>Parsed command line. Deliberately small, no external parser.</summary>
public sealed class ParsedArgs
{
    public string Command { get; init; } = "help";
    public List<string> Positional { get; } = new();
    public List<EnvBinding> Env { get; } = new();
    public string? Reason { get; set; }
    public string? Label { get; set; }
    public bool Replace { get; set; }
    public int? Tail { get; set; }
    /// <summary>Everything after "--" (for <c>run</c>).</summary>
    public List<string> Rest { get; } = new();
}

public static partial class CommandLine
{
    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex EnvName();

    public static ParsedArgs Parse(string[] args)
    {
        if (args.Length == 0) return new ParsedArgs { Command = "help" };
        var result = new ParsedArgs { Command = args[0].ToLowerInvariant() };
        for (int i = 1; i < args.Length; i++)
        {
            var a = args[i];
            string Next()
            {
                if (i + 1 >= args.Length) throw new VaultException($"Missing value after {a}.", ExitCodes.Usage);
                return args[++i];
            }
            switch (a)
            {
                case "--":
                    result.Rest.AddRange(args.Skip(i + 1));
                    i = args.Length;
                    break;
                case "--reason":
                    result.Reason = Next();
                    break;
                case "--label":
                    result.Label = Next();
                    break;
                case "--replace":
                    result.Replace = true;
                    break;
                case "--tail":
                    result.Tail = int.TryParse(Next(), out var n) && n > 0 ? n : throw new VaultException("--tail needs a positive number.", ExitCodes.Usage);
                    break;
                case "--env":
                    result.Env.Add(ParseEnv(Next()));
                    break;
                case "--value" or "--password" or "--secret":
                    throw new VaultException("Secret values are never accepted as arguments. 'svault add' opens a dialog for the human.", ExitCodes.Usage);
                default:
                    if (a.StartsWith("--")) throw new VaultException($"Unknown option {a}.", ExitCodes.Usage);
                    result.Positional.Add(a);
                    break;
            }
        }
        return result;
    }

    public static EnvBinding ParseEnv(string text)
    {
        var eq = text.IndexOf('=');
        if (eq <= 0) throw new VaultException($"--env expects NAME=vault://ref, got '{text}'.", ExitCodes.Usage);
        var name = text[..eq];
        if (!EnvName().IsMatch(name)) throw new VaultException($"Invalid environment variable name '{name}'.", ExitCodes.Usage);
        return new EnvBinding(name, VaultRef.Parse(text[(eq + 1)..], requireScheme: true));
    }
}
