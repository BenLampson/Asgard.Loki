namespace Asgard.Loki.Image.Internal;

/// <summary>注册时复制生成和编辑的完整目标集合；每个构建的客户端分别持有操作状态。</summary>
internal sealed class ImageConfiguration
{
    /// <summary>仅管理 Image 自己的配置，不读取目标之外的供应商/能力数据。</summary>
    internal ImageConfiguration(ImageOptions options)
    {
        ArgumentNullException.ThrowIfNull(options); ArgumentNullException.ThrowIfNull(options.Routes); ArgumentNullException.ThrowIfNull(options.EditRoutes);
        foreach (var route in options.Routes)
        {
            ArgumentNullException.ThrowIfNull(route); ArgumentException.ThrowIfNullOrWhiteSpace(route.Model); ArgumentNullException.ThrowIfNull(route.Targets);
            var targets = route.Targets.ToArray(); ArgumentOutOfRangeException.ThrowIfZero(targets.Length);
            foreach (var target in targets) ArgumentNullException.ThrowIfNull(target);
            if (!GenerationRoutes.TryAdd(route.Model, targets)) throw new ArgumentException("图像生成路由重复。", nameof(options));
        }
        foreach (var route in options.EditRoutes)
        {
            ArgumentNullException.ThrowIfNull(route); ArgumentException.ThrowIfNullOrWhiteSpace(route.Model); ArgumentNullException.ThrowIfNull(route.Targets);
            var targets = route.Targets.ToArray(); ArgumentOutOfRangeException.ThrowIfZero(targets.Length);
            foreach (var target in targets) { ArgumentNullException.ThrowIfNull(target); ArgumentException.ThrowIfNullOrWhiteSpace(target.Id); }
            if (!EditRoutes.TryAdd(route.Model, targets)) throw new ArgumentException("图像编辑路由重复。", nameof(options));
        }
        if (GenerationRoutes.Count == 0 && EditRoutes.Count == 0) throw new ArgumentOutOfRangeException(nameof(options), "至少配置一种图像操作。");
    }
    /// <summary>仅模块内部读取，不暴露给调用者修改。</summary>
    internal Dictionary<string, IImageGenerationAdapter[]> GenerationRoutes { get; } = new(StringComparer.Ordinal);
    /// <summary>仅编辑操作使用的目标集合。</summary>
    internal Dictionary<string, IImageEditAdapter[]> EditRoutes { get; } = new(StringComparer.Ordinal);
}
