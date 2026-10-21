namespace Asgard.Loki.Image.Azure.Internal;

/// <summary>创建 HTTP 客户端之前固定的生成正文；输出计数只用于原生流终态校验。</summary>
internal sealed record AzureImageGenerationPayload(string Model, byte[] Body, int ExpectedImages);
