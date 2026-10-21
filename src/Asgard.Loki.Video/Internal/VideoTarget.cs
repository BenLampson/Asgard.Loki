using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Asgard.Loki.Video.Internal;

/// <summary>注册时捕获完整目标身份，公开引用只包含其摘要，不暴露凭据或内部配置。</summary>
internal sealed class VideoTarget
{
    private readonly string _model;
    /// <summary>固定目标实例和配置身份；适配器仍由调用者拥有。</summary>
    internal VideoTarget(string model, IVideoTaskAdapter adapter)
    {
        ArgumentNullException.ThrowIfNull(adapter); ArgumentException.ThrowIfNullOrWhiteSpace(adapter.Id);
        ArgumentException.ThrowIfNullOrWhiteSpace(adapter.BindingIdentity); VideoJson.Text(adapter.Id); VideoJson.Text(adapter.BindingIdentity);
        _model = model; Adapter = adapter; Id = adapter.Id; Fingerprint = CurrentFingerprint();
    }
    /// <summary>原生目标的借用实例。</summary>
    internal IVideoTaskAdapter Adapter { get; }
    /// <summary>稳定目标标识。</summary>
    internal string Id { get; }
    /// <summary>完整配置在此公共路由下的摘要。</summary>
    internal string Fingerprint { get; }
    /// <summary>不能在注册后悄悄改变服务器、凭据、模型或适配实现。</summary>
    internal void Validate()
    {
        if (Adapter.Id != Id || CurrentFingerprint() != Fingerprint) throw new InvalidOperationException("视频原目标配置或适配实现已改变。");
    }
    /// <summary>使用 JSON 边界避免字段分隔歧义，不把密钥写入公共引用。</summary>
    private string CurrentFingerprint()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Adapter.BindingIdentity); VideoJson.Text(Adapter.BindingIdentity);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { Model = _model, Id = Adapter.Id, Adapter.BindingIdentity }))));
    }
}
