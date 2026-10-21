using System.Runtime.CompilerServices;

namespace Asgard.Loki.Image.Internal;

/// <summary>Image 自己的编辑入口实现，JSON/multipart 显式分开，不引入跨能力协调层。</summary>
internal sealed class ImageEditingClient(ImageConfiguration configuration)
{
    private readonly Dictionary<string, ImageEditRouteState> _routes = configuration.EditRoutes.ToDictionary(p => p.Key, p => new ImageEditRouteState(p.Value), StringComparer.Ordinal);
    /// <summary>独立 JSON 快照，派发一次完整编辑目标。</summary>
    internal Task<ImageGenerationResult> EditAsync(ImageEditRequest request, CancellationToken token)
    { var snapshot = ImageEditSnapshot.Create(request, token); return Route(snapshot.Model).Select().EditAsync(snapshot, token); }
    /// <summary>仅复制上传元数据，保持输入与遮罩流的身份/当前位置。</summary>
    internal Task<ImageGenerationResult> EditMultipartAsync(ImageMultipartEditRequest request, CancellationToken token)
    { var snapshot = ImageEditSnapshot.Create(request, token); return Route(snapshot.Model).Select().EditMultipartAsync(snapshot, token); }
    /// <summary>只有整个路由支持原生流式时才选择目标。</summary>
    internal IAsyncEnumerable<ImageEditEvent> StreamEditAsync(ImageEditRequest request, CancellationToken token)
    {
        var snapshot = ImageEditSnapshot.Create(request, token); var target = Streaming(snapshot.Model);
        return Completed(target.StreamEditAsync(snapshot, token), token);
    }
    /// <summary>multipart 同样要求完整目标集合支持流式，不能回退到 JSON 或重新上传。</summary>
    internal IAsyncEnumerable<ImageEditEvent> StreamMultipartEditAsync(ImageMultipartEditRequest request, CancellationToken token)
    {
        var snapshot = ImageEditSnapshot.Create(request, token); var target = Streaming(snapshot.Model);
        return Completed(target.StreamMultipartEditAsync(snapshot, token), token);
    }
    /// <summary>未知模型不借用生成目标或其他能力。</summary>
    private ImageEditRouteState Route(string model) => _routes.TryGetValue(model, out var route) ? route : throw new InvalidOperationException("图像编辑模型未配置。");
    /// <summary>功能检查在选择之前完成，不消耗被拒绝请求的路由序列。</summary>
    private IStreamingImageEditAdapter Streaming(string model)
    {
        var route = Route(model); if (!route.SupportsStreaming) throw new NotSupportedException("图像编辑路由的全部目标必须显式支持原生事件流。");
        return (IStreamingImageEditAdapter)route.Select();
    }
    /// <summary>仅暂存原始完成对象，源必须正常结束及释放；未知事件不会因名称被当作成功。</summary>
    private static async IAsyncEnumerable<ImageEditEvent> Completed(IAsyncEnumerable<ImageEditEvent> source, [EnumeratorCancellation] CancellationToken token)
    {
        ImageEditEvent? terminal = null;
        await foreach (var item in source.WithCancellation(token).ConfigureAwait(false))
        {
            token.ThrowIfCancellationRequested();
            if (item is null) throw new InvalidDataException("编辑目标返回空事件。");
            if (terminal is not null) throw new InvalidDataException("图像编辑完成后仍有数据事件。");
            if (item.IsCompleted) terminal = item; else yield return item;
        }
        token.ThrowIfCancellationRequested(); yield return terminal ?? throw new InvalidDataException("图像编辑流缺少原生成功完成项。");
    }
}
