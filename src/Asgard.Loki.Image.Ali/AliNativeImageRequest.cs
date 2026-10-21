using System.Text.Json.Nodes;

namespace Asgard.Loki.Image.Ali;

/// <summary>在调用者已持有的阿里目标执行完整原生请求，不进行公共模型路由。</summary>
public sealed class AliNativeImageRequest
{
    /// <summary>仅用于结果和任务绑定的公共模型身份。</summary>
    public required string PublicModel { get; init; }
    /// <summary>完整 input、parameters 和扩展根字段，不能覆盖固定 model。</summary>
    public JsonObject Parameters { get; init; } = [];
}
