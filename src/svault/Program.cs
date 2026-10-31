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
          svault import-google [file.csv]                            (Google Password Manager export → vault)
          svault export-google [file.csv]                            (vault → CSV for Google import; wiped after)
          svault check                                               (weak / reused passwords, names only)
          svault rotate <vault://ref> --reason "text"                (new password via clipboard, saved on confirm)
          svault log [--tail N]
          svault mcp                                                 (MCP server over stdio for AI agents)
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

            case "import-google":
            {
                ExpectUpTo(parsed, 1);
                var file = parsed.Positional.Count == 1 ? parsed.Positional[0] : Pickers.OpenCsv();
                if (file == null) throw new VaultException("Cancelled.", ExitCodes.Denied);
                if (!File.Exists(file)) throw new VaultException("File not found: " + file);
                var report = await service.ImportGoogleAsync(GoogleCsv.Parse(File.ReadAllText(file)), Path.GetFileName(file));
                Console.WriteLine($"Added {report.Added.Count}, changed {report.Changed.Count}, unchanged {report.Unchanged}, " +
                                  $"only in vault {report.OnlyInVault.Count}, duplicate rows skipped {report.SkippedDuplicates}.");
                PrintList("Added", report.Added);
                PrintList("Changed (old value kept as previous)", report.Changed);
                PrintList("Only in the vault (not in this file; left untouched)", report.OnlyInVault);
                WipeIfAgreed(file,
                    "Паролите са внесени в трезора.\n\nФайлът от Google съдържа ВСИЧКИ пароли в явен вид:\n" + file +
                    "\n\nДа го изтрия ли сега (презаписване + изтриване)?\n\n" +
                    "Imported. The Google file holds ALL passwords in plain text. Wipe it now?");
                return 0;
            }

            case "export-google":
            {
                ExpectUpTo(parsed, 1);
                var file = parsed.Positional.Count == 1 ? parsed.Positional[0] : Pickers.SaveCsv();
                if (file == null) throw new VaultException("Cancelled.", ExitCodes.Denied);
                var count = await service.ExportGoogleAsync(file,
                    parsed.Reason ?? "Изнасяне за внасяне в Google Password Manager / export for import into Google Password Manager");
                Console.WriteLine($"Wrote {count} logins to {file}. Content not shown.");
                Pickers.OpenUrl("https://passwords.google.com/options");
                WipeIfAgreed(file,
                    $"Записах {count} пароли за Google в:\n{file}\n\n" +
                    "1. В отворената страница: Настройки → Внасяне на пароли → изберете този файл.\n" +
                    "2. Когато Google потвърди внасянето, натиснете „Изтрий файла“.\n\n" +
                    "In Google Password Manager: Settings → Import passwords → choose this file. When done, press the wipe button.");
                return 0;
            }

            case "check":
            {
                Expect(parsed, 0);
                var report = await service.CheckAsync(parsed.Reason ?? "Проверка за слаби и повторени пароли / check for weak and reused passwords");
                Console.WriteLine(McpServer.FormatCheck(report));
                return 0;
            }

            case "rotate":
                Expect(parsed, 1);
                return await service.RotateAsync(VaultRef.Parse(parsed.Positional[0], requireScheme: false), parsed.Reason ?? "",
                    new WinClipboard(), new RotationDialog()) ? 0 : ExitCodes.Denied;

            case "log":
                Expect(parsed, 0);
                foreach (var line in audit.ReadLines(parsed.Tail)) Console.WriteLine(line);
                return 0;

            case "mcp":
                Expect(parsed, 0);
                {
                    // stdout is the protocol channel: UTF-8, no BOM, nothing else written to it.
                    var utf8 = new System.Text.UTF8Encoding(false);
                    using var input = new StreamReader(Console.OpenStandardInput(), utf8);
                    using var output = new StreamWriter(Console.OpenStandardOutput(), utf8) { AutoFlush = true };
                    var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";
                    await new McpServer(service, audit, new SecretInputDialog(), version, new WinClipboard(), new RotationDialog()).RunAsync(input, output);
                }
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

    private static void ExpectUpTo(ParsedArgs p, int max)
    {
        if (p.Positional.Count > max)
            throw new VaultException($"'{p.Command}' expects at most {max} argument(s). See 'svault help'.", ExitCodes.Usage);
        if (p.Rest.Count > 0) throw new VaultException($"'{p.Command}' does not take '--'.", ExitCodes.Usage);
    }

    private static void PrintList(string title, IReadOnlyList<string> items)
    {
        if (items.Count == 0) return;
        Console.WriteLine();
        Console.WriteLine(title + ":");
        foreach (var i in items) Console.WriteLine("  " + i);
    }

    private static void WipeIfAgreed(string file, string message)
    {
        if (Ask.YesNo("Shinkansen Vault — файл с пароли / password file", message, "Изтрий файла / Wipe file", "Остави го / Keep it"))
        {
            SecureFile.Wipe(file);
            Console.Error.WriteLine("The password file was wiped.");
        }
        else
        {
            Console.Error.WriteLine("WARNING: the password file was kept and holds passwords in plain text: " + file);
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
