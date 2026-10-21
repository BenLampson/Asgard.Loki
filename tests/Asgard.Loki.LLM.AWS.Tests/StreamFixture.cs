using System.Buffers.Binary;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Asgard.Loki.LLM.Ollama.Tests;

namespace Asgard.Loki.LLM.AWS.Tests;

/// <summary>独立 Python 二进制金样；故障组合使用测试侧位计算编码，不调用生产帧解析器。</summary>
internal static class StreamFixture
{
    internal const string Start = "{\"type\":\"message_start\",\"message\":{\"id\":\"native\",\"content\":[]}}";
    internal const string Delta = "{\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\"},\"usage\":{\"output_tokens\":1}}";
    internal const string Stop = "{\"type\":\"message_stop\"}";
    internal static byte[] Bytes()
    {
        using var source = Assembly.GetExecutingAssembly().GetManifestResourceStream("Asgard.Loki.LLM.AWS.Tests.Fixtures.native.eventstream")!; using var result = new MemoryStream(); source.CopyTo(result); return result.ToArray();
    }
    internal static HttpResponseMessage Reply(Stream body)
    {
        var reply = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) }; reply.Content.Headers.ContentType = new("application/vnd.amazon.eventstream"); reply.Headers.Add("x-amzn-bedrock-content-type", "application/json"); return reply;
    }
    internal static HttpResponseMessage Reply(byte[] bytes) => Reply(new OllamaBodyStream(bytes));
    internal static byte[] Chunk(string json) => Frame("event", "chunk", JsonSerializer.Serialize(new { bytes = Convert.ToBase64String(Encoding.UTF8.GetBytes(json)), future = (object?)null }));
    internal static byte[] Frame(string kind, string type, string payload, string? headerName = null, string? headerValue = null, string media = "application/json")
    {
        using var headers = new MemoryStream(); Header(headers, ":message-type", kind); Header(headers, kind == "exception" ? ":exception-type" : kind == "error" ? ":error-code" : ":event-type", type); Header(headers, ":content-type", media); if (kind == "error") Header(headers, ":error-message", "original AWS error"); if (headerName is not null) Header(headers, headerName, headerValue!); return Wire(headers.ToArray(), Encoding.UTF8.GetBytes(payload));
    }
    internal static byte[] Wire(byte[] headers, byte[] payload)
    {
        var bytes = new byte[16 + headers.Length + payload.Length]; BinaryPrimitives.WriteInt32BigEndian(bytes, bytes.Length); BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(4), headers.Length); FixPrelude(bytes); headers.CopyTo(bytes, 12); payload.CopyTo(bytes, 12 + headers.Length); FixMessage(bytes); return bytes;
    }
    internal static void FixPrelude(byte[] bytes) => BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(8), Crc(bytes.AsSpan(0, 8)));
    internal static void FixMessage(byte[] bytes) => BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(bytes.Length - 4), Crc(bytes.AsSpan(0, bytes.Length - 4)));
    private static uint Crc(ReadOnlySpan<byte> bytes) { var crc = uint.MaxValue; foreach (var item in bytes) { crc ^= item; for (var bit = 0; bit < 8; bit++) crc = (crc & 1) == 0 ? crc >> 1 : (crc >> 1) ^ 0xedb88320u; } return ~crc; }
    private static void Header(Stream stream, string name, string value) { var key = Encoding.UTF8.GetBytes(name); var text = Encoding.UTF8.GetBytes(value); stream.WriteByte((byte)key.Length); stream.Write(key); stream.WriteByte(7); Span<byte> length = stackalloc byte[2]; BinaryPrimitives.WriteUInt16BigEndian(length, (ushort)text.Length); stream.Write(length); stream.Write(text); }
    internal static async Task Consume(AwsLlmProvider provider, CancellationToken token, Action<ClaudeMessageEvent>? observe = null)
    {
        await foreach (var item in provider.StreamAsync(AwsFixture.Request(), AwsFixture.Context(), token)) observe?.Invoke(item);
    }
}
