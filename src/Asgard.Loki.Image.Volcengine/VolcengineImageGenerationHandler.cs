using System.Text.Json;
using System.Text.Json.Nodes;

namespace Asgard.Loki.Image.Volcengine;

/// <summary>Volcengine 图片协议处理器，可在多个完整上游间作为单例复用。</summary>
public sealed class VolcengineImageGenerationHandler : LokiImageGenerationHandler
{
    private static readonly IReadOnlyDictionary<string, string> Paths = new Dictionary<string, string>
    {
        [nameof(LokiImageGenerationRequest.Count)] = "sequential_image_generation",
        [nameof(LokiImageGenerationRequest.Size)] = "size",
        [nameof(LokiImageGenerationRequest.OutputFormat)] = "output_format",
        [nameof(LokiImageGenerationRequest.Seed)] = "seed",
        [nameof(LokiImageGenerationRequest.GuidanceScale)] = "guidance_scale",
        [nameof(LokiImageGenerationRequest.Watermark)] = "watermark",
    };
    /// <inheritdoc />
    public override string Name => "volcengine-image-generation";
    /// <inheritdoc />
    protected override IReadOnlyDictionary<string, string> ParameterPaths => Paths;
    /// <inheritdoc />
    protected override IImageGenerationAdapter CreateAdapter(LokiImageInvocation invocation)
    {
        var provider = invocation.Provider;
        return new VolcengineImageAdapter(invocation.HttpClientFactory, new VolcengineImageOptions
        {
            Id = provider.Id, Model = provider.UpstreamModel, ApiKey = provider.ApiKey,
            BaseAddress = provider.BaseAddress, HttpClientName = provider.HttpClientName,

        });
    }
    /// <inheritdoc />
    public override Task<LokiImageGenerationResult> GenerateNativeAsync(LokiImageInvocation invocation, LokiNativeImageGenerationRequest request,
        CancellationToken cancellationToken = default)
        => WrapAsync(((VolcengineImageAdapter)CreateAdapter(invocation)).GenerateNativeAsync(new VolcengineNativeImageRequest
        { PublicModel = request.Model, Parameters = (JsonObject)request.Parameters.DeepClone() }, cancellationToken), cancellationToken);
    /// <summary>方舟 max_images 是上限，不能映射为精确数量；只可靠表达单图模式。</summary>
    protected override JsonNode? MapValue(LokiImageInvocation invocation, string parameter, JsonElement value)
    {
        if (parameter != nameof(LokiImageGenerationRequest.Count)) return base.MapValue(invocation, parameter, value);
        if (value.GetInt32() != 1) throw new LokiImageParameterException(invocation.Provider.Id,
            invocation.Provider.UpstreamModel, parameter, value.GetRawText(), "方舟组图最多数量不能转换为精确张数，请使用原生组图参数。", "1");
        return JsonValue.Create("disabled");
    }
}
