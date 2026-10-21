using System.Text.Json;

namespace Asgard.Loki.LLM.Azure.Internal;

/// <summary>单次原生 Responses 创建流的生命周期，未知非终态事件保留。</summary>
internal sealed class AzureResponseStreamState
{
    private string? _id;
    /// <summary>原生 completed/incomplete/failed/error 均结束本次创建，不合成成功。</summary>
    internal bool Terminal { get; private set; }
    /// <summary>检验原生响应身份和终态快照状态，不更改原始事件。</summary>
    internal void Observe(JsonElement data, string? eventName)
    {
        if (Terminal) throw new InvalidDataException("Azure Responses 在终态后追加事件。");
        var type = data.TryGetProperty("type", out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : eventName;
        if (string.IsNullOrEmpty(type)) throw new InvalidDataException("Azure Responses SSE 缺少原生事件类型。");
        var hasResponse = data.TryGetProperty("response", out var response) && response.ValueKind == JsonValueKind.Object;
        if (hasResponse && response.TryGetProperty("id", out var id))
        {
            if (id.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(id.GetString())) throw new InvalidDataException("Azure Responses 原生身份无效。");
            if (_id is not null && _id != id.GetString()) throw new InvalidDataException("Azure Responses 在创建流中切换响应身份。"); _id = id.GetString();
        }
        var status = type switch { "response.completed" => "completed", "response.incomplete" => "incomplete", "response.failed" => "failed", _ => null };
        if (status is not null)
        {
            if (!hasResponse || !response.TryGetProperty("id", out var nativeId) || nativeId.ValueKind != JsonValueKind.String || !response.TryGetProperty("status", out var nativeStatus) || nativeStatus.ValueKind != JsonValueKind.String || nativeStatus.GetString() != status) throw new InvalidDataException("Azure Responses 终态缺少一致的原生快照。");
            Terminal = true;
        }
        else if (type == "error") Terminal = true;
    }
    /// <summary>EOF 或 DONE 均不能补出原生终态。</summary>
    internal void Complete()
    {
        if (!Terminal) throw new InvalidDataException("Azure Responses 在原生终态之前截断。");
    }
}
