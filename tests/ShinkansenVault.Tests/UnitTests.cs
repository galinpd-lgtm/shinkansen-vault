using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace ShinkansenVault.Tests;

public class VaultRefTests
{
    [Theory]
    [InlineData("vault://varnahub/cpanel", "varnahub/cpanel")]
    [InlineData("vault://a", "a")]
    [InlineData("VAULT://x.y/z-1_2", "x.y/z-1_2")]
    public void ParsesValid(string text, string path) => Assert.Equal(path, VaultRef.Parse(text).Path);

    [Theory]
    [InlineData("varnahub/cpanel")]          // scheme required
    [InlineData("vault://")]
    [InlineData("vault://../etc")]
    [InlineData("vault://a//b")]
    [InlineData("vault://a b")]
    [InlineData("vault:///a")]
    public void RejectsInvalid(string text) => Assert.Throws<VaultException>(() => VaultRef.Parse(text));

    [Fact]
    public void SchemeOptionalWhenAllowed() => Assert.Equal("a/b", VaultRef.Parse("a/b", requireScheme: false).Path);
}

public class CryptoTests
{
    [Fact]
    public void SealOpenRoundTrip()
    {
        var key = VaultCrypto.RandomBytes(32);
        var data = Encoding.UTF8.GetBytes("fake-value-123");
        var sealedData = VaultCrypto.Seal(key, data, Encoding.UTF8.GetBytes("aad"));
        Assert.Equal(data, VaultCrypto.Open(key, sealedData, Encoding.UTF8.GetBytes("aad")));
    }

    [Fact]
    public void TamperOrWrongAadFails()
    {
        var key = VaultCrypto.RandomBytes(32);
        var sealedData = VaultCrypto.Seal(key, Encoding.UTF8.GetBytes("fake"), Encoding.UTF8.GetBytes("aad"));
        Assert.ThrowsAny<CryptographicException>(() => VaultCrypto.Open(key, sealedData, Encoding.UTF8.GetBytes("other")));
        sealedData[^1] ^= 1;
        Assert.ThrowsAny<CryptographicException>(() => VaultCrypto.Open(key, sealedData, Encoding.UTF8.GetBytes("aad")));
    }

    [Fact]
    public void KekIsDeterministicAndSaltBound()
    {
        var sig = VaultCrypto.RandomBytes(256);
        var salt = VaultCrypto.RandomBytes(32);
        Assert.Equal(VaultCrypto.DeriveKek(sig, salt), VaultCrypto.DeriveKek(sig, salt));
        Assert.NotEqual(VaultCrypto.DeriveKek(sig, salt), VaultCrypto.DeriveKek(sig, VaultCrypto.RandomBytes(32)));
    }
}

public class SecretMaskerTests
{
    [Fact]
    public void MasksWholeText() =>
        Assert.Equal("user=x pass=•••• end", SecretMasker.MaskAll("user=x pass=FAKE-Secret-1 end", new[] { "FAKE-Secret-1" }));

    [Fact]
    public void MasksAcrossChunkBoundaries()
    {
        var m = new SecretMasker(new[] { "FAKE-Secret-1" });
        var input = "a FAKE-Secret-1 b FAKE-Secret-1";
        var sb = new StringBuilder();
        foreach (var c in input) sb.Append(m.Push(new[] { c }));
        sb.Append(m.Flush());
        Assert.Equal("a •••• b ••••", sb.ToString());
    }

    [Fact]
    public void CompleteLineIsEmittedAtOnce()
    {
        // A long secret used to hold back the end of every line until the next write.
        var m = new SecretMasker(new[] { "FAKE-a-very-long-secret-token-0123456789" });
        Assert.Equal("{\"id\":1,\"ok\":true}\n", m.Push("{\"id\":1,\"ok\":true}\n"));
        Assert.Equal("", m.Flush());
    }

    [Fact]
    public void LineIsMaskedBeforeItIsEmitted()
    {
        var m = new SecretMasker(new[] { "FAKE-a-very-long-secret-token-0123456789" });
        var sb = new StringBuilder();
        foreach (var c in "x=FAKE-a-very-long-secret-token-0123456789\ny=") sb.Append(m.Push(new[] { c }));
        Assert.Equal("x=••••\n", sb.ToString());
        Assert.Equal("y=", m.Flush());
    }

    [Fact]
    public void PartialLineStillHeldForSplitSecret()
    {
        var m = new SecretMasker(new[] { "FAKE-Secret-1" });
        var sb = new StringBuilder();
        sb.Append(m.Push("done\nkey=FAKE-Sec"));
        Assert.Equal("done\n", sb.ToString());
        sb.Append(m.Push("ret-1 end\n"));
        Assert.Equal("done\nkey=•••• end\n", sb.ToString());
    }

    [Fact]
    public void MultilineSecretIsMaskedAcrossLines()
    {
        var m = new SecretMasker(new[] { "FAKE-line-one\nFAKE-line-two" });
        var sb = new StringBuilder();
        sb.Append(m.Push("k=FAKE-line-one\n"));
        sb.Append(m.Push("FAKE-line-two\n"));
        sb.Append(m.Flush());
        Assert.Equal("k=••••\n", sb.ToString());
    }

    [Fact]
    public void LongestSecretWins() =>
        Assert.Equal("[••••]", SecretMasker.MaskAll("[abcdef]", new[] { "abc", "abcdef" }));

    [Fact]
    public void NoSecretsPassesThrough() =>
        Assert.Equal("hello", SecretMasker.MaskAll("hello", Array.Empty<string>()));
}

public class TemplateTests
{
    [Fact]
    public void FindsAndRendersPlaceholders()
    {
        var t = "host=x\npass={{vault://site/db}}\nkey={{ vault://site/api }}\nagain={{vault://site/db}}";
        var refs = TemplateRenderer.FindRefs(t);
        Assert.Equal(new[] { "site/db", "site/api" }, refs.Select(r => r.Path));
        var outText = TemplateRenderer.Render(t, new Dictionary<string, string> { ["site/db"] = "P1", ["site/api"] = "K2" });
        Assert.Equal("host=x\npass=P1\nkey=K2\nagain=P1", outText);
    }

    [Theory]
    [InlineData("CON")]
    [InlineData("con.txt")]
    [InlineData("NUL")]
    [InlineData("-")]
    [InlineData(@"\\.\pipe\x")]
    [InlineData("/dev/stdout")]
    public void RejectsDeviceOutputs(string output) =>
        Assert.Throws<VaultException>(() => OutputPathGuard.Validate(output, "template.txt"));

    [Fact]
    public void RejectsOverwritingTemplate() =>
        Assert.Throws<VaultException>(() => OutputPathGuard.Validate("t.txt", "t.txt"));
}

public class CommandLineTests
{
    [Fact]
    public void ParsesRun()
    {
        var p = CommandLine.Parse(new[] { "run", "--env", "DB_PASS=vault://site/db", "--reason", "deploy", "--", "tool", "--flag", "x" });
        Assert.Equal("run", p.Command);
        Assert.Equal("DB_PASS", p.Env.Single().Name);
        Assert.Equal("site/db", p.Env.Single().Ref.Path);
        Assert.Equal("deploy", p.Reason);
        Assert.Equal(new[] { "tool", "--flag", "x" }, p.Rest);
    }

    [Theory]
    [InlineData("--value")]
    [InlineData("--password")]
    [InlineData("--secret")]
    public void RefusesValueArguments(string opt) =>
        Assert.Throws<VaultException>(() => CommandLine.Parse(new[] { "add", "vault://a", opt, "x" }));

    [Theory]
    [InlineData("1BAD=vault://a")]
    [InlineData("NAME=a")]
    [InlineData("NAME")]
    public void RejectsBadEnv(string env) => Assert.Throws<VaultException>(() => CommandLine.ParseEnv(env));
}

public class MaskEncodingTests
{
    [Fact]
    public void UnicodeOutputGetsBullets() =>
        Assert.Equal(SecretMasker.Mask, SecretMasker.MaskFor(new System.Text.UTF8Encoding(false)));

    [Fact]
    public void AsciiOutputGetsStars() =>
        Assert.Equal(SecretMasker.AsciiMask, SecretMasker.MaskFor(System.Text.Encoding.ASCII));

    [Fact]
    public void CustomMaskIsUsed()
    {
        var m = new SecretMasker(new[] { "FAKE-x" }, SecretMasker.AsciiMask);
        Assert.Equal("a **** b", m.Push("a FAKE-x b") + m.Flush());
    }
}
