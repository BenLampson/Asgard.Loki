using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Asgard.Loki.Image.OpenAI.Tests;

/// <summary>从实际序列化的 MIME 字节读取完整 part 头部/原文，不用生产构造函数生成期望。</summary>
internal static class EditMultipartWire
{
    internal static async Task<JsonElement> Read(HttpContent content, CancellationToken token)
    {
        Assert.Equal("multipart/form-data", content.Headers.ContentType!.MediaType);
        var boundary = content.Headers.ContentType.Parameters.Single(p => p.Name == "boundary").Value!.Trim('"');
        var wire = Encoding.Latin1.GetString(await content.ReadAsByteArrayAsync(token));
        var pieces = wire.Split("--" + boundary, StringSplitOptions.None); Assert.Equal("", pieces[0]); Assert.Equal("--\r\n", pieces[^1]);
        var result = new JsonArray();
        foreach (var piece in pieces.Skip(1).SkipLast(1))
        {
            Assert.StartsWith("\r\n", piece); Assert.EndsWith("\r\n", piece);
            var raw = piece[2..^2]; var separator = raw.IndexOf("\r\n\r\n", StringComparison.Ordinal); Assert.True(separator >= 0);
            var headers = raw[..separator].Split("\r\n", StringSplitOptions.None);
            var disposition = ContentDispositionHeaderValue.Parse(headers.Single(h => h.StartsWith("Content-Disposition: ", StringComparison.Ordinal))[21..]);
            var bytes = Encoding.Latin1.GetBytes(raw[(separator + 4)..]);
            var item = new JsonObject { ["headers"] = new JsonArray(headers.Select(h => (JsonNode?)JsonValue.Create(h)).ToArray()) };
            if (disposition.Name?.Trim('"') is "image[]" or "mask") item["base64"] = Convert.ToBase64String(bytes);
            else item["text"] = new UTF8Encoding(false, true).GetString(bytes);
            result.Add(item);
        }
        return JsonSerializer.SerializeToElement(result);
    }
}
