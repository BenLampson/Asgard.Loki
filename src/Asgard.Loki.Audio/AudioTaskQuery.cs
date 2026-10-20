namespace Asgard.Loki.Audio;

/// <summary>查询一个既有原生任务及已绑定操作，不携带凭据或触发提交。</summary>
public sealed record AudioTaskQuery(string TaskId, string Operation);
