using System.Text.Json;

namespace Asgard.Loki.Image.Generation.Tests;

/// <summary>应用扩展点的离线处理器，可返回直接二进制或故意错误的身份以验证客户端边界。</summary>
internal sealed class CustomImageHandler : ILokiImageGenerationHandler
{
    internal string? WrongProviderId { get; init; }
    internal LokiImageGenerationState? State { get; init; }
    internal bool ReturnTaskId { get; init; }
    internal bool NoImages { get; init; }
    internal bool FailImage { get; init; }
    internal bool MutateTarget { get; init; }
    public string Name => "fixture-custom";
    public Task<LokiImageGenerationResult> GenerateAsync(LokiImageInvocation invocation, LokiImageGenerationRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (MutateTarget) invocation.Provider.Settings["changed"] = true;
        var data = JsonSerializer.SerializeToElement(new { error = FailImage ? "fixture-failure" : (string?)null });
        var image = new GeneratedImage(data, null, null)
        { Content = FailImage ? null : new(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, "image/png") };
        var native = new ImageGenerationResult(JsonSerializer.SerializeToElement(new { custom = true }),
            new(request.Model, invocation.Provider.UpstreamModel, WrongProviderId ?? invocation.Provider.Id),
            NoImages ? [] : [image]);
        return Task.FromResult(new LokiImageGenerationResult(native, State, ReturnTaskId ? "fixture-task" : null));
    }
    public Task<LokiImageGenerationResult> GenerateNativeAsync(LokiImageInvocation invocation, LokiNativeImageGenerationRequest request,
        CancellationToken cancellationToken = default)
        => GenerateAsync(invocation, new() { Model = request.Model, Prompt = "native-fixture" }, cancellationToken);
}
