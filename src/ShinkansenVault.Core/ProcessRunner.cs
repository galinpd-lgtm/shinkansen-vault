using System.Diagnostics;

namespace ShinkansenVault;

/// <summary>Runs a child process with extra environment variables and masks secrets in its output.</summary>
public static class ProcessRunner
{
    public static async Task<int> RunAsync(
        IReadOnlyList<string> command,
        IReadOnlyDictionary<string, string> environment,
        IReadOnlyCollection<string> secrets,
        TextWriter stdout,
        TextWriter stderr)
    {
        if (command.Count == 0) throw new VaultException("No command given after '--'.", ExitCodes.Usage);
        var psi = new ProcessStartInfo(command[0])
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
        };
        foreach (var a in command.Skip(1)) psi.ArgumentList.Add(a);
        foreach (var (k, v) in environment) psi.Environment[k] = v;

        Process process;
        try
        {
            process = Process.Start(psi) ?? throw new VaultException($"Could not start '{command[0]}'.");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new VaultException($"Could not start '{command[0]}': {ex.Message}");
        }

        using (process)
        {
            var outTask = PumpAsync(process.StandardOutput, stdout, secrets);
            var errTask = PumpAsync(process.StandardError, stderr, secrets);
            await process.WaitForExitAsync();
            await Task.WhenAll(outTask, errTask);
            return process.ExitCode;
        }
    }

    private static async Task PumpAsync(StreamReader reader, TextWriter writer, IReadOnlyCollection<string> secrets)
    {
        var masker = new SecretMasker(secrets);
        var buffer = new char[4096];
        int n;
        while ((n = await reader.ReadAsync(buffer.AsMemory())) > 0)
        {
            var safe = masker.Push(buffer.AsSpan(0, n));
            if (safe.Length > 0)
            {
                await writer.WriteAsync(safe);
                await writer.FlushAsync();
            }
        }
        Array.Clear(buffer);
        await writer.WriteAsync(masker.Flush());
        await writer.FlushAsync();
    }
}
