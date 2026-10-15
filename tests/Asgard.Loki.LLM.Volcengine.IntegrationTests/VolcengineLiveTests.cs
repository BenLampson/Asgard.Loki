using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Asgard.Loki.LLM.Volcengine.IntegrationTests;

/// <summary>五次真实请求验证公共入口、最终参数、输出结构和响应链续接。</summary>
[Trait("Suite", "Smoke")]
[Collection("Volcengine live")]
public sealed class VolcengineLiveTests(ITestOutputHelper output)
{
    /// <summary>供 xUnit 动态跳过使用，不将离线执行报告成真实联调成功。</summary>
    public static bool IsLiveEnabled => LiveConfiguration.IsEnabled();

    /// <summary>覆盖 Chat/Responses 的普通与流式调用，以及跨客户端的上游上下文保持。</summary>
    [Fact(Skip = "Set ASGARD_LOKI_TEST_LIVE=true to run real Volcengine requests.", SkipUnless = nameof(IsLiveEnabled))]
    public async Task LlmProtocols_CompleteRealRequestsAndContinueSession()
    {
        var configuration = LiveConfiguration.Load();
        await using var context = new LiveLlmContext(configuration);
        await using var client = context.Build();
        List<object> evidence = [];
        var passed = false;
        object? upstreamFailure = null;
        try
        {
            // JSON 模式不仅检查参数发出，还检查真实输出确实是指定结构。
            using (var deadline = Deadline())
            {
                var result = await client.LLM.ChatCompletions.CompleteAsync(ChatRequest(
                    "Return only a JSON object with a string field loki equal to connected.", true), Call(), deadline.Token);
                Assert.False(string.IsNullOrWhiteSpace(result.Id));
                Assert.Equal(configuration.ChatModel, result.Metadata.UpstreamModel);
                var text = result.Data.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
                using var json = JsonDocument.Parse(text!);
                Assert.Equal("connected", json.RootElement.GetProperty("loki").GetString());
                Assert.True(result.Data.GetProperty("usage").GetProperty("total_tokens").GetInt32() > 0);
                evidence.Add(new { scenario = "chat-json", result.Id, passed = true });
            }

            using (var deadline = Deadline())
            {
                var text = new StringBuilder();
                var completed = false;
                await foreach (var item in client.LLM.ChatCompletions.StreamAsync(
                    ChatRequest("Reply briefly with Loki connected.", false), Call(), deadline.Token))
                {
                    foreach (var choice in item.Data.GetProperty("choices").EnumerateArray())
                    {
                        if (choice.TryGetProperty("delta", out var delta) && delta.TryGetProperty("content", out var content)
                            && content.ValueKind == JsonValueKind.String) text.Append(content.GetString());
                        if (choice.TryGetProperty("finish_reason", out var finish) && finish.ValueKind == JsonValueKind.String)
                        { Assert.Equal("stop", finish.GetString()); completed = true; }
                    }
                }
                Assert.True(completed);
                Assert.False(string.IsNullOrWhiteSpace(text.ToString()));
                evidence.Add(new { scenario = "chat-stream", characters = text.Length, passed = true });
            }

            var marker = Guid.NewGuid().ToString("N");
            var session = Call();
            Response original;
            using (var deadline = Deadline())
            {
                original = await client.LLM.Responses.CreateAsync(
                    ResponseRequest($"Remember this test marker: {marker}. Echo the marker once.", true), session, deadline.Token);
                AssertCompleted(original.Data);
                Assert.Contains(marker, ResponseText(original.Data));
                Assert.False(string.IsNullOrWhiteSpace(original.Id));
                Assert.Equal(configuration.ResponsesModel, original.Metadata.UpstreamModel);
                evidence.Add(new { scenario = "responses-json", original.Id, passed = true });
            }

            using (var deadline = Deadline())
            {
                var text = new StringBuilder();
                var completed = false;
                await foreach (var item in client.LLM.Responses.StreamAsync(
                    ResponseRequest("Reply briefly with Loki connected.", false), Call(), deadline.Token))
                {
                    Assert.False(item.Type is "error" or "response.failed" or "response.incomplete", "Responses stream returned a failure terminal.");
                    if (item.Type == "response.output_text.delta") text.Append(item.Data.GetProperty("delta").GetString());
                    if (item.Type == "response.completed")
                    {
                        var response = item.Data.GetProperty("response");
                        AssertCompleted(response);
                        Assert.Equal(text.ToString(), ResponseText(response));
                        completed = true;
                    }
                }
                Assert.True(completed);
                Assert.False(string.IsNullOrWhiteSpace(text.ToString()));
                evidence.Add(new { scenario = "responses-stream", characters = text.Length, passed = true });
            }

            // 重新创建 LokiClient，第二轮只发问题，不重发 marker 或前一轮消息。
            await using var reconnected = context.Build();
            using (var deadline = Deadline())
            {
                var continuation = ResponseRequest("What was the test marker? Reply only with that marker.", false)
                    with { PreviousResponseId = original.Id };
                var result = await reconnected.LLM.Responses.CreateAsync(continuation, session, deadline.Token);
                AssertCompleted(result.Data);
                Assert.Contains(marker, ResponseText(result.Data));
                Assert.Equal(original.Metadata, result.Metadata);
                Assert.NotEqual(original.Id, result.Id);
                evidence.Add(new { scenario = "responses-continuation", result.Id, passed = true });
            }

            Assert.Equal(LiveConfiguration.MaximumRequests, context.Handler.Requests.Count);
            foreach (var (path, body, status) in context.Handler.Requests)
            {
                Assert.Equal(200, status);
                Assert.Equal(path.EndsWith("chat/completions", StringComparison.Ordinal)
                    ? configuration.ChatModel : configuration.ResponsesModel, body.GetProperty("model").GetString());
                Assert.Equal("disabled", body.GetProperty("thinking").GetProperty("type").GetString());
            }
            var requests = context.Handler.Requests;
            Assert.Equal("json_object", requests[0].Body.GetProperty("response_format").GetProperty("type").GetString());
            Assert.Equal(0.1, requests[0].Body.GetProperty("temperature").GetDouble());
            Assert.True(requests[1].Body.GetProperty("stream").GetBoolean());
            Assert.True(requests[2].Body.GetProperty("store").GetBoolean());
            Assert.True(requests[3].Body.GetProperty("stream").GetBoolean());
            Assert.Equal(original.Id, requests[4].Body.GetProperty("previous_response_id").GetString());
            passed = true;
        }
        catch (LlmUpstreamException failure)
        {
            // 不输出上游原始错误正文，避免供应商错误内容意外包含认证信息。
            var code = ErrorCode(failure.ResponseBody);
            upstreamFailure = new { status = (int)failure.StatusCode, code };
            output.WriteLine($"Upstream failure: HTTP {(int)failure.StatusCode}, code {code}; inspect account/key/model configuration.");
            throw;
        }
        finally
        {
            var directory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts/live/volcengine-llm"));
            Directory.CreateDirectory(directory);
            var report = Path.Combine(directory, $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.json");
            var data = new
            {
                passed, upstreamFailure, configuration.ChatModel, configuration.ResponsesModel, completedScenarios = evidence,
                requests = context.Handler.Requests.Select(item => new { item.Path, item.Status }),
                requestLimit = LiveConfiguration.MaximumRequests
            };
            await File.WriteAllTextAsync(report, JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }), CancellationToken.None);
            output.WriteLine($"Live evidence: {report}; completed scenarios: {evidence.Count}; requests: {context.Handler.Requests.Count}.");
        }
    }

    /// <summary>只保留有界的错误码，不将上游正文或可能回显的凭据写入日志。</summary>
    private static string ErrorCode(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("error", out var error) && error.TryGetProperty("code", out var value)
                && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 and <= 80 } code
                && code.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '.' or '-')) return code;
        }
        catch (JsonException) { }
        return "unknown";
    }

    /// <summary>每次调用使用独立截止时间，覆盖网络读取和完整 SSE 枚举。</summary>
    private static CancellationTokenSource Deadline()
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        source.CancelAfter(LiveConfiguration.RequestTimeout);
        return source;
    }

    /// <summary>每个独立场景使用不同 Session，续接场景显式复用原会话。</summary>
    private static LlmCallOptions Call() => new() { SessionId = "live-" + Guid.NewGuid().ToString("N") };

    /// <summary>测试固定的 Chat 参数，实际模型由路由替换。</summary>
    private static ChatCompletionRequest ChatRequest(string prompt, bool json)
    {
        var parameters = Parameters("max_tokens");
        if (json) parameters["response_format"] = JsonSerializer.SerializeToElement(new { type = "json_object" });
        return new() { Model = "live-chat", Messages = [new JsonObject { ["role"] = "user", ["content"] = prompt }], Parameters = parameters };
    }

    /// <summary>测试固定的 Responses 参数，只有响应链首轮显式启用存储。</summary>
    private static ResponseRequest ResponseRequest(string prompt, bool store)
    {
        var parameters = Parameters("max_output_tokens");
        parameters["store"] = JsonSerializer.SerializeToElement(store);
        return new() { Model = "live-responses", Input = JsonValue.Create(prompt), Parameters = parameters };
    }

    /// <summary>参数写入用例，不扩大环境变量配置面。</summary>
    private static Dictionary<string, JsonElement> Parameters(string limitName) => new()
    {
        [limitName] = JsonSerializer.SerializeToElement(128),
        ["temperature"] = JsonSerializer.SerializeToElement(0.1),
        ["thinking"] = JsonSerializer.SerializeToElement(new { type = "disabled" })
    };

    /// <summary>必须完成而非仅返回 HTTP 200。</summary>
    private static void AssertCompleted(JsonElement response)
    {
        Assert.Equal("completed", response.GetProperty("status").GetString());
        Assert.False(response.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null);
        Assert.True(response.GetProperty("usage").GetProperty("total_tokens").GetInt32() > 0);
    }

    /// <summary>只提取真实协议中的输出文本，不混入推理项。</summary>
    private static string ResponseText(JsonElement response)
    {
        var text = new StringBuilder();
        foreach (var item in response.GetProperty("output").EnumerateArray())
            if (item.TryGetProperty("content", out var content))
                foreach (var part in content.EnumerateArray())
                    if (part.TryGetProperty("type", out var type) && type.GetString() == "output_text")
                        text.Append(part.GetProperty("text").GetString());
        return text.ToString();
    }
}
