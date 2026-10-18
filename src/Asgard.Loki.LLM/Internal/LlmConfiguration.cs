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
            if (!Providers.TryAdd(provider.Id, provider)) throw new ArgumentException("供应商实例标识重复。", nameof(options));
        }

        foreach (var credential in options.Credentials)
        {
            ArgumentNullException.ThrowIfNull(credential);
            if (!Credentials.TryAdd(credential.Id, credential)) throw new ArgumentException("凭据标识重复。", nameof(options));
        }

        Dictionary<string, LlmTarget> targets = new(StringComparer.Ordinal);
        foreach (var route in options.Routes)
        {
            ArgumentNullException.ThrowIfNull(route);
            ArgumentException.ThrowIfNullOrWhiteSpace(route.Model);
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
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(target) + "\n" + Credentials[target.CredentialId].ApiKey
            + "\n" + Providers[target.ProviderId].BindingIdentity)));

    /// <summary>校验目标引用及已声明的协议能力。</summary>
    private LlmTarget ValidateTarget(LlmTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentException.ThrowIfNullOrWhiteSpace(target.Id);
        ArgumentException.ThrowIfNullOrWhiteSpace(target.ProviderId);
        ArgumentException.ThrowIfNullOrWhiteSpace(target.UpstreamModel);
        ArgumentException.ThrowIfNullOrWhiteSpace(target.CredentialId);
        ArgumentException.ThrowIfNullOrWhiteSpace(target.Version);
        ArgumentNullException.ThrowIfNull(target.BaseAddress);
        if (!target.BaseAddress.IsAbsoluteUri || target.BaseAddress.Scheme is not ("https" or "http")
            || target.BaseAddress.Query.Length != 0 || target.BaseAddress.Fragment.Length != 0 || target.BaseAddress.UserInfo.Length != 0)
            throw new ArgumentException("上游基础地址必须是没有凭据、查询参数和片段的 HTTP(S) 绝对地址。");
        if (!Providers.TryGetValue(target.ProviderId, out var provider)) throw new ArgumentException("目标引用了未注册的供应商。");
        if (!Credentials.ContainsKey(target.CredentialId)) throw new ArgumentException("目标引用了不存在的凭据。");
        if (target.Protocols == LlmProtocol.None || (target.Protocols & ~(LlmProtocol.ChatCompletions | LlmProtocol.Responses
            | LlmProtocol.ClaudeMessages | LlmProtocol.Gemini)) != 0)
            throw new ArgumentException("目标协议能力无效。");
        if ((target.Features & ~LlmTargetFeatures.ResponsesCompaction) != 0)
            throw new ArgumentException("目标可选操作无效。");
        if (target.Features.HasFlag(LlmTargetFeatures.ResponsesCompaction)
            && (!target.Protocols.HasFlag(LlmProtocol.Responses) || provider is not IResponsesCompactionAdapter))
            throw new ArgumentException("目标的 Responses 压缩能力缺少原生协议或适配实现。");
        if (target.Protocols.HasFlag(LlmProtocol.ChatCompletions) && provider is not IChatCompletionsAdapter
            || target.Protocols.HasFlag(LlmProtocol.Responses) && provider is not IResponsesAdapter
            || target.Protocols.HasFlag(LlmProtocol.ClaudeMessages) && provider is not IClaudeMessagesAdapter
            || target.Protocols.HasFlag(LlmProtocol.Gemini) && provider is not IGeminiAdapter)
            throw new ArgumentException("目标声明的协议没有对应适配实现。");
        return target;
    }
}
