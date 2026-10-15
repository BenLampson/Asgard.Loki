# Asgard.Loki 开发约定

- 只能在功能分支上修改代码与文档，分支以 `ben/` 开头，通过 PR 合并到 `main`。不得直接推送或强推 `main`，不得关闭保护或跳过双平台 CI。
- NuGet 使用 `publish.yml` 与 `nuget` 环境进行 OIDC 可信发布。默认模式 `verify` 仅验证登录；正式发布必须明确选择 `publish` 并确认版本，不得把配置验证当作发布授权。
- 架构权威文档是 [docs/architecture.md](docs/architecture.md)。调整公共接口、依赖方向或供应商分包时同步更新该文档，并使用 UML 说明。
- 这是纯 .NET 类库。不得引入 Web 项目、Controller、监听端口或应用业务功能。
- 统一抽象只包含模块机制。入口包只引用统一抽象；能力包之间不互相引用。
- 供应商按能力分包：`Asgard.Loki.{能力}.{供应商}`。当前实现 `LLM.Volcengine`、`LLM.OpenAI`、`Audio.Volcengine` 与 `Image.OpenAI`。
- 供应商包引用自己的能力包。LLM 和音频分别维护协议、结果、流程和状态，不能共用一个业务协调层。
- LLM 的调度单位是完整上游目标，不是某个模型的一组 Key。Session 必须保持服务器、实际模型、凭据身份和适配实现的完整绑定。
- Session 缓存依赖 `IDistributedCache`。首次绑定的并发协调必须使用显式协调契约，不得声称普通 Get/Set 是跨实例原子操作。
- 单元测试使用 xUnit v3。缓存测试使用 `MemoryDistributedCache`，HTTP 测试使用替代处理器；不得依赖真实 Key、网络和 Redis。
- 真实供应商联调单独执行，不可把本地协议测试描述成真实模型验证。不得读取或修改其他项目来获取凭据。
- 使用 .NET 10 / C# 14、UTF-8、CRLF、文件级命名空间、中文 XML 注释；每个文件一个类型，不超过 400 行。
- 不提交密钥。供应商生成请求不自动重试或切换目标。借用的缓存、协调器和 HTTP 工厂不由能力模块释放。
- README 以英文为主，中文使用 `README.zh-CN.md` 辅助。每个供应商包必须记录支持矩阵、初始化方式、限制和真实联调状态。
