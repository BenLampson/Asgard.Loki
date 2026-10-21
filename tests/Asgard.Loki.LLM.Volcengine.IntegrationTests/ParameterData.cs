using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Asgard.Loki.LLM.Volcengine.IntegrationTests;

/// <summary>分别构造两种原生协议的参数，不在测试中把 Chat 参数误当作 Responses 参数。</summary>
internal static class ParameterData
{
    /// <summary>工具与结构化输出场景使用短输出，推理场景显式扩大 token 预算。</summary>
    internal static Dictionary<string, JsonElement> Parameters(bool responses, bool thinking = false, int? limit = null) => new()
    {
        [responses ? "max_output_tokens" : "max_completion_tokens"] = JsonSerializer.SerializeToElement(limit ?? (thinking ? 1024 : 512)),
        ["thinking"] = JsonSerializer.SerializeToElement(new { type = thinking ? "enabled" : "disabled" })
    };

    /// <summary>生成新的 Chat 请求，避免共享可变节点污染其他用例。</summary>
    internal static ChatCompletionRequest Chat(string prompt, Dictionary<string, JsonElement>? parameters = null) => new()
    {
        Model = "live-chat", Messages = [new JsonObject { ["role"] = "user", ["content"] = prompt }],
        Parameters = parameters ?? Parameters(false)
    };

    /// <summary>生成不存储上游状态的 Responses 请求；工具续接场景自行覆盖 store。</summary>
    internal static ResponseRequest Responses(string prompt, Dictionary<string, JsonElement>? parameters = null)
    {
        parameters ??= Parameters(true);
        parameters.TryAdd("store", JsonSerializer.SerializeToElement(false));
        return new() { Model = "live-responses", Input = JsonValue.Create(prompt), Parameters = parameters };
    }

    /// <summary>只提取最终回答，不将推理摘要拼到用户文本里。</summary>
    internal static string ResponseText(JsonElement response) => string.Concat(response.GetProperty("output").EnumerateArray()
        .Where(item => item.TryGetProperty("content", out _))
        .SelectMany(item => item.GetProperty("content").EnumerateArray())
        .Where(part => part.TryGetProperty("type", out var type) && type.GetString() == "output_text")
        .Select(part => part.GetProperty("text").GetString()));

    /// <summary>从完成事件中提取最终对象，仍校验服务端状态和用量。</summary>
    internal static JsonElement FinalResponse(IReadOnlyList<ResponseEvent> events)
    {
        var final = Assert.Single(events, item => item.Type == "response.completed").Data.GetProperty("response");
        Completed(final);
        return final;
    }

    /// <summary>Responses 返回 HTTP 200 之外，还必须真正完成且存在有效 token 用量。</summary>
    internal static void Completed(JsonElement response)
    {
        Assert.Equal("completed", response.GetProperty("status").GetString());
        Assert.False(response.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null);
        Assert.True(response.GetProperty("usage").GetProperty("total_tokens").GetInt32() > 0);
    }

    /// <summary>严格 Schema 包含枚举、嵌套对象、数组、布尔值与显式 null。</summary>
    internal static JsonElement Schema()
    {
        using var document = JsonDocument.Parse("""
        {"type":"object","properties":{
          "kind":{"type":"string","enum":["loki-schema"]},
          "count":{"type":"integer"},"active":{"type":"boolean"},
          "tags":{"type":"array","items":{"type":"string"}},
          "note":{"type":["string","null"]},
          "metadata":{"type":"object","properties":{"source":{"type":"string","enum":["live"]}},"required":["source"],"additionalProperties":false}
        },"required":["kind","count","active","tags","note","metadata"],"additionalProperties":false}
        """);
        return document.RootElement.Clone();
    }

    /// <summary>同时验证 Schema 结构与已指定的合成业务值。</summary>
    internal static void AssertSchema(string text)
    {
        using var document = JsonDocument.Parse(text);
        var data = document.RootElement;
        Assert.Equal(6, data.EnumerateObject().Count());
        Assert.Equal("loki-schema", data.GetProperty("kind").GetString());
        Assert.Equal(3, data.GetProperty("count").GetInt32());
        Assert.False(data.GetProperty("active").GetBoolean());
        Assert.Equal(new[] { "alpha", "beta" }, data.GetProperty("tags").EnumerateArray().Select(item => item.GetString()));
        Assert.Equal(JsonValueKind.Null, data.GetProperty("note").ValueKind);
        Assert.Single(data.GetProperty("metadata").EnumerateObject());
        Assert.Equal("live", data.GetProperty("metadata").GetProperty("source").GetString());
    }

    /// <summary>只有本地合成工具，不连接第三方服务或执行模型生成的任意代码。</summary>
    internal static JsonElement Tool(bool responses)
    {
        var definition = new JsonObject
        {
            ["name"] = "loki_get_receipt", ["description"] = "Look up the opaque receipt for an order.", ["strict"] = true,
            ["parameters"] = JsonNode.Parse("""{"type":"object","properties":{"order_id":{"type":"string","enum":["order-42"]}},"required":["order_id"],"additionalProperties":false}""")
        };
        JsonObject tool = responses ? definition : new JsonObject { ["function"] = definition };
        tool["type"] = "function";
        return JsonSerializer.SerializeToElement(new JsonArray(tool));
    }

    /// <summary>指定工具名时，两种协议的对象层级不同。</summary>
    internal static JsonElement NamedChoice(bool responses) => JsonSerializer.SerializeToElement(responses
        ? (object)new { type = "function", name = "loki_get_receipt" }
        : new { type = "function", function = new { name = "loki_get_receipt" } });

    /// <summary>校验严格工具参数，返回本地随机结果前先确认调用的真实函数名与参数。</summary>
    internal static void AssertTool(string? name, string? arguments)
    {
        Assert.Equal("loki_get_receipt", name);
        using var document = JsonDocument.Parse(arguments!);
        Assert.Single(document.RootElement.EnumerateObject());
        Assert.Equal("order-42", document.RootElement.GetProperty("order_id").GetString());
    }
}
