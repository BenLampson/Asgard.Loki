using System.Text.Json;
using Asgard.Loki.Image.Midjourney.Internal;

namespace Asgard.Loki.Image.Midjourney;

/// <summary>保留原生提交确认和绑定引用，提交/排队不是图片生成完成。</summary>
public sealed class MidjourneyImageSubmission : MidjourneyImageResponse
{
    internal MidjourneyImageSubmission(JsonElement data, MidjourneyImageHttpResponse http, MidjourneyImageTaskReference reference) : base(data, http)
    {
        Reference = reference;
        if (data.TryGetProperty("properties", out var properties) && properties.ValueKind == JsonValueKind.Object && properties.TryGetProperty("status", out _))
            Status = MidjourneyImageJson.RequiredText(properties, "status");
    }
    /// <summary>原始 result 任务 ID 与完整目标的关联。</summary>
    public MidjourneyImageTaskReference Reference { get; }
    /// <summary>仅原生 properties.status，未提供时为 null，不从 code 伪造。</summary>
    public string? Status { get; }
    /// <summary>仅明确的 SUCCESS、FAILURE、CANCEL 为终态，进度和排队码不能完成任务。</summary>
    public bool IsTerminal => Status is "SUCCESS" or "FAILURE" or "CANCEL";
}
