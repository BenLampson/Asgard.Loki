namespace Asgard.Loki.LLM.Internal;

/// <summary>在任何上游调用前恢复或建立完整 Session 绑定。</summary>
internal sealed class LlmInvocation(LlmConfiguration configuration)
{
    /// <summary>暴露共享存储供协议客户端登记响应归属。</summary>
    internal ILlmSessionStore Sessions => configuration.Sessions;

    /// <summary>连接已建立后只验证原绑定，不允许会话过期或被替换时重新创建绑定。</summary>
    internal async ValueTask ValidateBoundAsync(LlmSessionBinding expected, string? previousResponseId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var current = await Sessions.GetAsync(expected.SessionId, cancellationToken).ConfigureAwait(false);
        if (current != expected) throw new InvalidOperationException("连接的原 Session 绑定不存在、已经过期或已被替换，不允许重新调度。");
        var target = configuration.Routes[expected.Model].Targets.SingleOrDefault(item => item.Id == expected.TargetId);
        if (target is null || !target.Protocols.HasFlag(expected.Protocol) || configuration.Fingerprint(target) != expected.Fingerprint)
            throw new InvalidOperationException("连接的原目标配置或适配实现已改变，不允许继续使用。");
        if (previousResponseId is not null
            && !await Sessions.OwnsResponseAsync(expected, previousResponseId, cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("引用的响应 ID 不属于当前 Session 或记录已过期。");
        await Sessions.RefreshAsync(expected.SessionId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>恢复已有目标，首次调用才经过目标选择器。</summary>
    internal async ValueTask<(LlmAdapterContext Context, LlmSessionBinding Binding, ILlmProvider Provider)> ResolveAsync(
        string model, LlmProtocol protocol, LlmCallOptions options, string? previousResponseId, CancellationToken cancellationToken,
        LlmTargetFeatures requiredFeatures = LlmTargetFeatures.None)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.SessionId);
        LlmJsonInput.Text(model); LlmJsonInput.Text(options.SessionId); LlmJsonInput.Text(previousResponseId);
        cancellationToken.ThrowIfCancellationRequested();
        if (!configuration.Routes.TryGetValue(model, out var route)) throw new InvalidOperationException("对外模型未配置。");
        var binding = await Sessions.GetAsync(options.SessionId, cancellationToken).ConfigureAwait(false);
        if (binding is null)
        {
            if (previousResponseId is not null) throw new InvalidOperationException("旧响应链的 Session 绑定不存在或已经过期。");
            var candidates = route.Targets.Where(target => target.Protocols.HasFlag(protocol) && target.Features.HasFlag(requiredFeatures)).ToArray();
            if (candidates.Length == 0) throw new NotSupportedException("此路由没有支持请求协议的目标。");
            var candidate = configuration.Selector.Select(candidates);
            if (!candidates.Contains(candidate)) throw new InvalidOperationException("目标选择器返回了候选集之外的目标。");
            binding = await Sessions.GetOrCreateAsync(new LlmSessionBinding(options.SessionId, model, protocol,
                candidate.Id, configuration.Fingerprint(candidate)), cancellationToken).ConfigureAwait(false);
        }

        if (binding.SessionId != options.SessionId || binding.Model != model || binding.Protocol != protocol)
            throw new InvalidOperationException("Session 不能改变模型或协议。");
        var target = route.Targets.SingleOrDefault(item => item.Id == binding.TargetId)
            ?? throw new InvalidOperationException("Session 原目标已移除，不允许自动切换。");
        if (!target.Protocols.HasFlag(protocol) || configuration.Fingerprint(target) != binding.Fingerprint)
            throw new InvalidOperationException("Session 原目标配置或凭据已改变，不允许自动切换。");
        if (!target.Features.HasFlag(requiredFeatures)) throw new NotSupportedException("Session 原目标不支持请求的原生可选操作，不允许切换目标。");
        if (previousResponseId is not null
            && !await Sessions.OwnsResponseAsync(binding, previousResponseId, cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("previous_response_id 不属于当前 Session 或记录已过期。");
        await Sessions.RefreshAsync(options.SessionId, cancellationToken).ConfigureAwait(false);
        return (new LlmAdapterContext(target, configuration.Credentials[target.CredentialId], model, options.SessionId),
            binding, configuration.Providers[target.ProviderId]);
    }

    /// <summary>复制协议对象，只修改实际模型和传输流式开关。</summary>
    internal static JsonObject Payload<T>(T request, string upstreamModel, bool stream)
    {
        var payload = JsonSerializer.SerializeToNode(request)!.AsObject();
        payload["model"] = upstreamModel;
        payload["stream"] = stream;
        return payload;
    }

    /// <summary>阻止扩展字段覆盖有明确语义的公共字段。</summary>
    internal static void ValidateParameters(Dictionary<string, JsonElement> parameters, params string[] reserved)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        if (reserved.Any(parameters.ContainsKey)) throw new ArgumentException("扩展参数不能覆盖公共字段。", nameof(parameters));
    }
}
