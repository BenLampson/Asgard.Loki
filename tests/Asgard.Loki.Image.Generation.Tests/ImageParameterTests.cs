using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.Image.Ali;
using Asgard.Loki.Image.OpenAI;
using Asgard.Loki.Image.Volcengine;
using Asgard.Loki.Testing;
using Xunit;

namespace Asgard.Loki.Image.Generation.Tests;

/// <summary>不支持参数、值域和原生冲突在发送之前失败，不自动挑选另一个上游。</summary>
public sealed class ImageParameterTests
{
    /// <summary>超出 2^53 的整数必须按原始精度校验，不被 double 舍入成合法边界。</summary>
    [Fact]
    public async Task LargeIntegerRange_IsComparedWithoutRounding()
    {
        using var http = new StubHttpHandler((_, _) => throw new InvalidOperationException("No IO"));
        var rules = GenerationFixture.Rules();
        rules["Seed"] = new() { Maximum = 9007199254740992d };
        var target = GenerationFixture.Target(new VolcengineImageGenerationHandler()) with { Parameters = rules };
        var error = await Assert.ThrowsAsync<LokiImageParameterException>(async () =>
            await GenerationFixture.Client(new GenerationHttpFactory(http), target)
                .GenerateAsync(GenerationFixture.Request() with { Seed = 9007199254740993L }, TestContext.Current.CancellationToken));
        Assert.Equal("Seed", error.ParamName); Assert.Equal("9007199254740993", error.RequestedValue); Assert.Equal(0, http.Calls);
    }
    /// <summary>精确诊断选中上游、实际模型、参数和请求值，并保持零 HTTP 调用。</summary>
    [Theory]
    [InlineData("unsupported")] [InlineData("undeclared")] [InlineData("range")] [InlineData("size")]
    [InlineData("native-collision")] [InlineData("case-collision")] [InlineData("ark-count")]
    public async Task UnsupportedParameter_FailsLocallyWithoutFallback(string kind)
    {
        using var http = new StubHttpHandler((_, _) => throw new InvalidOperationException("No IO"));
        var transport = new GenerationHttpFactory(http); var target = GenerationFixture.Target();
        var request = GenerationFixture.Request();
        switch (kind)
        {
            case "unsupported": request = request with { Seed = 7 }; break;
            case "undeclared": target = target with { Parameters = new Dictionary<string, LokiImageParameterRule>() }; break;
            case "range": request = request with { Count = 3 }; break;
            case "size": request = request with { Size = new(512, 512) }; break;
            case "native-collision": request.NativeParameters["n"] = 1; break;
            case "case-collision": request.NativeParameters["N"] = 1; break;
            case "ark-count": target = target with { Handler = new VolcengineImageGenerationHandler() }; request = request with { Count = 2 }; break;
        }
        var error = await Assert.ThrowsAsync<LokiImageParameterException>(async () =>
            await GenerationFixture.Client(transport, target, GenerationFixture.Target(id: "two"))
                .GenerateAsync(request, TestContext.Current.CancellationToken));
        Assert.Equal("one", error.ProviderId); Assert.Equal("native-one", error.UpstreamModel);
        Assert.False(string.IsNullOrWhiteSpace(error.ParamName)); Assert.False(string.IsNullOrWhiteSpace(error.RequestedValue));
        Assert.DoesNotContain(target.ApiKey, error.Message); Assert.Equal(0, transport.Calls);
    }

    /// <summary>目标只配置 prompt 时，不给未设置的统一字段填默认值。</summary>
    [Fact]
    public async Task OmittedParameters_DoNotRequireCapabilitiesOrInjectDefaults()
    {
        using var http = new StubHttpHandler(async (message, token) =>
        {
            using var body = JsonDocument.Parse(await message.Content!.ReadAsByteArrayAsync(token));
            Assert.Equal(2, body.RootElement.EnumerateObject().Count());
            return GenerationFixture.Response();
        });
        var client = GenerationFixture.Client(new GenerationHttpFactory(http),
            GenerationFixture.Target() with { Parameters = new Dictionary<string, LokiImageParameterRule>() });
        await client.GenerateAsync(new() { Model = "pictures", Prompt = "cat" }, TestContext.Current.CancellationToken);
    }

    /// <summary>嵌套原生扩展可合并不同字段，但不能覆盖统一映射的叶子或父级类型。</summary>
    [Theory]
    [InlineData("leaf")] [InlineData("parent")] [InlineData("case")]
    public async Task NestedCollision_IsRejected(string kind)
    {
        using var http = new StubHttpHandler((_, _) => throw new InvalidOperationException("No IO"));
        var input = new LokiImageGenerationRequest
        {
            Model = "pictures", Prompt = "cat", Count = 1,
            NativeParameters = kind switch
            {
                "leaf" => new() { ["parameters"] = new JsonObject { ["n"] = 1 } },
                "case" => new() { ["Parameters"] = new JsonObject() },
                _ => new() { ["parameters"] = null }
            }
        };
        await Assert.ThrowsAsync<LokiImageParameterException>(async () =>
            await GenerationFixture.Client(new GenerationHttpFactory(http), GenerationFixture.Target(new AliImageGenerationHandler()))
                .GenerateAsync(input, TestContext.Current.CancellationToken));
        Assert.Equal(0, http.Calls);
    }

    /// <summary>原生扩展未知字段保持原始 JSON，不经过统一参数支持表。</summary>
    [Fact]
    public async Task NativeExtensions_AreAllowedWhenUnifiedFieldIsOmitted()
    {
        using var http = new StubHttpHandler(async (message, token) =>
        {
            using var body = JsonDocument.Parse(await message.Content!.ReadAsByteArrayAsync(token));
            Assert.Equal(9, body.RootElement.GetProperty("n").GetInt32());
            Assert.Equal(JsonValueKind.Null, body.RootElement.GetProperty("vendor_future").ValueKind);
            return GenerationFixture.Response();
        });
        await GenerationFixture.Client(new GenerationHttpFactory(http)).GenerateAsync(new()
        { Model = "pictures", Prompt = "cat", NativeParameters = new() { ["n"] = 9, ["vendor_future"] = null } }, TestContext.Current.CancellationToken);
        Assert.Equal(1, http.Calls);
    }

    /// <summary>固定字段覆盖与无效值不会触发 HTTP 创建。</summary>
    [Theory]
    [InlineData("model")] [InlineData("MODEL")] [InlineData("stream")] [InlineData("prompt")]
    public async Task ReservedNativeOverrides_FailBeforeIo(string name)
    {
        using var http = new StubHttpHandler((_, _) => throw new InvalidOperationException("No IO"));
        var transport = new GenerationHttpFactory(http);
        await Assert.ThrowsAnyAsync<ArgumentException>(async () =>
            await GenerationFixture.Client(transport).GenerateAsync(new()
            { Model = "pictures", Prompt = "cat", NativeParameters = new() { [name] = "override" } }, TestContext.Current.CancellationToken));
        Assert.Equal(0, transport.Calls);
    }

    /// <summary>无效 Unicode 必须在 JSON 序列化器替换字符之前被拒绝。</summary>
    [Fact]
    public async Task InvalidUnicode_IsNotSilentlyRepaired()
    {
        using var http = new StubHttpHandler((_, _) => throw new InvalidOperationException("No IO"));
        var transport = new GenerationHttpFactory(http);
        await Assert.ThrowsAnyAsync<ArgumentException>(async () => await GenerationFixture.Client(transport).GenerateAsync(new()
        { Model = "pictures", Prompt = "cat", NativeParameters = new() { ["bad"] = "\uD800" } }, TestContext.Current.CancellationToken));
        Assert.Equal(0, transport.Calls);
    }

    /// <summary>生成失败不重试、不切换到同模型下另一目标。</summary>
    [Fact]
    public async Task UpstreamFailure_IsNotRetried()
    {
        using var http = new StubHttpHandler((_, _) => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.BadRequest)
        { Content = new StringContent("""{"error":{"message":"fixture"}}""", System.Text.Encoding.UTF8, "application/json") }));
        var error = await Assert.ThrowsAsync<ImageUpstreamException>(async () =>
            await GenerationFixture.Client(new GenerationHttpFactory(http), GenerationFixture.Target(), GenerationFixture.Target(id: "two"))
                .GenerateAsync(GenerationFixture.Request(), TestContext.Current.CancellationToken));
        Assert.Equal(400, error.StatusCode); Assert.Equal(1, http.Calls);
    }
}
