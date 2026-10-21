using System.Net;

namespace Asgard.Loki.Image.Azure.Internal;

/// <summary>完整 HTTP 交换后的独立字节载荷，不携带调度或任务状态。</summary>
internal sealed record AzureImageResponse(HttpStatusCode Status, byte[] Body, string? ContentType);
