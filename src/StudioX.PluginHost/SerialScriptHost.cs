namespace StudioX.PluginHost;

using System.Text.Json;
using Jint;
using StudioX.Extensions.Abstractions;

internal static class SerialScriptHost
{
    public static async Task<int> RunAsync()
    {
        Engine? engine = null; string? source = null;
        while (await SerialScriptContract.ReadLineAsync(Console.In) is { } line)
        {
            SerialScriptRequest? request = null;
            try
            {
                request = JsonSerializer.Deserialize<SerialScriptRequest>(line, SerialScriptContract.Json);
                if (request is null || request.ApiVersion != 1) throw new InvalidDataException("脚本 API 版本不匹配。");
                if (request.Operation == "init")
                {
                    source = request.Source;
                    if (string.IsNullOrWhiteSpace(source) || source.Length > SerialScriptContract.MaxSource) throw new InvalidDataException("脚本应为 1–65536 字符。");
                }
                if (request.Operation is "init" or "reset")
                {
                    if (source is null) throw new InvalidOperationException("脚本尚未初始化。");
                    engine?.Dispose();
                    // 不开放 CLR、文件、网络、进程或串口对象。另有父进程超时和进程内存监测。
                    engine = new Engine(o => o.LimitMemory(16 * 1024 * 1024).TimeoutInterval(TimeSpan.FromMilliseconds(250))
                        .MaxStatements(200000).LimitRecursion(64));
                    engine.Execute(source);
                    if (!engine.Evaluate("typeof decode === 'function'").AsBoolean()) throw new InvalidDataException("请声明 function decode(bytes, context)。");
                    await Reply(new(1, request.Id, new(0, []))); continue;
                }
                if (request.Operation != "decode" || engine is null || request.Bytes is null || request.Bytes.Length > SerialScriptContract.MaxInput || request.Bytes.Any(b => b is < 0 or > 255))
                    throw new InvalidDataException("脚本解码请求无效。");
                var context = JsonSerializer.Serialize(new { apiVersion = 1, direction = request.Direction, timestamp = request.Timestamp, flush = request.Flush, clock = "host-utc" });
                // 使用纯 JSON 构造 JS 数组，不把 CLR 对象或反射能力交给用户脚本。
                var json = engine.Evaluate("JSON.stringify(decode(" + JsonSerializer.Serialize(request.Bytes) + "," + context + "))").AsString();
                if (json.Length > SerialScriptContract.MaxLine / 2) throw new InvalidDataException("脚本输出过长。");
                var result = JsonSerializer.Deserialize<SerialScriptResult>(json, SerialScriptContract.Json);
                SerialScriptContract.Validate(result, request.Bytes.Length);
                await Reply(new(1, request.Id, result));
            }
            catch (Exception ex)
            {
                var error = ex.ToString(); if (error.Length > 8000) error = error[..8000];
                await Reply(new(1, request?.Id ?? 0, Error: error));
                engine?.Dispose(); return 1; // 异常后不复用可能已损坏的用户状态。
            }
        }
        engine?.Dispose(); return 0;
    }
    private static Task Reply(SerialScriptResponse response) => Console.Out.WriteLineAsync(JsonSerializer.Serialize(response, SerialScriptContract.Json));
}
