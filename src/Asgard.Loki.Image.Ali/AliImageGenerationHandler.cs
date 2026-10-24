using System.Text.Json;
using System.Text.Json.Nodes;

namespace Asgard.Loki.Image.Ali;

/// <summary>Ali 图片协议处理器，可在多个完整上游间作为单例复用。</summary>
public class AliImageGenerationHandler : LokiImageGenerationHandler
{
    private static readonly IReadOnlyDictionary<string, string> Paths = new Dictionary<string, string>
    {
        [nameof(LokiImageGenerationRequest.Count)] = "parameters.n",
        [nameof(LokiImageGenerationRequest.Size)] = "parameters.size",
        [nameof(LokiImageGenerationRequest.Seed)] = "parameters.seed",
        [nameof(LokiImageGenerationRequest.NegativePrompt)] = "parameters.negative_prompt",
        [nameof(LokiImageGenerationRequest.Watermark)] = "parameters.watermark",
    };
    /// <inheritdoc />
    public override string Name => "ali-image-generation";
    /// <inheritdoc />
    protected override IReadOnlyDictionary<string, string> ParameterPaths => Paths;
    /// <inheritdoc />
    protected override IImageGenerationAdapter CreateAdapter(LokiImageInvocation invocation)
    {
        var provider = invocation.Provider;
        return new AliImageAdapter(invocation.HttpClientFactory, new AliImageOptions
        {
            Id = provider.Id, Model = provider.UpstreamModel, ApiKey = provider.ApiKey,
            BaseAddress = provider.BaseAddress, HttpClientName = provider.HttpClientName,

        });
    }
    /// <inheritdoc />
    public override Task<LokiImageGenerationResult> GenerateNativeAsync(LokiImageInvocation invocation, LokiNativeImageGenerationRequest request,
        CancellationToken cancellationToken = default)
        => WrapAsync(((AliImageAdapter)CreateAdapter(invocation)).GenerateNativeAsync(new AliNativeImageRequest
        { PublicModel = request.Model, Parameters = (JsonObject)request.Parameters.DeepClone() }, cancellationToken), cancellationToken);
}
