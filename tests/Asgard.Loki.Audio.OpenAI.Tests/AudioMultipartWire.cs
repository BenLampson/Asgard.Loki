using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Asgard.Loki.Audio.OpenAI.Tests;

/// <summary>独立读取实际序列化的 MIME 字节样本；只规范化随机 boundary，不使用生产构造逻辑生成期望。</summary>
internal static class AudioMultipartWire
{
    internal static async Task<JsonElement> Read(HttpContent content, CancellationToken token)
    {
        Assert.Equal("multipart/form-data", content.Headers.ContentType!.MediaType);
        var boundary = content.Headers.ContentType.Parameters.Single(p => p.Name == "boundary").Value!.Trim('"');
        var bytes = await content.ReadAsByteArrayAsync(token); var wire = Encoding.Latin1.GetString(bytes);
        Assert.StartsWith("--" + boundary + "\r\n", wire); Assert.EndsWith("--" + boundary + "--\r\n", wire);
        var pieces = wire.Split("--" + boundary, StringSplitOptions.None); Assert.Equal("", pieces[0]); Assert.Equal("--\r\n", pieces[^1]);
        var result = new JsonArray();
        foreach (var piece in pieces.Skip(1).SkipLast(1))
        {
            Assert.StartsWith("\r\n", piece); Assert.EndsWith("\r\n", piece);
            var raw = piece[2..^2]; var separator = raw.IndexOf("\r\n\r\n", StringComparison.Ordinal); Assert.True(separator >= 0);
            var headers = raw[..separator].Split("\r\n", StringSplitOptions.None);
            var disposition = ContentDispositionHeaderValue.Parse(headers.Single(h => h.StartsWith("Content-Disposition: ", StringComparison.Ordinal))[21..]);
            var body = Encoding.Latin1.GetBytes(raw[(separator + 4)..]); var item = new JsonObject
            { ["headers"] = new JsonArray(headers.Select(h => (JsonNode?)JsonValue.Create(h)).ToArray()) };
            if (disposition.Name?.Trim('"') == "file") item["base64"] = Convert.ToBase64String(body);
            else item["text"] = new UTF8Encoding(false, true).GetString(body);
            result.Add(item);
        }
        return JsonSerializer.SerializeToElement(result);
    }
}
