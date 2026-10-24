using System.Text.Json;
using System.Text.Json.Nodes;

namespace Asgard.Loki.Image.MiniMax;

/// <summary>MiniMax 图片协议处理器，可在多个完整上游间作为单例复用。</summary>
public sealed class MiniMaxImageGenerationHandler : LokiImageGenerationHandler
{
    private static readonly IReadOnlyDictionary<string, string> Paths = new Dictionary<string, string>
    {
        [nameof(LokiImageGenerationRequest.Count)] = "n",
        [nameof(LokiImageGenerationRequest.AspectRatio)] = "aspect_ratio",
        [nameof(LokiImageGenerationRequest.Watermark)] = "aigc_watermark",
    };
    /// <inheritdoc />
    public override string Name => "minimax-image-generation";
    /// <inheritdoc />
    protected override IReadOnlyDictionary<string, string> ParameterPaths => Paths;
    /// <inheritdoc />
    protected override IImageGenerationAdapter CreateAdapter(LokiImageInvocation invocation)
    {
        var provider = invocation.Provider;
        return new MiniMaxImageAdapter(invocation.HttpClientFactory, new MiniMaxImageOptions
        {
            Id = provider.Id, Model = provider.UpstreamModel, ApiKey = provider.ApiKey,
            Endpoint = provider.BaseAddress, HttpClientName = provider.HttpClientName,

        });
    }
}
