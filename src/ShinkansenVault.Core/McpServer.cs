using System.Text.Json;
using System.Text.Json.Nodes;

namespace ShinkansenVault;

/// <summary>
/// Minimal MCP server over stdio (JSON-RPC 2.0, one message per line).
/// Tools: vault_list, vault_add, vault_run, vault_render, vault_log.
/// No tool returns a secret value. vault_add only opens the input dialog: the human types the value,
/// the agent gets back the reference. Every use still shows the consent dialog and asks for Windows Hello.
/// </summary>
public sealed class McpServer
{
    public const string ProtocolVersion = "2025-06-18";
    private static readonly string[] KnownVersions = { "2025-06-18", "2025-03-26", "2024-11-05" };
    private const int MaxOutputChars = 100_000;

    private readonly VaultService _service;
    private readonly AuditLog _audit;
    private readonly ISecretInput _input;
    private readonly string _version;

    public McpServer(VaultService service, AuditLog audit, ISecretInput input, string version)
    {
        _service = service;
        _audit = audit;
        _input = input;
        _version = version;
    }

    /// <summary>Reads requests until the input closes. Requests are handled one at a time.</summary>
    public async Task RunAsync(TextReader input, TextWriter output, CancellationToken ct = default)
    {
        string? line;
        while (!ct.IsCancellationRequested && (line = await input.ReadLineAsync(ct)) != null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var response = await HandleLineAsync(line);
            if (response != null)
            {
                await output.WriteLineAsync(response.ToJsonString());
                await output.FlushAsync(ct);
            }
        }
    }

    public async Task<JsonObject?> HandleLineAsync(string line)
    {
        JsonObject request;
        try
        {
            request = JsonNode.Parse(line) as JsonObject ?? throw new JsonException("not an object");
        }
        catch (JsonException)
        {
            return Error(null, -32700, "Parse error");
        }

        var id = request["id"]?.DeepClone();
        var method = request["method"]?.GetValue<string>();
        var parameters = request["params"] as JsonObject;
        bool isNotification = id == null;

        if (method == null) return isNotification ? null : Error(id, -32600, "Invalid request");

        try
        {
            JsonNode? result = method switch
            {
                "initialize" => Initialize(parameters),
                "ping" => new JsonObject(),
                "tools/list" => new JsonObject { ["tools"] = ToolDefinitions() },
                "tools/call" => await CallToolAsync(parameters),
                _ when method.StartsWith("notifications/") => null,
                _ => throw new MethodNotFound(method),
            };
            if (isNotification) return null;
            return new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result ?? new JsonObject() };
        }
        catch (MethodNotFound)
        {
            return isNotification ? null : Error(id, -32601, $"Method not found: {method}");
        }
        catch (ArgumentException ex)
        {
            return isNotification ? null : Error(id, -32602, ex.Message);
        }
    }

    private JsonObject Initialize(JsonObject? p)
    {
        var requested = p?["protocolVersion"]?.GetValue<string>();
        return new JsonObject
        {
            ["protocolVersion"] = requested != null && KnownVersions.Contains(requested) ? requested : ProtocolVersion,
            ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
            ["serverInfo"] = new JsonObject { ["name"] = "shinkansen-vault", ["version"] = _version },
            ["instructions"] =
                "Never ask the human to paste a password, token or key into the chat. If you need one that is not in " +
                "vault_list, call vault_add: the human types it into a dialog and you get back only its vault:// reference. " +
                "Secrets are referenced as vault://name and are never returned. Use vault_run to inject them into a " +
                "command's environment (output is masked) or vault_render to write them into a file you must not read. " +
                "Every use shows the human a consent dialog and asks for their Windows Hello PIN. Always give an honest, " +
                "specific reason. If denied, stop and ask the human.",
        };
    }

    private async Task<JsonObject> CallToolAsync(JsonObject? p)
    {
        var name = p?["name"]?.GetValue<string>() ?? throw new ArgumentException("Missing tool name.");
        var args = p?["arguments"] as JsonObject ?? new JsonObject();
        try
        {
            return name switch
            {
                "vault_list" => Text(ListText()),
                "vault_add" => await AddToolAsync(args),
                "vault_run" => await RunToolAsync(args),
                "vault_render" => await RenderToolAsync(args),
                "vault_log" => Text(string.Join("\n", _audit.ReadLines(OptionalInt(args, "tail") ?? 20))),
                _ => throw new ArgumentException($"Unknown tool: {name}"),
            };
        }
        catch (VaultException ex)
        {
            var prefix = ex.ExitCode == ExitCodes.Denied ? "DENIED: " : "ERROR: ";
            return Text(prefix + ex.Message, isError: true);
        }
        catch (JsonException ex)
        {
            return Text("ERROR: bad arguments: " + ex.Message, isError: true);
        }
        catch (InvalidOperationException ex)
        {
            return Text("ERROR: bad arguments: " + ex.Message, isError: true);
        }
    }

    private string ListText()
    {
        var items = _service.List();
        if (items.Count == 0) return "The vault is empty. Ask the human to run: svault add vault://project/name";
        return string.Join("\n", items.Select(i => i.Label is null ? i.Ref : $"{i.Ref}\t{i.Label}"));
    }

    private async Task<JsonObject> AddToolAsync(JsonObject args)
    {
        var reference = VaultRef.Parse(args["ref"]?.GetValue<string>() ?? throw new VaultException("'ref' is required.", ExitCodes.Usage));
        var label = args["label"]?.GetValue<string>();
        var reason = args["reason"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(reason))
            throw new VaultException("'reason' is required and is shown to the human.", ExitCodes.Usage);
        var replace = args["replace"] is JsonValue r && r.TryGetValue<bool>(out var b) && b;
        await _service.AddAsync(reference, label, replace, _input, reason);
        return Text($"Stored {reference}. Use this reference; you will never see the value.");
    }

    private async Task<JsonObject> RunToolAsync(JsonObject args)
    {
        var envObj = args["env"] as JsonObject ?? throw new VaultException("'env' must be an object like {\"NAME\": \"vault://ref\"}.", ExitCodes.Usage);
        var bindings = envObj.Select(kv => CommandLine.ParseEnv($"{kv.Key}={kv.Value?.GetValue<string>()}")).ToList();
        var command = (args["command"] as JsonArray ?? throw new VaultException("'command' must be an array of strings.", ExitCodes.Usage))
            .Select(n => n?.GetValue<string>() ?? "").ToList();
        var reason = args["reason"]?.GetValue<string>() ?? "";

        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exit = await _service.RunAsync(bindings, reason, command, stdout, stderr, closeStdin: true);
        var text = $"exit code: {exit}\n--- stdout ---\n{Truncate(stdout.ToString())}\n--- stderr ---\n{Truncate(stderr.ToString())}";
        return Text(text, isError: exit != 0);
    }

    private async Task<JsonObject> RenderToolAsync(JsonObject args)
    {
        var template = args["template"]?.GetValue<string>() ?? throw new VaultException("'template' is required.", ExitCodes.Usage);
        var output = args["output"]?.GetValue<string>() ?? throw new VaultException("'output' is required.", ExitCodes.Usage);
        var reason = args["reason"]?.GetValue<string>() ?? "";
        var info = new StringWriter();
        await _service.RenderAsync(template, output, reason, info);
        return Text(info.ToString().Trim() + "\nDo not read the output file.");
    }

    private static string Truncate(string s) =>
        s.Length <= MaxOutputChars ? s : s[..MaxOutputChars] + $"\n[... truncated {s.Length - MaxOutputChars} chars]";

    private static int? OptionalInt(JsonObject args, string key) =>
        args[key] is JsonValue v && v.TryGetValue<int>(out var n) && n > 0 ? n : null;

    private static JsonObject Text(string text, bool isError = false) => new()
    {
        ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
        ["isError"] = isError,
    };

    private static JsonObject Error(JsonNode? id, int code, string message) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id,
        ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
    };

    private static JsonArray ToolDefinitions() => new()
    {
        Tool("vault_list",
            "List secret references (vault://...) and their labels. Never returns values.",
            new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() }),
        Tool("vault_add",
            "Ask the human to store a secret you need (password, token, API key). Opens a dialog on their screen " +
            "where they type the value; you get back only the vault:// reference. Use this instead of asking for " +
            "secrets in chat. Then Windows Hello confirms.",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["ref"] = new JsonObject { ["type"] = "string", ["description"] = "Reference to create, e.g. vault://kagami/mail-claude" },
                    ["label"] = new JsonObject { ["type"] = "string", ["description"] = "Short human-readable label (no secrets)." },
                    ["reason"] = new JsonObject { ["type"] = "string", ["description"] = "What you need it for; shown in the dialog." },
                    ["replace"] = new JsonObject { ["type"] = "boolean", ["description"] = "Replace an existing value (the dialog says so)." },
                },
                ["required"] = new JsonArray("ref", "reason"),
            }),
        Tool("vault_run",
            "Run a command with secrets injected as environment variables. The human sees a consent dialog with the " +
            "reason, references and exact command, then confirms with Windows Hello. Secret values in the output are " +
            "masked. Do not write the values anywhere you can read.",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["env"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["description"] = "Map of environment variable name to reference, e.g. {\"MAIL_PASS\": \"vault://kagami/mail-claude\"}",
                        ["additionalProperties"] = new JsonObject { ["type"] = "string" },
                    },
                    ["command"] = new JsonObject
                    {
                        ["type"] = "array",
                        ["items"] = new JsonObject { ["type"] = "string" },
                        ["description"] = "Program and arguments, e.g. [\"python\", \"check_mail.py\"]. Not run through a shell.",
                    },
                    ["reason"] = new JsonObject { ["type"] = "string", ["description"] = "Honest, specific reason shown to the human." },
                },
                ["required"] = new JsonArray("env", "command", "reason"),
            }),
        Tool("vault_render",
            "Replace {{vault://ref}} placeholders in a template file and write the result to an output file. " +
            "Returns only a confirmation. Never read the output file afterwards.",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["template"] = new JsonObject { ["type"] = "string", ["description"] = "Path to the template file." },
                    ["output"] = new JsonObject { ["type"] = "string", ["description"] = "Path of the file to write." },
                    ["reason"] = new JsonObject { ["type"] = "string", ["description"] = "Honest, specific reason shown to the human." },
                },
                ["required"] = new JsonArray("template", "output", "reason"),
            }),
        Tool("vault_log",
            "Show the last audit log entries (time, refs, target, reason, approved/denied). Never contains values.",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["tail"] = new JsonObject { ["type"] = "integer", ["description"] = "Number of entries (default 20)." },
                },
            }),
    };

    private static JsonObject Tool(string name, string description, JsonObject schema) => new()
    {
        ["name"] = name,
        ["description"] = description,
        ["inputSchema"] = schema,
    };

    private sealed class MethodNotFound : Exception
    {
        public MethodNotFound(string method) : base(method) { }
    }
}
