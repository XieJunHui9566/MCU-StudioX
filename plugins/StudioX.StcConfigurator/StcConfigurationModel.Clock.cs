namespace StudioX.StcConfigurator;

using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

public sealed partial class StcConfigurationModel
{
    /// <summary>只在显式保存时接管；启动与读取不修改工程。凭据和原配置在同一次文件保存中发布。</summary>
    public string GenerationInputHash()
    {
        PrepareClockValidation();
        return Configuration["profileData"]!["projectClock"]!["inputSha256"]!.GetValue<string>();
    }

    public void PrepareClockValidation()
    {
        var policy = Configuration["profileData"]!["projectClock"]!.AsObject();
        policy["managed"] = true;
        policy["valid"] = Errors().Count == 0 && SourceClockHz is not null;
        var projection = (JsonObject)Document.DeepClone();
        var projectedPolicy = projection["configuration"]!["profileData"]!["projectClock"]!.AsObject();
        projectedPolicy.Remove("inputSha256");
        projectedPolicy.Remove("valid");
        projection["generation"] = null;
        // 此准确 profile 的所有数值均为安全整数；按 UTF-16 属性序与 UTF-8 字符串编码生成 JCS 投影。
        var options = new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        string Canonical(JsonNode? node) => node switch
        {
            JsonObject value => "{" + string.Join(",", value.OrderBy(item => item.Key, StringComparer.Ordinal).Select(item => JsonSerializer.Serialize(item.Key, options) + ":" + Canonical(item.Value))) + "}",
            JsonArray array => "[" + string.Join(",", array.Select(Canonical)) + "]",
            null => "null",
            _ => node.ToJsonString(options)
        };
        policy["inputSha256"] = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Canonical(projection))));
    }
}
