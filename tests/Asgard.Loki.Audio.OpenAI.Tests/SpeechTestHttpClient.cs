namespace Asgard.Loki.Audio.OpenAI.Tests;

/// <summary>独立观察客户端的释放，响应流的释放不能代替此断言。</summary>
internal sealed class SpeechTestHttpClient(HttpMessageHandler handler) : HttpClient(handler, disposeHandler: false)
{
    internal bool Disposed { get; private set; }
    protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
}
