namespace Asgard.Loki.LLM;

/// <summary>声明供应商实例的注册身份。</summary>
public interface ILlmProvider
{
    /// <summary>可被多个目标引用的实例标识。</summary>
    string Id { get; }
    /// <summary>影响 Session 绑定的适配实现身份；实现及转换配置变化时必须变化。</summary>
    string BindingIdentity => GetType().AssemblyQualifiedName ?? GetType().FullName!;
}
