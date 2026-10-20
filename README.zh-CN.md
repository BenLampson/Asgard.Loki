# Asgard.Loki

[English](README.md) | **简体中文**

SDK 范围固定为 **LLM、音频、视频、图片、Embedding、Rerank** 六类；Embedding/Rerank 继续使用独立能力包及供应商分包，不并入 LLM。不做模型微调、独立文件管理、后台或计费。

目前未实现模型微调或供应商通用文件管理（独立上传、列举、查询、下载、删除）。已有音频和图片上传，是为转写、翻译或图片编辑请求提供输入素材。

纯 .NET 10 / C# 14 类库：统一 `LokiClient` 入口，能力及供应商按需安装。英文 README 是完整使用说明，结构规则见 [架构规范](docs/architecture.md)。

`Asgard.Loki.Infrastructure` 提供共享的底层 SSE 解析和有界 HTTP 字节读取；入口仍只引用统一抽象，六类能力各自维护协议、结果和状态。Video 提供原生提交/查询与持久化完整目标引用契约；[方舟视频包](src/Asgard.Loki.Video.Volcengine/README.md) 、[万相视频包](src/Asgard.Loki.Video.Ali/README.md)和[Gemini Veo 视频包](src/Asgard.Loki.Video.Google/README.md) 支持各自原生视频任务。


可选的[原生音频任务契约](src/Asgard.Loki.Audio/README.md#native-audio-tasks)支持显式提交、绑定原目标的状态查询、原生批量查询与独立结果读取，由应用提供适配器。批量缺失任务明确报告，不用媒体地址推断完成；能力核心本身不实现供应商音乐协议。

## 已实现

| 能力 | 供应商包 | 支持内容 |
| --- | --- | --- |
| LLM | `Asgard.Loki.LLM.Volcengine` | 方舟 Chat Completions、Responses，JSON/SSE，响应链与完整 Session 目标绑定 |
| LLM | `Asgard.Loki.LLM.OpenAI` | OpenAI Chat Completions、Responses，JSON/SSE；显式原生 compaction（JSON）、Responses WebSocket 与独立 Realtime 原生事件 |
| LLM | `Asgard.Loki.LLM.Anthropic` | Claude Messages 原生 JSON/SSE，保留工具、推理签名及未知字段 |
| LLM | `Asgard.Loki.LLM.Google` | Gemini generateContent 原生 JSON/SSE，保留原生多模态与工具结构 |
| LLM | `Asgard.Loki.LLM.Ollama` | 原生 Chat/generate JSON/NDJSON，保留工具关联、prompt/context、thinking、format；显式匿名或 Bearer 身份 |
| LLM | `Asgard.Loki.LLM.Azure` | 显式 v1/日期版本 Chat、Responses JSON/SSE；api-key 或借用外部主体令牌；过滤/用量尾帧及终态校验 |
| LLM | `Asgard.Loki.LLM.GoogleVertex` | 标准 Vertex Gemini/Claude JSON/SSE 与借用 External；独立 Express Gemini JSON/SSE 与显式 API Key；MaaS Chat JSON/SSE 与借用令牌 |
| LLM | `Asgard.Loki.LLM.AWS` | Bedrock Claude、独立 Converse 与 Nova JSON/二进制 EventStream；显式目标，借用 SigV4 或显式 Bedrock API Key/Bearer |
| 显式转换 | `Asgard.Loki.LLM.Conversion` | 四种文本协议间十二个方向的公共子集请求、响应和流；另支持 Nova ↔ Converse 原生完整 JSON 显式转换 |
| 音频 | `Asgard.Loki.Audio.Volcengine` | V3 HTTP TTS；显式 V1 HTTP query / 二进制 WebSocket submit、原生 JSON、元数据保留与音频聚合 |
| 音频 | `Asgard.Loki.Audio.OpenAI` | Speech 原生二进制及显式 SSE、音频增量/done、自定义 voice 与原始用量 |
| 兼容音频配置 | `Asgard.Loki.Audio.OpenAI` | 显式硅基流动音色 TTS、multipart 转录；复用已有适配器，原始参数/结果和错误保留 |
| 音频 | `Asgard.Loki.Audio.MiniMax` | 原生同步 `t2a_v2`、完整 JSON、hex 音频或原始 URL；不自动下载音频或字幕 |
| 音频识别/翻译 | `Asgard.Loki.Audio.Cloudflare` | 显式原始二进制识别或 JSON/Base64 原生 task；完整结果信封、借用上传，不自动切换格式 |
| Azure 音频 | `Asgard.Loki.Audio.Azure` | 显式部署/API 版本/api-key；原生 JSON 语音与 multipart 转录/翻译，可选固定请求体模型，不猜部署映射 |
| 音乐／歌词任务 | `Asgard.Loki.Audio.SunoAPI` | 应用显式配置代理；原生 MUSIC/LYRICS 提交、状态／批量 POST 与独立结果 GET | 单次原生 JSON；无后台轮询或媒体下载 |
| 文件转录/翻译 | `Asgard.Loki.Audio.OpenAI` | 原生 multipart、完整 JSON/字幕结果及转录 delta/segment/done SSE |
| 图片 | `Asgard.Loki.Image.OpenAI` | Images Generations、原生 JSON/multipart 编辑、Base64/URL、生成/编辑部分与最终图像 SSE |
| 图片 | `Asgard.Loki.Image.Azure` | 原生日期版本部署路径；JSON 生成及 multipart 编辑；保留完整图片、过滤信息和用量 | 原生生成／编辑 SSE；保留逐图事件与整次编辑完成标志 |
| 图片 | `Asgard.Loki.Image.Volcengine` | 原生方舟生成／图像输入、无提示词图层原生入口；保留混合结果 | 保留逐图成功／失败与汇总 SSE；支持单图原生 EOF |
| 图片 | `Asgard.Loki.Image.Ali` | 原生多模态 JSON／图文 SSE；显式现代与旧版图片异步提交 | 单次任务查询，保留文本／图片／用量／失败 |
| 图片 | `Asgard.Loki.Image.Google` | Gemini 原生图片生成／编辑输入；保留内联图片、签名与安全结果 | 原生 SSE、候选终态及用量元数据 |
| 图片 | `Asgard.Loki.Image.GoogleVertex` | 显式 Vertex 项目／Express 资源及 Bearer／API Key 认证；保留图片与安全结果 | 原生 Gemini JSON／SSE，保留终态与用量 |
| 嵌入 | `Asgard.Loki.Embedding.OpenAI` | 原生 Embeddings、文本/token 批输入、Float/Base64、原始索引及完整 JSON |
| 嵌入 | `Asgard.Loki.Embedding.Google` | 原生单条/同步批嵌入、原生配置与多模态 JSON、按原顺序返回向量 |
| 嵌入 | `Asgard.Loki.Embedding.Ollama` | 原生文本/媒体混合批输入、设置与可选计数、按原顺序返回向量 |
| 嵌入 | `Asgard.Loki.Embedding.Jina` | 原生文本/媒体/融合组、带输入索引的单向量与逐 token Float/Base64 多向量 |
| 重排 | `Asgard.Loki.Rerank.Cohere`、自定义适配器 | 显式 v1/v2 原生调用、原文档索引/分数、完整 JSON 与固定目标路由 |
| 重排 | `Asgard.Loki.Rerank.Jina` | 原生文本/图片查询、候选文档、listwise 选项、原始索引与可选文档向量 |
| 重排 | `Asgard.Loki.Rerank.Ali` | DashScope 原生嵌套文本/媒体请求、原始文档索引、分数与完整用量 |
| 重排 | `Asgard.Loki.Rerank.SiliconFlow` | 原生标量/文本/图片请求、原始索引/文档及可选 token 元数据 |
| 视频 | `Asgard.Loki.Video.Volcengine` | 方舟原生任务提交/查询、原始媒体/资产引用与结果地址，持久化完整目标绑定 |
| 视频 | `Asgard.Loki.Video.Volcengine` | 即梦 Visual 原生签名提交/查询，固定 req_key，原始媒体、结果及可选查询配置 |
| 视频 | `Asgard.Loki.Video.Ali` | 百炼万相原生 video-generation/image2video 提交与任务查询，完整目标绑定及原始结果 |
| 视频 | `Asgard.Loki.Video.Google` | Gemini Veo 原生长操作提交、查询与显式结果复制；预览模型公告于 2026-10-22 下线 |
| 视频 | `Asgard.Loki.Video.GoogleVertex` | Vertex Veo 完整资源作用域内原生提交/查询、借用令牌、原始 GCS 与内联结果 |
| 视频 | `Asgard.Loki.Video.MiniMax` | MiniMax 原生 v1/v2 显式提交/查询、v1 视频结果元数据，保留媒体、用量和错误 |
| 视频 | `Asgard.Loki.Video.Kling` | 可灵原生文生/图生视频提交与对应服务查询，显式 API Key 或 AK/SK JWT，保留完整原生结果 |
| 视频 | `Asgard.Loki.Video.Vidu` | 显式文生/图生/首尾帧/参考生视频与 creations 查询，原生 Token 鉴权，保留全部结果及元数据 |
| 视频 | `Asgard.Loki.Video.XAI` | xAI 原生生成/编辑/延长与单次查询，原始媒体、审核和用量，明确保留空正文 202 |
| 视频 | `Asgard.Loki.Video.Midjourney` | 明确配置的非官方代理视频生成/延长与单次查询，保留原生码、父任务索引及全部视频结果 |
| 视频素材库 | `Asgard.Loki.Video.Volcengine` | 方舟 Asset/AssetGroup 十个原生操作，独立 AK/SK 签名，显式分页和状态查询 |

Video 结果可选保留原始 HTTP 状态、正文和媒体类型；空正文 202 的运行中窗口通过 `HasJsonData=false` 明确没有原生 JSON，不补造状态或对象，详见[结果契约](src/Asgard.Loki.Video/README.md)。

供应商包仅引用所属能力，不会带入其他能力包。入口只引用统一抽象。音频核心的可选原生 JSON 调用契约保留 voice 对象和未知参数，并在源枚举及释放成功后才发出完成块；[OpenAI 语音](src/Asgard.Loki.Audio.OpenAI/README.md) 已接入二进制和显式 SSE 聚合/流式读取；SSE 信封保留原生 JSON、原始 data 文本、事件身份及原始用量，done 在尾部读取和释放成功后才交付。

Loki 保持可嵌入应用的标准 SDK 定位。Claude 使用 `loki.LLM.ClaudeMessages`，Gemini 使用 `loki.LLM.Gemini`；目标需要明确设置对应 `Protocols`。调用者提供 HTTP 工厂、凭据和 Session 依赖，不增加转发服务、后台或计费功能。

跨协议转换必须显式安装并注册 `ConvertingLlmProvider`，供应商包不引用转换包。默认拒绝未知字段或语义损耗，结果通过 `Metadata.Conversion` 暴露原始上游 JSON 与诊断。支持文本、内联图片、函数工具及结果关联、基础生成参数和 token 用量；推理签名、加密项和跨协议响应链始终拒绝转换。文本即时流式输出，函数参数完整后输出。完整支持矩阵及限制见[转换包说明](src/Asgard.Loki.LLM.Conversion/README.md)，原生调用不经过此转换层。

Session 指纹新增适配实现和转换配置身份；旧版本建立的绑定将校验失败，需要调用者使用新 Session ID 或管理旧绑定过期，SDK 不静默重新绑定。

显式转换已扩展推理强度、可表达的 Claude/Gemini 数值预算及动态/关闭模式、摘要显示意图和四协议 JSON Schema 输出外壳。Schema 约束原样保留；已支持 Gemini 现行 `responseFormat.text` 的读取及显式目标选项 `GeminiOutputFormat.ResponseFormat`，默认仍保留旧 JSON Schema 外壳；无名 Schema 转 OpenAI 需明确提供 `DefaultSchemaName`，无法等价表达的包装元数据、严格程度和预算替代仍需诊断及显式损耗同意。手动 thinking 模式由调用者提供 `TargetThinkingBudgetTokens`，不推算预算、不改模型后缀或调大输出上限。模型支持范围由调用者确认，详见[转换配置与限制](src/Asgard.Loki.LLM.Conversion/README.md#explicit-generation-settings)。

LLM Session 使用标准 `IDistributedCache` 保存完整目标，首次绑定需要显式注入共享协调器。不会因为重连、Key 池变化或目标失效而偷偷切换服务器、模型和 Key。单元测试使用 `MemoryDistributedCache` 与测试协调器，不依赖真实 Redis。

原生 OpenAI 压缩窗口通过 `loki.LLM.Responses.CompactAsync` 显式调用，目标需同时声明 Responses 协议和 `Features = LlmTargetFeatures.ResponsesCompaction`，供应商还必须实现 `IResponsesCompactionAdapter`。完整结果保留所有窗口项及加密内容，由调用者决定后续输入，不自动压缩或裁剪历史。可选 `PreviousResponseId` 继续校验 Session 归属；压缩结果自身 ID 不登记为生成响应链。原目标不支持该操作时明确失败，不切换目标。火山和转换装饰器当前不声明该能力，初始化及示例见[英文说明](README.md#native-responses-compaction)。

原生 OpenAI Responses WebSocket 通过 `OpenWebSocketAsync` 显式建立连接，目标需声明 `ResponsesWebSocket` 功能。调用者负责收发原生事件及连接释放；完整目标、响应归属、多 lane、steering 和失败/不完整终态均有离线测试。示例及资源上限见[供应商说明](src/Asgard.Loki.LLM.OpenAI/README.md#native-responses-websocket)，未进行真实 OpenAI 联调。Image 已提供独立 JSON 与借用多图/遮罩流的 multipart 编辑契约及可选原生事件流，可接自定义适配器；[Image 说明](src/Asgard.Loki.Image/README.md#native-image-editing-contracts)明确了支持范围。OpenAI 原生 JSON/multipart 非流式及 SSE 编辑已接入，保留多图顺序、独立遮罩、文件字节、完整 JSON 和事件身份；完成项等待正常 EOF 及传输资源释放后公开，OpenAI 图片 variations、音乐生成、视频生成和事件重放尚不在这些包的支持范围内。Audio 已提供原生文件转录/翻译扩展契约，支持自定义适配器、借用上传流和完整原生结果；OpenAI 原生 multipart 转录/翻译已接入，上传流保持借用关系，转录 SSE 已保留完整事件/用量/SSE 身份，并校验分段引用与终态。公共请求保留扩展参数，结果保留供应商完整 JSON；具体模型、音色与账号授权仍需真实联调验证。

初始化、调用、支持矩阵及验证命令请参阅 [英文 README](README.md)。CI 在 Windows/Linux 上执行构建、测试及二十九个 NuGet 包的依赖与元数据检查，打包产物作为运行附件保存。独立的 `Publish NuGet` 工作流使用 OIDC 可信发布，默认仅验证登录；正式发布需手动选择 `publish` 并填写准确版本，步骤见 [发布说明](docs/releasing.md)。


原生 Realtime 通过 `loki.LLM.Realtime.OpenAsync` 使用，目标声明 `LlmProtocol.Realtime`。调用者负责原生事件、音频数据和工具结果的收发；SDK 保留所有状态与未知字段，正常关闭不表示生成成功，不自动执行工具、播放音频或重连。初始化、资源限制和离线验证见[OpenAI 包](src/Asgard.Loki.LLM.OpenAI/README.md#native-realtime-websocket)。


火山 LLM 真实联调仅使用[测试规范](docs/testing.md)及 [.env.example](.env.example) 的四个变量。运行 `pwsh -File eng/Test-Live.ps1`，扩展参数用 `-Suite Parameters` 或 `-Suite All`。两种协议的 `tool_choice=none` 严格失败断言仍保留，详见[实测记录](docs/llm-parameter-verification.md)。Realtime、Video 及其他供应商的真实模型验收状态以各包说明为准；离线协议测试不代表真实上游验证。


本项目使用 [Apache-2.0 许可证](LICENSE)。贡献与安全问题见 [CONTRIBUTING.md](CONTRIBUTING.md) 和 [SECURITY.md](SECURITY.md)。
