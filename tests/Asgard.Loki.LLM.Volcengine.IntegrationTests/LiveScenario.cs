using Asgard.Loki.Abstractions;
using System.Text.Json;
using Xunit;

namespace Asgard.Loki.LLM.Volcengine.IntegrationTests;

/// <summary>隔离参数场景，报告只记录断言证据，不记录推理原文或不透明加密块。</summary>
internal sealed class LiveScenario : IAsyncDisposable
{
    private readonly LiveLlmContext _context;
    /// <summary>场景自己的客户端，所有调用经过公开入口。</summary>
    internal ILokiClient Client { get; }
    /// <summary>已验证的模型与认证配置，不序列化此对象。</summary>
    internal LiveConfiguration Configuration { get; }
    /// <summary>实际发往上游的参数快照，不持久化完整正文。</summary>
    internal List<(string Path, JsonElement Body, int Status)> Requests => _context.Handler.Requests;
    /// <summary>可安全保存的计数、参数路径与验证结果。</summary>
    internal Dictionary<string, object> Evidence { get; } = [];

    /// <summary>完整配置先验证，再建立 HTTP 与会话依赖。</summary>
    private LiveScenario()
    {
        Configuration = LiveConfiguration.Load();
        _context = new LiveLlmContext(Configuration);
        Client = _context.Build();
    }

    /// <summary>执行并保存单个场景的结果，失败仍向 xUnit 传播。</summary>
    internal static async Task RunAsync(string name, ITestOutputHelper output, Func<LiveScenario, Task> run)
    {
        await using var scenario = new LiveScenario();
        var passed = false;
        object? failure = null;
        try
        {
            await run(scenario);
            Assert.NotEmpty(scenario.Requests);
            foreach (var (path, body, status) in scenario.Requests)
            {
                Assert.Equal(200, status);
                Assert.Equal(path.EndsWith("chat/completions", StringComparison.Ordinal)
                    ? scenario.Configuration.ChatModel : scenario.Configuration.ResponsesModel, body.GetProperty("model").GetString());
            }
            passed = true;
        }
        catch (Exception exception)
        {
            failure = exception is LlmUpstreamException upstream
                ? new { type = nameof(LlmUpstreamException), status = (int)upstream.StatusCode, code = ErrorCode(upstream.ResponseBody) }
                : (object)new { type = exception.GetType().Name };
            throw;
        }
        finally
        {
            var directory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts/live/volcengine-llm"));
            Directory.CreateDirectory(directory);
            var report = Path.Combine(directory, $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{name}-{Guid.NewGuid():N}.json");
            var data = new
            {
                scenario = name, passed, failure, scenario.Configuration.ChatModel, scenario.Configuration.ResponsesModel,
                evidence = scenario.Evidence,
                requests = scenario.Requests.Select(item => new { item.Path, item.Status, fields = item.Body.EnumerateObject().Select(property => property.Name).ToArray() }),
                processRequestLimit = LiveRequestBudget.Maximum
            };
            await File.WriteAllTextAsync(report, JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }), CancellationToken.None);
            output.WriteLine($"{name}: passed={passed}, requests={scenario.Requests.Count}, report={report}");
            if (failure is not null) output.WriteLine(JsonSerializer.Serialize(failure));
        }
    }

    /// <summary>独立 Session 标识；需要续接时由用例显式复用。</summary>
    internal static LlmCallOptions Call() => new() { SessionId = "parameters-" + Guid.NewGuid().ToString("N") };

    /// <summary>完整调用截止时间包括所有流式事件的枚举。</summary>
    private static CancellationTokenSource Deadline()
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        source.CancelAfter(LiveConfiguration.RequestTimeout);
        return source;
    }

    /// <summary>执行一次普通 Chat 请求，不重试。</summary>
    internal async Task<ChatCompletion> ChatAsync(ChatCompletionRequest request, LlmCallOptions? call = null)
    {
        using var deadline = Deadline();
        return await Client.LLM.ChatCompletions.CompleteAsync(request, call ?? Call(), deadline.Token);
    }

    /// <summary>完整读取 Chat 流。</summary>
    internal async Task<List<ChatCompletionEvent>> ChatStreamAsync(ChatCompletionRequest request, LlmCallOptions? call = null)
    {
        using var deadline = Deadline();
        List<ChatCompletionEvent> events = [];
        await foreach (var item in Client.LLM.ChatCompletions.StreamAsync(request, call ?? Call(), deadline.Token)) events.Add(item);
        return events;
    }

    /// <summary>执行一次普通 Responses 请求。</summary>
    internal async Task<Response> ResponsesAsync(ResponseRequest request, LlmCallOptions? call = null)
    {
        using var deadline = Deadline();
        return await Client.LLM.Responses.CreateAsync(request, call ?? Call(), deadline.Token);
    }

    /// <summary>完整读取 Responses 流，失败终态不能当作普通输出通过。</summary>
    internal async Task<List<ResponseEvent>> ResponsesStreamAsync(ResponseRequest request, LlmCallOptions? call = null)
    {
        using var deadline = Deadline();
        List<ResponseEvent> events = [];
        await foreach (var item in Client.LLM.Responses.StreamAsync(request, call ?? Call(), deadline.Token))
        {
            Assert.False(item.Type is "error" or "response.failed" or "response.incomplete", "The Responses stream did not complete successfully.");
            events.Add(item);
        }
        return events;
    }

    /// <summary>错误码限长且限制字符集，永不输出上游原始正文。</summary>
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

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await Client.DisposeAsync();
        await _context.DisposeAsync();
    }
}
