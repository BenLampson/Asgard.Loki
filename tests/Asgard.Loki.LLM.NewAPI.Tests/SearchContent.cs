namespace Asgard.Loki.LLM.NewAPI.Tests;

/// <summary>明确模拟响应内容释放失败或取消，不依赖真实网络。</summary>
internal sealed class SearchContent(Stream body) : StreamContent(body)
{
    internal Action? OnDispose { get; init; }
    internal bool FailDispose { get; init; }
    internal bool Disposed { get; private set; }
    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    { base.Dispose(disposing); if (disposing && !Disposed) { Disposed = true; OnDispose?.Invoke(); if (FailDispose) throw new IOException("response content close"); } }
}
