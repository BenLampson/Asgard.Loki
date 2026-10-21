namespace Asgard.Loki.Video.MiniMax;

/// <summary>明确选择原生视频契约，不按模型名称猜测路径或结果结构。</summary>
public enum MiniMaxVideoApiVersion
{
    /// <summary>平铺请求、查询参数 task_id、base_resp 与 file_id 视频结果。</summary>
    V1 = 1,
    /// <summary>多模态 content、路径 task_id、原生 task 对象与内容 URL。</summary>
    V2 = 2
}
