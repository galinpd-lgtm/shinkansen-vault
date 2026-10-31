using Xunit;

namespace ShinkansenVault.Tests;

/// <summary>Unlock once per Windows sign-in: one consent + one Windows Hello, then no prompts until sign-out or lock.</summary>
public class SessionUnlockTests
{
    private const string Secret = "FAKE-session-pw-17";
    private static readonly VaultRef Db = VaultRef.Parse("vault://demo/db");

    private static async Task<TestVault> NewVaultAsync()
    {
        var v = new TestVault(unlockForSignIn: true);
        await v.Service.InitAsync();
        await v.Service.AddAsync(Db, "Demo DB", false, new FakeInput(Secret));
        return v;
    }

    private static IReadOnlyList<string> Echo(string name) =>
        OperatingSystem.IsWindows()
            ? new[] { "cmd.exe", "/c", $"echo value=%{name}%" }
            : new[] { "sh", "-c", $"echo value=${name}" };

    private static Task<int> RunAsync(TestVault v, StringWriter? stdout = null) =>
        v.Service.RunAsync(new[] { new EnvBinding("X", Db) }, "unit test", Echo("X"), stdout ?? new StringWriter(), TextWriter.Null);

    [Fact]
    public async Task AsksOnceThenRunsWithoutPrompts()
    {
        using var v = await NewVaultAsync();   // add already unlocked it (Hello once, no consent dialog)
        var signs = v.Hello.SignCount;
        for (int i = 0; i < 5; i++)
        {
            var stdout = new StringWriter();
            Assert.Equal(0, await RunAsync(v, stdout));
            Assert.Contains("value=" + SecretMasker.Mask, stdout.ToString());
        }
        Assert.Empty(v.Consent.Requests);
        Assert.Equal(signs, v.Hello.SignCount);
        Assert.True(v.Service.IsUnlocked());
        // still logged, with the reason, every time
        var runs = v.Audit.ReadLines().Where(l => l.Contains("\"run\"")).ToList();
        Assert.Equal(5, runs.Count);
        Assert.All(runs, l => Assert.Contains("unlocked for this sign-in", l));
        Assert.DoesNotContain(Secret, v.AllFilesText());
    }

    [Fact]
    public async Task FirstUseAfterSignInAsksThenStaysUnlocked()
    {
        using var v = await NewVaultAsync();
        v.Unlock!.SignOut();
        Assert.False(v.Service.IsUnlocked());
        var signs = v.Hello.SignCount;

        await RunAsync(v);
        await RunAsync(v);
        await v.Service.CheckAsync("unit test");

        Assert.Single(v.Consent.Requests);
        Assert.Equal(signs + 1, v.Hello.SignCount);
    }

    [Fact]
    public async Task DeniedFirstUseKeepsItLocked()
    {
        using var v = await NewVaultAsync();
        v.Unlock!.SignOut();
        v.Consent.Answer = false;
        await Assert.ThrowsAsync<VaultException>(() => RunAsync(v));
        Assert.False(v.Service.IsUnlocked());
        v.Hello.Cancel = true;
        v.Consent.Answer = true;
        await Assert.ThrowsAsync<VaultException>(() => RunAsync(v));
        Assert.False(v.Service.IsUnlocked());
    }

    [Fact]
    public async Task LockAsksAgain()
    {
        using var v = await NewVaultAsync();
        Assert.True(v.Service.Lock());
        Assert.False(v.Service.Lock());
        Assert.Contains("\"lock\"", v.Audit.ReadLines().Last());
        await RunAsync(v);
        Assert.Single(v.Consent.Requests);
    }

    [Fact]
    public async Task ExportOfAllPasswordsAlwaysAsks()
    {
        using var v = new TestVault(unlockForSignIn: true);
        await v.Service.InitAsync();
        await v.Service.ImportGoogleAsync(GoogleCsv.Parse("name,url,username,password,note\na.test,https://a.test/,u,FAKE-a,\n"), "in.csv");
        var signs = v.Hello.SignCount;
        var before = v.Consent.Requests.Count;
        var outFile = Path.Combine(v.Dir, "..", "svault-export-" + Guid.NewGuid().ToString("N") + ".csv");
        try
        {
            v.Consent.Answer = false;
            await Assert.ThrowsAsync<VaultException>(() => v.Service.ExportGoogleAsync(outFile, "unit test"));
            v.Consent.Answer = true;
            Assert.Equal(1, await v.Service.ExportGoogleAsync(outFile, "unit test"));
        }
        finally
        {
            SecureFile.Wipe(outFile);
        }
        Assert.Equal(before + 2, v.Consent.Requests.Count);   // the dialog every time
        Assert.Equal(signs, v.Hello.SignCount);               // but no Windows Hello while unlocked
    }

    [Fact]
    public async Task KeptKeyIsBoundToTheVault()
    {
        using var v = await NewVaultAsync();
        // Another vault (re-created at the same place) must not open with this key.
        Assert.True(v.Service.IsUnlocked());
        File.Delete(v.VaultPath);
        await v.Service.InitAsync();
        Assert.False(v.Service.IsUnlocked());   // the kept key belongs to the old vault
        var signs = v.Hello.SignCount;
        await v.Service.AddAsync(Db, null, false, new FakeInput("FAKE-other"));
        Assert.Equal(signs + 1, v.Hello.SignCount);
        Assert.True(v.Service.IsUnlocked());
    }

    [Fact]
    public async Task WithoutSignInUnlockEveryUseAsks()
    {
        using var v = new TestVault();
        await v.Service.InitAsync();
        await v.Service.AddAsync(Db, null, false, new FakeInput(Secret));
        await RunAsync(v);
        await RunAsync(v);
        Assert.Equal(2, v.Consent.Requests.Count);
        Assert.False(v.Service.IsUnlocked());
    }
}
