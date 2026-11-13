using System.Text.Json;
using System.Text.Json.Nodes;

namespace Asgard.Loki.Image.Zhipu;

/// <summary>Zhipu 图片协议处理器，可在多个完整上游间作为单例复用。</summary>
public sealed class ZhipuImageGenerationHandler : LokiImageGenerationHandler
{
    private static readonly IReadOnlyDictionary<string, string> Paths = new Dictionary<string, string>
    {
        [nameof(LokiImageGenerationRequest.Size)] = "size",
        [nameof(LokiImageGenerationRequest.Quality)] = "quality",
        [nameof(LokiImageGenerationRequest.Watermark)] = "watermark_enabled",
    };
    /// <inheritdoc />
    public override string Name => "zhipu-image-generation";
    /// <inheritdoc />
    protected override IReadOnlyDictionary<string, string> ParameterPaths => Paths;
    /// <inheritdoc />
    protected override IImageGenerationAdapter CreateAdapter(LokiImageInvocation invocation)
    {
        var provider = invocation.Provider;
        return new ZhipuImageAdapter(invocation.HttpClientFactory, new ZhipuImageOptions
        {
            Id = provider.Id, Model = provider.UpstreamModel, ApiKey = provider.ApiKey,
            Endpoint = provider.BaseAddress, HttpClientName = provider.HttpClientName,

        });
    }
}
