using System.Text.Json.Nodes;
using Asgard.Loki.Testing;
using Xunit;

namespace Asgard.Loki.Rerank.SiliconFlow.Tests;

/// <summary>非法原生请求在创建 HTTP 客户端前失败，不通过模型后缀、空值或默认值修补。</summary>
public sealed class SiliconFlowRerankRequestTests
{
    /// <summary>已核对字段的类型、正整数和有界参数逐项验证；未来字段仍按原生扩展发送。</summary>
    public static TheoryData<string, string> InvalidFields => new()
    {
        { "query", "null" }, { "query", "\"\"" }, { "query", "123" }, { "query", "[]" }, { "query", "{}" },
        { "query", "{\"text\":\"q\"}" }, { "query", "{\"image\":\"url\",\"text\":\"q\"}" }, { "query", "{\"image\":null}" }, { "query", "{\"video\":\"url\"}" },
        { "documents", "null" }, { "documents", "[]" }, { "documents", "{}" }, { "documents", "[null]" }, { "documents", "[123]" },
        { "documents", "[{\"text\":null}]" }, { "documents", "[{\"image\":\"\"}]" }, { "documents", "[{\"image\":\"url\",\"text\":\"q\"}]" },
        { "documents", "[{\"video\":\"url\"}]" }, { "top_n", "0" }, { "top_n", "-1" }, { "top_n", "null" }, { "top_n", "1.00000000000000001" },
        { "top_n", "\"2\"" }, { "max_chunks_per_doc", "0" }, { "max_chunks_per_doc", "false" }, { "max_chunks_per_doc", "null" },
        { "overlap_tokens", "-1" }, { "overlap_tokens", "81" }, { "overlap_tokens", "80.000000000000001" }, { "overlap_tokens", "null" },
        { "return_documents", "null" }, { "return_documents", "0" }, { "instruction", "null" }, { "instruction", "\"\"" }, { "instruction", "false" }, { "model", "\"override\"" }
    };
    /// <summary>完整 SDK 入口与直接适配器入口共用供应商校验，不发任何网络请求。</summary>
    [Theory] [MemberData(nameof(InvalidFields))]
    public async Task InvalidKnownField_FailsBeforeHttp(string field, string json)
    {
        var parameters = SiliconFlowRerankFixtures.Read("array", "parameters"); parameters[field] = JsonNode.Parse(json);
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException());
        await Assert.ThrowsAnyAsync<ArgumentException>(() => SiliconFlowRerankFixtures.Adapter(handler).RerankAsync(SiliconFlowRerankFixtures.Request(parameters), TestContext.Current.CancellationToken));
        Assert.Equal(0, handler.Calls);
    }
    /// <summary>必需 query/documents 不从其他参数、模型或默认值补出。</summary>
    [Theory] [InlineData("query")] [InlineData("documents")]
    public async Task RequiredField_IsNotInvented(string field)
    {
        var parameters = SiliconFlowRerankFixtures.Read("array", "parameters"); parameters.Remove(field);
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException());
        await using var client = SiliconFlowRerankFixtures.Build(SiliconFlowRerankFixtures.Adapter(handler));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => client.Rerank.RerankAsync(SiliconFlowRerankFixtures.Request(parameters), TestContext.Current.CancellationToken)); Assert.Equal(0, handler.Calls);
    }
}
