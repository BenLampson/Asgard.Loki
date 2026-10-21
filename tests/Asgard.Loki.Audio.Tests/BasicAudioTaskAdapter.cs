namespace Asgard.Loki.Audio.Tests;

/// <summary>只实现单任务契约，验证可选接口不会被隐式模拟。</summary>
internal sealed class BasicAudioTaskAdapter : IAudioTaskAdapter
{
    public string Id => "basic";
    public string BindingIdentity => "basic-native-target";
    internal int Gets { get; private set; }
    public Task<AudioTaskSnapshot> SubmitAsync(AudioTaskRequest request, CancellationToken cancellationToken = default)
        => Task.FromResult(StubAudioTaskAdapter.Window("basic-task", request.Operation));
    public Task<AudioTaskSnapshot> GetAsync(AudioTaskQuery query, CancellationToken cancellationToken = default)
    { Gets++; return Task.FromResult(StubAudioTaskAdapter.Window(query.TaskId, query.Operation)); }
}
