namespace Asgard.Loki.Image.OpenAI.Tests;

/// <summary>响应内容自身释放失败，独立于源流异步释放和客户端释放故障。</summary>
internal sealed class EditDisposalContent(Stream stream) : StreamContent(stream)
{
    protected override void Dispose(bool disposing)
    { base.Dispose(disposing); throw new IOException("fixture response content disposal failure"); }
}
