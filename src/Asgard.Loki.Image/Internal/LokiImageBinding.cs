using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Asgard.Loki.Image.Internal;

/// <summary>任务绑定完整目标和处理实现；不在引用中保存明文凭据。</summary>
internal static class LokiImageBinding
{
    internal static string Fingerprint(string model, LokiImageUpstreamProvider provider)
        => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new[]
        {
            model, provider.Id, provider.BaseAddress.AbsoluteUri, provider.UpstreamModel,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(provider.ApiKey))),
            provider.HttpClientName, provider.Handler!.GetType().AssemblyQualifiedName!,
            provider.Handler.Name, provider.Handler.BindingIdentity, Canonical(provider.Settings)
        })));

    private static string Canonical(JsonNode? node) => node switch
    {
        JsonObject obj => "{" + string.Join(",", obj.OrderBy(field => field.Key, StringComparer.Ordinal)
            .Select(field => JsonSerializer.Serialize(field.Key) + ":" + Canonical(field.Value))) + "}",
        JsonArray array => "[" + string.Join(",", array.Select(Canonical)) + "]",
        null => "null",
        _ => node.ToJsonString()
    };
}
