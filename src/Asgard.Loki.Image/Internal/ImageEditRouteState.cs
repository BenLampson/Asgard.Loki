namespace Asgard.Loki.Image.Internal;

/// <summary>每个客户端/编辑模型独立的完整目标选择状态，不保存图像业务或会话。</summary>
internal sealed class ImageEditRouteState(IImageEditAdapter[] targets)
{
    private int _sequence = -1;
    /// <summary>原生事件流要求整个目标集合支持，不过滤再选择。</summary>
    internal bool SupportsStreaming => targets.All(target => target is IStreamingImageEditAdapter);
    /// <summary>单次选择完整目标；异常/取消不重试或切换。</summary>
    internal IImageEditAdapter Select() => targets[(int)((uint)Interlocked.Increment(ref _sequence) % (uint)targets.Length)];
}
