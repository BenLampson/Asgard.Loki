namespace Asgard.Loki.Abstractions;

/// <summary>声明能力包可使用的通用模块构建契约。</summary>
public interface ILokiClientBuilder
{
    /// <summary>注册模块工厂；由客户端持有工厂创建的模块。</summary>
    ILokiClientBuilder AddModule<T>(Func<T> factory) where T : class;

    /// <summary>创建客户端；具体模块在首次访问时初始化。</summary>
    ILokiClient Build();
}
