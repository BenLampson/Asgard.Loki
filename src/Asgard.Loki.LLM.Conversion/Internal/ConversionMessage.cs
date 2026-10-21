using System.Text.Json.Nodes;

namespace Asgard.Loki.LLM.Conversion.Internal;

/// <summary>仅在可选转换包内存在的有序消息块，不作为原生调用统一模型。</summary>
internal sealed class ConversionMessage(string role)
{
    /// <summary>消息角色。</summary>
    internal string Role { get; } = role;
    /// <summary>保留顺序的文本、图片、函数调用和结果。</summary>
    internal List<JsonObject> Blocks { get; } = [];
}
