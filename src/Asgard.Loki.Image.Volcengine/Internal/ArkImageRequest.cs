namespace Asgard.Loki.Image.Volcengine.Internal;

/// <summary>创建客户端之前冻结的原生正文及供应商分组模式，不公开或推导模型能力。</summary>
internal sealed record ArkImageRequest(string PublicModel, byte[] Body, bool Grouped);
