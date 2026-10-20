namespace Asgard.Loki.Video;

/// <summary>可选原生 multipart 视频提交能力，不支持时不能改发 JSON 或选择另一目标。</summary>
public interface IVideoMultipartTaskAdapter : IVideoTaskAdapter
{
    /// <summary>提交一次原生表单，借用所有上传流并在返回前完成调用资源释放与取消检查。</summary>
    Task<VideoTaskSnapshot> SubmitAsync(VideoMultipartTaskRequest request, CancellationToken cancellationToken = default);
}
