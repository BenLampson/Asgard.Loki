using System.Text.Json;

namespace Asgard.Loki.Audio.Tests;

/// <summary>应用拥有的原生音频任务替身，不调用其他能力或网络。</summary>
internal sealed class StubAudioTaskAdapter(string id = "native") : IAudioBatchTaskAdapter, IAudioTaskResultAdapter, IDisposable
{
    public string Id { get; set; } = id;
    public string BindingIdentity { get; set; } = "server|actual-model|credential|adapter";
    internal int Submits { get; private set; }
    internal int Gets { get; private set; }
    internal int Batches { get; private set; }
    internal int Results { get; private set; }
    internal bool Disposed { get; private set; }
    internal AudioTaskRequest? LastRequest { get; private set; }
    internal IReadOnlyList<AudioTaskQuery>? LastQueries { get; private set; }
    internal Action? OnCall { get; set; }
    internal Func<AudioTaskRequest, Task<AudioTaskSnapshot>>? Submit { get; set; }
    internal Func<AudioTaskQuery, AudioTaskSnapshot>? Get { get; set; }
    internal Func<IReadOnlyList<AudioTaskQuery>, AudioTaskBatchSnapshot>? Batch { get; set; }
    internal Func<AudioTaskQuery, JsonElement>? Result { get; set; }
    public Task<AudioTaskSnapshot> SubmitAsync(AudioTaskRequest request, CancellationToken cancellationToken = default)
    {
        Submits++; LastRequest = request; OnCall?.Invoke();
        return Submit?.Invoke(request) ?? Task.FromResult(Window("task-" + Submits, request.Operation));
    }
    public Task<AudioTaskSnapshot> GetAsync(AudioTaskQuery query, CancellationToken cancellationToken = default)
    {
        Gets++; LastQueries = [query]; OnCall?.Invoke();
        return Task.FromResult(Get?.Invoke(query) ?? Window(query.TaskId, query.Operation));
    }
    public Task<AudioTaskBatchSnapshot> GetBatchAsync(IReadOnlyList<AudioTaskQuery> queries, CancellationToken cancellationToken = default)
    {
        Batches++; LastQueries = queries; OnCall?.Invoke();
        return Task.FromResult(Batch?.Invoke(queries) ?? new(queries.Reverse().Select(query => Window(query.TaskId, query.Operation)).ToArray(), Json("{\"native\":[2,1]}")));
    }
    public Task<JsonElement> GetResultAsync(AudioTaskQuery query, CancellationToken cancellationToken = default)
    {
        Results++; LastQueries = [query]; OnCall?.Invoke();
        return Task.FromResult(Result?.Invoke(query) ?? Json("{\"data\":[{\"audio_url\":\"https://fixture.invalid/a?sig=A%2fB\",\"future\":9007199254740993}]}"));
    }
    internal static AudioTaskSnapshot Window(string id, string operation, AudioTaskState state = AudioTaskState.Submitted)
        => new(id, operation, "native-status", state, Json("{ \"unknown\":[null,false,0,9007199254740993], \"native_status\":\"original\" }"));
    internal static JsonElement Json(string value) { using var document = JsonDocument.Parse(value); return document.RootElement.Clone(); }
    public void Dispose() => Disposed = true;
}
