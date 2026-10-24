using System.Text.Json;
using System.Text.Json.Nodes;

namespace Asgard.Loki.Image.SiliconFlow;

/// <summary>SiliconFlow 图片协议处理器，可在多个完整上游间作为单例复用。</summary>
public sealed class SiliconFlowImageGenerationHandler : LokiImageGenerationHandler
{
    private static readonly IReadOnlyDictionary<string, string> Paths = new Dictionary<string, string>
    {
        [nameof(LokiImageGenerationRequest.Count)] = "batch_size",
        [nameof(LokiImageGenerationRequest.Size)] = "image_size",
        [nameof(LokiImageGenerationRequest.Seed)] = "seed",
        [nameof(LokiImageGenerationRequest.NegativePrompt)] = "negative_prompt",
        [nameof(LokiImageGenerationRequest.Steps)] = "num_inference_steps",
        [nameof(LokiImageGenerationRequest.GuidanceScale)] = "guidance_scale",
    };
    /// <inheritdoc />
    public override string Name => "siliconflow-image-generation";
    /// <inheritdoc />
    protected override IReadOnlyDictionary<string, string> ParameterPaths => Paths;
    /// <inheritdoc />
    protected override IImageGenerationAdapter CreateAdapter(LokiImageInvocation invocation)
    {
        var provider = invocation.Provider;
        return new SiliconFlowImageAdapter(invocation.HttpClientFactory, new SiliconFlowImageOptions
        {
            Id = provider.Id, Model = provider.UpstreamModel, ApiKey = provider.ApiKey,
            Endpoint = provider.BaseAddress, HttpClientName = provider.HttpClientName,

        });
    }
}
