using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Asgard.Loki.Audio.Internal;

/// <summary>捕获完整音频目标，任务引用仅公开配置与操作的摘要。</summary>
internal sealed class AudioTaskTarget
{
    private readonly string _model;
    private readonly string _identity;
    /// <summary>适配器由应用拥有，不转移释放责任。</summary>
    internal AudioTaskTarget(string model, IAudioTaskAdapter adapter)
    {
        ArgumentNullException.ThrowIfNull(adapter); ArgumentException.ThrowIfNullOrWhiteSpace(adapter.Id);
        ArgumentException.ThrowIfNullOrWhiteSpace(adapter.BindingIdentity);
        AudioTaskJson.Text(adapter.Id); AudioTaskJson.Text(adapter.BindingIdentity);
        _model = model; Adapter = adapter; Id = adapter.Id; _identity = adapter.BindingIdentity;
    }
    /// <summary>完整原生适配器。</summary>
    internal IAudioTaskAdapter Adapter { get; }
    /// <summary>捕获的目标标识。</summary>
    internal string Id { get; }
    /// <summary>将操作纳入绑定，不能把歌词引用解释成音乐。</summary>
    internal string Fingerprint(string operation) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        JsonSerializer.Serialize(new { Model = _model, Id, BindingIdentity = _identity, Operation = operation }))));
    /// <summary>查询前后都验证原服务器、模型、凭据和适配实现身份。</summary>
    internal void Validate()
    {
        if (Adapter.Id != Id || Adapter.BindingIdentity != _identity) throw new InvalidOperationException("音频原目标配置或适配实现已改变。");
    }
}
