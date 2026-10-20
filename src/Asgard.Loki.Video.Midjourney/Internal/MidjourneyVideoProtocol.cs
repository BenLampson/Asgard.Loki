using System.Text.Json;

namespace Asgard.Loki.Video.Midjourney.Internal;

/// <summary>只解释代理视频原生状态和输出载体，不生成媒体 URL 或复用图片结果。</summary>
internal static class MidjourneyVideoProtocol
{
    /// <summary>只映射已知原生状态，弹窗和未来状态不作为自动完成。</summary>
    internal static VideoTaskState State(string status) => status switch
    {
        "NOT_START" => VideoTaskState.Queued, "SUBMITTED" => VideoTaskState.Submitted, "IN_PROGRESS" => VideoTaskState.Running,
        "FAILURE" => VideoTaskState.Failed, "SUCCESS" => VideoTaskState.Succeeded, "CANCEL" => VideoTaskState.Cancelled, _ => VideoTaskState.Unknown
    };
    /// <summary>成功必须有完整原生视频载体，所有列表项均验证，不修复或丢弃字段。</summary>
    internal static void ValidateVideoResult(JsonElement native, bool legacyVideoContext)
    {
        if (!string.IsNullOrWhiteSpace(OptionalText(native, "failReason"))) throw new InvalidDataException("代理成功视频包含矛盾的失败原因。");
        var hasVideo = !string.IsNullOrWhiteSpace(OptionalText(native, "videoUrl"));
        if (native.TryGetProperty("videoUrls", out var videos) && videos.ValueKind != JsonValueKind.Null)
        {
            if (videos.ValueKind != JsonValueKind.Array) throw new InvalidDataException("代理 videoUrls 必须是原生列表或 null。");
            foreach (var video in videos.EnumerateArray())
            {
                if (video.ValueKind != JsonValueKind.Object) throw new InvalidDataException("代理视频列表项必须是原生对象。");
                _ = MidjourneyVideoJson.RequiredText(video, "url"); hasVideo = true;
            }
        }
        var legacy = OptionalText(native, "imageUrl");
        if (!hasVideo && !(legacyVideoContext && !string.IsNullOrWhiteSpace(legacy))) throw new InvalidDataException("代理成功任务缺少原生视频结果。");
    }
    /// <summary>允许原生可选空值，不将数字或其他类型强制转换为链接。</summary>
    private static string? OptionalText(JsonElement native, string name)
    {
        if (!native.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        return value.ValueKind == JsonValueKind.String ? value.GetString() : throw new InvalidDataException("代理视频可选字段必须是原始文本或 null：" + name);
    }
}
