namespace Asgard.Loki.LLM.Dify;

/// <summary>调用者显式声明原生应用类别，不通过模型名、Key 或响应猜测终态规则。</summary>
public enum DifyChatProfile
{
    /// <summary>普通聊天应用，以原生 message_end 结束生成。</summary>
    Chatbot = 1,
    /// <summary>新旧 Agent 应用，仅支持流式，保留原生工具思考及完整结束消息。</summary>
    Agent = 2,
    /// <summary>聊天工作流应用，启动运行后成功生成需要 message_end 和 workflow_finished；原生无运行直答保留 message_end。</summary>
    Chatflow = 3
}
