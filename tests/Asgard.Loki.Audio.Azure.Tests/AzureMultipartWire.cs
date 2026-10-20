using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Asgard.Loki.Audio.Azure.Tests;

/// <summary>独立读取实际 MIME 字节，仅标准化随机 boundary，不使用生产编码器生成期望。</summary>
internal static class AzureMultipartWire
{
    internal static async Task<JsonElement> Read(HttpContent content, CancellationToken token)
    {
        Assert.Equal("multipart/form-data", content.Headers.ContentType!.MediaType);
        var boundary = content.Headers.ContentType.Parameters.Single(x => x.Name == "boundary").Value!.Trim('"');
        var bytes = await content.ReadAsByteArrayAsync(token); var raw = Encoding.Latin1.GetString(bytes);
        var pieces = raw.Split("--" + boundary, StringSplitOptions.None); Assert.Equal("", pieces[0]); Assert.Equal("--\r\n", pieces[^1]);
        var result = new JsonArray();
        foreach (var piece in pieces.Skip(1).SkipLast(1))
        {
            Assert.StartsWith("\r\n", piece); Assert.EndsWith("\r\n", piece);
            var part = piece[2..^2]; var split = part.IndexOf("\r\n\r\n", StringComparison.Ordinal); Assert.True(split >= 0);
            var headers = part[..split].Split("\r\n", StringSplitOptions.None);
            var disposition = ContentDispositionHeaderValue.Parse(headers.Single(x => x.StartsWith("Content-Disposition: ", StringComparison.Ordinal))[21..]);
            var body = Encoding.Latin1.GetBytes(part[(split + 4)..]); var item = new JsonObject { ["headers"] = new JsonArray(headers.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray()) };
            if (disposition.Name?.Trim('"') == "file") item["base64"] = Convert.ToBase64String(body);
            else item["text"] = new UTF8Encoding(false, true).GetString(body);
            result.Add(item);
        }
        return JsonSerializer.SerializeToElement(result);
    }
}
