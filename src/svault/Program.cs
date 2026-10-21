using System.Reflection;

namespace ShinkansenVault.Cli;

internal static class Program
{
    private const string Help = """
        Shinkansen Vault — Windows Hello password vault for AI agents.
        There is no command that prints a secret value.

        Usage:
          svault init
          svault add <vault://ref> [--label "text"] [--replace]     (value typed in a dialog)
          svault list
          svault run --env NAME=vault://ref [--env ...] --reason "text" -- <command> [args...]
          svault render <template> <output> --reason "text"
          svault clip <vault://ref> --reason "text"                  (clears after 20 s)
          svault log [--tail N]
          svault version

        Vault folder: %LOCALAPPDATA%\ShinkansenVault (override with SVAULT_HOME).
        """;

    private static int Main(string[] args)
    {
        try
        {
            return MainAsync(args).GetAwaiter().GetResult();
        }
        catch (VaultException ex)
        {
            Console.Error.WriteLine("svault: " + ex.Message);
            return ex.ExitCode;
        }
        catch (Exception ex)
        {
            // Never print stack traces with locals; just the type and message.
            Console.Error.WriteLine($"svault: unexpected error ({ex.GetType().Name}): {ex.Message}");
            return ExitCodes.Error;
        }
    }

    private static async Task<int> MainAsync(string[] args)
    {
        var parsed = CommandLine.Parse(args);
        var home = Environment.GetEnvironmentVariable("SVAULT_HOME") is { Length: > 0 } h
            ? h
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ShinkansenVault");
        var audit = new AuditLog(Path.Combine(home, "audit.jsonl"));
        var service = new VaultService(Path.Combine(home, "vault.svlt"),
            new WindowsHelloKey(), new DpapiProtector(), new ConsentDialog(), audit, Console.Error);

        switch (parsed.Command)
        {
            case "init":
                Expect(parsed, 0);
                await service.InitAsync();
                return 0;

            case "add":
                Expect(parsed, 1);
                await service.AddAsync(VaultRef.Parse(parsed.Positional[0], requireScheme: false), parsed.Label, parsed.Replace, new SecretInputDialog());
                return 0;

            case "list":
                Expect(parsed, 0);
                foreach (var (r, label) in service.List())
                    Console.WriteLine(label is null ? r : $"{r}\t{label}");
                return 0;

            case "run":
                Expect(parsed, 0);
                return await service.RunAsync(parsed.Env, parsed.Reason ?? "", parsed.Rest, Console.Out, Console.Error);

            case "render":
                Expect(parsed, 2);
                await service.RenderAsync(parsed.Positional[0], parsed.Positional[1], parsed.Reason ?? "");
                return 0;

            case "clip":
                Expect(parsed, 1);
                await service.ClipAsync(VaultRef.Parse(parsed.Positional[0], requireScheme: false), parsed.Reason ?? "", new WinClipboard(), TimeSpan.FromSeconds(20));
                return 0;

            case "log":
                Expect(parsed, 0);
                foreach (var line in audit.ReadLines(parsed.Tail)) Console.WriteLine(line);
                return 0;

            case "version" or "--version":
                Console.WriteLine("svault " + Assembly.GetExecutingAssembly().GetName().Version?.ToString(3));
                return 0;

            case "help" or "--help" or "-h" or "/?":
                Console.WriteLine(Help);
                return 0;

            default:
                Console.Error.WriteLine($"Unknown command '{parsed.Command}'.\n");
                Console.Error.WriteLine(Help);
                return ExitCodes.Usage;
        }
    }

    private static void Expect(ParsedArgs p, int positional)
    {
        if (p.Positional.Count != positional)
            throw new VaultException($"'{p.Command}' expects {positional} argument(s). See 'svault help'.", ExitCodes.Usage);
        if (p.Command != "run" && p.Rest.Count > 0)
            throw new VaultException($"'{p.Command}' does not take '--'.", ExitCodes.Usage);
    }
}
