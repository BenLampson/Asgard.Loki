using System.Text.Json;

namespace Asgard.Loki.Image.Azure.Tests;

/// <summary>测试观察原生字段与可选编辑完成标志，不推导生成调用成功或图片关联。</summary>
internal sealed record AzureImageStreamEvent(JsonElement Data, bool? Completed, ImageResultMetadata Metadata,
    string? EventName, string? EventId, long? RetryMilliseconds);
