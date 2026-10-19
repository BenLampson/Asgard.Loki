using System.Security.Cryptography;
using System.Text;

namespace Asgard.Loki.LLM.Internal;

/// <summary>校验并持有配置快照，防止外部集合变化改写已有路由。</summary>
internal sealed class LlmConfiguration
{
    /// <summary>按标识查找供应商。</summary>
    internal Dictionary<string, ILlmProvider> Providers { get; } = new(StringComparer.Ordinal);
    /// <summary>按标识查找凭据。</summary>
    internal Dictionary<string, LlmCredential> Credentials { get; } = new(StringComparer.Ordinal);
    /// <summary>按对外名称查找路由快照。</summary>
    internal Dictionary<string, LlmRoute> Routes { get; } = new(StringComparer.Ordinal);
    /// <summary>共享的会话存储。</summary>
    internal ILlmSessionStore Sessions { get; }
    /// <summary>仅用于首次调度的策略。</summary>
    internal ILlmTargetSelector Selector { get; }

    /// <summary>在模块注册时完成结构校验，不访问外部服务。</summary>
    internal LlmConfiguration(LlmOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.SessionStore);
        ArgumentNullException.ThrowIfNull(options.TargetSelector);
        ArgumentNullException.ThrowIfNull(options.Providers);
        ArgumentNullException.ThrowIfNull(options.Credentials);
        ArgumentNullException.ThrowIfNull(options.Routes);
        Sessions = options.SessionStore;
        Selector = options.TargetSelector;
        foreach (var provider in options.Providers)
        {
            ArgumentNullException.ThrowIfNull(provider);
            ArgumentException.ThrowIfNullOrWhiteSpace(provider.Id);
            ArgumentException.ThrowIfNullOrWhiteSpace(provider.BindingIdentity);
            LlmJsonInput.Text(provider.Id); LlmJsonInput.Text(provider.BindingIdentity);
            if (!Providers.TryAdd(provider.Id, provider)) throw new ArgumentException("供应商实例标识重复。", nameof(options));
        }

        foreach (var credential in options.Credentials)
        {
            ArgumentNullException.ThrowIfNull(credential);
            LlmJsonInput.Text(credential.Id);
            if (credential.Kind == LlmCredentialKind.ApiKey) LlmJsonInput.Text(credential.ApiKey);
            if (credential.Kind == LlmCredentialKind.External) LlmJsonInput.Text(credential.PrincipalIdentity);
            if (!Credentials.TryAdd(credential.Id, credential)) throw new ArgumentException("凭据标识重复。", nameof(options));
        }

        Dictionary<string, LlmTarget> targets = new(StringComparer.Ordinal);
        foreach (var route in options.Routes)
        {
            ArgumentNullException.ThrowIfNull(route);
            ArgumentException.ThrowIfNullOrWhiteSpace(route.Model);
            LlmJsonInput.Text(route.Model);
            ArgumentNullException.ThrowIfNull(route.Targets);
            ArgumentOutOfRangeException.ThrowIfZero(route.Targets.Count);
            var copies = route.Targets.Select(target => ValidateTarget(target) with { }).ToArray();
            foreach (var target in copies)
            {
                if (targets.TryGetValue(target.Id, out var prior) && prior != target)
                    throw new ArgumentException("同一目标标识不能对应不同配置。", nameof(options));
                targets[target.Id] = target;
            }

            if (copies.Select(target => target.Id).Distinct(StringComparer.Ordinal).Count() != copies.Length)
                throw new ArgumentException("路由中存在重复目标。", nameof(options));
            if (!Routes.TryAdd(route.Model, route with { Targets = Array.AsReadOnly(copies) }))
                throw new ArgumentException("对外模型名称重复。", nameof(options));
        }

        ArgumentOutOfRangeException.ThrowIfZero(Routes.Count);
    }

    /// <summary>对目标及凭据创建稳定指纹，缓存中不保存密钥本身。</summary>
    internal string Fingerprint(LlmTarget target)
    {
        var credential = Credentials[target.CredentialId]; var provider = Providers[target.ProviderId].BindingIdentity;
        LlmJsonInput.Text(provider);
        var serialized = JsonSerializer.Serialize(target);
        // 旧 Key/匿名字节不变；新主体与实现分量采用独立 JSON 字符串边界，不能通过换行移动分隔符。
        var identity = credential.Kind switch
        {
            LlmCredentialKind.ApiKey => serialized + "\n" + credential.ApiKey + "\n" + provider,
            LlmCredentialKind.Anonymous => "loki-llm:anonymous:v1\n" + serialized + "\n" + provider,
            LlmCredentialKind.External => "loki-llm:external:v1\n" + JsonSerializer.Serialize(new { Target = serialized, Principal = credential.PrincipalIdentity, Provider = provider }),
            _ => throw new ArgumentException("不支持此认证方式。")
        };
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
    }

    /// <summary>校验目标引用及已声明的协议能力。</summary>
    private LlmTarget ValidateTarget(LlmTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentException.ThrowIfNullOrWhiteSpace(target.Id);
        ArgumentException.ThrowIfNullOrWhiteSpace(target.ProviderId);
        ArgumentException.ThrowIfNullOrWhiteSpace(target.UpstreamModel);
        ArgumentException.ThrowIfNullOrWhiteSpace(target.CredentialId);
        ArgumentException.ThrowIfNullOrWhiteSpace(target.Version);
        foreach (var value in new[] { target.Id, target.ProviderId, target.UpstreamModel, target.CredentialId, target.Version }) LlmJsonInput.Text(value);
        ArgumentNullException.ThrowIfNull(target.BaseAddress);
        if (!target.BaseAddress.IsAbsoluteUri || target.BaseAddress.Scheme is not ("https" or "http")
            || target.BaseAddress.Query.Length != 0 || target.BaseAddress.Fragment.Length != 0 || target.BaseAddress.UserInfo.Length != 0)
            throw new ArgumentException("上游基础地址必须是没有凭据、查询参数和片段的 HTTP(S) 绝对地址。");
        if (!Providers.TryGetValue(target.ProviderId, out var provider)) throw new ArgumentException("目标引用了未注册的供应商。");
        if (!Credentials.TryGetValue(target.CredentialId, out var credential)) throw new ArgumentException("目标引用了不存在的凭据。");
        var supported = provider is ILlmCredentialSupport declared ? declared.SupportsCredential(credential.Kind) : credential.Kind == LlmCredentialKind.ApiKey;
        if (!supported) throw new ArgumentException("目标供应商未声明支持所选认证方式，不允许自动降级或借用凭据。");
        if (target.Protocols == LlmProtocol.None || (target.Protocols & ~(LlmProtocol.ChatCompletions | LlmProtocol.Responses
            | LlmProtocol.ClaudeMessages | LlmProtocol.Gemini | LlmProtocol.OllamaChat | LlmProtocol.OllamaGenerate | LlmProtocol.BedrockConverse | LlmProtocol.BedrockNova)) != 0)
            throw new ArgumentException("目标协议能力无效。");
        if (provider is ILlmProtocolSupport protocolSupport)
            foreach (var protocol in Enum.GetValues<LlmProtocol>())
                if (protocol != LlmProtocol.None && target.Protocols.HasFlag(protocol) && !protocolSupport.SupportsProtocol(protocol))
                    throw new ArgumentException("目标供应商未声明支持所选协议；不能按接口存在推断转换组合可用。");
        if ((target.Features & ~(LlmTargetFeatures.ResponsesCompaction | LlmTargetFeatures.ResponsesWebSocket)) != 0)
            throw new ArgumentException("目标可选操作无效。");
        if (target.Features.HasFlag(LlmTargetFeatures.ResponsesCompaction)
            && (!target.Protocols.HasFlag(LlmProtocol.Responses) || provider is not IResponsesCompactionAdapter))
            throw new ArgumentException("目标的 Responses 压缩能力缺少原生协议或适配实现。");
        if (target.Features.HasFlag(LlmTargetFeatures.ResponsesWebSocket)
            && (!target.Protocols.HasFlag(LlmProtocol.Responses) || provider is not IResponsesWebSocketAdapter))
            throw new ArgumentException("目标的 Responses WebSocket 能力缺少原生协议或适配实现。");
        if (target.Protocols.HasFlag(LlmProtocol.ChatCompletions) && provider is not IChatCompletionsAdapter
            || target.Protocols.HasFlag(LlmProtocol.Responses) && provider is not IResponsesAdapter
            || target.Protocols.HasFlag(LlmProtocol.ClaudeMessages) && provider is not IClaudeMessagesAdapter
            || target.Protocols.HasFlag(LlmProtocol.Gemini) && provider is not IGeminiAdapter
            || target.Protocols.HasFlag(LlmProtocol.OllamaChat) && provider is not IOllamaChatAdapter
            || target.Protocols.HasFlag(LlmProtocol.OllamaGenerate) && provider is not IOllamaGenerateAdapter
            || target.Protocols.HasFlag(LlmProtocol.BedrockConverse) && provider is not IBedrockConverseAdapter
            || target.Protocols.HasFlag(LlmProtocol.BedrockNova) && provider is not IBedrockNovaAdapter)
            throw new ArgumentException("目标声明的协议没有对应适配实现。");
        return target;
    }
}
