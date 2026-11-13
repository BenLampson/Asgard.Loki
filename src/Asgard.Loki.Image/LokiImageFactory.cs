using System.Collections.ObjectModel;
using Asgard.Loki.Image.Internal;

namespace Asgard.Loki.Image;

/// <summary>冻结模型注册，创建独立调度客户端；不拥有 HTTP 工厂或 Handler 生命周期。</summary>
public sealed class LokiImageFactory
{
    private readonly LokiImageModel[] _models;
    private readonly IHttpClientFactory _httpFactory;
    private readonly LokiImageContentOptions _content;

    /// <summary>验证并冻结完整上游配置；创建时不联网。</summary>
    public LokiImageFactory(LokiImageProvider registration, IHttpClientFactory httpClientFactory, LokiImageContentOptions? contentOptions = null)
    {
        ArgumentNullException.ThrowIfNull(registration); ArgumentNullException.ThrowIfNull(httpClientFactory);
        ArgumentNullException.ThrowIfNull(registration.Models); ArgumentNullException.ThrowIfNull(registration.Handlers);
        _httpFactory = httpClientFactory; _content = contentOptions ?? new();
        ArgumentException.ThrowIfNullOrWhiteSpace(_content.HttpClientName);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(_content.MaximumBytes, 0);
        var names = new HashSet<string>(StringComparer.Ordinal);
        _models = registration.Models.Select(model =>
        {
            ArgumentNullException.ThrowIfNull(model); ArgumentException.ThrowIfNullOrWhiteSpace(model.Name);
            LokiImageSnapshots.Text(model.Name);
            if (!names.Add(model.Name)) throw new ArgumentException("公开图片模型重复。", nameof(registration));
            ArgumentNullException.ThrowIfNull(model.Providers);
            if (model.Providers.Count == 0) throw new ArgumentException("公开图片模型至少需要一个完整上游。", nameof(registration));
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var providers = model.Providers.Select(provider =>
            {
                ArgumentNullException.ThrowIfNull(provider); ArgumentException.ThrowIfNullOrWhiteSpace(provider.Id);
                if (!ids.Add(provider.Id)) throw new ArgumentException("同一图片模型内上游 Id 重复。", nameof(registration));
                ArgumentException.ThrowIfNullOrWhiteSpace(provider.UpstreamModel); ArgumentException.ThrowIfNullOrWhiteSpace(provider.ApiKey);
                ArgumentException.ThrowIfNullOrWhiteSpace(provider.HttpClientName); ArgumentNullException.ThrowIfNull(provider.BaseAddress);
                foreach (var text in new[] { provider.Id, provider.UpstreamModel, provider.ApiKey, provider.HttpClientName })
                    LokiImageSnapshots.Text(text);
                if (!provider.BaseAddress.IsAbsoluteUri || provider.BaseAddress.Scheme is not ("https" or "http")
                    || provider.BaseAddress.UserInfo.Length != 0 || provider.BaseAddress.Fragment.Length != 0 || provider.BaseAddress.Query.Length != 0)
                    throw new ArgumentException("上游必须是无内嵌凭据、查询和片段的 HTTP(S) 地址。");
                var handler = provider.Handler;
                if (provider.HandlerName is not null)
                {
                    if (handler is not null) throw new ArgumentException("Handler 和 HandlerName 不能同时设置。");
                    if (!registration.Handlers.TryGetValue(provider.HandlerName, out handler)) throw new ArgumentException("图片 Handler 名称没有注册。");
                }
                ArgumentNullException.ThrowIfNull(handler); ArgumentException.ThrowIfNullOrWhiteSpace(handler.Name);
                return Snapshot(provider with { Handler = handler, HandlerName = null });
            }).ToArray();
            return model with { Providers = Array.AsReadOnly(providers) };
        }).ToArray();
        if (_models.Length == 0) throw new ArgumentException("至少注册一个公开图片模型。", nameof(registration));
    }

    /// <summary>创建客户端；默认选择器按客户端和公开模型分别计数。</summary>
    public LokiImageClient CreateClient() => new(_models, _httpFactory, _content);

    /// <summary>隔离注册表、选择器及 Handler 接收到的可变 JSON。</summary>
    internal static LokiImageUpstreamProvider Snapshot(LokiImageUpstreamProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider.Parameters); ArgumentNullException.ThrowIfNull(provider.Settings);
        LokiImageSnapshots.Node(provider.Settings);
        var rules = new Dictionary<string, LokiImageParameterRule>(StringComparer.Ordinal);
        foreach (var (name, rule) in provider.Parameters)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name); ArgumentNullException.ThrowIfNull(rule); ArgumentNullException.ThrowIfNull(rule.AllowedValues);
            if (rule.Minimum is { } minimum && !double.IsFinite(minimum) || rule.Maximum is { } maximum && !double.IsFinite(maximum)
                || rule.Minimum > rule.Maximum) throw new ArgumentException("图片参数支持范围无效。");
            foreach (var value in rule.AllowedValues) LokiImageSnapshots.Element(value);
            rules.Add(name, rule with { AllowedValues = Array.AsReadOnly(rule.AllowedValues.Select(value => value.Clone()).ToArray()) });
        }
        return provider with { Parameters = new ReadOnlyDictionary<string, LokiImageParameterRule>(rules), Settings = (System.Text.Json.Nodes.JsonObject)provider.Settings.DeepClone() };
    }
}
