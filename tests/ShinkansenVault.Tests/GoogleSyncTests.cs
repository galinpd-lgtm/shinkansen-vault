using Xunit;

namespace ShinkansenVault.Tests;

/// <summary>Google Password Manager sync, check and rotate. Obviously fake values only.</summary>
public class GoogleSyncTests
{
    private const string Header = "name,url,username,password,note\r\n";

    private static string Csv(params string[] lines) => Header + string.Join("\r\n", lines) + "\r\n";

    private static async Task<TestVault> NewVaultAsync()
    {
        var v = new TestVault();
        await v.Service.InitAsync();
        return v;
    }

    private sealed class FakeRotation : IRotationPrompt
    {
        public bool Answer { get; set; } = true;
        public string? Site { get; private set; }
        public bool ConfirmChanged(string reference, string? site, Action copyNew, Action copyOld)
        {
            Site = site;
            copyOld();
            copyNew();
            return Answer;
        }
    }

    [Fact]
    public void ParsesQuotedFieldsNewlinesAndBom()
    {
        var text = "﻿" + Csv(
            "example.com,https://example.com/login,user@example.test,\"FAKE,pw\"\"1\",\"line1\nline2\"",
            "empty.test,https://empty.test/,someone,,",
            "app,android://hash@com.example.app/,u2,FAKE-app-pw,");
        var rows = GoogleCsv.Parse(text);
        Assert.Equal(2, rows.Count); // the row without a password is skipped
        Assert.Equal("FAKE,pw\"1", rows[0].Password);
        Assert.Equal("line1\nline2", rows[0].Note);
        Assert.Equal("google/android.com.example.app", GoogleCsv.RefBase(rows[1].Url, rows[1].Name));
    }

    [Fact]
    public void RejectsNonGoogleCsv()
    {
        Assert.Throws<VaultException>(() => GoogleCsv.Parse("a,b,c\r\n1,2,3\r\n"));
    }

    [Fact]
    public void WriteThenParseRoundTrips()
    {
        var rows = new[] { new GoogleRow("n,1", "https://x.test/", "u\"q", "FAKE pw ", "a\r\nb") };
        var back = GoogleCsv.Parse(GoogleCsv.Write(rows));
        Assert.Equal(rows[0], back.Single() with { Note = back.Single().Note.Replace("\r\n", "\n").Replace("\n", "\r\n") });
    }

    [Fact]
    public void RefBaseUsesHostWithoutWww()
    {
        Assert.Equal("google/github.com", GoogleCsv.RefBase("https://www.github.com/login", "x"));
        Assert.Equal("google/localhost-8080", GoogleCsv.RefBase("http://localhost:8080/", "x"));
        Assert.Equal("google/my-site", GoogleCsv.RefBase("not a url", "My Site"));
    }

    [Fact]
    public async Task ImportAddsUpdatesAndReports()
    {
        using var v = await NewVaultAsync();
        var first = GoogleCsv.Parse(Csv(
            "github.com,https://github.com/login,a@example.test,FAKE-gh-1,",
            "github.com,https://github.com/login,b@example.test,FAKE-gh-2,",
            "shop.test,https://shop.test/,a@example.test,FAKE-shop,"));
        var r1 = await v.Service.ImportGoogleAsync(first, "first.csv");
        Assert.Equal(new[] { "vault://google/github.com", "vault://google/github.com-2", "vault://google/shop.test" }, r1.Added);
        Assert.Empty(r1.Changed);

        var second = GoogleCsv.Parse(Csv(
            "github.com,https://github.com/login,a@example.test,FAKE-gh-1,",
            "github.com,https://github.com/login,b@example.test,FAKE-gh-2-NEW,",
            "github.com,https://github.com/login,b@example.test,FAKE-dupe,",
            "new.test,https://new.test/,c@example.test,FAKE-new,"));
        var r2 = await v.Service.ImportGoogleAsync(second, "second.csv");
        Assert.Equal(new[] { "vault://google/new.test" }, r2.Added);
        Assert.Equal(new[] { "vault://google/github.com-2" }, r2.Changed);
        Assert.Equal(1, r2.Unchanged);
        Assert.Equal(1, r2.SkippedDuplicates);
        Assert.Equal(new[] { "vault://google/shop.test" }, r2.OnlyInVault);

        // Values, usernames and URLs never appear in plain text on disk or in the output.
        var all = v.AllFilesText();
        foreach (var secret in new[] { "FAKE-gh-1", "FAKE-gh-2-NEW", "FAKE-shop", "FAKE-new", "a@example.test", "github.com/login" })
            Assert.DoesNotContain(secret, all);
    }

    [Fact]
    public async Task ImportDeniedChangesNothing()
    {
        using var v = await NewVaultAsync();
        v.Consent.Answer = false;
        var rows = GoogleCsv.Parse(Csv("x.test,https://x.test/,u,FAKE-x,"));
        var ex = await Assert.ThrowsAsync<VaultException>(() => v.Service.ImportGoogleAsync(rows, "x.csv"));
        Assert.Equal(ExitCodes.Denied, ex.ExitCode);
        Assert.Empty(v.Service.List());
    }

    [Fact]
    public async Task ExportWritesGoogleCsvThatImportsBack()
    {
        using var v = await NewVaultAsync();
        var rows = GoogleCsv.Parse(Csv(
            "github.com,https://github.com/login,a@example.test,FAKE-gh-1,note 1",
            "shop.test,https://shop.test/,a@example.test,\"FAKE,shop\","));
        await v.Service.ImportGoogleAsync(rows, "in.csv");
        // A manually added secret without site details is not exported.
        await v.Service.AddAsync(VaultRef.Parse("vault://kagami/api"), null, false, new FakeInput("FAKE-api"));

        var outDir = Path.Combine(Path.GetTempPath(), "svault-export-" + Guid.NewGuid().ToString("N"));
        var outFile = Path.Combine(outDir, "for-google.csv");
        try
        {
            var n = await v.Service.ExportGoogleAsync(outFile, "unit test");
            Assert.Equal(2, n);
            var back = GoogleCsv.Parse(File.ReadAllText(outFile));
            Assert.Equal(rows.OrderBy(r => r.Url), back.OrderBy(r => r.Url));
            Assert.DoesNotContain("FAKE-api", File.ReadAllText(outFile));
            Assert.Contains(v.Consent.Requests, r => r.Action == "export-google" && r.Target == Path.GetFullPath(outFile));

            SecureFile.Wipe(outFile);
            Assert.False(File.Exists(outFile));
        }
        finally
        {
            try { Directory.Delete(outDir, true); } catch { }
        }
    }

    [Fact]
    public async Task CheckFindsWeakAndReusedByNameOnly()
    {
        using var v = await NewVaultAsync();
        await v.Service.ImportGoogleAsync(GoogleCsv.Parse(Csv(
            "a.test,https://a.test/,u,FAKE-Same-pw-123!,",
            "b.test,https://b.test/,u,FAKE-Same-pw-123!,",
            "c.test,https://c.test/,u,short,",
            "d.test,https://d.test/,u,Fake-Unique-pw-77#xyz,")), "in.csv");
        var report = await v.Service.CheckAsync("unit test");
        Assert.Equal(4, report.Total);
        Assert.Equal(new[] { "vault://google/a.test", "vault://google/b.test" }, report.ReusedGroups.Single());
        Assert.Equal("vault://google/c.test", report.Weak.Single().Ref);
        var text = McpServer.FormatCheck(report);
        Assert.DoesNotContain("FAKE-Same", text);
        Assert.DoesNotContain("short,", text);
    }

    [Fact]
    public void GeneratedPasswordsAreStrongAndDifferent()
    {
        var a = PasswordCheck.Generate();
        var b = PasswordCheck.Generate();
        Assert.Equal(20, a.Length);
        Assert.NotEqual(a, b);
        Assert.Null(PasswordCheck.WeakReason(a));
    }

    [Fact]
    public async Task RotateSavesOnlyAfterConfirmationAndClearsClipboard()
    {
        using var v = await NewVaultAsync();
        await v.Service.ImportGoogleAsync(GoogleCsv.Parse(Csv("a.test,https://a.test/,u,FAKE-old-pw,")), "in.csv");
        var r = VaultRef.Parse("vault://google/a.test");
        var clip = new FakeClipboard();

        var no = new FakeRotation { Answer = false };
        Assert.False(await v.Service.RotateAsync(r, "unit test", clip, no));
        Assert.Null(clip.Content);
        Assert.Equal("https://a.test/", no.Site);
        Assert.Equal("FAKE-old-pw", clip.History[^2]); // copyOld put the old value
        var rejected = clip.History[^1]!;

        var yes = new FakeRotation { Answer = true };
        Assert.True(await v.Service.RotateAsync(r, "unit test", clip, yes));
        Assert.Null(clip.Content);
        var newPw = clip.History[^1]!;
        Assert.NotEqual("FAKE-old-pw", newPw);
        Assert.NotEqual(rejected, newPw);

        // The vault now holds the new value (checked through an export, not a shell:
        // generated passwords may contain characters that cmd.exe interprets).
        var outFile = Path.Combine(Path.GetTempPath(), "svault-rotate-" + Guid.NewGuid().ToString("N") + ".csv");
        try
        {
            await v.Service.ExportGoogleAsync(outFile, "unit test");
            Assert.Equal(newPw, GoogleCsv.Parse(File.ReadAllText(outFile)).Single().Password);
        }
        finally
        {
            SecureFile.Wipe(outFile);
        }
        Assert.DoesNotContain(newPw, v.AllFilesText());
    }
}
