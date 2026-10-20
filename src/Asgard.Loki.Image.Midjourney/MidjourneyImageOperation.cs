namespace Asgard.Loki.Image.Midjourney;

/// <summary>显式选择一种原生图片任务操作；代理版本是否启用该操作由上游决定。</summary>
public enum MidjourneyImageOperation
{
    /// <summary>原生绘图提交。</summary>
    Imagine,
    /// <summary>原生图片描述。</summary>
    Describe,
    /// <summary>原生图片混合。</summary>
    Blend,
    /// <summary>经典 change 操作，不改写为按钮或另一种协议。</summary>
    Change,
    /// <summary>经典 simple-change 原始 content 操作，不解析或重排内容。</summary>
    SimpleChange,
    /// <summary>原生按钮操作，保持 taskId/customId 关联原文。</summary>
    Action,
    /// <summary>原生弹窗提交，保持 taskId 和 mask 角色。</summary>
    Modal,
    /// <summary>原生提示词缩短任务。</summary>
    Shorten,
    /// <summary>原生图像编辑。</summary>
    Edits,
    /// <summary>原生图像换脸任务，调用者明确提供源与目标。</summary>
    SwapFace
}
