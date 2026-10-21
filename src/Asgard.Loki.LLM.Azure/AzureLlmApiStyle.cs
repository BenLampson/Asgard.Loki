namespace Asgard.Loki.LLM.Azure;

/// <summary>明确选择 Azure 地址格式，不根据主机名、模型名或调用失败猜测。</summary>
public enum AzureLlmApiStyle
{
    /// <summary>调用者提供 openai/v1 API 根，部署名位于原生 model 字段。</summary>
    V1,
    /// <summary>调用者提供 openai API 根及 api-version；Chat 使用 deployments 路径。</summary>
    Dated
}
