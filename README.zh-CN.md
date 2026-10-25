# Asgard.Loki

**为你的 .NET 应用接入文本、语音、图片、视频与检索模型。**

[![CI](https://github.com/BenLampson/Asgard.Loki/actions/workflows/ci.yml/badge.svg)](https://github.com/BenLampson/Asgard.Loki/actions/workflows/ci.yml)
[![.NET 10](https://img.shields.io/badge/.NET-10-512BD4)](https://dotnet.microsoft.com/)
[![NuGet](https://img.shields.io/nuget/v/Asgard.Loki.svg)](https://www.nuget.org/packages/Asgard.Loki)
[![License: Apache-2.0](https://img.shields.io/badge/License-Apache--2.0-blue.svg)](LICENSE)

[English](README.md) | **简体中文**

Asgard.Loki 是面向 **.NET 10 / C# 14** 的模块化 AI SDK。通过一个 `LokiClient` 使用六类独立能力，按需安装供应商 NuGet 包，同时保留原生参数、完整结果与流式事件。

可接入 **OpenAI、Claude、Gemini、Ollama、Azure OpenAI、Amazon Bedrock、火山引擎**等服务。不同供应商支持的操作各有范围，选型请查看[完整支持矩阵](#已实现)。

[快速开始](#快速开始生成图片) · [LLM 初始化](README.md#initialize-llm) · [供应商](#已实现) · [包清单](README.md#packages-and-dependencies) · [参与贡献](CONTRIBUTING.md)

## 能用来做什么？

| 能力 | 应用场景 | 入口 |
| --- | --- | --- |
| **LLM** | 聊天、流式回答、结构化输出、获取原生工具调用数据 | [LLM 初始化](README.md#initialize-llm) · [JSON / JSON Schema](docs/llm-output-formats.md) |
| **Audio 音频** | 语音合成、文件转录与翻译、音乐和歌词任务 | [语音示例](README.md#volcengine-speech) · [音频指南](src/Asgard.Loki.Audio/README.md) |
| **Image 图片** | 图片生成、供应商支持的原生编辑、获取图片二进制 | [快速开始](#快速开始生成图片) · [统一生成指南](docs/image-generation.md) |
| **Video 视频** | 提交生成任务，查询进度与结果 | [视频指南](src/Asgard.Loki.Video/README.md) |
| **Embedding 嵌入** | 将支持的文本或媒体输入转为向量，用于语义检索 | [嵌入指南](src/Asgard.Loki.Embedding/README.md) |
| **Rerank 重排** | 按查询对候选文档排序，接入搜索或 RAG 流程 | [重排指南](src/Asgard.Loki.Rerank/README.md) |

## 为什么使用 Loki？

- **依赖按需安装。** 能力包和供应商包独立，接入图片生成时只需引入对应依赖。
- **保留原生控制。** 获取原始 JSON、工具关联、推理字段、用量与流式事件。可选的[协议转换](src/Asgard.Loki.LLM.Conversion/README.md)明确记录支持的映射，并报告不支持的语义。
- **会话绑定完整上游。** LLM Session 同时绑定服务器、实际模型、凭据身份和适配实现，可用于多实例应用。
- **复用图片生成代码。** 可选的图片 Factory 在注册的供应商之间映射支持的通用参数，保留原生结果，不支持的参数值在生成请求发出前报错。

适合希望明确控制模型调用与供应商配置的 .NET 应用。调用方提供凭据、执行工具，并决定何时查询异步任务；生成调用只尝试一次，不自动重试或切换目标。

## 安装

使用 **.NET 10**，安装入口包及所需供应商包，能力依赖会自动传递。下面使用相同的 **1.1.0** 版本。

```sh
dotnet new console --framework net10.0 -n LokiDemo
cd LokiDemo
dotnet add package Asgard.Loki --version 1.1.0
dotnet add package Asgard.Loki.Image.OpenAI --version 1.1.0
```

接入 LLM 时，可选择 `Asgard.Loki.LLM.OpenAI`、`Asgard.Loki.LLM.Anthropic`、`Asgard.Loki.LLM.Google` 或 `Asgard.Loki.LLM.Volcengine` 等分包，然后按[LLM 示例](README.md#initialize-llm)初始化。也可[从源码构建](README.md#build-and-test)，将 `artifacts/packages` 作为本地 NuGet 源。

## 快速开始：生成图片

下面是完整的控制台示例：配置一个 OpenAI 图片目标、生成图片，再显式获取图片字节，不需要 Session 缓存。替换 `Program.cs`，将两个占位值改为有权限使用的图片模型与 API Key；实际应用请从密钥配置读取 Key。

```csharp
using Asgard.Loki;
using Asgard.Loki.Image;
using Asgard.Loki.Image.OpenAI;
using Microsoft.Extensions.DependencyInjection;

var services = new ServiceCollection();
services.AddHttpClient("images", client => client.Timeout = TimeSpan.FromMinutes(3))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
    {
        AllowAutoRedirect = false, UseCookies = false
    });
using var dependencies = services.BuildServiceProvider();
var httpFactory = dependencies.GetRequiredService<IHttpClientFactory>();

var factory = new LokiImageFactory(new LokiImageProvider
{
    Models =
    [
        new LokiImageModel
        {
            Name = "pictures",
            Providers =
            [
                new LokiImageUpstreamProvider
                {
                    Id = "openai",
                    BaseAddress = new Uri("https://api.openai.com/v1/"),
                    UpstreamModel = "<your-image-model>",
                    ApiKey = "<your-api-key>",
                    HttpClientName = "images",
                    Handler = new OpenAiImageGenerationHandler()
                }
            ]
        }
    ]
}, httpFactory);

await using var loki = new LokiClientBuilder()
    .AddImage(new ImageOptions { GenerationFactory = factory })
    .Build();

var result = await loki.Image.Generation.GenerateAsync(new LokiImageGenerationRequest
{
    Model = "pictures",
    Prompt = "A watercolor lighthouse at dawn"
});

Console.WriteLine($"{result.State} | {result.ProviderId} | {result.UpstreamModel}");
foreach (var image in result.Images)
{
    if (image.Error is not null) continue;
    var content = await loki.Image.Generation.GetImageContentAsync(image);
    Console.WriteLine($"{content.Bytes.Length} bytes | {content.ContentType}");
}
```

运行 `dotnet run`，程序会输出生成状态、选中的上游，以及每张成功图片的字节数和媒体类型。应用可保存或返回 `content.Bytes`，通过 `result.Data` 读取完整原生响应。这会发起真实供应商调用，可能产生供应商费用。

`pictures` 是应用定义的公开模型别名，`UpstreamModel` 是实际模型。示例的可选参数使用上游默认值；要设置数量、尺寸或质量，请先按[统一图片指南](docs/image-generation.md)声明该实际模型的支持范围。该指南还介绍多个上游、原生调用及异步图片任务；[1.1.0 发布说明](docs/releases/1.1.0.md)记录统一生成流程及兼容性。

## 下一步

| 你想做的事 | 文档 |
| --- | --- |
| 调用 Chat / Responses，流式输出对话 | [LLM 初始化](README.md#initialize-llm) · [Session 存储](README.md#session-storage) |
| 使用 Claude 或 Gemini 原生请求 | [原生协议与显式转换](README.md#native-claude-gemini-and-explicit-conversion) |
| 生成语音或使用图片原生参数 | [语音](README.md#volcengine-speech) · [原生图片](README.md#openai-images) |
| 接入语义检索与文档排序 | [Embedding](src/Asgard.Loki.Embedding/README.md) · [Rerank](src/Asgard.Loki.Rerank/README.md) |
| 确认某个供应商的操作、初始化与限制 | [完整支持矩阵](#已实现) · [供应商专题指南](README.md#additional-provider-guides) |
| 扩展 SDK 或参与贡献 | [架构](docs/architecture.md) · [贡献指南](CONTRIBUTING.md) · [构建与测试](README.md#build-and-test) |

## 支持范围与验证状态

支持矩阵说明 SDK 已实现的操作。真实账号权限、模型可用性和参数效果需要单独联调；离线协议测试不能证明真实模型效果。每个供应商 README 都记录初始化、限制和真实联调状态；独立联调入口及结果见[测试规范](docs/testing.md)和[LLM 参数实测](docs/llm-parameter-verification.md)。

LLM 初始化需要调用者提供 `IHttpClientFactory`、`IDistributedCache` 和显式 Session 协调器。多进程首次绑定需通过同一分布式系统协调，普通缓存 Get/Set 不提供原子绑定保证。SDK 不保存聊天历史；原生工具数据交给应用处理。

Loki 是嵌入应用的客户端类库，不包含模型微调或通用文件管理。音频、图片及视频上传是模型输入；[火山方舟资产库](src/Asgard.Loki.Video.Volcengine/README.md)是面向模型素材的供应商专属能力。

## 已实现

<details>
<summary>展开完整能力与供应商支持矩阵</summary>

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
| 音乐／歌词任务 | `Asgard.Loki.Audio.SunoAPI` | 应用显式配置代理；原生 MUSIC/LYRICS 提交、状态／批量 POST 与独立结果 GET · 单次原生 JSON；无后台轮询或媒体下载 |
| 文件转录/翻译 | `Asgard.Loki.Audio.OpenAI` | 原生 multipart、完整 JSON/字幕结果及转录 delta/segment/done SSE |
| 图片 | `Asgard.Loki.Image.OpenAI` | Images Generations、原生 JSON/multipart 编辑、Base64/URL、生成/编辑部分与最终图像 SSE |
| 兼容图片生成 | `Asgard.Loki.Image.OpenAI` | 显式 xAI 原生完整 JSON 配置，独立地址／模型／凭据并保留 data[] · 已做离线协议验证；xAI 编辑使用不同的原生 JSON 契约 |
| 图片编辑 | `Asgard.Loki.Image.XAI` | xAI 原生单图／有序多图 JSON 引用，保留完整结果 · 独立编辑路由，不转换 multipart 或管理文件 |
| 图片 | `Asgard.Loki.Image.Midjourney` | 显式非官方代理图片操作、原生任务／文本结果、输入上传与 seed 请求 · 完整目标绑定，单次／批量查询；不伪造同步生成结果 |
| 图片 | `Asgard.Loki.Image.Replicate` | 原生模型预测／JSON 编辑与显式图片输入上传，完整保留原生状态和结果 · 调用者驱动快照；不自动轮询／下载，严格区分未完成与成功 |
| 图片 | `Asgard.Loki.Image.Zhipu` | 智谱 v4 原生图片生成，保留图片字段别名、审核、时间戳和元数据 · 完整原生 JSON，不下载图片或补造元数据 |
| 图片 | `Asgard.Loki.Image.Azure` | 原生日期版本部署路径；JSON 生成及 multipart 编辑；保留完整图片、过滤信息和用量 · 原生生成／编辑 SSE；保留逐图事件与整次编辑完成标志 |
| 图片 | `Asgard.Loki.Image.Volcengine` | 原生方舟生成／图像输入和无提示词图层；独立即梦 Visual CVProcess 签名 JSON · 保留逐图成功／失败与汇总 SSE；支持单图原生 EOF |
| 图片 | `Asgard.Loki.Image.Ali` | 原生多模态 JSON／图文 SSE；显式现代与旧版图片异步提交 · 单次任务查询，保留文本／图片／用量／失败 |
| 图片 | `Asgard.Loki.Image.Google` | Gemini 原生图片生成／编辑输入；保留内联图片、签名与安全结果 · 原生 SSE、候选终态及用量元数据 |
| 图片 | `Asgard.Loki.Image.GoogleVertex` | 显式 Vertex 项目／Express 资源及 Bearer／API Key 认证；保留图片与安全结果 · 原生 Gemini JSON／SSE，保留终态与用量 |
| 图片 | `Asgard.Loki.Image.MiniMax` | 原生 image_generation JSON、参考图及 URL／Base64 数组 · 完整结果，保留计数与原生业务错误 |
| 图片 | `Asgard.Loki.Image.SiliconFlow` | 原生 image_size／batch_size／参考图与 images／timings／seed 结果 · 完整原始 JSON，不隐式转换 OpenAI 字段 |
| 嵌入 | `Asgard.Loki.Embedding.Azure` | 原生部署式嵌入，显式版本／api-key，保留文本／token 输入及 float／Base64 向量 · 保留批量顺序并验证索引覆盖；完整错误与有界 HTTP 生命周期 |
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

</details>

## 开发与贡献

构建、离线测试及打包命令见[英文 README](README.md#build-and-test)，扩展约定见[架构](docs/architecture.md)与[贡献指南](CONTRIBUTING.md)。普通单元测试使用 xUnit v3、内存缓存和替代传输，不依赖真实 Key、网络或 Redis。真实联调需要单独显式开启，见[测试规范](docs/testing.md)。

欢迎提交可复现的问题、使用反馈和 PR；涉及安全问题请按[安全说明](SECURITY.md)报告。NuGet 发布流程见[发布说明](docs/releasing.md)。

## 许可证

本项目使用 [Apache-2.0 许可证](LICENSE)。
