using System.Text.Json;

namespace Asgard.Loki.Video.Volcengine;

/// <summary>独立拥有完整方舟素材响应，API 调用完成不等于素材预处理完成。</summary>
public sealed class VolcengineAssetResult
{
    /// <summary>仅由客户端在传输及原生响应校验完成后创建。</summary>
    internal VolcengineAssetResult(string action, JsonElement data) { Action = action; Data = data.Clone(); }
    /// <summary>本次明确调用的素材或素材组操作。</summary>
    public string Action { get; }
    /// <summary>保留 ResponseMetadata、Result 和未知成员的完整原生 JSON。</summary>
    public JsonElement Data { get; }
    /// <summary>原生结果；Status、Error、Items 和分页凭证由调用者按原生语义读取。</summary>
    public JsonElement Result => Data.GetProperty("Result");
}
