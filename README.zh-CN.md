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

真实联调统一使用 `ASGARD_LOKI_TEST_` 环境变量，按供应商、能力与可选目标配置组分别设置，详见 [测试配置规范](docs/testing.md) 和 [.env.example](.env.example)。普通单元测试无需配置密钥；真实联调入口及 `.env` 自动加载尚未实现，模板用于后续用例遵循同一约定。
