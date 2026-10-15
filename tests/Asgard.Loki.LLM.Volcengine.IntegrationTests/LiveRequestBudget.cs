namespace Asgard.Loki.LLM.Volcengine.IntegrationTests;

/// <summary>对整个测试进程计数，避免参数用例重建客户端后绕过请求上限。</summary>
internal static class LiveRequestBudget
{
    private static int _requests;
    /// <summary>完整冒烟与参数套件的总请求预算，不含任何自动重试。</summary>
    internal const int Maximum = 32;
    /// <summary>发送前原子占用预算，超额时不访问网络。</summary>
    internal static void Charge()
    {
        if (Interlocked.Increment(ref _requests) > Maximum)
            throw new InvalidOperationException("The live process request budget was exhausted; request was not sent.");
    }
}
