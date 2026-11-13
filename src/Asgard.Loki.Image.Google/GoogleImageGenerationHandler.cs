using System.Text.Json;
using System.Text.Json.Nodes;

namespace Asgard.Loki.Image.Google;

/// <summary>Google 图片协议处理器，可在多个完整上游间作为单例复用。</summary>
public sealed class GoogleImageGenerationHandler : LokiImageGenerationHandler
{
    private static readonly IReadOnlyDictionary<string, string> Paths = new Dictionary<string, string>
    {
        [nameof(LokiImageGenerationRequest.AspectRatio)] = "generationConfig.imageConfig.aspectRatio",
    };
    /// <inheritdoc />
    public override string Name => "google-image-generation";
    /// <inheritdoc />
    protected override IReadOnlyDictionary<string, string> ParameterPaths => Paths;
    /// <inheritdoc />
    protected override IImageGenerationAdapter CreateAdapter(LokiImageInvocation invocation)
    {
        var provider = invocation.Provider;
        return new GoogleImageAdapter(invocation.HttpClientFactory, new GoogleImageOptions
        {
            Id = provider.Id, Model = provider.UpstreamModel, ApiKey = provider.ApiKey,
            BaseAddress = provider.BaseAddress, HttpClientName = provider.HttpClientName,

        });
    }
    /// <inheritdoc />
    public override Task<LokiImageGenerationResult> GenerateNativeAsync(LokiImageInvocation invocation, LokiNativeImageGenerationRequest request,
        CancellationToken cancellationToken = default)
        => WrapAsync(((GoogleImageAdapter)CreateAdapter(invocation)).GenerateNativeAsync(new GoogleNativeImageRequest
        { PublicModel = request.Model, Parameters = (JsonObject)request.Parameters.DeepClone() }, cancellationToken), cancellationToken);
}
