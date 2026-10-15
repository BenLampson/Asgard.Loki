# Asgard.Loki

[English](README.md) | **简体中文**

纯 .NET 10 / C# 14 类库：统一 `LokiClient` 入口，能力及供应商按需安装。英文 README 是完整使用说明，结构规则见 [架构规范](docs/architecture.md)。

## 已实现

| 能力 | 供应商包 | 支持内容 |
| --- | --- | --- |
| LLM | `Asgard.Loki.LLM.Volcengine` | 方舟 Chat Completions、Responses，JSON/SSE，响应链与完整 Session 目标绑定 |
| LLM | `Asgard.Loki.LLM.OpenAI` | OpenAI Chat Completions、Responses，JSON/SSE |
| 音频 | `Asgard.Loki.Audio.Volcengine` | V3 HTTP 单向流式 TTS、音频聚合、新旧认证方式 |
| 图片 | `Asgard.Loki.Image.OpenAI` | Images Generations、Base64/URL、部分与最终图像 SSE |
| 视频 | — | 保留项目骨架 |

供应商包仅引用所属能力，不会带入其他能力包。入口只引用统一抽象。

LLM Session 使用标准 `IDistributedCache` 保存完整目标，首次绑定需要显式注入共享协调器。不会因为重连、Key 池变化或目标失效而偷偷切换服务器、模型和 Key。单元测试使用 `MemoryDistributedCache` 与测试协调器，不依赖真实 Redis。

当前未实现原生 Responses WebSocket、图片编辑、语音识别、音乐生成、视频生成和事件重放。公共请求保留扩展参数，结果保留供应商完整 JSON；具体模型、音色与账号授权仍需真实联调验证。

初始化、调用、支持矩阵及验证命令请参阅 [英文 README](README.md)。CI 在 Windows/Linux 上执行构建、测试及十个 NuGet 包的依赖与元数据检查，打包产物作为运行附件保存。独立的 `Publish NuGet` 工作流使用 OIDC 可信发布，默认仅验证登录；正式发布需手动选择 `publish` 并填写准确版本，步骤见 [发布说明](docs/releasing.md)。

本项目使用 [Apache-2.0 许可证](LICENSE)。贡献与安全问题请参阅 [CONTRIBUTING.md](CONTRIBUTING.md) 和 [SECURITY.md](SECURITY.md)。

当前火山 LLM 真实联调只需要四个环境变量：启用开关、API Key、Chat 模型、Responses 模型，详见 [测试配置规范](docs/testing.md) 和 [.env.example](.env.example)。运行 `pwsh -File eng/Test-Live.ps1`；Windows 下支持读取本机当前用户变量，不自动加载 `.env`。

离线测试 219 项通过，两种 LLM 供应商各 93 项。标准方舟 API 的 `doubao-seed-evolving` 已实测通过普通/流式调用、工具调用与结果回填、严格 JSON Schema、推理开关、输出截断、响应链续接及加密推理项原样回传。**两种协议的 `tool_choice=none` 严格测试仍失败**：最终请求参数正确，但上游仍返回工具调用。失败断言保留，详见 [参数与协议验证记录](docs/llm-parameter-verification.md)。OpenAI 当前只有离线验证。

运行 `pwsh -File eng/Test-Live.ps1 -Suite Parameters` 或 `-Suite All` 执行扩展联调；增加 `-Scenario Structured`、`Tools`、`ToolChoice`、`Reasoning`、`Limits` 或 `Replay` 可单独选组。默认冒烟五次请求，参数套件计划 26 次，全部计划 31 次，单进程硬上限 32 次。上述工具禁用问题存在期间，参数及全部套件会返回失败。其他模型、供应商、能力和 Redis 跨进程联调仍需单独验证；普通单元测试和自动 CI 保持离线。
