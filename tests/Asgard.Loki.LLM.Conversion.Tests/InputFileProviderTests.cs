using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Asgard.Loki.Testing;
using Xunit;

namespace Asgard.Loki.LLM.Conversion.Tests;

/// <summary>通过公共 SDK、原生适配器及替代 HTTP/SSE 验证 PDF 最终报文和明确失败边界。</summary>
public sealed class InputFileProviderTests
{
    /// <summary>所有十二个方向均覆盖完整和流式调用，响应只返回独立的普通文本。</summary>
    public static TheoryData<LlmProtocol, LlmProtocol, bool> Calls
    {
        get
        {
            TheoryData<LlmProtocol, LlmProtocol, bool> data = [];
            foreach (var source in InputFileFixtures.Protocols) foreach (var target in InputFileFixtures.Protocols)
                if (source != target) { data.Add(source, target, false); data.Add(source, target, true); }
            return data;
        }
    }

    /// <summary>对完整原生请求做金样比较，保证文件、工具历史和实际模型均通过真实客户端流水线。</summary>
    [Theory] [MemberData(nameof(Calls))]
    public async Task PublicPipeline_PreservesFileAndHistory(LlmProtocol source, LlmProtocol target, bool stream)
    {
        using var handler = new StubHttpHandler(async (request, token) =>
        {
            Assert.Equal("wire.invalid", request.RequestUri!.Host);
            var expected = InputFileFixtures.Read(target, true); Wire(expected, target, stream);
            CustomToolConversionTests.Equal(expected, JsonNode.Parse(await request.Content!.ReadAsStringAsync(token)));
            return Reply(target, stream);
        });
        await using var client = ConvertingProviderTests.Build(handler, source, target); var request = InputFileFixtures.Read(source); request["model"] = "public";
        var before = request.ToJsonString();
        if (stream)
        {
            var result = await ConvertingProviderTests.StreamAsync(client, source, request); Assert.NotEmpty(result);
            Assert.Contains(result, e => e.Data.GetRawText().Contains("done", StringComparison.Ordinal));
            Assert.All(result, e => Assert.Empty(e.Metadata.Conversion!.Diagnostics));
        }
        else
        {
            var result = await ConvertingProviderTests.CompleteAsync(client, source, request); Assert.Contains("done", result.Data.GetRawText());
            Assert.Empty(result.Metadata.Conversion!.Diagnostics);
            CustomToolConversionTests.Equal(HostedSearchFixtures.TextResponse(target), JsonNode.Parse(result.Metadata.Conversion.UpstreamData.GetRawText()));
        }
        Assert.Equal(before, request.ToJsonString()); Assert.Equal(1, handler.Calls);
    }

    /// <summary>四协议的来源引用在完整与流式入口均先于上游请求失败。</summary>
    public static TheoryData<LlmProtocol, bool> References
    {
        get { TheoryData<LlmProtocol, bool> data = []; foreach (var source in InputFileFixtures.Protocols) { data.Add(source, false); data.Add(source, true); } return data; }
    }

    /// <summary>普通损耗许可不授权自动下载、上传、跨目标复用文件 ID 或重试请求。</summary>
    [Theory] [MemberData(nameof(References))]
    public async Task ReferenceFailure_HappensBeforeAnyHttp(LlmProtocol source, bool stream)
    {
        var target = source == LlmProtocol.Gemini ? LlmProtocol.Responses : LlmProtocol.Gemini;
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException());
        await using var client = ConvertingProviderTests.Build(handler, source, target, options: new() { AllowLoss = true });
        var request = InputFileBoundaryTests.Reference(source); request["model"] = "public";
        if (stream) await Assert.ThrowsAsync<LlmConversionException>(() => ConvertingProviderTests.StreamAsync(client, source, request));
        else await Assert.ThrowsAsync<LlmConversionException>(() => ConvertingProviderTests.CompleteAsync(client, source, request));
        Assert.Equal(0, handler.Calls);
    }

    /// <summary>文件名的元数据损耗在实际 SDK 的两个模式下都需显式许可。</summary>
    public static TheoryData<LlmProtocol, bool, bool> FilenamePolicies
    {
        get
        {
            TheoryData<LlmProtocol, bool, bool> data = [];
            foreach (var target in new[] { LlmProtocol.ClaudeMessages, LlmProtocol.Gemini }) foreach (var stream in new[] { false, true })
                foreach (var allowLoss in new[] { false, true }) data.Add(target, stream, allowLoss);
            return data;
        }
    }

    /// <summary>许可后保持文件内容并在元数据报告文件名损耗；未许可则零 HTTP。</summary>
    [Theory] [MemberData(nameof(FilenamePolicies))]
    public async Task FilenameConsent_IsVisibleInPublicMetadata(LlmProtocol target, bool stream, bool allowLoss)
    {
        using var handler = new StubHttpHandler(async (request, token) =>
        {
            var expected = InputFileFixtures.Read(target, true); Wire(expected, target, stream);
            CustomToolConversionTests.Equal(expected, JsonNode.Parse(await request.Content!.ReadAsStringAsync(token)));
            return Reply(target, stream);
        });
        await using var client = ConvertingProviderTests.Build(handler, LlmProtocol.ChatCompletions, target, options: new() { AllowLoss = allowLoss });
        var request = InputFileFixtures.Read(LlmProtocol.ChatCompletions); request["model"] = "public";
        InputFileFixtures.File(request, LlmProtocol.ChatCompletions)["filename"] = "named.pdf";
        if (!allowLoss)
        {
            if (stream) await Assert.ThrowsAsync<LlmConversionException>(() => ConvertingProviderTests.StreamAsync(client, LlmProtocol.ChatCompletions, request));
            else await Assert.ThrowsAsync<LlmConversionException>(() => ConvertingProviderTests.CompleteAsync(client, LlmProtocol.ChatCompletions, request));
            Assert.Equal(0, handler.Calls); return;
        }
        if (stream)
        {
            var result = await ConvertingProviderTests.StreamAsync(client, LlmProtocol.ChatCompletions, request);
            Assert.All(result, e => Assert.Contains(e.Metadata.Conversion!.Diagnostics, d => d.Code == "file_name_not_representable"));
        }
        else
        {
            var result = await ConvertingProviderTests.CompleteAsync(client, LlmProtocol.ChatCompletions, request);
            Assert.Equal("file_name_not_representable", Assert.Single(result.Metadata.Conversion!.Diagnostics).Code);
        }
        Assert.Equal(1, handler.Calls);
    }

    /// <summary>直接可表达的 URL 只随模型请求发送，不出现独立媒体请求。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task NativeUrlReference_DoesNotDownloadInSdk(bool stream)
    {
        using var handler = new StubHttpHandler(async (request, token) =>
        {
            Assert.Equal("https://wire.invalid/v1/responses", request.RequestUri!.AbsoluteUri);
            var expected = InputFileFixtures.Read(LlmProtocol.Responses, true); Wire(expected, LlmProtocol.Responses, stream);
            InputFileFixtures.Parts(expected, LlmProtocol.Responses)[1] = CustomToolFixtures.Parse("""{"type":"input_file","file_url":"https://source.invalid/file.pdf?query=1"}""");
            CustomToolConversionTests.Equal(expected, JsonNode.Parse(await request.Content!.ReadAsStringAsync(token)));
            return Reply(LlmProtocol.Responses, stream);
        });
        await using var client = ConvertingProviderTests.Build(handler, LlmProtocol.ClaudeMessages, LlmProtocol.Responses);
        var request = InputFileFixtures.Read(LlmProtocol.ClaudeMessages); request["model"] = "public";
        InputFileFixtures.Parts(request, LlmProtocol.ClaudeMessages)[1]!["source"] = CustomToolFixtures.Parse("""{"type":"url","url":"https://source.invalid/file.pdf?query=1"}""");
        if (stream) await ConvertingProviderTests.StreamAsync(client, LlmProtocol.ClaudeMessages, request);
        else await ConvertingProviderTests.CompleteAsync(client, LlmProtocol.ClaudeMessages, request);
        Assert.Equal(1, handler.Calls);
    }

    /// <summary>目标字段差异由独立预期报文的原生 API 契约决定。</summary>
    private static void Wire(JsonObject expected, LlmProtocol target, bool stream)
    {
        if (target == LlmProtocol.Gemini) expected.Remove("model"); else expected["stream"] = stream;
    }

    /// <summary>普通文本回包只验证文件请求的 SDK 接线，不证明上游能成功解析样本 PDF。</summary>
    private static HttpResponseMessage Reply(LlmProtocol target, bool stream)
    {
        if (!stream) return new(HttpStatusCode.OK) { Content = new StringContent(HostedSearchFixtures.TextResponse(target).ToJsonString(), Encoding.UTF8, "application/json") };
        var body = string.Concat(InputFileFixtures.TextStream(target).Select(e => "data: " + e.ToJsonString() + "\n\n"));
        if (target == LlmProtocol.ChatCompletions) body += "data: [DONE]\n\n";
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new FragmentedReadStream(body)) };
        response.Content.Headers.ContentType = new("text/event-stream"); return response;
    }
}
