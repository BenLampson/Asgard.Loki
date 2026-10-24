namespace Asgard.Loki.Image.Generation.Tests;

/// <summary>模拟正文完整读取后仍发生释放错误，验证不能提前交付成功。</summary>
internal sealed class FaultImageStream(byte[] bytes) : MemoryStream(bytes)
{
    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing) throw new IOException("fixture disposal failure");
    }
}
