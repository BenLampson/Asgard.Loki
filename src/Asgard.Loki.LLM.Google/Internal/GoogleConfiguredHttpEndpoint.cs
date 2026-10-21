using System.Globalization;
using System.Text;

namespace Asgard.Loki.LLM.Google.Internal;

/// <summary>验证显式相对操作和查询，不允许配置隐藏切换完整目标服务器。</summary>
internal static class GoogleConfiguredHttpEndpoint
{
    internal static void Validate(GoogleHttpTransportOptions options, GoogleHttpAuthentication authentication)
    {
        if (!Enum.IsDefined(authentication)) throw new ArgumentOutOfRangeException(nameof(options));
        if (options.FixedUpstreamModel is not null) { ArgumentException.ThrowIfNullOrWhiteSpace(options.FixedUpstreamModel); Text(options.FixedUpstreamModel); }
        foreach (var path in new[] { options.GenerateContentPath, options.StreamGenerateContentPath }) ModelPath(path, options.FixedUpstreamModel is not null);
        Text(options.CredentialPrefix); Text(options.CredentialSuffix);
        if (authentication is GoogleHttpAuthentication.ApiKey or GoogleHttpAuthentication.Bearer or GoogleHttpAuthentication.None)
        { if (options.CredentialName is not null || options.CredentialPrefix.Length != 0 || options.CredentialSuffix.Length != 0) throw new ArgumentException("ApiKey/Bearer/None 不接受额外凭据位置或格式。"); }
        else
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(options.CredentialName); Text(options.CredentialName);
            if (authentication == GoogleHttpAuthentication.Header && (!options.CredentialName.All(Token) || new[] { "Host", "Content-Type", "Content-Length", "Accept", "Connection", "Transfer-Encoding", "Trailer", "Upgrade" }.Contains(options.CredentialName, StringComparer.OrdinalIgnoreCase))) throw new ArgumentException("显式认证头必须是非结构性的 HTTP 字段名称。");
            if (authentication == GoogleHttpAuthentication.Query)
                foreach (var path in new[] { options.GenerateContentPath, options.StreamGenerateContentPath })
                    if (QueryNames(path).Any(name => name.Equals(options.CredentialName, StringComparison.OrdinalIgnoreCase))) throw new ArgumentException("固定查询不能同时包含选中的认证参数。");
        }
    }
    internal static Uri Create(Uri root, string path, GoogleHttpTransportOptions options, string? value, GoogleHttpAuthentication authentication)
    {
        if (!root.IsAbsoluteUri || root.Scheme is not ("http" or "https") || root.UserInfo.Length != 0 || root.Query.Length != 0 || root.Fragment.Length != 0) throw new ArgumentException("显式 HTTP 需要无凭据、查询和片段的完整 HTTP(S) 根。");
        var address = root.AbsoluteUri.TrimEnd('/') + "/" + path.TrimStart('/');
        if (authentication == GoogleHttpAuthentication.Query) address += (path.Contains('?') ? path.EndsWith('?') || path.EndsWith('&') ? "" : "&" : "?") + Uri.EscapeDataString(options.CredentialName!) + "=" + Uri.EscapeDataString(value!);
        var endpoint = new Uri(address, UriKind.Absolute);
        if (endpoint.Authority != root.Authority || endpoint.Scheme != root.Scheme) throw new ArgumentException("显式操作不能改变选中服务器。");
        return endpoint;
    }
    private static void ModelPath(string path, bool fixedModel)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path); var parts = path.Split('?', 2); var count = (parts[0].Length-parts[0].Replace("{model}", "", StringComparison.Ordinal).Length)/7;
        if (count != (fixedModel ? 0 : 1) || parts.Length == 2 && parts[1].Contains("{model}", StringComparison.Ordinal)) throw new ArgumentException("普通 Gemini 路径必须有一个模型位置；固定模型路径必须省略该位置，查询不能代替原生模型路径。");
        Path(path.Replace("{model}", "configured-model", StringComparison.Ordinal));
    }
    internal static string ModelOperation(GoogleHttpTransportOptions options, string model, bool stream)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model); Text(model);
        if (options.FixedUpstreamModel is { } fixedModel && !fixedModel.Equals(model, StringComparison.Ordinal)) throw new ArgumentException("固定原生端点的模型声明与目标实际模型不一致。");
        if (model is "." or "..") throw new ArgumentException("实际模型不能是隐式路径跳转。");
        var path = stream ? options.StreamGenerateContentPath : options.GenerateContentPath;
        return path.Replace("{model}", Uri.EscapeDataString(model), StringComparison.Ordinal);
    }
    private static void Path(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path); Text(path);
        if (path.Any(char.IsWhiteSpace) || path.StartsWith("//", StringComparison.Ordinal) || path.Contains('\\') || path.Contains('#') || path.Split('?')[0].Contains("://", StringComparison.Ordinal) || path.Contains('{') || path.Contains('}')) throw new ArgumentException("操作路径必须是无片段、模板或其他服务器的明确相对地址。");
        var decoded = Decode(path.Split('?')[0]);
        if (decoded.Trim('/').Length == 0 || decoded.Split('/').Any(part => part is "." or "..") || decoded.Contains('\\')) throw new ArgumentException("操作路径不能包含隐式路径跳转。");
        _ = QueryNames(path).ToArray();
    }
    private static IEnumerable<string> QueryNames(string path)
    {
        var query = path.IndexOf('?'); if (query < 0) yield break;
        foreach (var pair in path[(query+1)..].Split('&')) { var index = pair.IndexOf('='); var name = index < 0 ? pair : pair[..index]; if (index >= 0) _ = Decode(pair[(index+1)..]); yield return Decode(name.Replace('+', ' ')); }
    }
    private static string Decode(string value)
    {
        List<byte> bytes = []; var utf8 = new UTF8Encoding(false, true); var start = 0;
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] != '%') continue;
            bytes.AddRange(utf8.GetBytes(value[start..i]));
            if (i+2 >= value.Length || !byte.TryParse(value.AsSpan(i+1, 2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var parsed)) throw new ArgumentException("操作地址包含无效百分号转义。");
            bytes.Add(parsed); i += 2; start = i+1;
        }
        bytes.AddRange(utf8.GetBytes(value[start..])); var text = utf8.GetString(bytes.ToArray()); Text(text); return text;
    }
    internal static void Text(string value)
    { ArgumentNullException.ThrowIfNull(value); _ = new UTF8Encoding(false, true).GetByteCount(value); if (value.Any(char.IsControl)) throw new ArgumentException("显式传输文本不能包含控制字符。"); }
    private static bool Token(char value) => value is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' || "!#$%&'*+-.^_`|~".Contains(value);
}
