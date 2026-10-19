# Asgard.Loki

[English](README.md) | **简体中文**

纯 .NET 10 / C# 14 类库：统一 `LokiClient` 入口，能力及供应商按需安装。英文 README 是完整使用说明，结构规则见 [架构规范](docs/architecture.md)。

## 已实现

| 能力 | 供应商包 | 支持内容 |
| --- | --- | --- |
| LLM | `Asgard.Loki.LLM.Volcengine` | 方舟 Chat Completions、Responses，JSON/SSE，响应链与完整 Session 目标绑定 |
| LLM | `Asgard.Loki.LLM.OpenAI` | OpenAI Chat Completions、Responses，JSON/SSE；显式原生 compaction（JSON）与 Responses WebSocket 多 lane 原生事件 |
| LLM | `Asgard.Loki.LLM.Anthropic` | Claude Messages 原生 JSON/SSE，保留工具、推理签名及未知字段 |
| LLM | `Asgard.Loki.LLM.Google` | Gemini generateContent 原生 JSON/SSE，保留原生多模态与工具结构 |
| 显式转换 | `Asgard.Loki.LLM.Conversion` | 四种文本协议间十二个方向的公共子集请求、响应和流式转换 |
| 音频 | `Asgard.Loki.Audio.Volcengine` | V3 HTTP 单向流式 TTS、音频聚合、新旧认证方式 |
| 图片 | `Asgard.Loki.Image.OpenAI` | Images Generations、Base64/URL、部分与最终图像 SSE |
| 嵌入 | `Asgard.Loki.Embedding.OpenAI` | 原生 Embeddings、文本/token 批输入、Float/Base64、原始索引及完整 JSON |
| 嵌入 | `Asgard.Loki.Embedding.Google` | 原生单条/同步批嵌入、原生配置与多模态 JSON、按原顺序返回向量 |
| 重排 | 核心能力、自定义适配器 | 独立原生请求/结果和完整目标路由；内置供应商待补 |
| 视频 | — | 保留项目骨架 |

供应商包仅引用所属能力，不会带入其他能力包。入口只引用统一抽象。

Loki 保持可嵌入应用的标准 SDK 定位。Claude 使用 `loki.LLM.ClaudeMessages`，Gemini 使用 `loki.LLM.Gemini`；目标需要明确设置对应 `Protocols`。调用者提供 HTTP 工厂、凭据和 Session 依赖，不增加转发服务、后台或计费功能。

跨协议转换必须显式安装并注册 `ConvertingLlmProvider`，供应商包不引用转换包。默认拒绝未知字段或语义损耗，结果通过 `Metadata.Conversion` 暴露原始上游 JSON 与诊断。支持文本、内联图片、函数工具及结果关联、基础生成参数和 token 用量；推理签名、加密项和跨协议响应链始终拒绝转换。文本即时流式输出，函数参数完整后输出。完整支持矩阵及限制见[转换包说明](src/Asgard.Loki.LLM.Conversion/README.md)，原生调用不经过此转换层。

Session 指纹新增适配实现和转换配置身份；旧版本建立的绑定将校验失败，需要调用者使用新 Session ID 或管理旧绑定过期，SDK 不静默重新绑定。

显式转换已扩展推理强度、可表达的 Claude/Gemini 数值预算及动态/关闭模式、摘要显示意图和四协议 JSON Schema 输出外壳。Schema 约束原样保留；已支持 Gemini 现行 `responseFormat.text` 的读取及显式目标选项 `GeminiOutputFormat.ResponseFormat`，默认仍保留旧 JSON Schema 外壳；无名 Schema 转 OpenAI 需明确提供 `DefaultSchemaName`，无法等价表达的包装元数据、严格程度和预算替代仍需诊断及显式损耗同意。手动 thinking 模式由调用者提供 `TargetThinkingBudgetTokens`，不推算预算、不改模型后缀或调大输出上限。模型支持范围由调用者确认，详见[转换配置与限制](src/Asgard.Loki.LLM.Conversion/README.md#explicit-generation-settings)。

LLM Session 使用标准 `IDistributedCache` 保存完整目标，首次绑定需要显式注入共享协调器。不会因为重连、Key 池变化或目标失效而偷偷切换服务器、模型和 Key。单元测试使用 `MemoryDistributedCache` 与测试协调器，不依赖真实 Redis。

原生 OpenAI 压缩窗口通过 `loki.LLM.Responses.CompactAsync` 显式调用，目标需同时声明 Responses 协议和 `Features = LlmTargetFeatures.ResponsesCompaction`，供应商还必须实现 `IResponsesCompactionAdapter`。完整结果保留所有窗口项及加密内容，由调用者决定后续输入，不自动压缩或裁剪历史。可选 `PreviousResponseId` 继续校验 Session 归属；压缩结果自身 ID 不登记为生成响应链。原目标不支持该操作时明确失败，不切换目标。火山和转换装饰器当前不声明该能力，初始化及示例见[英文说明](README.md#native-responses-compaction)。

原生 OpenAI Responses WebSocket 通过 `OpenWebSocketAsync` 显式建立连接，目标需声明 `ResponsesWebSocket` 功能。调用者负责收发原生事件及连接释放；完整目标、响应归属、多 lane、steering 和失败/不完整终态均有离线测试。示例及资源上限见[供应商说明](src/Asgard.Loki.LLM.OpenAI/README.md#native-responses-websocket)，未进行真实 OpenAI 联调。当前未实现图片编辑、语音识别、音乐生成、视频生成和事件重放。公共请求保留扩展参数，结果保留供应商完整 JSON；具体模型、音色与账号授权仍需真实联调验证。

初始化、调用、支持矩阵及验证命令请参阅 [英文 README](README.md)。CI 在 Windows/Linux 上执行构建、测试及十三个 NuGet 包的依赖与元数据检查，打包产物作为运行附件保存。独立的 `Publish NuGet` 工作流使用 OIDC 可信发布，默认仅验证登录；正式发布需手动选择 `publish` 并填写准确版本，步骤见 [发布说明](docs/releasing.md)。

已于 2026-10-19 再次对照 New API `058c8231`，按本库 `5403f1d` 更新[模型调用补齐计划](docs/model-call-roadmap.md#remaining-sdk-work-at-the-current-checkpoint)。原生 Claude/Gemini、已声明范围的四协议转换、OpenAI compaction/Responses WebSocket、OpenAI/Google Embedding 和独立 Rerank 核心已实现。下一步先补 Cohere Rerank，再补 Jina；转换继续收口搜索执行/结果、引用流、文档媒体、工具结果/错误及复杂输出。Ollama 原生 Embedding、Jina 约束和 DashScope 等 Rerank 方言列为剩余供应商差距，不能因已有 OpenAI/Google 包就算全部覆盖。

后续依次补图片编辑、OpenAI 音频、云认证及其他原生供应商、Realtime 和 Video；旧式 completion、moderation、原生模型发现和可独立调用的搜索/fetch 保留为低优先级清单。每个小检查点附独立协议样本、本地公共 SDK 测试和分包/架构文档；Windows/Linux GitHub CI 留到 PR 和显式发布门禁执行，经授权的远端推送只面向 dev。原生传递、显式转换、真实模型验证分别验收；后台、计费、代理服务、工具执行和任务托管不纳入 SDK。

本项目使用 [Apache-2.0 许可证](LICENSE)。贡献与安全问题请参阅 [CONTRIBUTING.md](CONTRIBUTING.md) 和 [SECURITY.md](SECURITY.md)。

当前火山 LLM 真实联调只需要四个环境变量：启用开关、API Key、Chat 模型、Responses 模型，详见 [测试配置规范](docs/testing.md) 和 [.env.example](.env.example)。运行 `pwsh -File eng/Test-Live.ps1`；Windows 下支持读取本机当前用户变量，不自动加载 `.env`。

离线测试共 2069 项通过：Rerank 核心 34 项、Embedding 核心 34 项、Embedding.OpenAI 87 项、Embedding.Google 114 项、显式转换 1367 项、OpenAI 199 项、火山 93 项、Claude 22 项、Gemini 36 项、LLM 核心 65 项及其他 18 项；转换回归覆盖嵌套字段损耗、待回填工具关联、ID 冲突、完整及截断流生命周期、done 与最终快照一致性，以及缓存和用量口径。原有 OpenAI/火山协议用例继续保留，并增加原生压缩与功能绑定验证。标准方舟 API 的 `doubao-seed-evolving` 已实测通过普通/流式调用、工具调用与结果回填、严格 JSON Schema、推理开关、输出截断、响应链续接及加密推理项原样回传。**两种协议的 `tool_choice=none` 严格测试仍失败**：最终请求参数正确，但上游仍返回工具调用。失败断言保留，详见 [参数与协议验证记录](docs/llm-parameter-verification.md)。OpenAI、Claude、Gemini 和显式转换当前只有离线验证。

运行 `pwsh -File eng/Test-Live.ps1 -Suite Parameters` 或 `-Suite All` 执行扩展联调；增加 `-Scenario Structured`、`Tools`、`ToolChoice`、`Reasoning`、`Limits` 或 `Replay` 可单独选组。默认冒烟五次请求，参数套件计划 26 次，全部计划 31 次，单进程硬上限 32 次。上述工具禁用问题存在期间，参数及全部套件会返回失败。其他模型、供应商、能力和 Redis 跨进程联调仍需单独验证；普通单元测试和自动 CI 保持离线。

日常开发检查点只做本地构建、测试和打包检查。GitHub CI 仅由面向 main 的 PR 或显式 NuGet 发布流程复用触发，不因普通 push 运行，也不提供独立手动 CI 入口；PR 合并和发布仍保留 Windows/Linux 双平台门禁。
