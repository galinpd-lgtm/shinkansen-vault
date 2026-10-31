using System.Text.Json.Nodes;
using Xunit;

namespace ShinkansenVault.Tests;

public class McpServerTests
{
    private const string Secret = "FAKE-mail-pw-77";

    private static async Task<(TestVault Vault, McpServer Server)> NewAsync(FakeInput? input = null)
    {
        var v = new TestVault();
        await v.Service.InitAsync();
        await v.Service.AddAsync(VaultRef.Parse("vault://kagami/mail-test"), "Test mail", false, new FakeInput(Secret));
        return (v, new McpServer(v.Service, v.Audit, input ?? new FakeInput(null), "9.9.9"));
    }

    private static string Req(int id, string method, JsonObject? p = null) =>
        new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = p ?? new JsonObject() }.ToJsonString();

    private static string Call(int id, string tool, JsonObject args) =>
        Req(id, "tools/call", new JsonObject { ["name"] = tool, ["arguments"] = args });

    private static string ResultText(JsonObject? response) =>
        response!["result"]!["content"]![0]!["text"]!.GetValue<string>();

    private static bool IsError(JsonObject? response) =>
        response!["result"]!["isError"]!.GetValue<bool>();

    private static JsonArray EchoEnv(string name) =>
        OperatingSystem.IsWindows()
            ? new JsonArray("cmd.exe", "/c", $"echo value=%{name}%")
            : new JsonArray("sh", "-c", $"echo value=${name}");

    [Fact]
    public async Task InitializeReportsServerAndToolsCapability()
    {
        var (v, s) = await NewAsync();
        using var _ = v;
        var r = await s.HandleLineAsync(Req(1, "initialize", new JsonObject { ["protocolVersion"] = "2025-03-26" }));
        Assert.Equal("2025-03-26", r!["result"]!["protocolVersion"]!.GetValue<string>());
        Assert.Equal("shinkansen-vault", r["result"]!["serverInfo"]!["name"]!.GetValue<string>());
        Assert.NotNull(r["result"]!["capabilities"]!["tools"]);
    }

    [Fact]
    public async Task NotificationsGetNoResponse()
    {
        var (v, s) = await NewAsync();
        using var _ = v;
        Assert.Null(await s.HandleLineAsync("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}"));
    }

    [Fact]
    public async Task UnknownMethodAndBadJson()
    {
        var (v, s) = await NewAsync();
        using var _ = v;
        Assert.Equal(-32601, (await s.HandleLineAsync(Req(1, "nope")))!["error"]!["code"]!.GetValue<int>());
        Assert.Equal(-32700, (await s.HandleLineAsync("{not json"))!["error"]!["code"]!.GetValue<int>());
    }

    [Fact]
    public async Task ToolListHasNoWayToReadOrAddValues()
    {
        var (v, s) = await NewAsync();
        using var _ = v;
        var tools = (await s.HandleLineAsync(Req(1, "tools/list")))!["result"]!["tools"]!.AsArray()
            .Select(t => t!["name"]!.GetValue<string>()).ToList();
        Assert.Equal(new[] { "vault_list", "vault_add", "vault_run", "vault_render", "vault_check", "vault_rotate", "vault_log" }, tools);
    }

    [Fact]
    public async Task AddOpensDialogAndReturnsOnlyTheReference()
    {
        var input = new FakeInput("FAKE-new-token-123");
        var (v, s) = await NewAsync(input);
        using var _ = v;
        var r = await s.HandleLineAsync(Call(1, "vault_add", new JsonObject
        {
            ["ref"] = "vault://demo/api", ["label"] = "Demo API", ["reason"] = "Deploy script needs it",
        }));
        Assert.False(IsError(r));
        Assert.Contains("vault://demo/api", ResultText(r));
        Assert.DoesNotContain("FAKE-new-token-123", r!.ToJsonString());
        Assert.Equal("Deploy script needs it", input.LastReason);
        Assert.Contains(v.Service.List(), x => x.Ref == "vault://demo/api");
    }

    [Fact]
    public async Task AddDoesNotOverwriteUnlessAskedAndWarnsInDialog()
    {
        var input = new FakeInput("FAKE-replacement");
        var (v, s) = await NewAsync(input);
        using var _ = v;
        var noReplace = await s.HandleLineAsync(Call(1, "vault_add", new JsonObject
        {
            ["ref"] = "vault://kagami/mail-test", ["reason"] = "r",
        }));
        Assert.True(IsError(noReplace));
        var replace = await s.HandleLineAsync(Call(2, "vault_add", new JsonObject
        {
            ["ref"] = "vault://kagami/mail-test", ["reason"] = "password changed", ["replace"] = true,
        }));
        Assert.False(IsError(replace));
        Assert.StartsWith("ЗАМЕНЯ", input.LastReason);
    }

    [Fact]
    public async Task AddCancelledIsReportedAsDenied()
    {
        var (v, s) = await NewAsync(new FakeInput(null));
        using var _ = v;
        var r = await s.HandleLineAsync(Call(1, "vault_add", new JsonObject { ["ref"] = "vault://x/y", ["reason"] = "r" }));
        Assert.True(IsError(r));
        Assert.StartsWith("DENIED:", ResultText(r));
    }

    [Fact]
    public async Task ListShowsRefsNotValues()
    {
        var (v, s) = await NewAsync();
        using var _ = v;
        var text = ResultText(await s.HandleLineAsync(Call(1, "vault_list", new JsonObject())));
        Assert.Contains("vault://kagami/mail-test", text);
        Assert.Contains("Test mail", text);
        Assert.DoesNotContain(Secret, text);
    }

    [Fact]
    public async Task RunInjectsAndMasks()
    {
        var (v, s) = await NewAsync();
        using var _ = v;
        var r = await s.HandleLineAsync(Call(1, "vault_run", new JsonObject
        {
            ["env"] = new JsonObject { ["MAIL_PASS"] = "vault://kagami/mail-test" },
            ["command"] = EchoEnv("MAIL_PASS"),
            ["reason"] = "MCP test",
        }));
        var text = ResultText(r);
        Assert.False(IsError(r));
        Assert.Contains("exit code: 0", text);
        Assert.Contains("value=" + SecretMasker.Mask, text);
        Assert.DoesNotContain(Secret, text);
        Assert.Equal("MCP test", Assert.Single(v.Consent.Requests).Reason);
    }

    [Fact]
    public async Task RunDeniedIsReportedAsError()
    {
        var (v, s) = await NewAsync();
        using var _ = v;
        v.Consent.Answer = false;
        var r = await s.HandleLineAsync(Call(1, "vault_run", new JsonObject
        {
            ["env"] = new JsonObject { ["MAIL_PASS"] = "vault://kagami/mail-test" },
            ["command"] = EchoEnv("MAIL_PASS"),
            ["reason"] = "MCP test",
        }));
        Assert.True(IsError(r));
        Assert.StartsWith("DENIED:", ResultText(r));
    }

    [Fact]
    public async Task RunRequiresReasonAndValidRefs()
    {
        var (v, s) = await NewAsync();
        using var _ = v;
        var noReason = await s.HandleLineAsync(Call(1, "vault_run", new JsonObject
        {
            ["env"] = new JsonObject { ["X"] = "vault://kagami/mail-test" },
            ["command"] = EchoEnv("X"),
        }));
        Assert.True(IsError(noReason));
        var badRef = await s.HandleLineAsync(Call(2, "vault_run", new JsonObject
        {
            ["env"] = new JsonObject { ["X"] = "not-a-ref" },
            ["command"] = EchoEnv("X"),
            ["reason"] = "r",
        }));
        Assert.True(IsError(badRef));
        Assert.Empty(v.Consent.Requests);
    }

    [Fact]
    public async Task ChildCannotReadTheProtocolChannel()
    {
        var (v, s) = await NewAsync();
        using var _ = v;
        var cmd = OperatingSystem.IsWindows()
            ? new JsonArray("powershell", "-NoProfile", "-Command", "[Console]::In.ReadToEnd().Length; 'done'")
            : new JsonArray("sh", "-c", "wc -c; echo done");
        var r = await s.HandleLineAsync(Call(1, "vault_run", new JsonObject
        {
            ["env"] = new JsonObject { ["X"] = "vault://kagami/mail-test" },
            ["command"] = cmd,
            ["reason"] = "stdin test",
        }));
        var text = ResultText(r);
        Assert.Contains("done", text);
        Assert.Matches(@"(^|\s)0(\s|$)", text.Split("--- stdout ---")[1]);
    }

    [Fact]
    public async Task RenderReturnsOnlyConfirmation()
    {
        var (v, s) = await NewAsync();
        using var _ = v;
        var template = Path.Combine(v.Dir, "t.template");
        var output = Path.Combine(v.Dir, "out", "t.conf");
        File.WriteAllText(template, "pass={{vault://kagami/mail-test}}");
        var r = await s.HandleLineAsync(Call(1, "vault_render", new JsonObject
        {
            ["template"] = template, ["output"] = output, ["reason"] = "render via MCP",
        }));
        var text = ResultText(r);
        Assert.False(IsError(r));
        Assert.Contains("Rendered 1 reference(s)", text);
        Assert.DoesNotContain(Secret, text);
        Assert.Equal("pass=" + Secret, File.ReadAllText(output));
    }

    [Fact]
    public async Task StreamLoopAnswersEachRequestOnItsOwnLine()
    {
        var (v, s) = await NewAsync();
        using var _ = v;
        var input = new StringReader(Req(1, "initialize") + "\n" +
            "{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}\n" + Req(2, "tools/list") + "\n");
        var output = new StringWriter();
        await s.RunAsync(input, output);
        var lines = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        Assert.Equal(1, JsonNode.Parse(lines[0])!["id"]!.GetValue<int>());
        Assert.Equal(2, JsonNode.Parse(lines[1])!["id"]!.GetValue<int>());
        Assert.DoesNotContain(Secret, output.ToString());
    }
}
