using Xunit;

namespace ShinkansenVault.Tests;

public class VaultServiceTests
{
    // Obviously fake test values only.
    private const string Secret = "FAKE-pw-not-real-42";
    private const string Secret2 = "FAKE-token-xyz-0000";

    private static async Task<TestVault> NewVaultWithSecretAsync()
    {
        var v = new TestVault();
        await v.Service.InitAsync();
        await v.Service.AddAsync(VaultRef.Parse("vault://demo/db"), "Demo DB", false, new FakeInput(Secret));
        await v.Service.AddAsync(VaultRef.Parse("vault://demo/api"), null, false, new FakeInput(Secret2));
        return v;
    }

    private static IReadOnlyList<string> EchoEnvCommand(string name) =>
        OperatingSystem.IsWindows()
            ? new[] { "cmd.exe", "/c", $"echo value=%{name}% & echo err=%{name}% 1>&2" }
            : new[] { "sh", "-c", $"echo value=${name}; echo err=${name} 1>&2" };

    [Fact]
    public async Task InitTwiceFails()
    {
        using var v = new TestVault();
        await v.Service.InitAsync();
        await Assert.ThrowsAsync<VaultException>(() => v.Service.InitAsync());
    }

    [Fact]
    public async Task ListShowsRefsAndLabelsButNeverValues()
    {
        using var v = await NewVaultWithSecretAsync();
        var list = v.Service.List();
        Assert.Equal(new[] { "vault://demo/api", "vault://demo/db" }, list.Select(x => x.Ref));
        Assert.Equal("Demo DB", list.Single(x => x.Ref == "vault://demo/db").Label);
        Assert.DoesNotContain(Secret, v.AllFilesText());   // not in vault file, audit log or info output
        Assert.DoesNotContain(Secret2, v.AllFilesText());
    }

    [Fact]
    public async Task AddExistingRequiresReplace()
    {
        using var v = await NewVaultWithSecretAsync();
        await Assert.ThrowsAsync<VaultException>(() => v.Service.AddAsync(VaultRef.Parse("vault://demo/db"), null, false, new FakeInput("x")));
        await v.Service.AddAsync(VaultRef.Parse("vault://demo/db"), null, true, new FakeInput("FAKE-new"));
    }

    [Fact]
    public async Task RunInjectsEnvAndMasksStdoutAndStderr()
    {
        using var v = await NewVaultWithSecretAsync();
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var code = await v.Service.RunAsync(
            new[] { new EnvBinding("SV_TEST_PW", VaultRef.Parse("vault://demo/db")) },
            "unit test", EchoEnvCommand("SV_TEST_PW"), stdout, stderr);

        Assert.Equal(0, code);
        Assert.Contains("value=" + SecretMasker.Mask, stdout.ToString());
        Assert.Contains("err=" + SecretMasker.Mask, stderr.ToString());
        Assert.DoesNotContain(Secret, stdout.ToString() + stderr.ToString());

        var req = Assert.Single(v.Consent.Requests);
        Assert.Equal("run", req.Action);
        Assert.Equal("unit test", req.Reason);
        Assert.Contains("SV_TEST_PW = vault://demo/db", req.Refs);
        Assert.Contains("approved", v.Audit.ReadLines().Last());
        Assert.DoesNotContain(Secret, v.AllFilesText());
    }

    [Fact]
    public async Task DeniedConsentNeverCallsHelloAndIsLogged()
    {
        using var v = await NewVaultWithSecretAsync();
        v.Consent.Answer = false;
        var before = v.Hello.SignCount;
        var ex = await Assert.ThrowsAsync<VaultException>(() => v.Service.RunAsync(
            new[] { new EnvBinding("X", VaultRef.Parse("vault://demo/db")) }, "why", EchoEnvCommand("X"), TextWriter.Null, TextWriter.Null));
        Assert.Equal(ExitCodes.Denied, ex.ExitCode);
        Assert.Equal(before, v.Hello.SignCount);
        Assert.Contains("\"denied\"", v.Audit.ReadLines().Last());
    }

    [Fact]
    public async Task RunMasksValueTypedIntoCommandInAuditLog()
    {
        using var v = await NewVaultWithSecretAsync();
        // Someone typed the value itself into the command, next to the reference.
        IReadOnlyList<string> cmd = OperatingSystem.IsWindows()
            ? new[] { "cmd.exe", "/c", "echo ok & rem " + Secret }
            : new[] { "sh", "-c", "echo ok # " + Secret };
        var code = await v.Service.RunAsync(
            new[] { new EnvBinding("X", VaultRef.Parse("vault://demo/db")) }, "unit test", cmd, TextWriter.Null, TextWriter.Null);

        Assert.Equal(0, code);
        var last = v.Audit.ReadLines().Last();
        Assert.Contains("\"approved\"", last);
        Assert.Contains("echo ok", last);
        Assert.DoesNotContain(Secret, v.AllFilesText());
        Assert.Single(v.Audit.ReadLines(), l => l.Contains("\"run\""));   // one entry per run, not two
    }

    [Fact]
    public async Task CancelledHelloDenies()
    {
        using var v = await NewVaultWithSecretAsync();
        v.Hello.Cancel = true;
        var ex = await Assert.ThrowsAsync<VaultException>(() => v.Service.ClipAsync(VaultRef.Parse("vault://demo/db"), "why", new FakeClipboard(), TimeSpan.Zero));
        Assert.Equal(ExitCodes.Denied, ex.ExitCode);
        Assert.Contains("windows hello cancelled", v.Audit.ReadLines().Last());
    }

    [Fact]
    public async Task WrongHelloKeyCannotUnlock()
    {
        using var v = await NewVaultWithSecretAsync();
        v.Hello.ReplaceAllKeys();
        await Assert.ThrowsAsync<VaultException>(() => v.Service.ClipAsync(VaultRef.Parse("vault://demo/db"), "why", new FakeClipboard(), TimeSpan.Zero));
        Assert.Contains("unlock failed", v.Audit.ReadLines().Last());
    }

    [Fact]
    public async Task ReasonIsRequired()
    {
        using var v = await NewVaultWithSecretAsync();
        await Assert.ThrowsAsync<VaultException>(() => v.Service.ClipAsync(VaultRef.Parse("vault://demo/db"), " ", new FakeClipboard(), TimeSpan.Zero));
        Assert.Empty(v.Consent.Requests);
    }

    [Fact]
    public async Task UnknownRefFailsBeforeConsent()
    {
        using var v = await NewVaultWithSecretAsync();
        await Assert.ThrowsAsync<VaultException>(() => v.Service.ClipAsync(VaultRef.Parse("vault://nope/x"), "why", new FakeClipboard(), TimeSpan.Zero));
        Assert.Empty(v.Consent.Requests);
    }

    [Fact]
    public async Task RenderWritesFileAndPrintsNoContent()
    {
        using var v = await NewVaultWithSecretAsync();
        var outDir = Path.Combine(Path.GetTempPath(), "svault-out-" + Guid.NewGuid().ToString("N"));
        try
        {
            var template = Path.Combine(outDir, "app.env.template");
            var output = Path.Combine(outDir, "deploy", "app.env");
            Directory.CreateDirectory(outDir);
            File.WriteAllText(template, "DB={{vault://demo/db}}\nAPI={{vault://demo/api}}\n");

            await v.Service.RenderAsync(template, output, "render test");

            Assert.Equal($"DB={Secret}\nAPI={Secret2}\n", File.ReadAllText(output));
            Assert.DoesNotContain(Secret, v.Info.ToString());
            Assert.DoesNotContain(Secret, v.AllFilesText());
            Assert.Equal("render", v.Consent.Requests.Single().Action);
            Assert.Equal(2, v.Consent.Requests.Single().Refs.Count);
        }
        finally { Directory.Delete(outDir, true); }
    }

    [Fact]
    public async Task ClipSetsThenClears()
    {
        using var v = await NewVaultWithSecretAsync();
        var clip = new FakeClipboard();
        await v.Service.ClipAsync(VaultRef.Parse("vault://demo/db"), "paste in browser", clip, TimeSpan.FromMilliseconds(10));
        Assert.Equal(Secret, Assert.Single(clip.History));
        Assert.Null(clip.Content);
        Assert.DoesNotContain(Secret, v.Info.ToString());
    }

    [Fact]
    public async Task ClipDoesNotClearIfUserCopiedSomethingElse()
    {
        using var v = await NewVaultWithSecretAsync();
        var clip = new FakeClipboard();
        var task = v.Service.ClipAsync(VaultRef.Parse("vault://demo/db"), "x", clip, TimeSpan.FromMilliseconds(200));
        await Task.Delay(50);
        clip.SetText("something else");
        await task;
        Assert.Equal("something else", clip.Content);
    }

    [Fact]
    public async Task CancelledInputStoresNothing()
    {
        using var v = new TestVault();
        await v.Service.InitAsync();
        await Assert.ThrowsAsync<VaultException>(() => v.Service.AddAsync(VaultRef.Parse("vault://a"), null, false, new FakeInput(null)));
        Assert.Empty(v.Service.List());
    }

    [Fact]
    public async Task ServiceHasNoRevealMethod()
    {
        // Guard: the public API must not grow a method that returns plaintext values.
        var names = typeof(VaultService).GetMethods().Select(m => m.Name.ToLowerInvariant());
        Assert.DoesNotContain(names, n => n.Contains("reveal") || n.Contains("get") && n.Contains("value") || n == "show" || n == "print");
        await Task.CompletedTask;
    }
}
