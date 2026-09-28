namespace StudioX.Packages;

using System.Text.Json;
using System.Text.Json.Serialization;
using StudioX.Foundation;

/// <summary>独立清单拒绝未知与重复字段，避免误读另一种同扩展名的包。</summary>
internal static class ZephyrPackJson
{
    private static readonly JsonSerializerOptions Options = new(JsonStore.Options)
    {
        PropertyNameCaseInsensitive = false,
        NumberHandling = JsonNumberHandling.Strict,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    internal static async Task<T> ReadAsync<T>(Stream stream, CancellationToken token)
    {
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: token);
        RejectDuplicateProperties(document.RootElement);
        return document.RootElement.Deserialize<T>(Options) ??
            throw new StudioXException("ZEPHYR_PACK_JSON", "Zephyr 包的 JSON 文件为空。");
    }

    private static void RejectDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw new StudioXException("ZEPHYR_PACK_JSON", "Zephyr 包 JSON 包含重复字段：" + property.Name);
                }
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                RejectDuplicateProperties(item);
            }
        }
    }
}
