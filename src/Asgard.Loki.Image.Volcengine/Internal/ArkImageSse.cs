using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Asgard.Loki.Infrastructure;

namespace Asgard.Loki.Image.Volcengine.Internal;

/// <summary>方舟单图/组图原生终态，保留图片级失败，不要求汇总携带图片或对 max_images 作数量承诺。</summary>
internal static class ArkImageSse
{
    /// <summary>单图允许原生图片事件后正常 EOF；组图等待原生汇总或明确终止的内部服务错误。</summary>
    internal static async IAsyncEnumerable<ArkImageFrame> ReadAsync(Stream source, bool grouped, Func<ArkImageFrame, Exception> failure,
        [EnumeratorCancellation] CancellationToken token)
    {
        var indices = new HashSet<int>(); var completed = false; var done = false; var stoppedByError = false;
        await foreach (var frame in SseReader.ReadAsync(source, token, strictUtf8: true).ConfigureAwait(false))
        {
            token.ThrowIfCancellationRequested();
            if (frame.Data.Trim() == "[DONE]")
            {
                if (done || !(completed || !grouped && indices.Count == 1 || stoppedByError)) throw new InvalidDataException("方舟 DONE 不能替代原生结果或重复结束。");
                done = true; continue;
            }
            var data = ArkImageJson.Parse(Encoding.UTF8.GetBytes(frame.Data));
            var name = data.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String ? type.GetString() : null;
            var partialFailed = name == "image_generation.partial_failed";
            if (frame.EventName == "error" || name == "error" || !partialFailed && data.TryGetProperty("error", out var overallError) && overallError.ValueKind != JsonValueKind.Null)
                throw failure(new(data, frame.Data, frame.EventName, frame.EventId, frame.RetryMilliseconds, false));
            if (string.IsNullOrWhiteSpace(name)) throw new InvalidDataException("方舟图片 SSE 缺少原生 type。");
            if (frame.EventName is { Length: > 0 } eventName && eventName != "message" && eventName != name) throw new InvalidDataException("方舟 SSE event/type 冲突。");
            if (completed || done) throw new InvalidDataException("方舟原生汇总/结束之后仍有数据事件。");
            var partialSucceeded = name == "image_generation.partial_succeeded";
            if (partialSucceeded || partialFailed)
            {
                if (!data.TryGetProperty("image_index", out var index) || index.ValueKind != JsonValueKind.Number || !index.TryGetInt32(out var number) || number < 0 || !indices.Add(number))
                    throw new InvalidDataException("方舟原生图片索引无效或重复。");
                if (!grouped && indices.Count > 1) throw new InvalidDataException("方舟单图模式返回多条图片结果。");
                if (partialFailed)
                {
                    if (!data.TryGetProperty("error", out var error)) throw new InvalidDataException("方舟图片失败事件缺少原生错误。");
                    ArkImageJson.Error(error); stoppedByError |= error.GetProperty("code").GetString() == "InternalServiceError";
                }
                else ArkImageJson.Image(data);
            }
            if (name == "image_generation.completed") completed = true;
            // 原生单张结果与汇总分别决定延迟窗口，不改造/拼接原始事件。
            yield return new(data, frame.Data, frame.EventName, frame.EventId, frame.RetryMilliseconds, completed || !grouped && indices.Count > 0);
        }
        token.ThrowIfCancellationRequested();
        if (!(completed || !grouped && indices.Count == 1 || stoppedByError)) throw new InvalidDataException("方舟图片流缺少原生结束证据。");
    }
}
