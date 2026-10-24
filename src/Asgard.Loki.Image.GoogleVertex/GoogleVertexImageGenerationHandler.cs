using System.Text.Json.Nodes;

namespace Asgard.Loki.Image.GoogleVertex;

/// <summary>Vertex API Key 图片处理器，完整模型资源和服务器明确来自目标；令牌入口保留原生适配器。</summary>
public sealed class GoogleVertexImageGenerationHandler : LokiImageGenerationHandler
{
    private static readonly IReadOnlyDictionary<string, string> Paths = new Dictionary<string, string>
    {
        [nameof(LokiImageGenerationRequest.AspectRatio)] = "generationConfig.imageConfig.aspectRatio"
    };
    /// <inheritdoc />
    public override string Name => "google-vertex-image-generation";
    /// <inheritdoc />
    protected override IReadOnlyDictionary<string, string> ParameterPaths => Paths;
    /// <inheritdoc />
    protected override IImageGenerationAdapter CreateAdapter(LokiImageInvocation invocation)
    {
        var provider = invocation.Provider;
        return new GoogleVertexImageAdapter(invocation.HttpClientFactory, new()
        {
            Id = provider.Id, ModelResource = provider.UpstreamModel, ApiKey = provider.ApiKey,
            BaseAddress = provider.BaseAddress, HttpClientName = provider.HttpClientName,
            QuotaProjectId = provider.Settings["quota_project"]?.GetValue<string>()
        });
    }
    /// <inheritdoc />
    public override Task<LokiImageGenerationResult> GenerateNativeAsync(LokiImageInvocation invocation, LokiNativeImageGenerationRequest request,
        CancellationToken cancellationToken = default)
        => WrapAsync(((GoogleVertexImageAdapter)CreateAdapter(invocation)).GenerateNativeAsync(new GoogleVertexNativeImageRequest
        { PublicModel = request.Model, Parameters = (JsonObject)request.Parameters.DeepClone() }, cancellationToken), cancellationToken);
}
