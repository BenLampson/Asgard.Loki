# Asgard.Loki

[English](README.md) | **简体中文**

SDK 范围固定为 **LLM、音频、视频、图片、Embedding、Rerank** 六类；Embedding/Rerank 继续使用独立能力包及供应商分包，不并入 LLM。不做模型微调、独立文件管理、后台或计费。

目前未实现模型微调或供应商通用文件管理（独立上传、列举、查询、下载、删除）。已有音频和图片上传，是为转写、翻译或图片编辑请求提供输入素材。

纯 .NET 10 / C# 14 类库：统一 `LokiClient` 入口，能力及供应商按需安装。英文 README 是完整使用说明，结构规则见 [架构规范](docs/architecture.md)。

`Asgard.Loki.Infrastructure` 提供共享的底层 SSE/NDJSON 分帧和有界 HTTP 字节读取；入口仍只引用统一抽象，六类能力各自维护协议、结果和状态。Video 提供原生提交/查询与持久化完整目标引用契约；[方舟视频包](src/Asgard.Loki.Video.Volcengine/README.md) 、[万相视频包](src/Asgard.Loki.Video.Ali/README.md)和[Gemini Veo 视频包](src/Asgard.Loki.Video.Google/README.md) 支持各自原生视频任务。


可选的[原生音频任务契约](src/Asgard.Loki.Audio/README.md#native-audio-tasks)支持显式提交、绑定原目标的状态查询、原生批量查询与独立结果读取，由应用提供适配器。批量缺失任务明确报告，不用媒体地址推断完成；能力核心本身不实现供应商音乐协议。

## 已实现

| 能力 | 供应商包 | 支持内容 |
| --- | --- | --- |
| LLM | `Asgard.Loki.LLM.Volcengine` | 方舟 Chat Completions、Responses，JSON/SSE，响应链与完整 Session 目标绑定 |
| LLM | `Asgard.Loki.LLM.OpenAI` | OpenAI Chat Completions、Responses，JSON/SSE；原生 Moderations 完整 JSON；显式原生 compaction（JSON）、Responses WebSocket 与独立 Realtime 原生事件 |
| LLM | `Asgard.Loki.LLM.Anthropic` | Claude Messages 原生 JSON/SSE，保留工具、推理签名及未知字段 |
| LLM | `Asgard.Loki.LLM.Google` | Gemini generateContent 原生 JSON/SSE，保留原生多模态与工具结构 |
| LLM | `Asgard.Loki.LLM.Ollama` | 原生 Chat/generate JSON/NDJSON，保留工具关联、prompt/context、thinking、format；显式匿名或 Bearer 身份 |
| LLM | `Asgard.Loki.LLM.MiniMax` | 原生 v2 Chat JSON/SSE，保留推理、工具关联、敏感标记和用量；原生 base_resp 错误及终态完整性 |
| LLM | `Asgard.Loki.LLM.Perplexity` | 原生 Responses／Sonar Chat JSON/SSE，保留稀疏失败、全局终态、引用搜索信息及完整工具关联 |
| LLM | `Asgard.Loki.LLM.NewAPI` | 对已配置上游执行固定完整目标、实际模型与 API Key 的原生 alpha/search，保留状态、媒体类型和原始字节 |
| LLM | `Asgard.Loki.LLM.Cohere` | 原生 v1 Chat JSON/NDJSON，完整历史、引用、工具参数/结果与分片；独立于 Rerank |
| LLM | `Asgard.Loki.LLM.Cloudflare` | 显式原生 ai/run 文本 JSON/SSE；完整结果封装、工具数据与实际 DONE 帧；独立于兼容 OpenAI 配置 |
| LLM | `Asgard.Loki.LLM.Coze` | 原生 v3 Bot 回执/双层 Chat-Message SSE、显式状态/消息查询和完整目标引用；无自动轮询或伪造模型路由 |
| LLM | `Asgard.Loki.LLM.Dify` | 固定应用 Key 的原生聊天 JSON/SSE，显式 Chatbot/Agent/Chatflow 类型与完整工作流/TTS 尾流；无伪模型路由或后台托管 |
| LLM | `Asgard.Loki.LLM.Azure` | 显式 v1/日期版本 Chat、Responses 及原生文本补全 JSON/SSE；原生 Responses 压缩保留完整窗口，需目标显式声明能力；独立 Realtime JSON WebSocket；api-key 或借用外部主体令牌；过滤/用量尾帧及终态校验 |
| LLM | `Asgard.Loki.LLM.DeepSeek` | 原生 Search／Fetch 完整 JSON；独立固定服务及凭据；保留原生排序、评分、完整页面内容和空结果，无需模型／Session |
| LLM | `Asgard.Loki.LLM.Tencent` | 混元原生 JSON／SSE；供应商专属 TC3 和应用拥有的 External 主体凭据；保留工具分片、多候选终态及完整尾帧 |
| LLM | `Asgard.Loki.LLM.Xunfei` | 星火原生签名 WebSocket；完整消息窗口／逐条原生 JSON；文本及会话终态与正常关闭确认 |
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
| 兼容图片生成 | `Asgard.Loki.Image.OpenAI` | 显式 xAI 原生完整 JSON 配置，独立地址／模型／凭据并保留 data[] | 已做离线协议验证；xAI 编辑使用不同的原生 JSON 契约 |
| 图片编辑 | `Asgard.Loki.Image.XAI` | xAI 原生单图／有序多图 JSON 引用，保留完整结果 | 独立编辑路由，不转换 multipart 或管理文件 |
| 图片 | `Asgard.Loki.Image.Midjourney` | 显式非官方代理图片操作、原生任务／文本结果、输入上传与 seed 请求 | 完整目标绑定，单次／批量查询；不伪造同步生成结果 |
| 图片 | `Asgard.Loki.Image.Replicate` | 原生模型预测／JSON 编辑与显式图片输入上传，完整保留原生状态和结果 | 调用者驱动快照；不自动轮询／下载，严格区分未完成与成功 |
| 图片 | `Asgard.Loki.Image.Zhipu` | 智谱 v4 原生图片生成，保留图片字段别名、审核、时间戳和元数据 | 完整原生 JSON，不下载图片或补造元数据 |
| 图片 | `Asgard.Loki.Image.Azure` | 原生日期版本部署路径；JSON 生成及 multipart 编辑；保留完整图片、过滤信息和用量 | 原生生成／编辑 SSE；保留逐图事件与整次编辑完成标志 |
| 图片 | `Asgard.Loki.Image.Volcengine` | 原生方舟生成／图像输入和无提示词图层；独立即梦 Visual CVProcess 签名 JSON | 保留逐图成功／失败与汇总 SSE；支持单图原生 EOF |
| 图片 | `Asgard.Loki.Image.Ali` | 原生多模态 JSON／图文 SSE；显式现代与旧版图片异步提交 | 单次任务查询，保留文本／图片／用量／失败 |
| 图片 | `Asgard.Loki.Image.Google` | Gemini 原生图片生成／编辑输入；保留内联图片、签名与安全结果 | 原生 SSE、候选终态及用量元数据 |
| 图片 | `Asgard.Loki.Image.GoogleVertex` | 显式 Vertex 项目／Express 资源及 Bearer／API Key 认证；保留图片与安全结果 | 原生 Gemini JSON／SSE，保留终态与用量 |
| 图片 | `Asgard.Loki.Image.MiniMax` | 原生 image_generation JSON、参考图及 URL／Base64 数组 | 完整结果，保留计数与原生业务错误 |
| 图片 | `Asgard.Loki.Image.SiliconFlow` | 原生 image_size／batch_size／参考图与 images／timings／seed 结果 | 完整原始 JSON，不隐式转换 OpenAI 字段 |
| 嵌入 | `Asgard.Loki.Embedding.Azure` | 原生部署式嵌入，显式版本／api-key，保留文本／token 输入及 float／Base64 向量 | 保留批量顺序并验证索引覆盖；完整错误与有界 HTTP 生命周期 |
| 嵌入 | `Asgard.Loki.Embedding.OpenAI` | 原生 Embeddings、文本/token 批输入、Float/Base64、原始索引及完整 JSON；显式兼容文本配置（含 M3E／Workers AI）、有界原始错误和最终取消检查 |
| 嵌入 | `Asgard.Loki.Embedding.Google` | 原生单条/同步批嵌入、原生配置与多模态 JSON、按原顺序返回向量 |
| 嵌入 | `Asgard.Loki.Embedding.Ollama` | 原生文本/媒体混合批输入、设置与可选计数、按原顺序返回向量 |
| 嵌入 | `Asgard.Loki.Embedding.Jina` | 原生文本/媒体/融合组、带输入索引的单向量与逐 token Float/Base64 多向量 |
| 重排 | `Asgard.Loki.Rerank.Cohere`、自定义适配器 | 显式 v1/v2 原生调用、原文档索引/分数、完整 JSON 与固定目标路由 |
| 重排 | `Asgard.Loki.Rerank.Jina` | 原生文本/图片查询、候选文档、listwise 选项、原始索引与可选文档向量 |
| 重排 | `Asgard.Loki.Rerank.Ali` | DashScope 原生嵌套文本/媒体请求、原始文档索引、分数与完整用量 |
| 重排 | `Asgard.Loki.Rerank.SiliconFlow` | 原生标量/文本/图片请求、原始索引/文档及可选 token 元数据 |
| 重排 | `Asgard.Loki.Rerank.SGLang` | 原生文本／内容块、score／index 根数组、原始文档回显及 meta_info |
| 重排 | `Asgard.Loki.Rerank.OpenAICompatible` | 显式原生 results／index／relevance_score 协议、原始 JSON 候选和可缺省用量 |
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

原生 OpenAI／Azure 压缩窗口通过 `loki.LLM.Responses.CompactAsync` 显式调用，目标需同时声明 Responses 协议和 `Features = LlmTargetFeatures.ResponsesCompaction`，供应商还必须实现 `IResponsesCompactionAdapter`。完整结果保留所有窗口项及加密内容，由调用者决定后续输入，不自动压缩或裁剪历史。可选 `PreviousResponseId` 继续校验 Session 归属；压缩结果自身 ID 不登记为生成响应链。原目标不支持该操作时明确失败，不切换目标。火山和转换装饰器当前不声明该能力，初始化及示例见[英文说明](README.md#native-responses-compaction)。

原生 OpenAI Responses WebSocket 通过 `OpenWebSocketAsync` 显式建立连接，目标需声明 `ResponsesWebSocket` 功能。调用者负责收发原生事件及连接释放；完整目标、响应归属、多 lane、steering 和失败/不完整终态均有离线测试。示例及资源上限见[供应商说明](src/Asgard.Loki.LLM.OpenAI/README.md#native-responses-websocket)，未进行真实 OpenAI 联调。Image 已提供独立 JSON 与借用多图/遮罩流的 multipart 编辑契约及可选原生事件流，可接自定义适配器；[Image 说明](src/Asgard.Loki.Image/README.md#native-image-editing-contracts)明确了支持范围。OpenAI 原生 JSON/multipart 非流式及 SSE 编辑已接入，保留多图顺序、独立遮罩、文件字节、完整 JSON 和事件身份；完成项等待正常 EOF 及传输资源释放后公开，OpenAI 图片 variations、音乐生成、视频生成和事件重放尚不在这些包的支持范围内。Audio 已提供原生文件转录/翻译扩展契约，支持自定义适配器、借用上传流和完整原生结果；OpenAI 原生 multipart 转录/翻译已接入，上传流保持借用关系，转录 SSE 已保留完整事件/用量/SSE 身份，并校验分段引用与终态。公共请求保留扩展参数，结果保留供应商完整 JSON；具体模型、音色与账号授权仍需真实联调验证。

初始化、调用、支持矩阵及验证命令请参阅 [英文 README](README.md)。CI 在 Windows/Linux 上执行构建、测试及二十九个 NuGet 包的依赖与元数据检查，打包产物作为运行附件保存。独立的 `Publish NuGet` 工作流使用 OIDC 可信发布，默认仅验证登录；正式发布需手动选择 `publish` 并填写准确版本，步骤见 [发布说明](docs/releasing.md)。


原生 Realtime 通过 `loki.LLM.Realtime.OpenAsync` 使用，目标声明 `LlmProtocol.Realtime`。调用者负责原生事件、音频数据和工具结果的收发；SDK 保留所有状态与未知字段，正常关闭不表示生成成功，不自动执行工具、播放音频或重连。初始化、资源限制和离线验证见[OpenAI 包](src/Asgard.Loki.LLM.OpenAI/README.md#native-realtime-websocket)。


火山 LLM 真实联调仅使用[测试规范](docs/testing.md)及 [.env.example](.env.example) 的四个变量。运行 `pwsh -File eng/Test-Live.ps1`，扩展参数用 `-Suite Parameters` 或 `-Suite All`。两种协议的 `tool_choice=none` 严格失败断言仍保留，详见[实测记录](docs/llm-parameter-verification.md)。Realtime、Video 及其他供应商的真实模型验收状态以各包说明为准；离线协议测试不代表真实上游验证。


本项目使用 [Apache-2.0 许可证](LICENSE)。贡献与安全问题见 [CONTRIBUTING.md](CONTRIBUTING.md) 和 [SECURITY.md](SECURITY.md)。

星火原生契约提供 `loki.LLM.Spark.CompleteAsync`／`.StreamAsync`，要求目标显式声明 `LlmProtocol.Spark` 并提供 `ISparkAdapter`。`SparkRequest.Model` 只选择公共路由，原生 Parameters 在首次会话等待前冻结，选定实际模型仅映射至 `parameter.chat.domain`，不补根 model／stream、AppID 或消息历史。`SparkEvent.Data` 保留每条完整原生消息，`SparkCompletion.Events` 保留有序消息窗口中的插件、全部文本项、状态及用量，不合成首项文本响应或累计分片用量。[讯飞供应商包](src/Asgard.Loki.LLM.Xunfei/README.md)已实现原生签名、消息分片及文本／会话终态和正常关闭确认，见[原生契约](src/Asgard.Loki.LLM/README.md#native-spark-contract)。

混元原生契约提供 `loki.LLM.Hunyuan.CompleteAsync`／`.StreamAsync`，要求目标显式声明 `LlmProtocol.Hunyuan` 并提供 `IHunyuanAdapter`。`HunyuanRequest.Parameters` 保留原生 PascalCase 消息、工具 schema／参数字符串、工具关联、推理、多模态及未知字段；方法只控制原生 `Model`／`Stream`，继续使用完整 Session 目标绑定。完整结果保留 `Response` 信封，流事件保留原始平铺 JSON 和 SSE 元数据，不转换原生 ID、终态或省略的索引。[腾讯供应商包](src/Asgard.Loki.LLM.Tencent/README.md)已实现原生 TC3 JSON／SSE，并记录显式 TokenHub 兼容调用方式；原生传输、认证和终态验证由适配器负责，见[原生契约](src/Asgard.Loki.LLM/README.md#native-hunyuan-contract)。

原生文本补全契约使用 `loki.LLM.TextCompletions.CompleteAsync`／`.StreamAsync`，目标需声明 `LlmProtocol.TextCompletions` 且供应商实现 `ITextCompletionsAdapter`。`TextCompletionRequest.Parameters` 保留原生 prompt 文本／token 批次、suffix、echo、logprobs 和未知字段，不生成 Chat 消息或隐式转换。原始 choices 顺序、索引、过滤／用量及 SSE 元数据保持，仍使用完整 Session 目标绑定。Azure 包已实现原生 JSON/SSE；具体端点及输入能力由供应商实现校验，见[原生契约](src/Asgard.Loki.LLM/README.md#native-text-completions)。

[DeepSeek 原生工具客户端](src/Asgard.Loki.LLM.DeepSeek/README.md)通过 `DeepSeekToolsClient.SearchAsync`／`FetchAsync` 显式调用官方工具 API，配置独立 Key 和固定 tools 服务地址。查询选项、重复 URL、原生顺序／分数、正文和未知字段完整保留。它们不需要模型参数；SDK 不替应用自动串联搜索、获取页面或模型调用。

Cohere v1 原生入口 `loki.LLM.CohereChat.CompleteAsync`／`.StreamAsync` 要求显式 `LlmProtocol.CohereChat` 目标和 `ICohereChatAdapter`。仅替换已选实际模型和方法流标志，保留完整历史、原生工具参数/结果、引用与未知字段；response_id 与 generation_id 分开保留，NDJSON 使用 event_type，不虚构 SSE 身份。[Cohere 供应商包](src/Asgard.Loki.LLM.Cohere/README.md)负责原生 HTTP/NDJSON 与终态完整性；[核心契约](src/Asgard.Loki.LLM/README.md#native-cohere-v1-chat-contract)不代表真实供应商验证或自动协议转换。

Cloudflare Workers AI 的账号级兼容 Chat JSON/SSE 和当前支持的非流式 GPT-OSS Responses Create，可显式配置[现有 OpenAI 供应商包](src/Asgard.Loki.LLM.OpenAI/README.md#explicit-cloudflare-workers-ai-profile)。账号路径、实际模型和 API Token 维持完整 Session 绑定；工具关联、推理/加密项、参数和未知字段保持原生。这一配置不猜测模型能力，也不转换另一种原生 ai/run 文本协议；离线测试不代表真实账号或模型验证。

原生 `.LLM.WorkersAiText` 要求显式 `LlmProtocol.WorkersAiText` 目标与 `IWorkersAiTextAdapter`。公共模型只作路由信息，实际模型由原生 URL 拥有；prompt/messages 和未知参数保持原生正文，完整结果保留原始封装。流事件保留 SSE JSON 或实际 `[DONE]` 数据及独立传输身份，不虚构 JSON 终态。[Cloudflare 供应商包](src/Asgard.Loki.LLM.Cloudflare/README.md)提供原生 HTTP/SSE，详见[契约与适配器职责](src/Asgard.Loki.LLM/README.md#native-workers-ai-text-contract)。

[CozeChatClient](src/Asgard.Loki.LLM.Coze/README.md) 每个方法只发起一次原生提交/SSE/状态/Chat 消息请求，保留完整回执、多条回答、中间消息、工具/推理和原生 done；Message 完成与 Chat 终态分别处理。Bot ID 是远端服务配置，不冒充底层实际模型；引用绑定完整服务器/Bot/凭据/实现与原生 ID，不加入自动轮询、工具执行或 Bot/工作流管理。离线协议测试不代表真实 Bot 访问或回答质量验证。

### 显式 Codex 后端调用

现有 [OpenAI 分包](src/Asgard.Loki.LLM.OpenAI/README.md#explicit-codex-backend-responses) 还提供独立的 CodexLlmProvider，支持显式后端 Responses JSON/SSE 和 compact。完整服务器/模型/外部主体/账户/认证来源及策略保持现有 Session 绑定，短期令牌可以轮换。工具关联、推理/加密项、结构化输出与未知字段保持原生；不加入登录、刷新、账户管理、参数清理或自动重试。这是离线协议验证，不代表公开稳定 API 或真实账户/模型可用性验证。

同包 CodexSearchClient 另行提供固定账户的一次原生 alpha/search 调用，完整返回原始 HTTP 正文、状态和媒体类型。原生可选参数保持原值；不虚构模型 Session、搜索结果结构、用量或模型终态，也不下载结果 URL。

### 方舟原生 HTTP 边界

[火山供应商](src/Asgard.Loki.LLM.Volcengine/README.md#native-http-fidelity) 的 Chat/Responses JSON/SSE 使用明确的字节、尾部及 choice 关联上限，工厂/异步边界之前冻结完整请求。Chat 的实际 DONE 后继续校验 EOF 与晚到错误，结束帧等待完整资源关闭及最终绑定/取消检查。工具、推理、结构化输出、加密与未知字段保持原生；HTTP 策略和命名客户端参与现有完整目标 Session 绑定，不增加模型名路由、自动重试或能力依赖。

同一[火山分包](src/Asgard.Loki.LLM.Volcengine/README.md#published-bot-native-calls) 提供 VolcengineBotClient，用于已发布应用的原生 JSON/SSE。Bot 身份与返回的底层模型/接入点数据分开保留，插件、引用、群聊、用量和实际 DONE 不丢弃；不引入模型 Session、应用管理、Agent 运行时、执行循环或 URL 下载器。

方舟 Coding Plan 使用现有[火山包](src/Asgard.Loki.LLM.Volcengine/README.md#explicit-coding-plan-configuration) 的 `coding/v3/` Chat/Responses，以及现有[Anthropic 包](src/Asgard.Loki.LLM.Anthropic/README.md#explicit-ark-coding-plan-messages) 的完整 `coding/v1/` 根和显式 Bearer 原生 Messages。完整目标、实际模型及独立凭据由调用者配置；不新增品牌包装或供应商互相引用。完整协议窗口和 Session 绑定通过离线验证，未验证真实 Coding Plan 权限、模型或可选参数效果。

Ollama 兼容接口复用现有 [OpenAI 包](src/Asgard.Loki.LLM.OpenAI/README.md#explicit-ollama-compatible-endpoints) 的 Chat、无状态 Responses 和本地 compact，以及现有 [Anthropic 包](src/Asgard.Loki.LLM.Anthropic/README.md#explicit-ollama-messages) 的显式 Bearer 原生 Messages。完整根、实际模型、本地占位凭据或云端独立 Key 均由调用者明确配置；原生 `/api/chat`、`/api/generate` 留在 Ollama 包。完整窗口经过离线验证，不代表真实模型、可选参数效果或云端 compact 可用性验证。

[原生文本/FIM 补全](src/Asgard.Loki.LLM.OpenAI/README.md#native-compatible-text-completions) 使用现有 TextCompletions SDK 入口和 OpenAI 分包，完整端点、实际模型与独立 API Key 由调用者明确配置。prompt 模板、suffix、text/logprobs、choice 索引及未知字段保持原生，文本协议独立维护有界 choice/DONE 终态；不转成 Chat、不修补参数，也不把文本 ID 当作 Responses 续接 ID。已验证离线协议保真，未验证真实模型或参数效果。

[百炼 Chat/Responses/Text](src/Asgard.Loki.LLM.OpenAI/README.md#explicit-dashscope-native-endpoints) 和[原生 Messages](src/Asgard.Loki.LLM.Anthropic/README.md#explicit-dashscope-messages) 复用现有分包，完整兼容根、实际模型和独立凭据由调用者配置，按目标明确声明协议支持。工具关联、schema、推理、FIM 及未知字段保持原生；Responses 终态后拒绝已知推理/工具/搜索生命周期重开，Chat 业务元数据保持数据含义。已做离线协议验证，未验证真实模型或可选参数效果。


DeepSeek 的 [Chat／Responses／beta FIM](src/Asgard.Loki.LLM.OpenAI/README.md#explicit-deepseek-native-endpoints) 和[原生 Messages](src/Asgard.Loki.LLM.Anthropic/README.md#explicit-deepseek-messages) 复用现有分包，分别配置完整根、实际模型和独立凭据。原生思考、工具关联、结构化参数及未知字段完整保留；Responses 每次发送完整无状态输入并保留语义终态，FIM 要求真实 DONE。当前验证均为离线协议测试，未验证真实模型及可选参数效果。

[Moonshot／Kimi Chat、Partial 与 Responses](src/Asgard.Loki.LLM.OpenAI/README.md#explicit-moonshot-and-kimi-coding-native-endpoints) 和[原生 Messages](src/Asgard.Loki.LLM.Anthropic/README.md#explicit-moonshot-and-kimi-coding-messages) 复用现有供应商封装，平台与 Coding 完整根、模型和独立凭据均由调用者选择。推理摘要、schema、缓存字段、工具关联及未知数据保持原文；Responses 终态后拒绝已知摘要生命周期重开。当前为离线验证，未宣称旧文本托管接口可用或真实模型、参数效果及 Coding 权限已验证。

[MiniMax 原生 v2 Chat](src/Asgard.Loki.LLM.MiniMax/README.md) 通过已有 ChatCompletions 入口调用，完整目标、实际模型和独立 Key 均由调用者配置。保留完整原生数据和工具关联；HTTP200 内的 base_resp 错误保留原始正文、状态及媒体。结束分块和完整消息快照分别返回，成功尾部等待实际 EOF、资源关闭及最终取消检查。[原生 Messages](src/Asgard.Loki.LLM.Anthropic/README.md#explicit-minimax-messages) 复用现有 Anthropic 分包。当前为离线协议验证，未验证真实模型及参数效果。

[Mistral 原生 Chat](src/Asgard.Loki.LLM.OpenAI/README.md#explicit-mistral-native-chat) 复用现有 OpenAI 包，完整根、实际模型和独立凭据由调用者配置，目标仅声明 Chat。类型化思考／文本流式变化、工具关联、图片容器、schema／prefix 参数和未知字段保持原文；不重编号工具 ID，不在本地获取媒体或执行工具。完整 JSON／SSE 及实际 DONE、资源、Session 边界经过离线验证，未验证真实模型和参数效果。

[Perplexity 原生 Responses](src/Asgard.Loki.LLM.Perplexity/README.md) 使用已有 Responses 入口，显式绑定完整地址、实际模型和独立 Key。保留顶层 error 的稀疏失败、部分终态快照、搜索推理事件、工具关联及原生 response_format；不补造 ID 或终态。结束事件等待 EOF、资源关闭和最终取消检查。会覆盖绑定模型的 models/preset/profile 根选择器明确拒绝。当前仅完成离线协议验证，未验证真实模型及参数效果。

[Perplexity 原生 Sonar Chat](src/Asgard.Loki.LLM.Perplexity/README.md#sonar-chat-initialization) 通过同一供应商包内的 PerplexitySonarProvider 调用。chat/completions 或 v1/sonar 显式选择参与完整 Session 绑定；消息、推理／工具历史、原生 schema／搜索参数、引用、媒体参考及未知值保持原文。支持原生 end_of_stream、choice 结束及实际 DONE；文本 SSE 错误保留原始字节并撤回暂存的终态。不自动迁移调用、执行工具、下载结果、轮询或计算计费；当前仍为离线验证。

[xAI 原生 Chat／Responses](src/Asgard.Loki.LLM.OpenAI/README.md#explicit-xai-native-chat-and-responses) 复用现有 OpenAI 包，显式绑定完整地址、实际模型、独立 Key 和协议。完整历史、不透明加密推理／工具项、倒序工具结果、原生 schema／推理／搜索参数、引用和精确用量均保持原文。不按模型后缀修补参数，不注入搜索、不保存隐藏历史、不执行工具或重算计费。终态、已知生成事件及未知元数据经过离线验证；未验证真实模型、参数效果或加密内容可用性。

[SiliconFlow 原生 Chat／Prefix／FIM](src/Asgard.Loki.LLM.OpenAI/README.md#explicit-siliconflow-native-chat-prefix-and-fim) 复用现有 OpenAI 包，目标仅声明 Chat，并显式配置完整地址、实际模型及独立 Key。推理、工具、schema、多模态内容、根级 prefix／suffix、原始用量和未知字段保持原文。FIM 仍走 ChatCompletions，消息由调用者提供；不补造空消息、不拼接完整代码或合成终态。完整原生／代理 JSON／SSE、实际 DONE、资源关闭、晚期失败及 Session 边界已离线验证，未验证真实模型、参数效果和补全质量。

[百度千帆 v2 原生 Chat](src/Asgard.Loki.LLM.OpenAI/README.md#explicit-baidu-qianfan-v2-native-chat) 复用现有 OpenAI 包，显式配置完整地址、实际模型、独立 Key 与可选 appid 客户端请求头。推理、工具关联、schema、搜索状态／结果、过滤标记和精确用量保持原文。JSON／SSE、真实 DONE、原生错误、资源关闭与完整 Session 绑定已离线验证；未验证真实模型、参数效果、搜索或过滤策略效果。

[智谱 v4 原生 Chat／Responses](src/Asgard.Loki.LLM.OpenAI/README.md#explicit-zhipu-v4-native-chat-and-responses) 与 [Messages](src/Asgard.Loki.LLM.Anthropic/README.md#explicit-zhipu-v4-native-messages) 复用现有包，分别显式绑定地址、协议、实际模型和 Key。原生思考对象、工具历史、搜索／过滤元数据和用量保持原文。Chat DONE、Responses 终态与 Messages message_stop 分别经过离线验证；未验证真实模型、参数效果或 Coding Plan 权限。

[OpenRouter、Xinference 与 Submodel 显式配置](src/Asgard.Loki.LLM.OpenAI/README.md#explicit-openrouter-xinference-and-submodel-profiles) 复用现有 OpenAI 包，按实际协议声明 Chat／Responses／文本补全，并显式绑定完整地址、实际模型、Key 和可选调用者请求头。原生推理、工具关联、文本输入、OpenRouter 重复 finish 用量帧与错误均保持原文。协议／终态／Session 边界已离线验证，未验证历史托管端点或真实模型可用性。

独立的 [API Key 原生搜索客户端](src/Asgard.Loki.LLM.NewAPI/README.md) 只调用已配置 New API／Sub2API 上游的 alpha/search。完整地址、实际模型和独立 Key 固定在客户端；id／stream／query 与未知参数保持原文。结果保留完整 HTTP 状态、媒体类型及字节，允许二进制、空正文和 SSE，不推导生成终态或抓取引用。它与 Codex 外部账户鉴权分开；离线测试验证协议、输入、字节／长度限制、资源关闭和最终取消，未验证真实服务、模型或参数效果。

[Gemini 原生传输策略](src/Asgard.Loki.LLM.Google/README.md#native-http-and-ending-policies) 提供显式字节／尾帧／候选上限、严格 JSON／SSE 及原始错误字节。原生响应身份、每个已观察候选和 EOF／资源关闭／最终取消边界已离线验证；晚期原生错误通过转换链保持原文。完整 Session 绑定包含这些策略与实现版本。

对已配置的 New API／Sub2API 上游，[OpenAI 协议族](src/Asgard.Loki.LLM.OpenAI/README.md#explicit-native-upstream-chat-responses-text-and-compact)、[Messages](src/Asgard.Loki.LLM.Anthropic/README.md#explicit-native-upstream-messages) 和 [Gemini](src/Asgard.Loki.LLM.Google/README.md#native-http-and-ending-policies) 继续复用现有供应商包；API Key alpha/search 使用独立固定目标客户端。完整地址／模型／Key／协议、不透明历史／工具关联及原生终态／Session 边界已离线验证，不推导托管部署、模型或参数可用性，也不引入中转业务流程。

已配置的 vLLM／SGLang 部署复用现有 [OpenAI 原生 Chat／文本补全／Responses](src/Asgard.Loki.LLM.OpenAI/README.md#explicit-vllm-and-sglang-native-presets) 和 [Messages](src/Asgard.Loki.LLM.Anthropic/README.md#explicit-vllm-and-sglang-messages) 包，明确完整地址、实际服务模型和 Bearer 凭据。完整离线样本覆盖原文、工具关联、Session 与终态边界；部署能力和模型接受情况需另行真实验证，不加入部署／中转管理。

已有 OpenAI 原生协议部署可配置[操作路径及 Bearer／自定义头／查询参数／显式无认证](src/Asgard.Loki.LLM.OpenAI/README.md#explicit-native-http-endpoints-and-authentication)。配置与完整目标共同参与 Session 绑定，保留原生正文及生命周期，不增加中转路由或模型／服务器发现。

原生 Claude 兼容部署也支持[显式 Messages 路径及 ApiKey／Bearer／自定义头／查询参数／无认证](src/Asgard.Loki.LLM.Anthropic/README.md#explicit-native-messages-endpoint-and-authentication)，保留原生工具／历史／流式语义及完整目标绑定；协议转换仍由独立装饰器显式配置。
