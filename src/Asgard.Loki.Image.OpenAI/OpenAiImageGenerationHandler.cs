using System.Text.Json;
using System.Text.Json.Nodes;

namespace Asgard.Loki.Image.OpenAI;

/// <summary>OpenAI 图片协议处理器，可在多个完整上游间作为单例复用。</summary>
public sealed class OpenAiImageGenerationHandler : LokiImageGenerationHandler
{
    private static readonly IReadOnlyDictionary<string, string> Paths = new Dictionary<string, string>
    {
        [nameof(LokiImageGenerationRequest.Count)] = "n",
        [nameof(LokiImageGenerationRequest.Size)] = "size",
        [nameof(LokiImageGenerationRequest.Quality)] = "quality",
        [nameof(LokiImageGenerationRequest.OutputFormat)] = "output_format",
        [nameof(LokiImageGenerationRequest.Background)] = "background",
    };
    /// <inheritdoc />
    public override string Name => "openai-image-generation";
    /// <inheritdoc />
    protected override IReadOnlyDictionary<string, string> ParameterPaths => Paths;
    /// <inheritdoc />
    protected override IImageGenerationAdapter CreateAdapter(LokiImageInvocation invocation)
    {
        var provider = invocation.Provider;
        return new OpenAiImageAdapter(invocation.HttpClientFactory, new OpenAiImageOptions
        {
            Id = provider.Id, Model = provider.UpstreamModel, ApiKey = provider.ApiKey,
            BaseAddress = provider.BaseAddress, HttpClientName = provider.HttpClientName,
            Organization = provider.Settings["organization"]?.GetValue<string>(),
            Project = provider.Settings["project"]?.GetValue<string>(),
        });
    }
}
