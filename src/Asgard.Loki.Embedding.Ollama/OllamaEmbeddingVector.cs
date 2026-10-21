using System.Text.Json;
using Asgard.Loki.Embedding.Ollama.Internal;

namespace Asgard.Loki.Embedding.Ollama;

/// <summary>保存原生数组向量；输入关联使用返回数组次序，不虚构上游 index。</summary>
public sealed class OllamaEmbeddingVector
{
    /// <summary>校验并复制有限数值数组，结果不依赖外部 JSON 文档生命周期。</summary>
    public OllamaEmbeddingVector(JsonElement data)
    {
        _ = OllamaEmbeddingResponse.Validate(data); Data = data.Clone();
    }
    /// <summary>原始向量数组，保留原有数值表示。</summary>
    public JsonElement Data { get; }
    /// <summary>原生数组次序的数值视图，不进行归一化或维度变换。</summary>
    public IReadOnlyList<double> Values => Data.EnumerateArray().Select(v => v.GetDouble()).ToArray();
}
