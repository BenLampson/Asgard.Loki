using System.Text;
using System.Text.Json;
using System.Runtime.CompilerServices;
using Asgard.Loki.Infrastructure;

namespace Asgard.Loki.Image.Ali.Internal;

/// <summary>原生图文事件逐帧透传；finished 不表示结束，usage 不相加。</summary>
internal static class AliImageSse
{
    internal static async IAsyncEnumerable<ImageGenerationEvent> ReadAsync(Stream source, ImageResultMetadata metadata, int status,
        string? contentType, [EnumeratorCancellation] CancellationToken token)
    {
        var choices = new Dictionary<int, bool>(); string? requestId = null;
        await foreach (var frame in SseReader.ReadAsync(source, token, strictUtf8: true).ConfigureAwait(false))
        {
            var bytes = Encoding.UTF8.GetBytes(frame.Data); var root = AliImageJson.Parse(bytes);
            var result = new ImageGenerationEvent(root, metadata, frame.EventName, frame.EventId, frame.RetryMilliseconds);
            if (AliImageJson.Error(root) || frame.EventName == "error")
                throw new ImageUpstreamException("阿里图片原生 SSE 失败。", status, bytes, contentType) { GenerationEvent = result };
            if (root.TryGetProperty("request_id", out var id) && id.ValueKind != JsonValueKind.Null)
            {
                if (id.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(id.GetString())) throw new InvalidDataException("阿里流 request_id 无效。");
                if (requestId is not null && requestId != id.GetString()) throw new InvalidDataException("阿里流跨请求关联。");
                requestId = id.GetString();
            }
            if (root.TryGetProperty("output", out var output))
            {
                if (output.ValueKind != JsonValueKind.Object) throw new InvalidDataException("阿里原生流 output 必须为对象。");
                if (output.TryGetProperty("choices", out var items) && items.ValueKind != JsonValueKind.Null)
                {
                    if (items.ValueKind != JsonValueKind.Array) throw new InvalidDataException("阿里原生 choices 必须为数组。");
                    var position = 0; var seen = new HashSet<int>();
                    foreach (var choice in items.EnumerateArray())
                    {
                        if (choice.ValueKind != JsonValueKind.Object) throw new InvalidDataException("阿里 choice 必须为对象。");
                        var hasIndex = choice.TryGetProperty("index", out var index); var key = position++;
                        if (hasIndex && (index.ValueKind != JsonValueKind.Number || !index.TryGetInt32(out key) || key < 0)) throw new InvalidDataException("阿里原生 choice index 无效。");
                        if (!seen.Add(key)) throw new InvalidDataException("同帧阿里 choice index 重复。");
                        choices.TryAdd(key, false);
                        if (choice.TryGetProperty("finish_reason", out var reason) && reason.ValueKind != JsonValueKind.Null)
                        {
                            if (reason.ValueKind != JsonValueKind.String) throw new InvalidDataException("阿里 finish_reason 必须为字符串或 null。");
                            var text = reason.GetString();
                            if (!string.IsNullOrEmpty(text) && text != "null")
                            {
                                if (text != "stop") throw new InvalidDataException("阿里图文流没有成功 stop 终态。");
                                choices[key] = true;
                            }
                        }
                    }
                }
            }
            yield return result;
        }
        if (choices.Count == 0 || choices.Values.Any(stopped => !stopped)) throw new InvalidDataException("阿里图文流缺少各 choice 的原生 stop 终态。");
    }
    internal static bool HasStop(ImageGenerationEvent value)
        => value.Data.TryGetProperty("output", out var output) && output.ValueKind == JsonValueKind.Object
            && output.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array
            && choices.EnumerateArray().Any(choice => choice.TryGetProperty("finish_reason", out var reason) && reason.ValueKind == JsonValueKind.String && reason.GetString() == "stop");
}
