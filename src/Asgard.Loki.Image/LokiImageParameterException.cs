namespace Asgard.Loki.Image;

/// <summary>统一参数无法在选中的实际上游可靠表达，调用尚未发送。</summary>
public sealed class LokiImageParameterException : ArgumentException
{
    /// <summary>保存不含凭据的目标及参数诊断。</summary>
    public LokiImageParameterException(string providerId, string upstreamModel, string parameter,
        string requestedValue, string reason, string? supportedValues = null)
        : base($"图片上游 {providerId}（模型 {upstreamModel}）不支持参数 {parameter}={requestedValue}：{reason}"
            + (supportedValues is null ? "" : $"；支持范围：{supportedValues}"), parameter)
    {
        ProviderId = providerId; UpstreamModel = upstreamModel; RequestedValue = requestedValue;
        Reason = reason; SupportedValues = supportedValues;
    }
    /// <summary>选中的完整上游标识。</summary>
    public string ProviderId { get; }
    /// <summary>实际模型。</summary>
    public string UpstreamModel { get; }
    /// <summary>请求值。</summary>
    public string RequestedValue { get; }
    /// <summary>不支持或冲突原因。</summary>
    public string Reason { get; }
    /// <summary>声明的支持范围。</summary>
    public string? SupportedValues { get; }
}
