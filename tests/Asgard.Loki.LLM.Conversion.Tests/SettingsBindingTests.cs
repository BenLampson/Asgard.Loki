using System.Net;
using System.Text;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Asgard.Loki.LLM.Tests;
using Asgard.Loki.Testing;
using Xunit;

namespace Asgard.Loki.LLM.Conversion.Tests;

/// <summary>所有显式配置都属于 Session 绑定身份，不能沿用旧配置的会话。</summary>
public sealed class SettingsBindingTests
{
    /// <summary>新增名称、预算和替代强度配置变化必须在再次发送 HTTP 前拒绝旧绑定。</summary>
    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)] [InlineData(6)]
    [InlineData(7)] [InlineData(8)] [InlineData(9)] [InlineData(10)]
    [InlineData(11)] [InlineData(12)]
    public async Task ConfigurationChange_InvalidatesExistingSession(int option)
    {
        var store = new DistributedCacheLlmSessionStore(new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions())), new TestSessionLock());
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent(ConversionFixtures.Response(LlmProtocol.ChatCompletions).ToJsonString(), Encoding.UTF8, "application/json") }));
        var changed = option switch
        {
            0 => new LlmConversionOptions { DefaultSchemaName = "other" },
            1 => new LlmConversionOptions { TargetThinkingBudgetTokens = 2048 },
            2 => new LlmConversionOptions { TargetReasoningEffort = "medium" },
            3 => new LlmConversionOptions { GeminiOutputFormat = GeminiOutputFormat.ResponseFormat },
            4 => new LlmConversionOptions { GeminiOutputFormat = GeminiOutputFormat.LegacySchema },
            5 => new LlmConversionOptions { GeminiFunctionSchemaFormat = GeminiFunctionSchemaFormat.LegacySchema },
            6 => new LlmConversionOptions { TargetSettings = new() { MaximumOutputTokens = 10000 } },
            7 => new LlmConversionOptions { EnableCustomToolConversion = true },
            8 => new LlmConversionOptions { MaxToolConversionMappings = 256 },
            9 => new LlmConversionOptions { MaxToolConversionHistoryCalls = 8192 },
            10 => new LlmConversionOptions { MaxCustomToolInputBytes = 2097152 },
            11 => new LlmConversionOptions { MaxToolConversionOutputCalls = 256 },
            _ => new LlmConversionOptions { EnableNamespacedToolConversion = true }
        };
        await using var first = ConvertingProviderTests.Build(handler, LlmProtocol.ClaudeMessages, LlmProtocol.ChatCompletions, store);
        await using var second = ConvertingProviderTests.Build(handler, LlmProtocol.ClaudeMessages, LlmProtocol.ChatCompletions, store, changed);
        var request = ConversionFixtures.Request(LlmProtocol.ClaudeMessages); request["model"] = "public";
        await ConvertingProviderTests.CompleteAsync(first, LlmProtocol.ClaudeMessages, request);
        await Assert.ThrowsAsync<InvalidOperationException>(() => ConvertingProviderTests.CompleteAsync(second, LlmProtocol.ClaudeMessages, request));
        Assert.Equal(1, handler.Calls);
    }
}
