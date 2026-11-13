namespace Asgard.Loki.Image;

/// <summary>显式像素宽高，不对图片进行缩放或裁剪。</summary>
public sealed record LokiImageSize(int Width, int Height)
{
    /// <summary>以不受区域影响的宽高形式表示尺寸。</summary>
    public override string ToString() => FormattableString.Invariant($"{Width}x{Height}");
}
