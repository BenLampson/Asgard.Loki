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
| LLM | `Asgard.Loki.LLM.Ollama` | 原生 Chat/generate JSON/NDJSON，保留工具关联、prompt/context、thinking、format；显式匿名或 Bearer 身份 |
| LLM | `Asgard.Loki.LLM.Azure` | 显式 v1/日期版本 Chat、Responses JSON/SSE；api-key 或借用外部主体令牌；过滤/用量尾帧及终态校验 |
| LLM | `Asgard.Loki.LLM.GoogleVertex` | 标准 Vertex Gemini/Claude JSON/SSE 与借用 External；独立 Express Gemini JSON/SSE 与显式 API Key；MaaS Chat JSON/SSE 与借用令牌 |
| LLM | `Asgard.Loki.LLM.AWS` | Bedrock Claude 与独立 Converse JSON/二进制 EventStream；显式目标，借用 SigV4 或显式 Bedrock API Key/Bearer |
| 显式转换 | `Asgard.Loki.LLM.Conversion` | 四种文本协议间十二个方向的公共子集请求、响应和流式转换 |
| 音频 | `Asgard.Loki.Audio.Volcengine` | V3 HTTP 单向流式 TTS、音频聚合、新旧认证方式 |
| 音频 | `Asgard.Loki.Audio.OpenAI` | Speech 原生二进制及显式 SSE、音频增量/done、自定义 voice 与原始用量 |
| 文件转录/翻译 | `Asgard.Loki.Audio.OpenAI` | 原生 multipart、完整 JSON/字幕结果及转录 delta/segment/done SSE |
| 图片 | `Asgard.Loki.Image.OpenAI` | Images Generations、原生 JSON/multipart 编辑、Base64/URL、生成/编辑部分与最终图像 SSE |
| 嵌入 | `Asgard.Loki.Embedding.OpenAI` | 原生 Embeddings、文本/token 批输入、Float/Base64、原始索引及完整 JSON |
| 嵌入 | `Asgard.Loki.Embedding.Google` | 原生单条/同步批嵌入、原生配置与多模态 JSON、按原顺序返回向量 |
| 嵌入 | `Asgard.Loki.Embedding.Ollama` | 原生文本/媒体混合批输入、设置与可选计数、按原顺序返回向量 |
| 嵌入 | `Asgard.Loki.Embedding.Jina` | 原生文本/媒体/融合组、带输入索引的单向量与逐 token Float/Base64 多向量 |
| 重排 | `Asgard.Loki.Rerank.Cohere`、自定义适配器 | 显式 v1/v2 原生调用、原文档索引/分数、完整 JSON 与固定目标路由 |
| 重排 | `Asgard.Loki.Rerank.Jina` | 原生文本/图片查询、候选文档、listwise 选项、原始索引与可选文档向量 |
| 重排 | `Asgard.Loki.Rerank.Ali` | DashScope 原生嵌套文本/媒体请求、原始文档索引、分数与完整用量 |
| 重排 | `Asgard.Loki.Rerank.SiliconFlow` | 原生标量/文本/图片请求、原始索引/文档及可选 token 元数据 |
| 视频 | — | 保留项目骨架 |

供应商包仅引用所属能力，不会带入其他能力包。入口只引用统一抽象。音频核心的可选原生 JSON 调用契约保留 voice 对象和未知参数，并在源枚举及释放成功后才发出完成块；[OpenAI 语音](src/Asgard.Loki.Audio.OpenAI/README.md) 已接入二进制和显式 SSE 聚合/流式读取；SSE 信封保留原生 JSON、原始 data 文本、事件身份及原始用量，done 在尾部读取和释放成功后才交付。

Loki 保持可嵌入应用的标准 SDK 定位。Claude 使用 `loki.LLM.ClaudeMessages`，Gemini 使用 `loki.LLM.Gemini`；目标需要明确设置对应 `Protocols`。调用者提供 HTTP 工厂、凭据和 Session 依赖，不增加转发服务、后台或计费功能。

跨协议转换必须显式安装并注册 `ConvertingLlmProvider`，供应商包不引用转换包。默认拒绝未知字段或语义损耗，结果通过 `Metadata.Conversion` 暴露原始上游 JSON 与诊断。支持文本、内联图片、函数工具及结果关联、基础生成参数和 token 用量；推理签名、加密项和跨协议响应链始终拒绝转换。文本即时流式输出，函数参数完整后输出。完整支持矩阵及限制见[转换包说明](src/Asgard.Loki.LLM.Conversion/README.md)，原生调用不经过此转换层。

Session 指纹新增适配实现和转换配置身份；旧版本建立的绑定将校验失败，需要调用者使用新 Session ID 或管理旧绑定过期，SDK 不静默重新绑定。

显式转换已扩展推理强度、可表达的 Claude/Gemini 数值预算及动态/关闭模式、摘要显示意图和四协议 JSON Schema 输出外壳。Schema 约束原样保留；已支持 Gemini 现行 `responseFormat.text` 的读取及显式目标选项 `GeminiOutputFormat.ResponseFormat`，默认仍保留旧 JSON Schema 外壳；无名 Schema 转 OpenAI 需明确提供 `DefaultSchemaName`，无法等价表达的包装元数据、严格程度和预算替代仍需诊断及显式损耗同意。手动 thinking 模式由调用者提供 `TargetThinkingBudgetTokens`，不推算预算、不改模型后缀或调大输出上限。模型支持范围由调用者确认，详见[转换配置与限制](src/Asgard.Loki.LLM.Conversion/README.md#explicit-generation-settings)。

LLM Session 使用标准 `IDistributedCache` 保存完整目标，首次绑定需要显式注入共享协调器。不会因为重连、Key 池变化或目标失效而偷偷切换服务器、模型和 Key。单元测试使用 `MemoryDistributedCache` 与测试协调器，不依赖真实 Redis。

原生 OpenAI 压缩窗口通过 `loki.LLM.Responses.CompactAsync` 显式调用，目标需同时声明 Responses 协议和 `Features = LlmTargetFeatures.ResponsesCompaction`，供应商还必须实现 `IResponsesCompactionAdapter`。完整结果保留所有窗口项及加密内容，由调用者决定后续输入，不自动压缩或裁剪历史。可选 `PreviousResponseId` 继续校验 Session 归属；压缩结果自身 ID 不登记为生成响应链。原目标不支持该操作时明确失败，不切换目标。火山和转换装饰器当前不声明该能力，初始化及示例见[英文说明](README.md#native-responses-compaction)。

原生 OpenAI Responses WebSocket 通过 `OpenWebSocketAsync` 显式建立连接，目标需声明 `ResponsesWebSocket` 功能。调用者负责收发原生事件及连接释放；完整目标、响应归属、多 lane、steering 和失败/不完整终态均有离线测试。示例及资源上限见[供应商说明](src/Asgard.Loki.LLM.OpenAI/README.md#native-responses-websocket)，未进行真实 OpenAI 联调。Image 已提供独立 JSON 与借用多图/遮罩流的 multipart 编辑契约及可选原生事件流，可接自定义适配器；[Image 说明](src/Asgard.Loki.Image/README.md#native-image-editing-contracts)明确了支持范围。OpenAI 原生 JSON/multipart 非流式及 SSE 编辑已接入，保留多图顺序、独立遮罩、文件字节、完整 JSON 和事件身份；完成项等待正常 EOF 及传输资源释放后公开，图片 variations、音乐生成、视频生成和事件重放仍未实现。Audio 已提供原生文件转录/翻译扩展契约，支持自定义适配器、借用上传流和完整原生结果；OpenAI 原生 multipart 转录/翻译已接入，上传流保持借用关系，转录 SSE 已保留完整事件/用量/SSE 身份，并校验分段引用与终态。公共请求保留扩展参数，结果保留供应商完整 JSON；具体模型、音色与账号授权仍需真实联调验证。

初始化、调用、支持矩阵及验证命令请参阅 [英文 README](README.md)。CI 在 Windows/Linux 上执行构建、测试及二十八个 NuGet 包的依赖与元数据检查，打包产物作为运行附件保存。独立的 `Publish NuGet` 工作流使用 OIDC 可信发布，默认仅验证登录；正式发布需手动选择 `publish` 并填写准确版本，步骤见 [发布说明](docs/releasing.md)。

已于 2026-10-19 再次对照 New API `058c8231`，按本库 `5403f1d` 更新[模型调用补齐计划](docs/model-call-roadmap.md#remaining-sdk-work-at-the-current-checkpoint)。原生 Claude/Gemini、已声明范围的四协议转换、OpenAI compaction/Responses WebSocket、OpenAI/Google Embedding 和独立 Rerank 核心已实现。[Cohere Rerank](src/Asgard.Loki.Rerank.Cohere/README.md) 已补显式 v1/v2，[Jina Rerank](src/Asgard.Loki.Rerank.Jina/README.md) 已补原生文本/多模态/listwise 和独立离线样本，下一步收口转换字段/模式清单，再补剩余原生供应商；Responses 引用源流已补原始索引、Unicode 范围、done/最终快照与有界状态校验，允许显示损耗也不能绕过；Gemini 源流也已补跨帧累计来源索引、metadata 尾帧和硬资源边界校验；Responses ↔ Gemini 已补显式单一累计文本 part、OpenAI 端点索引单位声明下的双向引用流映射，源正常结束后才公开原生注解或 grounding，并验证 done/最终快照一致性；另有显式 `ConcatenatedTextParts` 可将 Responses 的顺序纯文本 part/消息拼接到一个累计目标，并精确平移引用；原单文本模式不变，工具混排、逆序 part 和交错正文仍明确拒绝；其他引用流布局和方向仍待补；已声明 UTF-8 的纯文本文件也已支持 Responses/Claude/Gemini 六向转换并保留文档容器，Chat 非 PDF 文件明确拒绝。转换继续收口搜索执行/结果、文档媒体、工具结果/错误及复杂输出。Ollama 原生 Embedding、Jina dense/per-token 和 DashScope Rerank 已补齐各自声明范围；[SiliconFlow Rerank](src/Asgard.Loki.Rerank.SiliconFlow/README.md) 也已补标量/文本/图片原生调用及独立索引/结果校验；Jina PDF/packed/sparse 等形态仍待补。OpenAI Speech 已补原生二进制及显式 SSE，文件识别/翻译、转录 SSE 和图像原生 JSON/multipart/SSE 编辑也已补离线协议验证；原有图像生成 SSE 也已补终态/错误/EOF/释放边界回归，原始完成项等待完整传输结束后才公开。

后续继续补搜索/引用转换、其他原生音频供应商、云认证及其他原生供应商、Realtime 和 Video；旧式 completion、moderation、原生模型发现和可独立调用的搜索/fetch 保留为低优先级清单。每个小检查点附独立协议样本、本地公共 SDK 测试和分包/架构文档；Windows/Linux GitHub CI 留到 PR 和显式发布门禁执行，经授权的远端推送只面向 dev。原生传递、显式转换、真实模型验证分别验收；后台、计费、代理服务、工具执行和任务托管不纳入 SDK。

本项目使用 [Apache-2.0 许可证](LICENSE)。贡献与安全问题请参阅 [CONTRIBUTING.md](CONTRIBUTING.md) 和 [SECURITY.md](SECURITY.md)。

当前火山 LLM 真实联调只需要四个环境变量：启用开关、API Key、Chat 模型、Responses 模型，详见 [测试配置规范](docs/testing.md) 和 [.env.example](.env.example)。运行 `pwsh -File eng/Test-Live.ps1`；Windows 下支持读取本机当前用户变量，不自动加载 `.env`。

[Anthropic 原生传输](src/Asgard.Loki.LLM.Anthropic/README.md#explicit-authentication-and-http-policy) 现支持显式 Key/Bearer 选择和有界终态校验，标准、Runtime、Mantle Messages 分别有完整离线样本。旧 Anthropic Session 需要新 ID 或到期。显式转换另验证推理 high/off、完整 Schema 约束、损耗许可和迟到错误；这是协议测试；[AWS 包的独立 Mantle 适配器](src/Asgard.Loki.LLM.AWS/README.md#explicit-mantle-sigv4-messages) 现已补原生 SigV4 JSON/SSE，Nova InvokeModel 完整 JSON 已有独立 `.LLM.BedrockNova` 入口；其二进制流和显式转换，以及其他计划缺口仍待补齐。

离线测试共 5983 项通过：Bedrock Claude 与 Converse JSON/EventStream 与 Mantle Messages 与 Nova JSON 539 项、Vertex 原生 JSON/SSE 451 项、Azure 原生 JSON/SSE 155 项、Ollama Chat/generate 167 项、SiliconFlow Rerank 143 项、OpenAI Image 350 项、Image 核心 96 项、OpenAI Audio 378 项、Jina Embedding 134 项、Ali Rerank 124 项、Ollama Embedding 125 项、Jina Rerank 133 项、Cohere Rerank 120 项、Rerank 核心 34 项、Embedding 核心 34 项、Embedding.OpenAI 106 项、Embedding.Google 114 项、显式转换 1917 项、OpenAI 284 项、火山 93 项、Claude 127 项、Gemini 36 项、LLM 核心 191 项、音频核心 122 项及其他 10 项；完整 Chat/Responses URL 引用与 Gemini Web grounding 已支持显式单位声明下的双向转换；转换回归覆盖嵌套字段损耗、待回填工具关联、ID 冲突、完整及截断流生命周期、done 与最终快照一致性，以及缓存和用量口径。原有 OpenAI/火山协议用例继续保留，并增加原生压缩与功能绑定验证。标准方舟 API 的 `doubao-seed-evolving` 已实测通过普通/流式调用、工具调用与结果回填、严格 JSON Schema、推理开关、输出截断、响应链续接及加密推理项原样回传。**两种协议的 `tool_choice=none` 严格测试仍失败**：最终请求参数正确，但上游仍返回工具调用。失败断言保留，详见 [参数与协议验证记录](docs/llm-parameter-verification.md)。OpenAI、Claude、Gemini、Embedding/Rerank 供应商及显式转换当前只有离线验证。

运行 `pwsh -File eng/Test-Live.ps1 -Suite Parameters` 或 `-Suite All` 执行扩展联调；增加 `-Scenario Structured`、`Tools`、`ToolChoice`、`Reasoning`、`Limits` 或 `Replay` 可单独选组。默认冒烟五次请求，参数套件计划 26 次，全部计划 31 次，单进程硬上限 32 次。上述工具禁用问题存在期间，参数及全部套件会返回失败。其他模型、供应商、能力和 Redis 跨进程联调仍需单独验证；普通单元测试和自动 CI 保持离线。

日常开发检查点只做本地构建、测试和打包检查。GitHub CI 仅由面向 main 的 PR 或显式 NuGet 发布流程复用触发，不因普通 push 运行，也不提供独立手动 CI 入口；PR 合并和发布仍保留 Windows/Linux 双平台门禁。

[Ollama 原生 Chat/generate](src/Asgard.Loki.LLM.Ollama/README.md) 已有 167 个离线用例（Chat 97、generate 62、公共快照编码 8）：独立完整请求/响应/事件样本、工具关联、大整数、请求快照、完整 Session、认证隔离和终态/资源故障。入口为 `loki.LLM.OllamaChat`，目标显式声明 `LlmProtocol.OllamaChat`。仅固定实际模型和方法流标志，其他原生字段保留；流式 done/error 等待 EOF 与资源释放成功。原生 generate 通过独立的 `.LLM.OllamaGenerate` 和 `LlmProtocol.OllamaGenerate` 调用，context 仅由调用者显式回传，不自动缓存或转换成 Chat。Ollama 跨协议转换、云身份刷新及真实模型验证仍未实现；四协议转换范围保持原有声明。当前全仓 5983 个离线测试，28 个生产分包。本地检查点不运行 GitHub Actions。

Claude/Gemini 工具错误回填已支持两种显式格式：`LlmToolErrorFormat.GeminiResponseObject` 保留完整原生 response JSON，`GeminiErrorText` 保留原始错误文本。默认仍拒绝跨协议错误；Chat/Responses 无等价失败标志时继续拒绝，普通结果的根级 error 键也不会被隐式升级成 Gemini 错误。原始调用 ID、逆序同名回填、内容上限、旧 Session 和晚到流错误都有回归；该检查点新增 84 个离线用例，当时算法身份为 `conversion-v24`；当前引用流扩展使用 `conversion-v25`。详见[格式说明](src/Asgard.Loki.LLM.Conversion/README.md#explicit-claudegemini-tool-errors)。媒体错误数组、持续回填/异步调度及更广协议对齐仍待补齐。

公共 LLM 快照已在序列化和 Session 等待之前检查原生 DOM/扩展 JSON 的字符串及属性名，防止非法 Unicode 被替换后改变工具 ID、错误文本和未知字段。压缩及双工发送同样检查；模型/目标/凭据/实现和调用身份进入指纹前也检查编码。合法 Unicode、原生数据及既有有效指纹不变。本轮新增 65 个离线回归，其中最初 8 个公共转换用例已先复现失败再修复；自定义 JsonValue/转换器仍由调用者负责其序列化策略，不反射执行应用对象属性。见[快照编码说明](src/Asgard.Loki.LLM/README.md#native-json-snapshot-encoding)。

LLM 已新增显式 `LlmCredential.External(id, principalIdentity)`：核心只保存稳定主体引用，不持有短期令牌或认证源。供应商必须明确声明支持；旧 Key-only 适配器直接调用也会提前失败。外部指纹分量采用独立结构化边界，主体/认证种类变化拒绝旧 Session，原 Key/匿名指纹字节保持兼容。新增 38 个离线回归；[Azure 原生端点及借用式令牌源](src/Asgard.Loki.LLM.Azure/README.md) 已实现完整 JSON 和原生 SSE；155 个离线用例验证两类路由、两类认证、原生字段、主体/配置绑定、过滤/用量尾帧、终态和资源生命周期，尚未进行真实云端认证。见[主体契约](src/Asgard.Loki.LLM/README.md#external-authentication-identity)。

[Vertex 原生 Gemini/Claude JSON/SSE](src/Asgard.Loki.LLM.GoogleVertex/README.md) 已接入完整资源名和借用令牌源；451 项离线用例验证完整报文/流事件、资源/主体绑定、认证、候选/提示拦截终态、ProtoJSON 数值、用量及 EOF/释放边界。原生字段与调用者提供的工具 ID 不静默删除，但不据此声称上游接受 Developer API 特有字段。MaaS Chat 原生 JSON/SSE 已交付，真实云端验证仍待完成。

Vertex Claude JSON 已通过既有 `.LLM.ClaudeMessages` 接入 `rawPredict`，正文版本与工具/签名/结构化输出保持原生；新增 40 项离线回归。默认省略版本选项保持既有 Gemini 绑定身份，显式版本、主体或协议变化则拒绝旧 Session。

Vertex Claude SSE 已接入 `streamRawPredict`，保留原生内容块/工具关联、推理签名、部分 JSON 参数、累计用量与未知事件。新增 58 项离线测试覆盖终态暂存、块关联、平台/原生错误、编码/资源故障及显式文本转换流；默认省略内容块策略保持旧绑定，显式变更策略拒绝旧 Session。

同一 Vertex 包新增 `GoogleVertexExpressLlmProvider`，显式使用 `publishers/google/models/{model}` 短资源及 `x-goog-api-key`，支持 Gemini JSON/SSE；不需要令牌源，也不隐式借用标准模式项目/主体。新增 67 项离线回归，验证报文/流事件、Key/目标绑定、默认头隔离和释放边界；未使用真实 Express 账户。
