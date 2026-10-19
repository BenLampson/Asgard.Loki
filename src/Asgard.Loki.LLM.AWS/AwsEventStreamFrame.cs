using System.Text.Json;

namespace Asgard.Loki.LLM.AWS;

/// <summary>经长度/CRC 校验的 AWS 原始帧快照；不向 Claude JSON 注入传输字段。</summary>
public sealed class AwsEventStreamFrame
{
    private readonly byte[] _wire;
    private readonly int _headersLength;
    internal AwsEventStreamFrame(byte[] wire, int headersLength, JsonElement headers) { _wire = wire; _headersLength = headersLength; Headers = headers.Clone(); }
    /// <summary>原顺序的 name/type/value 头数组；数值精度、二进制/UUID/时间戳类型均独立保留。</summary>
    public JsonElement Headers { get; }
    /// <summary>包含两个 CRC 的完整原始帧；每次访问独立复制，不能修改内部快照。</summary>
    public ReadOnlyMemory<byte> WireBytes => _wire.ToArray();
    /// <summary>原始外层载荷；对 chunk 是包含 Base64 bytes 和未知字段的完整 JSON。</summary>
    public ReadOnlyMemory<byte> Payload => _wire.AsMemory(12 + _headersLength, _wire.Length - _headersLength - 16).ToArray();
    /// <summary>读取协议规定的字符串控制头，不将其他头类型隐式转换为文本。</summary>
    internal string StringHeader(string name, bool required = true)
    {
        foreach (var header in Headers.EnumerateArray()) if (header.GetProperty("name").GetString() == name)
        {
            if (header.GetProperty("type").GetInt32() != 7) throw new InvalidDataException("AWS EventStream 控制头必须是字符串。"); return header.GetProperty("value").GetString()!;
        }
        return required ? throw new InvalidDataException("AWS EventStream 缺少所需控制头。") : "";
    }
}
