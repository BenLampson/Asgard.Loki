using Xunit;

namespace Asgard.Loki.Image.Tests;

/// <summary>独立图像模块配置验证不依赖任何实际供应商、网络或完整宿主。</summary>
public sealed class ImageConfigurationTests
{
    /// <summary>至少配置一种图像操作，集合不可为 null。</summary>
    [Theory] [InlineData("null")] [InlineData("empty")] [InlineData("routes")] [InlineData("edit-routes")]
    public void InvalidOptions_FailDuringRegistration(string kind)
    {
        var options = kind switch { "null" => null!, "routes" => new ImageOptions { Routes = null! },
            "edit-routes" => new ImageOptions { EditRoutes = null! }, _ => new ImageOptions() };
        Assert.IsAssignableFrom<ArgumentException>(Record.Exception(() => new LokiClientBuilder().AddImage(options)));
    }

    /// <summary>目标模型、数组、身份与重复路由在任何输入派发前校验。</summary>
    [Theory] [InlineData("route")] [InlineData("model")] [InlineData("targets")] [InlineData("empty")]
    [InlineData("target")] [InlineData("id")] [InlineData("duplicate")]
    public void InvalidEditRoute_FailsDuringRegistration(string kind)
    {
        var adapter = new StubImageEditAdapter(kind == "id" ? "" : "edit");
        IReadOnlyList<IImageEditAdapter> targets = kind switch { "targets" => null!, "empty" => [], "target" => [null!], _ => [adapter] };
        var route = new ImageEditRoute { Model = kind == "model" ? " " : "pictures", Targets = targets };
        var options = new ImageOptions { EditRoutes = kind switch { "route" => [null!], "duplicate" => [route, route], _ => [route] } };
        Assert.IsAssignableFrom<ArgumentException>(Record.Exception(() => new LokiClientBuilder().AddImage(options))); Assert.Equal(0, adapter.Edits + adapter.Streams);
    }

    /// <summary>原来的生成配置仍保持同样的本地验证，不因新增编辑而放宽。</summary>
    [Theory] [InlineData("route")] [InlineData("model")] [InlineData("targets")] [InlineData("empty")] [InlineData("target")] [InlineData("duplicate")]
    public void InvalidGenerationRoute_StillFailsDuringRegistration(string kind)
    {
        var adapter = new StubImageEditAdapter(); IReadOnlyList<IImageGenerationAdapter> targets = kind switch
        { "targets" => null!, "empty" => [], "target" => [null!], _ => [adapter] };
        var route = new ImageRoute { Model = kind == "model" ? " " : "pictures", Targets = targets };
        var options = new ImageOptions { Routes = kind switch { "route" => [null!], "duplicate" => [route, route], _ => [route] } };
        Assert.IsAssignableFrom<ArgumentException>(Record.Exception(() => new LokiClientBuilder().AddImage(options)));
    }
}
