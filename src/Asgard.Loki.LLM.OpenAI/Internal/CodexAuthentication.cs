using System.Text.Json;

namespace Asgard.Loki.LLM.OpenAI.Internal;

/// <summary>供应商内共享的固定账户外部认证，不是登录、刷新或跨能力协调层。</summary>
internal sealed class CodexAuthentication
{
    private readonly ICodexAccessTokenSource _source;
    private readonly string _sourceIdentity;
    private readonly TimeProvider _time;
    internal CodexAccountOptions Account { get; }
    internal string BindingIdentity { get; }
    /// <summary>首次调用前快照账户和来源身份，工厂/来源/时钟都由应用拥有。</summary>
    internal CodexAuthentication(CodexAccountOptions account, ICodexAccessTokenSource source, TimeProvider? time)
    {
        ArgumentNullException.ThrowIfNull(account); ArgumentNullException.ThrowIfNull(source); Header(account.AccountId, false);
        if (account.Beta is { } beta) Header(beta, true); if (account.Originator is { } originator) Header(originator, true);
        Account = account with { }; _source = source; _time = time ?? TimeProvider.System;
        var identity = source.BindingIdentity; ArgumentException.ThrowIfNullOrWhiteSpace(identity); OpenAiHttpJson.Text(identity); _sourceIdentity = identity;
        BindingIdentity = JsonSerializer.Serialize(new { Account, Source = identity });
    }
    /// <summary>只接受原始合法头，不裁剪、编码、猜测密钥格式或替换坏字符。</summary>
    internal static void Header(string value, bool spaces)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value); OpenAiHttpJson.Text(value);
        if (value.Any(c => c < (spaces ? ' ' : '!') || c > '~')) throw new ArgumentException("Codex 原生认证/账户/配置头必须是有效 ASCII 值。");
    }
    /// <summary>冻结正文之后只取一次外部令牌，来源不得静默换主体或账户。</summary>
    internal async Task<CodexHttpCall> PrepareAsync(Uri endpoint, byte[] body, string principal, bool stream, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); Source();
        var access = await _source.GetTokenAsync(principal, token).ConfigureAwait(false) ?? throw new InvalidOperationException("Codex 认证源未返回令牌。");
        token.ThrowIfCancellationRequested(); Validate(principal, access);
        return new(this, access, principal, endpoint, body, stream);
    }
    /// <summary>每个发送/读取/关闭边界维持同一来源、主体、账户和未过期凭据。</summary>
    internal void Validate(string principal, CodexAccessToken access)
    {
        Source();
        if (access.PrincipalIdentity != principal || access.AccountId != Account.AccountId) throw new InvalidOperationException("Codex 认证源改变了已选主体或账户。");
        if (access.ExpiresOn <= _time.GetUtcNow()) throw new InvalidOperationException("Codex 外部访问令牌已过期。");
    }
    /// <summary>来源配置变化不能沿用旧供应商/Session 指纹。</summary>
    private void Source()
    { if (_source.BindingIdentity != _sourceIdentity) throw new InvalidOperationException("Codex 认证来源配置已变化，需要重新注册供应商。"); }
}
