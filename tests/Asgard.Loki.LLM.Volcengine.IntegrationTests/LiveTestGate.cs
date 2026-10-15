namespace Asgard.Loki.LLM.Volcengine.IntegrationTests;

/// <summary>为各联调用例提供一致的运行时开关。</summary>
public static class LiveTestGate
{
    /// <summary>只有明确启用真实联调才执行请求。</summary>
    public static bool IsEnabled => LiveConfiguration.IsEnabled();
}
