namespace Asgard.Loki.Image.Azure;

/// <summary>Azure 部署式图片处理器；部署使用实际模型字段，版本和可选正文模型显式设置。</summary>
public sealed class AzureImageGenerationHandler : LokiImageGenerationHandler
{
    private static readonly IReadOnlyDictionary<string, string> Paths = new Dictionary<string, string>
    {
        [nameof(LokiImageGenerationRequest.Count)] = "n",
        [nameof(LokiImageGenerationRequest.Size)] = "size",
        [nameof(LokiImageGenerationRequest.Quality)] = "quality",
        [nameof(LokiImageGenerationRequest.OutputFormat)] = "output_format",
        [nameof(LokiImageGenerationRequest.Background)] = "background"
    };
    /// <inheritdoc />
    public override string Name => "azure-image-generation";
    /// <inheritdoc />
    protected override IReadOnlyDictionary<string, string> ParameterPaths => Paths;
    /// <inheritdoc />
    protected override IImageGenerationAdapter CreateAdapter(LokiImageInvocation invocation)
    {
        var provider = invocation.Provider;
        return new AzureImageAdapter(invocation.HttpClientFactory, new()
        {
            Id = provider.Id, Deployment = provider.UpstreamModel, Endpoint = provider.BaseAddress, ApiKey = provider.ApiKey,
            ApiVersion = provider.Settings["api_version"]?.GetValue<string>()
                ?? throw new ArgumentException("Azure 图片目标需显式设置 api_version。"),
            BodyModel = provider.Settings["body_model"]?.GetValue<string>(), HttpClientName = provider.HttpClientName
        });
    }
}
