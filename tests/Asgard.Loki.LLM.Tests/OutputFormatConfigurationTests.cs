using System.Text.Json;
using Asgard.Loki;
using Xunit;
using static Asgard.Loki.LLM.Tests.OutputFormatFixture;

namespace Asgard.Loki.LLM.Tests;

/// <summary>目标格式策略必须冻结、跨实例稳定，并遵守已有完整目标绑定与首次协调契约。</summary>
public sealed class OutputFormatConfigurationTests
{
    /// <summary>默认声明省略，不改变历史基础目标序列化和 Session 指纹。</summary>
    [Fact]
    public void DefaultDeclaration_PreservesLegacySerialization()
    {
        var target = Target(LlmProtocol.ChatCompletions);
        var legacy = new { target.Id, target.ProviderId, target.BaseAddress, target.UpstreamModel, target.CredentialId, target.Version, target.Protocols };
        Assert.Equal(JsonSerializer.Serialize(legacy), JsonSerializer.Serialize(target));
    }

    /// <summary>注册检查所有目标，不能因当前选择了另一个合法目标就藏起错误声明。</summary>
    [Theory] [InlineData("unknown")] [InlineData("duplicate")] [InlineData("null-list")]
    [InlineData("combined")] [InlineData("missing-protocol")] [InlineData("unsupported-protocol")] [InlineData("null-value")]
    public void InvalidDeclaration_IsRejectedEvenOnUnselectedTarget(string kind)
    {
        var provider = new OutputFormatProvider(); var valid = Target(LlmProtocol.ChatCompletions);
        var protocol = kind switch
        {
            "combined" => LlmProtocol.ChatCompletions | LlmProtocol.Responses,
            "missing-protocol" => LlmProtocol.Responses,
            "unsupported-protocol" => LlmProtocol.Realtime,
            _ => LlmProtocol.ChatCompletions
        };
        IReadOnlyList<string> values = kind switch
        {
            "unknown" => ["JSON_OBJECT"], "duplicate" => ["text", "text"], "null-list" => null!, "null-value" => [null!], _ => ["text"]
        };
        var invalid = valid with { Id = "invalid", AllowedOutputFormats = new Dictionary<LlmProtocol, IReadOnlyList<string>> { [protocol] = values } };
        Assert.ThrowsAny<ArgumentException>(() => new LokiClientBuilder().AddLLM(Options(provider, WebSocketFixture.Store(), valid, invalid)));
        Assert.Empty(provider.Calls);
    }

    /// <summary>修改原数组和字典不能放宽已构建客户端的策略。</summary>
    [Fact]
    public async Task MutableConfiguration_IsDeeplySnapshotted()
    {
        var provider = new OutputFormatProvider(); string[] values = ["text", "json_object"];
        var declarations = new Dictionary<LlmProtocol, IReadOnlyList<string>> { [LlmProtocol.ChatCompletions] = values };
        var target = Target(LlmProtocol.ChatCompletions) with { AllowedOutputFormats = declarations };
        await using var client = new LokiClientBuilder().AddLLM(Options(provider, WebSocketFixture.Store(), target)).Build();
        values[1] = "json_schema"; declarations.Clear();
        await Invoke(client, LlmProtocol.ChatCompletions, false, """{"response_format":{"type":"json_object"}}""");
        await Assert.ThrowsAsync<NotSupportedException>(() => Invoke(client, LlmProtocol.ChatCompletions, true, """{"response_format":{"type":"json_schema"}}"""));
        Assert.Single(provider.Calls);
    }

    /// <summary>同一策略用不同字典和列表顺序构建时，跨客户端会话指纹仍一致；共享目标也按配置值比较。</summary>
    [Fact]
    public async Task EquivalentDeclarations_HaveStableFingerprintsAndCanShareTargetIds()
    {
        var provider = new OutputFormatProvider(); var store = WebSocketFixture.Store();
        var first = Target(LlmProtocol.ChatCompletions | LlmProtocol.Responses) with
        {
            AllowedOutputFormats = new Dictionary<LlmProtocol, IReadOnlyList<string>>
            {
                [LlmProtocol.Responses] = ["json_schema", "text"], [LlmProtocol.ChatCompletions] = ["json_object", "text"]
            }
        };
        var reordered = first with
        {
            AllowedOutputFormats = new Dictionary<LlmProtocol, IReadOnlyList<string>>
            {
                [LlmProtocol.ChatCompletions] = ["text", "json_object"], [LlmProtocol.Responses] = ["text", "json_schema"]
            }
        };
        await using (var client = new LokiClientBuilder().AddLLM(Options(provider, store, first)).Build())
            await Invoke(client, LlmProtocol.ChatCompletions, false, "{}");
        var options = Options(provider, store, reordered);
        options = options with { Routes = [options.Routes[0], new() { Model = "alias", Targets = [first] }] };
        await using var restored = new LokiClientBuilder().AddLLM(options).Build();
        await Invoke(restored, LlmProtocol.ChatCompletions, false, "{}"); Assert.Equal(2, provider.Calls.Count);
    }

    /// <summary>显式空映射等同于未声明，而协议空列表是实际约束，不能在序列化中混为一谈。</summary>
    [Fact]
    public async Task EmptyMap_PreservesUndeclaredSessionButEmptyListChangesIt()
    {
        var provider = new OutputFormatProvider(); var store = WebSocketFixture.Store(); var target = Target(LlmProtocol.ChatCompletions);
        await using (var original = new LokiClientBuilder().AddLLM(Options(provider, store, target)).Build())
            await Invoke(original, LlmProtocol.ChatCompletions, false, "{}");
        await using (var empty = new LokiClientBuilder().AddLLM(Options(provider, store, target with { AllowedOutputFormats = new Dictionary<LlmProtocol, IReadOnlyList<string>>() })).Build())
            await Invoke(empty, LlmProtocol.ChatCompletions, false, "{}");
        await using var restricted = new LokiClientBuilder().AddLLM(Options(provider, store, Target(LlmProtocol.ChatCompletions, []))).Build();
        await Assert.ThrowsAsync<InvalidOperationException>(() => Invoke(restricted, LlmProtocol.ChatCompletions, false, "{}"));
        Assert.Equal(2, provider.Calls.Count);
    }

    /// <summary>放宽或收紧格式声明都使旧 Session 失效，即使当前请求未显式指定格式。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task ChangedDeclaration_RejectsExistingSession(bool widen)
    {
        var provider = new OutputFormatProvider(); var store = WebSocketFixture.Store();
        await using (var first = new LokiClientBuilder().AddLLM(Options(provider, store, Target(LlmProtocol.ChatCompletions, ["text", "json_object"]))).Build())
            await Invoke(first, LlmProtocol.ChatCompletions, false, "{}");
        await using var changed = new LokiClientBuilder().AddLLM(Options(provider, store, Target(LlmProtocol.ChatCompletions, widen ? ["text", "json_object", "json_schema"] : ["text"]))).Build();
        await Assert.ThrowsAsync<InvalidOperationException>(() => Invoke(changed, LlmProtocol.ChatCompletions, false, "{}")); Assert.Single(provider.Calls);
    }

    /// <summary>目标选择器不按格式过滤候选；选中的目标不支持时不能自动换到支持 Schema 的备选目标。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task UnsupportedSelectedTarget_DoesNotChooseAnotherTarget(bool existing)
    {
        var provider = new OutputFormatProvider(); var store = WebSocketFixture.Store();
        var first = Target(LlmProtocol.ChatCompletions, ["text", "json_object"]);
        var backup = Target(LlmProtocol.ChatCompletions, ["json_schema"]) with { Id = "schema-target" };
        await using var client = new LokiClientBuilder().AddLLM(Options(provider, store, first, backup)).Build();
        if (existing) await Invoke(client, LlmProtocol.ChatCompletions, false, "{}");
        await Assert.ThrowsAsync<NotSupportedException>(() => Invoke(client, LlmProtocol.ChatCompletions, false, """{"response_format":{"type":"json_schema"}}"""));
        Assert.Equal(existing ? 1 : 0, provider.Calls.Count); Assert.Equal(existing ? 1 : 0, store.GetOrCreateCalls);
    }

    /// <summary>首次协调返回其他并发调用已有的绑定时，检查真正绑定的目标而不是刚刚选择的候选。</summary>
    [Fact]
    public async Task FirstBindingRace_ValidatesActualBoundTarget()
    {
        var provider = new OutputFormatProvider(); var store = WebSocketFixture.Store();
        var a = Target(LlmProtocol.ChatCompletions, ["json_schema"]);
        var b = Target(LlmProtocol.ChatCompletions, ["json_object"]) with { Id = "b" };
        await using (var first = new LokiClientBuilder().AddLLM(Options(provider, store, a, b) with { TargetSelector = new TestTargetSelector(1) }).Build())
            await Invoke(first, LlmProtocol.ChatCompletions, false, "{}");
        store.ReadBinding = _ => null;
        await using var racing = new LokiClientBuilder().AddLLM(Options(provider, store, a, b)).Build();
        var error = await Assert.ThrowsAsync<NotSupportedException>(() => Invoke(racing, LlmProtocol.ChatCompletions, false, """{"response_format":{"type":"json_schema"}}"""));
        Assert.Contains("目标 b", error.Message); Assert.Single(provider.Calls); Assert.Equal(2, store.GetOrCreateCalls);
    }
}
