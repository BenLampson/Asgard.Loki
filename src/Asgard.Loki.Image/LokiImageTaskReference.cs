using System.Text.Json;

namespace Asgard.Loki.Image;

/// <summary>绑定完整上游的原生图片任务引用；不含明文凭据，不作为授权令牌。</summary>
public sealed record LokiImageTaskReference(string Model, string ProviderId, string TaskId,
    string TargetFingerprint, JsonElement? NativeReference = null);
