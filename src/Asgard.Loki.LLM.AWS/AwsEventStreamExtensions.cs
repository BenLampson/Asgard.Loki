using System.Runtime.CompilerServices;

namespace Asgard.Loki.LLM.AWS;

/// <summary>仅本供应商的原生事件/异常帧视图，不改变 LLM 核心或跨协议结果。</summary>
public static class AwsEventStreamExtensions
{
    private static readonly ConditionalWeakTable<object, AwsEventStreamFrame> Frames = new();
    /// <summary>取得 AWS 原生事件对应的完整帧；非 AWS 或显式转换的新事件返回空。</summary>
    public static AwsEventStreamFrame? GetAwsEventStreamFrame(this LlmJsonEvent value)
    {
        ArgumentNullException.ThrowIfNull(value); return Frames.TryGetValue(value, out var frame) ? frame : null;
    }
    /// <summary>取得处理有效帧时异常的原始帧；HTTP、帧校验或整体结束检查失败返回空。</summary>
    public static AwsEventStreamFrame? GetAwsEventStreamFrame(this Exception value)
    {
        ArgumentNullException.ThrowIfNull(value); return Frames.TryGetValue(value, out var frame) ? frame : null;
    }
    /// <summary>弱关联随事件/异常生命期释放，不按 Session/ID 建表或全局保留历史。</summary>
    internal static T Attach<T>(T value, AwsEventStreamFrame frame) where T : class
    {
        Frames.Add(value, frame); return value;
    }
}
