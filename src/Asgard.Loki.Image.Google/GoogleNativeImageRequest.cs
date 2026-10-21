using System.Text.Json.Nodes;

namespace Asgard.Loki.Image.Google;

/// <summary>在调用者已持有的Gemini 目标执行完整原生请求，不进行公共模型路由。</summary>
public sealed class GoogleNativeImageRequest
{
    /// <summary>仅用于结果和任务绑定的公共模型身份。</summary>
    public required string PublicModel { get; init; }
    /// <summary>完整 contents、generationConfig、工具与扩展根字段；model 绑定路径，stream 由显式方法确定。</summary>
    public JsonObject Parameters { get; init; } = [];
}
