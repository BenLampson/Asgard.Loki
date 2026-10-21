using System.Text.Json;

namespace Asgard.Loki.Image.Azure.Internal;

/// <summary>一条原生 JSON/SSE 身份及本次调用的完成位置，不重写原生事件。</summary>
internal sealed record AzureImageStreamFrame(JsonElement Data, string RawData, string? EventName, string? EventId,
    long? RetryMilliseconds, bool AfterCompletion, bool IsCompleted);
