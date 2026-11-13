using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Asgard.Loki.Image.Ali.Internal;

namespace Asgard.Loki.Image.Ali;

/// <summary>阿里原生图片任务提交及单次查询；不轮询、不重试、不借用视频任务协调层。</summary>
public sealed class AliImageTaskClient
{
    private readonly AliImageHttp _http;
    private readonly string _service;
    private readonly string _fingerprint;
    /// <summary>显式绑定一种原生异步提交服务及完整目标。</summary>
    public AliImageTaskClient(IHttpClientFactory factory, AliImageOptions options, AliImageTaskOperation operation)
    {
        _http = new(factory, options);
        _service = operation switch
        {
            AliImageTaskOperation.ImageGeneration => "image-generation/generation",
            AliImageTaskOperation.TextToImage => "text2image/image-synthesis",
            AliImageTaskOperation.ImageToImage => "image2image/image-synthesis",
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        };
        _fingerprint = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new[] { GetType().FullName!, options.Id, options.BaseAddress.AbsoluteUri,
            options.Model, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(options.ApiKey))), _service, options.HttpClientName,
            options.MaxRequestBytes.ToString(System.Globalization.CultureInfo.InvariantCulture), options.MaxResponseBytes.ToString(System.Globalization.CultureInfo.InvariantCulture) })));
    }
    /// <summary>一次原生提交，原样保留 task_id 和可选原生 task_status；不把确认当作生成完成。</summary>
    public Task<AliImageTask> SubmitAsync(AliNativeImageRequest request, CancellationToken cancellationToken = default)
        => SubmitCoreAsync(AliImagePayload.Prepare(request, _http.Options, false, cancellationToken, asyncTask: true), request.PublicModel, cancellationToken);
    private async Task<AliImageTask> SubmitCoreAsync(byte[] body, string model, CancellationToken token)
    {
        var response = await _http.SendRawAsync(_http.Address("services/aigc/" + _service), body, true, token).ConfigureAwait(false);
        var root = response.Data; var output = Output(root); var id = Text(output, "task_id"); ValidateId(id); ValidateStatus(output, false);
        token.ThrowIfCancellationRequested(); return new(root, new(model, _http.Options.Id, Fingerprint(model), id)) { RawResponse = response.Raw };
    }
    /// <summary>只查询当前完整目标绑定的原生任务；任务 ID 作为单个路径段转义。</summary>
    public Task<AliImageTask> GetAsync(AliImageTaskReference reference, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested(); ArgumentNullException.ThrowIfNull(reference); ArgumentException.ThrowIfNullOrWhiteSpace(reference.PublicModel);
        AliImageJson.Text(reference.PublicModel); ValidateId(reference.TaskId);
        if (reference.TargetId != _http.Options.Id || reference.TargetFingerprint != Fingerprint(reference.PublicModel)) throw new ArgumentException("阿里任务与完整目标绑定不符。", nameof(reference));
        return GetCoreAsync(reference, cancellationToken);
    }
    private async Task<AliImageTask> GetCoreAsync(AliImageTaskReference reference, CancellationToken token)
    {
        var response = await _http.SendRawAsync(_http.Address("tasks/" + Uri.EscapeDataString(reference.TaskId)), null, false, token).ConfigureAwait(false);
        var root = response.Data; var output = Output(root);
        if (Text(output, "task_id") != reference.TaskId) throw new InvalidDataException("阿里查询返回了其他任务。");
        ValidateStatus(output, true); token.ThrowIfCancellationRequested(); return new(root, reference) { RawResponse = response.Raw };
    }
    private string Fingerprint(string model) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new[] { _fingerprint, model })));
    private static JsonElement Output(JsonElement root) => root.TryGetProperty("output", out var output) && output.ValueKind == JsonValueKind.Object
        ? output : throw new InvalidDataException("阿里任务缺少原生 output 对象。");
    private static string Text(JsonElement root, string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
        ? value.GetString()! : throw new InvalidDataException("阿里任务缺少原生 " + name + "。");
    private static void ValidateStatus(JsonElement output, bool required)
    { if (required || output.TryGetProperty("task_status", out _)) _ = Text(output, "task_status"); }
    private static void ValidateId(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id); AliImageJson.Text(id);
        if (id is "." or ".." || id.Any(char.IsControl)) throw new ArgumentException("阿里任务 ID 不能为点段或控制字符。");
    }
}
