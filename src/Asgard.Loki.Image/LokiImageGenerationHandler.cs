using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.Image.Internal;

namespace Asgard.Loki.Image;

/// <summary>可靠字段映射和既有适配器桥接基类；不持有某个目标的请求或凭据。</summary>
public abstract class LokiImageGenerationHandler : ILokiImageGenerationHandler
{
    /// <inheritdoc />
    public abstract string Name { get; }
    /// <summary>由供应商声明支持的统一字段到原生字段路径的可靠映射。</summary>
    protected abstract IReadOnlyDictionary<string, string> ParameterPaths { get; }
    /// <summary>创建本次完整目标的既有原生适配器，不创建新的网络协议实现。</summary>
    protected abstract IImageGenerationAdapter CreateAdapter(LokiImageInvocation invocation);

    /// <inheritdoc />
    public virtual Task<LokiImageGenerationResult> GenerateAsync(LokiImageInvocation invocation, LokiImageGenerationRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(invocation);
        var mapped = MapRequest(invocation, request);
        return WrapAsync(CreateAdapter(invocation).GenerateAsync(mapped, cancellationToken), cancellationToken);
    }

    /// <summary>异步原生任务处理器可以复用相同的统一参数校验与映射。</summary>
    protected ImageGenerationRequest MapRequest(LokiImageInvocation invocation, LokiImageGenerationRequest request)
    {
        var snapshot = LokiImageSnapshots.Request(request);
        var parameters = (JsonObject)snapshot.NativeParameters.DeepClone();
        foreach (var (parameter, value) in LokiImageSnapshots.Values(snapshot))
        {
            if (!ParameterPaths.TryGetValue(parameter, out var path))
                throw Failure(invocation, parameter, value, "该 Handler 没有可靠的原生映射，请使用原生入口。");
            if (!invocation.Provider.Parameters.TryGetValue(parameter, out var rule))
                throw Failure(invocation, parameter, value, "目标未声明该统一参数的支持范围，请显式配置或使用原生入口。");
            ValidateRule(invocation, parameter, value, rule);
            Put(parameters, path, MapValue(invocation, parameter, value), invocation, parameter, value);
        }
        var native = new ImageGenerationRequest
        {
            Model = snapshot.Model, Prompt = snapshot.Prompt,
            Parameters = parameters.ToDictionary(field => field.Key, field => JsonSerializer.SerializeToElement(field.Value))
        };
        return native;
    }

    /// <inheritdoc />
    public virtual Task<LokiImageGenerationResult> GenerateNativeAsync(LokiImageInvocation invocation, LokiNativeImageGenerationRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = LokiImageSnapshots.Native(request);
        if (!snapshot.Parameters.TryGetPropertyValue("prompt", out var prompt)
            || prompt is not JsonValue value || !value.TryGetValue<string>(out var text) || string.IsNullOrWhiteSpace(text))
            throw new ArgumentException("本原生生成协议要求字符串 prompt；无提示词操作请使用对应供应商原生入口。", nameof(request));
        snapshot.Parameters.Remove("prompt");
        return WrapAsync(CreateAdapter(invocation).GenerateAsync(new ImageGenerationRequest
        {
            Model = snapshot.Model, Prompt = text,
            Parameters = snapshot.Parameters.ToDictionary(field => field.Key, field => JsonSerializer.SerializeToElement(field.Value))
        }, cancellationToken), cancellationToken);
    }

    /// <summary>既有原生适配器完成所有传输校验后创建统一视图。</summary>
    protected static async Task<LokiImageGenerationResult> WrapAsync(Task<ImageGenerationResult> operation, CancellationToken token)
    {
        var result = await operation.ConfigureAwait(false); token.ThrowIfCancellationRequested(); return new(result);
    }

    /// <summary>仅在语义可靠时改变原生值载体；默认保留统一值。</summary>
    protected virtual JsonNode? MapValue(LokiImageInvocation invocation, string parameter, JsonElement value)
        => JsonNode.Parse(value.GetRawText());

    /// <summary>写入可靠字段路径，拒绝统一字段与原生扩展的重叠和大小写别名。</summary>
    protected static void Put(JsonObject root, string path, JsonNode? value, LokiImageInvocation invocation, string parameter, JsonElement requested)
    {
        var parts = path.Split('.');
        var current = root;
        for (var index = 0; index < parts.Length; index++)
        {
            var part = parts[index];
            var existing = current.FirstOrDefault(field => field.Key.Equals(part, StringComparison.OrdinalIgnoreCase));
            if (existing.Key is not null)
            {
                if (existing.Key != part || index == parts.Length - 1 || existing.Value is not JsonObject)
                    throw Failure(invocation, parameter, requested, $"统一参数与原生字段 {path} 冲突。");
            }
            if (index == parts.Length - 1) { current.Add(part, value); return; }
            if (current[part] is not JsonObject child) { child = new JsonObject(); current.Add(part, child); }
            current = child;
        }
    }

    private static void ValidateRule(LokiImageInvocation invocation, string parameter, JsonElement value, LokiImageParameterRule rule)
    {
        var enumeration = rule.AllowedValues.Count == 0 || rule.AllowedValues.Any(allowed => JsonElement.DeepEquals(allowed, value));
        var range = WithinRange(value, rule);
        if (!enumeration || !range)
        {
            var support = string.Join(", ", rule.AllowedValues.Select(item => item.GetRawText()));
            if (rule.Minimum is not null || rule.Maximum is not null)
                support += FormattableString.Invariant($" [{rule.Minimum}, {rule.Maximum}]");
            throw Failure(invocation, parameter, value, "参数值不在目标声明的支持范围内。", support);
        }
    }

    /// <summary>可精确表示的 JSON 数值先用 decimal 比较，避免大整数先转 double 丢失边界。</summary>
    private static bool WithinRange(JsonElement value, LokiImageParameterRule rule)
    {
        if (rule.Minimum is null && rule.Maximum is null) return true;
        if (value.ValueKind != JsonValueKind.Number) return false;
        var minimum = JsonSerializer.SerializeToElement(rule.Minimum);
        var maximum = JsonSerializer.SerializeToElement(rule.Maximum);
        if (value.TryGetDecimal(out var precise)
            && (rule.Minimum is null || minimum.TryGetDecimal(out _))
            && (rule.Maximum is null || maximum.TryGetDecimal(out _)))
            return (rule.Minimum is null || precise >= minimum.GetDecimal())
                && (rule.Maximum is null || precise <= maximum.GetDecimal());
        return value.TryGetDouble(out var number)
            && (rule.Minimum is null || number >= rule.Minimum) && (rule.Maximum is null || number <= rule.Maximum);
    }

    private static LokiImageParameterException Failure(LokiImageInvocation invocation, string parameter, JsonElement value,
        string reason, string? supported = null)
        => new(invocation.Provider.Id, invocation.Provider.UpstreamModel, parameter, value.GetRawText(), reason, supported);
}
