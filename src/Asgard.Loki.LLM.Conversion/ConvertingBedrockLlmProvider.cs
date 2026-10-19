using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.LLM.Conversion.Internal;

namespace Asgard.Loki.LLM.Conversion;

/// <summary>固定 Nova/Converse 上游的显式 JSON/逻辑事件装饰器，不冒充四协议供应商或生成二进制帧。</summary>
public sealed partial class ConvertingBedrockLlmProvider : IBedrockConverseAdapter, IBedrockNovaAdapter, ILlmCredentialSupport
{
    private readonly ILlmProvider _upstream;
    private readonly LlmProtocol _protocol;
    private readonly LlmConversionOptions _options;

    /// <summary>显式固定原生上游和格式策略；借用供应商和认证源，不创建供应商间依赖。</summary>
    public ConvertingBedrockLlmProvider(ILlmProvider upstream, LlmProtocol upstreamProtocol, string id, LlmConversionOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(upstream); ArgumentException.ThrowIfNullOrWhiteSpace(id);
        if (!BedrockConversion.IsBedrock(upstreamProtocol)) throw new ArgumentOutOfRangeException(nameof(upstreamProtocol));
        if (upstream is ConvertingLlmProvider or ConvertingBedrockLlmProvider) throw new ArgumentException("不能嵌套转换供应商。", nameof(upstream));
        if (upstreamProtocol == LlmProtocol.BedrockNova && upstream is not IBedrockNovaAdapter
            || upstreamProtocol == LlmProtocol.BedrockConverse && upstream is not IBedrockConverseAdapter)
            throw new ArgumentException("供应商未实现明确选择的原生协议。", nameof(upstream));
        _options = ConversionOptionsSnapshot.Capture(options); LlmProtocolConverter.Validate(new(), upstreamProtocol, upstreamProtocol, _options);
        _upstream = upstream; _protocol = upstreamProtocol; Id = id;
    }

    /// <inheritdoc />
    public string Id { get; }
    /// <summary>绑定域独立于原有四协议 v25；格式/位置选项和完整原生上游均参与绑定。</summary>
    public string BindingIdentity => $"{GetType().AssemblyQualifiedName}:bedrock-json-conversion-v1:{_upstream.Id}:{_upstream.BindingIdentity}:{_protocol}:{JsonSerializer.Serialize(_options)}";
    /// <summary>认证能力完全委托明确上游，不提供降级、身份获取或自动重试。</summary>
    public bool SupportsCredential(LlmCredentialKind kind) => _upstream is ILlmCredentialSupport declared
        ? declared.SupportsCredential(kind) : kind == LlmCredentialKind.ApiKey;

    /// <inheritdoc />
    public async Task<BedrockConverseResponse> ConverseAsync(JsonObject request, LlmAdapterContext context, CancellationToken cancellationToken = default)
    {
        var result = await Complete(request, LlmProtocol.BedrockConverse, context, cancellationToken).ConfigureAwait(false);
        return new(result.Data, result.Metadata);
    }
    /// <inheritdoc />
    public async Task<BedrockNovaResponse> InvokeAsync(JsonObject request, LlmAdapterContext context, CancellationToken cancellationToken = default)
    {
        var result = await Complete(request, LlmProtocol.BedrockNova, context, cancellationToken).ConfigureAwait(false);
        return new(result.Data, result.Metadata);
    }

    /// <summary>请求与响应分别映射，任何失败不发布已完成结果；不拥有或释放借入的供应商。</summary>
    private async Task<LlmJsonResult> Complete(JsonObject request, LlmProtocol source, LlmAdapterContext context, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); var converted = LlmProtocolConverter.ConvertRequest(request, source, _protocol, _options);
        var payload = Parse(converted.Data);
        LlmJsonResult raw = _protocol == LlmProtocol.BedrockNova
            ? await ((IBedrockNovaAdapter)_upstream).InvokeAsync(payload, context, token).ConfigureAwait(false)
            : await ((IBedrockConverseAdapter)_upstream).ConverseAsync(payload, context, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested(); if (source == _protocol) return raw;
        var result = LlmProtocolConverter.ConvertResponse(Parse(raw.Data), converted.Context!); token.ThrowIfCancellationRequested();
        var metadata = context.Metadata with { Conversion = new(_protocol, source, raw.Data, converted.Diagnostics.Concat(result.Diagnostics)) };
        return new BedrockNovaResponse(result.Data, metadata);
    }
    /// <summary>结果已经独立快照，重新取得可变原生对象仅用于本次调用。</summary>
    private static JsonObject Parse(JsonElement data) => JsonNode.Parse(data.GetRawText())!.AsObject();
}
