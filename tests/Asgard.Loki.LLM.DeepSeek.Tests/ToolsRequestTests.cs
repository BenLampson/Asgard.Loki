using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Asgard.Loki.LLM.DeepSeek.Tests;

/// <summary>官方原生输入限制和完整快照，模型参数、默认值、字符或地址均不由 SDK 猜测。</summary>
public sealed class ToolsRequestTests
{
    /// <summary>Unicode 字符按标量计数；代理对、组合字符保持原样，不按字节或 UTF-16 长度误限。</summary>
    [Theory] [InlineData("query", false)] [InlineData("query", true)] [InlineData("ori_question", false)] [InlineData("ori_question", true)]
    public async Task UnicodeScalarBoundaryIsNative(string field, bool above)
    {
        var value = string.Concat(Enumerable.Repeat("😀", 128 + (above ? 1 : 0))); var body = new JsonObject { ["query"] = "q", [field] = value }; using var factory = ToolsFixture.Factory(); factory.OnSend = async (request, token) =>
        { Assert.Equal(value, ToolsFixture.Element(await request.Content!.ReadAsStringAsync(token)).GetProperty(field).GetString()); return ToolsFixture.Json(ToolsFixture.Response()); };
        var client = new DeepSeekToolsClient(factory, ToolsFixture.Options()); if (above) await Assert.ThrowsAsync<ArgumentException>(() => client.SearchAsync(body, TestContext.Current.CancellationToken)); else await client.SearchAsync(body, TestContext.Current.CancellationToken); Assert.Equal(above ? 0 : 1, factory.Creates);
    }
    /// <summary>查询、计数、可空原问题、原生布尔值和字符串都有明确结构；不强制转换错误字段。</summary>
    [Theory] [InlineData("query-missing")] [InlineData("query-null")] [InlineData("query-number")] [InlineData("query-space")]
    [InlineData("count-zero")] [InlineData("count-high")] [InlineData("count-float")] [InlineData("count-null")]
    [InlineData("original-empty")] [InlineData("original-bool")] [InlineData("localization-null")] [InlineData("content-null")] [InlineData("content-string")]
    [InlineData("stream")] [InlineData("STREAM")]
    public async Task InvalidSearchCannotReachFactory(string kind)
    {
        var body = new JsonObject { ["query"] = "q" };
        switch (kind)
        {
            case "query-missing": body.Clear(); break;
            case "query-null": body["query"] = null; break;
            case "query-number": body["query"] = 1; break;
            case "query-space": body["query"] = " \t\n "; break;
            case "count-zero": body["count"] = 0; break;
            case "count-high": body["count"] = 51; break;
            case "count-float": body["count"] = JsonNode.Parse("1.5"); break;
            case "count-null": body["count"] = null; break;
            case "original-empty": body["ori_question"] = " "; break;
            case "original-bool": body["ori_question"] = false; break;
            case "localization-null": body["localization"] = null; break;
            case "content-null": body["need_content"] = null; break;
            case "content-string": body["need_content"] = "false"; break;
            default: body[kind] = false; break;
        }
        using var factory = ToolsFixture.Factory(); await Assert.ThrowsAsync<ArgumentException>(() => new DeepSeekToolsClient(factory, ToolsFixture.Options()).SearchAsync(body, TestContext.Current.CancellationToken)); Assert.Equal(0, factory.Creates); Assert.Equal(0, factory.Calls);
    }
    /// <summary>抓取 URL 只允许完整 HTTP(S) 页面地址，不能携带用户信息或由 URI 解析器移除原始控制字符。</summary>
    [Theory] [InlineData("relative")] [InlineData("ftp://page.invalid/a")] [InlineData("https://user:password@page.invalid/a")]
    [InlineData("https://user%3Apass@page.invalid/a")] [InlineData(" https://page.invalid/a")] [InlineData("https://page.invalid/a\nb")]
    public async Task InvalidFetchUrlCannotReachFactory(string value)
    {
        using var factory = ToolsFixture.Factory(true); await Assert.ThrowsAsync<ArgumentException>(() => new DeepSeekToolsClient(factory, ToolsFixture.Options()).FetchAsync(new() { ["url"] = value }, TestContext.Current.CancellationToken)); Assert.Equal(0, factory.Creates);
    }
    /// <summary>URL 字符数恰到上限允许且原文不重写，少量超限不创建 HTTP 资源。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task FetchUrlCharacterBoundary(bool above)
    {
        const string prefix = "https://page.invalid/"; var value = prefix + new string('a', 8192 - prefix.Length + (above ? 1 : 0)); using var factory = ToolsFixture.Factory(true); factory.OnSend = async (request, token) =>
        { Assert.Equal(value, ToolsFixture.Element(await request.Content!.ReadAsStringAsync(token)).GetProperty("url").GetString()); return ToolsFixture.Json(ToolsFixture.Response(true)); };
        var client = new DeepSeekToolsClient(factory, ToolsFixture.Options()); if (above) await Assert.ThrowsAsync<ArgumentException>(() => client.FetchAsync(new() { ["url"] = value }, TestContext.Current.CancellationToken)); else await client.FetchAsync(new() { ["url"] = value }, TestContext.Current.CancellationToken); Assert.Equal(above ? 0 : 1, factory.Creates);
    }
    /// <summary>请求上限按最终原生 JSON UTF-8 字节精确判断，超限在工厂之前失败。</summary>
    [Theory] [InlineData(false, false)] [InlineData(false, true)] [InlineData(true, false)] [InlineData(true, true)]
    public async Task ExactRequestByteBoundary(bool fetch, bool below)
    {
        var raw = fetch ? "{\"url\":\"http://p.invalid/\"}" : "{\"query\":\"q\"}"; var expected = Encoding.UTF8.GetBytes(raw); using var factory = ToolsFixture.Factory(fetch); factory.OnSend = async (request, token) => { Assert.Equal(expected, await request.Content!.ReadAsByteArrayAsync(token)); return ToolsFixture.Json(ToolsFixture.Response(fetch)); };
        var client = new DeepSeekToolsClient(factory, ToolsFixture.Options(request: expected.Length - (below ? 1 : 0))); var body = JsonNode.Parse(raw)!.AsObject();
        if (below) await Assert.ThrowsAsync<ArgumentException>(() => ToolsFixture.Invoke(client, fetch, TestContext.Current.CancellationToken, body)); else await ToolsFixture.Invoke(client, fetch, TestContext.Current.CancellationToken, body); Assert.Equal(below ? 0 : 1, factory.Creates);
    }
    /// <summary>未知字段的非法原始 Unicode 不能被序列化替代后发送。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task InvalidOriginalUnicodeCannotBeReplaced(bool key)
    {
        var body = ToolsFixture.Request(); if (key) body["\ud800"] = false; else body["future"] = "\ud800"; using var factory = ToolsFixture.Factory();
        await Assert.ThrowsAsync<EncoderFallbackException>(() => new DeepSeekToolsClient(factory, ToolsFixture.Options()).SearchAsync(body, TestContext.Current.CancellationToken)); Assert.Equal(0, factory.Creates);
    }
    /// <summary>已解析 JSON 的非法转义和重复未知字段也在传输前失败。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task InvalidParsedUnknownFieldsCannotSend(bool duplicate)
    {
        var body = JsonNode.Parse(duplicate ? "{\"query\":\"q\",\"future\":{\"same\":1,\"same\":2}}" : "{\"query\":\"q\",\"future\":\"\\ud800\"}")!.AsObject(); using var factory = ToolsFixture.Factory();
        var error = await Record.ExceptionAsync(() => new DeepSeekToolsClient(factory, ToolsFixture.Options()).SearchAsync(body, TestContext.Current.CancellationToken)); Assert.NotNull(error); if (duplicate) Assert.IsAssignableFrom<ArgumentException>(error); else Assert.IsType<InvalidDataException>(error); Assert.Equal(0, factory.Creates);
    }
    /// <summary>显式边界值和 null 保留，不填充、修正或删去合法字段。</summary>
    [Theory] [InlineData(1)] [InlineData(50)]
    public async Task NativeOptionalFieldsKeepOriginalPresence(int count)
    {
        using var factory = ToolsFixture.Factory(); var body = new JsonObject { ["query"] = " q ", ["count"] = count, ["ori_question"] = null, ["localization"] = "", ["need_content"] = false };
        factory.OnSend = async (request, token) => { Assert.True(JsonElement.DeepEquals(ToolsFixture.Element(body.ToJsonString()), ToolsFixture.Element(await request.Content!.ReadAsStringAsync(token)))); return ToolsFixture.Json(ToolsFixture.Response()); };
        await new DeepSeekToolsClient(factory, ToolsFixture.Options()).SearchAsync(body, TestContext.Current.CancellationToken); Assert.Equal(1, factory.Calls);
    }
}
