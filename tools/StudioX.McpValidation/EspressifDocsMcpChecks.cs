using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using StudioX.Application;
using StudioX.Application.Mcp;
using StudioX.Engine;
using StudioX.Foundation;

/// <summary>验证内置与外部 MCP 的公开乐鑫契约，未登录夹具不连接网络、打开浏览器或读取用户授权。</summary>
internal static class EspressifDocsMcpChecks
{
    private static readonly string[] KnowledgeTools =
    [
        "espressif_docs_status", "espressif_docs_search",
        "espressif_components_search", "espressif_component_info"
    ];

    public static async Task RunAsync(string outputDirectory, string? cliExecutable = null)
    {
        outputDirectory = Path.GetFullPath(outputDirectory);
        if (Directory.Exists(outputDirectory) && Directory.EnumerateFileSystemEntries(outputDirectory).Any())
        {
            throw new ArgumentException("验证输出必须是空目录，避免覆盖已有记录。", nameof(outputDirectory));
        }
        cliExecutable = Path.GetFullPath(cliExecutable ?? "src/StudioX.Cli/bin/Debug/net10.0/StudioX.Cli.exe");
        if (!File.Exists(cliExecutable))
        {
            throw new FileNotFoundException("先编译 StudioX.Cli，再运行外部 MCP 验证。", cliExecutable);
        }

        Directory.CreateDirectory(outputDirectory);
        var temporary = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        var root = Path.GetFullPath(Path.Combine(temporary, "studiox-espressif-docs-mcp-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        var checks = new List<string>();
        var observations = new List<object>();
        void Check(bool condition, string description)
        {
            if (!condition)
            {
                throw new InvalidOperationException(description);
            }
            checks.Add(description);
            Console.WriteLine("PASS " + description);
        }

        try
        {
            var project = Path.Combine(root, "project");
            var runtime = Path.Combine(root, "runtime");
            var data = Path.Combine(root, "data");
            Directory.CreateDirectory(runtime);
            await JsonStore.WriteAsync(Path.Combine(project, ".studiox/project.json"),
                new ProjectManifest(1, "espressif-docs-contract", "test.pack", "1.0.0", "test", "test-device",
                    "test-template", "test-tools", "1.0.0", "gcc"));
            await using var services = new WorkbenchService(runtime, data);
            await using var session = await StudioXMcpSession.CreateAsync(
                new StudioXMcpTools(services, project, new DenyStudioXMcpAuthorizer()));
            var tools = await session.ListToolsAsync();
            Check(KnowledgeTools.All(name => tools.Count(tool => tool.Name == name) == 1),
                "built-in MCP handshake advertises all four official knowledge tools exactly once");
            VerifySchemas(tools, Check);

            var statusText = await session.CallToolAsync("espressif_docs_status", "{}");
            var status = ReadSafeStatus(statusText, Check, "built-in");
            observations.Add(new
            {
                entry = "built-in",
                status
            });
            var searchText = await session.CallToolAsync("espressif_docs_search", "{\"query\":\"ESP32-S3 GPIO\"}");
            using var searchResult = JsonDocument.Parse(searchText);
            Check(searchResult.RootElement.GetProperty("status").GetString() == "authentication_required",
                "unauthenticated built-in documentation search gives actionable authentication_required");
            Check(!Directory.EnumerateFileSystemEntries(project, "*", SearchOption.AllDirectories)
                    .Any(path => Path.GetFileName(path) != ".studiox" && Path.GetFileName(path) != "project.json"),
                "read-only documentation calls do not change project files");
            Check(!Directory.Exists(Path.Combine(data, "secure", "espressif-docs")) ||
                    !Directory.EnumerateFiles(Path.Combine(data, "secure", "espressif-docs")).Any(),
                "unauthenticated calls create no credentials, account data or documentation cache");

            using var canceled = new CancellationTokenSource();
            canceled.Cancel();
            var cancellationObserved = false;
            try
            {
                await session.CallToolAsync("espressif_docs_search", "{\"query\":\"ESP32-S3 ADC\"}", canceled.Token);
            }
            catch (OperationCanceledException)
            {
                cancellationObserved = true;
            }
            Check(cancellationObserved, "canceling a knowledge call remains cancellation rather than a tool error");

            var transport = new KnowledgeRoundTransport();
            var agent = new AiAgentService(transport, new AiSettings(), session);
            var reply = await agent.SendAsync(project, "核对 ESP32-S3 GPIO 的官方资料");
            Check(reply.Text == KnowledgeRoundTransport.Answer && transport.Requests.Count == 3,
                "built-in Agent invokes documentation status and search through its actual MCP session");
            Check(transport.Requests[1].Messages.Any(message => message.Role == "tool" &&
                    message.Content?.Contains("authenticationSaved", StringComparison.Ordinal) == true) &&
                    transport.Requests[2].Messages.Any(message => message.Role == "tool" &&
                    message.Content?.Contains("authentication_required", StringComparison.Ordinal) == true),
                "Agent receives safe connection state and authentication recovery as tool data");
            Check(transport.Requests.Select(request => request.Messages.First().Content)
                    .Distinct(StringComparer.Ordinal).Count() == 1,
                "knowledge routing keeps the system prefix stable within the Agent turn");

            await RunExternalAsync(cliExecutable, project, runtime, Path.Combine(root, "external-data"),
                Check, observations);
            await File.WriteAllTextAsync(Path.Combine(outputDirectory, "result.json"),
                JsonSerializer.Serialize(new
                {
                    passed = true,
                    checkCount = checks.Count,
                    evidence = "actual built-in Agent/MCP and external stdio MCP handshakes; isolated unauthenticated data; no network or hardware",
                    checks,
                    observations
                }, JsonStore.Options));
            Console.WriteLine($"PASS {checks.Count} Espressif knowledge MCP checks.");
        }
        finally
        {
            // 回收前只接受本次随机临时根；验证记录保留在用户指定输出目录。
            if (string.Equals(Path.GetDirectoryName(root), temporary, StringComparison.OrdinalIgnoreCase) &&
                Path.GetFileName(root).StartsWith("studiox-espressif-docs-mcp-", StringComparison.Ordinal) &&
                Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static void VerifySchemas(IReadOnlyList<AiToolDefinition> tools, Action<bool, string> check)
    {
        foreach (var name in KnowledgeTools)
        {
            using var schema = JsonDocument.Parse(tools.Single(tool => tool.Name == name).ParametersJson);
            var properties = schema.RootElement.GetProperty("properties");
            check(!properties.TryGetProperty("cancellationToken", out _), name + " hides cancellation from model parameters");
            var required = schema.RootElement.TryGetProperty("required", out var requiredElement)
                ? requiredElement.EnumerateArray().Select(item => item.GetString()).ToArray() : [];
            var expected = name switch
            {
                "espressif_docs_status" => Array.Empty<string>(),
                "espressif_component_info" => ["namespaceName", "componentName"],
                _ => ["query"]
            };
            check(required.Order(StringComparer.Ordinal).SequenceEqual(expected.Order(StringComparer.Ordinal)),
                name + " exposes only its public required arguments");
            if (name == "espressif_docs_search")
            {
                check(properties.GetProperty("language").GetProperty("default").GetString() == "zh",
                    "documentation search defaults to Chinese without inventing a version argument");
                check(!properties.TryGetProperty("version", out _) && !properties.TryGetProperty("target", out _),
                    "latest-only documentation service does not claim selectable SDK or chip coverage");
            }
        }
    }

    private static JsonElement ReadSafeStatus(string text, Action<bool, string> check, string entry)
    {
        using var document = JsonDocument.Parse(text);
        var status = document.RootElement;
        check(status.GetProperty("authenticationSaved").ValueKind == JsonValueKind.False,
            entry + " reports no saved authorization in its isolated data directory");
        check(status.GetProperty("endpoint").GetString() == "https://mcp.espressif.com/docs" &&
                status.GetProperty("status").GetString() == "authentication_required",
            entry + " identifies the official endpoint and explicit state");
        check(status.EnumerateObject().Select(property => property.Name).All(name =>
                name is "status" or "authenticationSaved" or "operation" or "message" or "endpoint" or "lastErrorCode"),
            entry + " status returns no account, bearer, authorization URL or credential fields");
        return status.Clone();
    }

    private static async Task RunExternalAsync(string executable, string project, string runtime, string data,
        Action<bool, string> check, ICollection<object> observations)
    {
        var environment = StdioClientTransportOptions.GetDefaultEnvironmentVariables();
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Command = executable,
            Arguments = ["mcp", project, runtime, data],
            Name = "StudioX isolated Espressif knowledge validation",
            InheritEnvironmentVariables = false,
            EnvironmentVariables = environment
        });
        await using var client = await McpClient.CreateAsync(transport);
        var tools = await client.ListToolsAsync();
        check(KnowledgeTools.All(name => tools.Count(tool => tool.ProtocolTool.Name == name) == 1),
            "external CLI stdio handshake exposes the same four knowledge tools");
        var result = await client.CallToolAsync("espressif_docs_status", new Dictionary<string, object?>());
        check(result.IsError != true && !result.Content.OfType<ImageContentBlock>().Any(),
            "external status is a successful read-only text response");
        var status = ReadSafeStatus(string.Join("\n", result.Content.OfType<TextContentBlock>().Select(block => block.Text)),
            check, "external");
        observations.Add(new
        {
            entry = "external",
            status
        });
        var search = await client.CallToolAsync("espressif_docs_search",
            new Dictionary<string, object?> { ["query"] = "ESP32-C6 GPIO" });
        using var searchResult = JsonDocument.Parse(string.Join("\n",
            search.Content.OfType<TextContentBlock>().Select(block => block.Text)));
        check(search.IsError != true && searchResult.RootElement.GetProperty("status").GetString() == "authentication_required",
            "external unauthenticated search reports recovery without browser login");
    }

    private sealed class KnowledgeRoundTransport : IAiAgentTransport
    {
        public const string Answer = "请在 AI 接口设置中连接乐鑫官方文档授权。";
        public List<AiChatRequest> Requests { get; } = [];

        public Task<AiChatResponse> CompleteAsync(AiSettings settings, AiChatRequest request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Requests.Add(request);
            var calls = Requests.Count switch
            {
                1 => new[] { new AiToolCall("docs-status", "espressif_docs_status", "{}") },
                2 => new[] { new AiToolCall("docs-search", "espressif_docs_search", "{\"query\":\"ESP32-S3 GPIO\"}") },
                _ => []
            };
            return Task.FromResult(new AiChatResponse(calls.Length == 0 ? Answer : null, calls,
                calls.Length == 0 ? "stop" : "tool_calls", null));
        }
    }
}
