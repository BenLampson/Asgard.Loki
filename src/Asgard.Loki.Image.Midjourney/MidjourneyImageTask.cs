using System.Text.Json;
using Asgard.Loki.Image.Midjourney.Internal;

namespace Asgard.Loki.Image.Midjourney;

/// <summary>单次原生任务快照，保留图片或文本结果，不发明生成结果、时间或状态。</summary>
public sealed class MidjourneyImageTask : MidjourneyImageResponse
{
    internal MidjourneyImageTask(JsonElement data, MidjourneyImageHttpResponse http, MidjourneyImageTaskReference reference) : base(data, http)
    {
        Reference = reference; Status = MidjourneyImageJson.RequiredText(data, "status");
        ImageUrl = MidjourneyImageJson.OptionalText(data, "imageUrl");
        var reason = MidjourneyImageJson.OptionalText(data, "failReason");
        if (Status == "SUCCESS" && !string.IsNullOrWhiteSpace(reason)) throw new InvalidDataException("代理成功图片任务包含矛盾的失败原因。");
        if (data.TryGetProperty("action", out var action) && action.ValueKind == JsonValueKind.String && action.GetString() == "VIDEO")
            throw new InvalidDataException("图片任务查询返回了视频任务。");
    }
    /// <summary>原始任务身份和完整目标关联。</summary>
    public MidjourneyImageTaskReference Reference { get; }
    /// <summary>原生任务状态，未知状态不猜测为成功。</summary>
    public string Status { get; }
    /// <summary>仅明确 SUCCESS、FAILURE、CANCEL 为终态；progress=100% 本身不代表完成。</summary>
    public bool IsTerminal => Status is "SUCCESS" or "FAILURE" or "CANCEL";
    /// <summary>原生图片引用，可能为输入、处理中预览或最终结果；不下载、不替换为生成载荷。</summary>
    public string? ImageUrl { get; }
}
