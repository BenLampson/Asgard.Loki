using System.Net.Http.Headers;
using Asgard.Loki.Image.Replicate.Internal;

namespace Asgard.Loki.Image.Replicate;

/// <summary>Replicate 原生模型图片预测和显式输入上传，不增加任务调度、文件管理或媒体下载。</summary>
public sealed class ReplicateImageAdapter : IImageGenerationAdapter, IImageEditAdapter
{
    private readonly ReplicateImageHttp _http;
    /// <summary>绑定固定 owner/name、地址、凭据及原生实现，借用工厂。</summary>
    public ReplicateImageAdapter(IHttpClientFactory factory, ReplicateImageOptions options) => _http = new(factory, options);
    /// <inheritdoc />
    public string Id => _http.Options.Id;
    /// <summary>公共完整生成入口，扩展字段属于原生 input；尚未完成/失败不能当作生成成功。</summary>
    public Task<ImageGenerationResult> GenerateAsync(ImageGenerationRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested(); ArgumentNullException.ThrowIfNull(request);
        return CompleteAsync(PredictAsync(ReplicateImagePayload.Bridge(request.Model, request.Prompt, request.Parameters, false), cancellationToken), cancellationToken);
    }
    /// <summary>JSON 编辑保留原生 image_prompt 引用及输入参数，不上传或下载引用。</summary>
    public Task<ImageGenerationResult> EditAsync(ImageEditRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested(); ArgumentNullException.ThrowIfNull(request);
        return CompleteAsync(PredictAsync(ReplicateImagePayload.Bridge(request.Model, request.Prompt, request.Parameters, true), cancellationToken), cancellationToken);
    }
    /// <summary>直接在本目标执行一次原生预测并返回任何原生状态，不轮询、重试或消费返回的 URL。</summary>
    public Task<ReplicateImagePrediction> PredictAsync(ReplicateNativeImageRequest request, CancellationToken cancellationToken = default)
        => PredictCoreAsync(ReplicateImagePayload.Prepare(request, _http.Options, cancellationToken), request.PublicModel, cancellationToken);
    /// <summary>显式上传一次图像模型输入并保留完整确认；调用者决定何时把 Url 放入原生 input。</summary>
    public async Task<ReplicateImageInput> UploadImageInputAsync(ImageUpload image, CancellationToken cancellationToken = default)
    {
        var response = await _http.UploadAsync(image, cancellationToken).ConfigureAwait(false); var data = ReplicateImageJson.Parse(response.Bytes);
        if (data.TryGetProperty("detail", out _) || data.TryGetProperty("error", out var error) && error.ValueKind != System.Text.Json.JsonValueKind.Null)
            throw new ImageUpstreamException("Replicate 模型输入上传返回原生 API 错误。", response.Status, response.Bytes, response.Type);
        var url = ReplicateImageJson.UploadUrl(data); cancellationToken.ThrowIfCancellationRequested(); return new(data, url);
    }
    /// <summary>预测入口返回完整 JSON，流 URL 不会被转换为 Image SSE。</summary>
    public IAsyncEnumerable<ImageGenerationEvent> StreamAsync(ImageGenerationRequest request, CancellationToken cancellationToken = default)
    { cancellationToken.ThrowIfCancellationRequested(); throw new NotSupportedException("此 Replicate 图片预测不消费原生输出 stream URL。"); }
    /// <summary>原生编辑提交为 JSON；先显式上传输入再提供原生 image_prompt，不将公共文本字段猜测为 JSON 类型。</summary>
    public Task<ImageGenerationResult> EditMultipartAsync(ImageMultipartEditRequest request, CancellationToken cancellationToken = default)
    { cancellationToken.ThrowIfCancellationRequested(); throw new NotSupportedException("Replicate 原生预测使用 JSON，请先 UploadImageInputAsync 再明确指定 image_prompt。"); }
    /// <summary>HTTP 完整资源边界之后构建原生快照，错误正文不会被空图片掩盖。</summary>
    private async Task<ReplicateImagePrediction> PredictCoreAsync(byte[] body, string publicModel, CancellationToken token)
    {
        var content = new ByteArrayContent(body); content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        var response = await _http.PostAsync(_http.Predictions, content, true, token).ConfigureAwait(false);
        var data = ReplicateImageJson.Parse(response.Bytes);
        if (!data.TryGetProperty("id", out _) && data.TryGetProperty("detail", out _)) throw new ImageUpstreamException("Replicate 返回原生 API 错误。", response.Status, response.Bytes, response.Type);
        var prediction = new ReplicateImagePrediction(data, new(publicModel, _http.Options.Model, Id), response.Bytes, response.Status, response.Type);
        token.ThrowIfCancellationRequested(); return prediction;
    }
    private static async Task<ImageGenerationResult> CompleteAsync(Task<ReplicateImagePrediction> operation, CancellationToken token)
    { var prediction = await operation.ConfigureAwait(false); token.ThrowIfCancellationRequested(); return prediction.AsCompletedResult(); }
}
