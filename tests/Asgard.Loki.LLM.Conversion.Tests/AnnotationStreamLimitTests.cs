using System.Text;
using System.Text.Json.Nodes;
using Xunit;

namespace Asgard.Loki.LLM.Conversion.Tests;

/// <summary>引用状态只按唯一原生条目计数、按 JSON 字节保留，损耗许可不能绕过资源限制。</summary>
public sealed class AnnotationStreamLimitTests
{
    /// <summary>同一引用在三个 done 快照反复出现不会被重复计数。</summary>
    [Fact]
    public void ExactLimits_AllowRepeatedSnapshotsWithoutDoubleCounting()
    {
        var source = AnnotationStreamFixtures.Read(); var annotation = source.Single(e => e["type"]!.GetValue<string>() == "response.output_text.annotation.added")["annotation"]!;
        var options = AnnotationStreamFixtures.Options with { MaxStreamAnnotations = 1, MaxStreamAnnotationBytes = Encoding.UTF8.GetByteCount(annotation.ToJsonString()) };
        Assert.Equal(6, AnnotationStreamFixtures.Run(source, options).Count);
    }

    /// <summary>条目和 UTF-8 字节上限都是硬失败，不能截断引用或在之后合成成功。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public void ExceededLimits_FailEvenWithLossConsent(bool bytes)
    {
        var source = bytes ? AnnotationStreamFixtures.Read() : AnnotationStreamFixtures.TwoAnnotations();
        var annotation = source.First(e => e["type"]!.GetValue<string>() == "response.output_text.annotation.added")["annotation"]!;
        var options = bytes ? AnnotationStreamFixtures.Options with { MaxStreamAnnotationBytes = Encoding.UTF8.GetByteCount(annotation.ToJsonString()) - 1 }
            : AnnotationStreamFixtures.Options with { MaxStreamAnnotations = 1 };
        var error = Assert.Throws<LlmConversionException>(() => AnnotationStreamFixtures.Run(source, options));
        Assert.Contains(error.Diagnostics, d => d.Code == (bytes ? "stream_annotation_byte_limit" : "stream_annotation_count_limit"));
    }

    /// <summary>原生完成快照首次出现引用时也受限制，不只限制 annotation.added 事件。</summary>
    [Fact]
    public void SnapshotOnlyAnnotation_CannotBypassByteLimit()
    {
        var source = AnnotationStreamFixtures.Read(); source.RemoveAll(e => e["type"]!.GetValue<string>() == "response.output_text.annotation.added");
        AnnotationStreamFixtures.Sequence(source);
        var error = Assert.Throws<LlmConversionException>(() => AnnotationStreamFixtures.Run(source, AnnotationStreamFixtures.Options with { MaxStreamAnnotationBytes = 1 }));
        Assert.Contains(error.Diagnostics, d => d.Code == "stream_annotation_byte_limit");
    }

    /// <summary>资源计数跨内容块累计，第一块关闭后仍需验证最终快照，不能重置额度。</summary>
    [Fact]
    public void AnnotationLimit_IsPerCallRatherThanPerPart()
    {
        var source = AnnotationStreamFixtures.Read(); var annotation = CustomToolFixtures.Parse("""{"type":"url_citation","url":"https://first.invalid/","title":"first","start_index":0,"end_index":1}""");
        var firstDone = source.Single(e => e["type"]!.GetValue<string>() == "response.content_part.done" && e["content_index"]!.GetValue<int>() == 0);
        firstDone["part"]!["annotations"]!.AsArray().Add(annotation.DeepClone());
        source.Single(e => e["type"]!.GetValue<string>() == "response.output_item.done")["item"]!["content"]![0]!["annotations"]!.AsArray().Add(annotation.DeepClone());
        source[^1]["response"]!["output"]![0]!["content"]![0]!["annotations"]!.AsArray().Add(annotation.DeepClone());
        var frame = source.Single(e => e["type"]!.GetValue<string>() == "response.output_text.annotation.added").DeepClone().AsObject();
        frame["content_index"] = 0; frame["annotation"] = annotation; source.Insert(source.IndexOf(firstDone), frame); AnnotationStreamFixtures.Sequence(source);
        var error = Assert.Throws<LlmConversionException>(() => AnnotationStreamFixtures.Run(source, AnnotationStreamFixtures.Options with { MaxStreamAnnotations = 1 }));
        Assert.Contains(error.Diagnostics, d => d.Code == "stream_annotation_count_limit");
    }

    /// <summary>配置必须为正数，构造和静态入口采用同一校验。</summary>
    [Theory] [InlineData(false, 0)] [InlineData(false, -1)] [InlineData(true, 0)] [InlineData(true, -1)]
    public void InvalidLimits_AreRejectedBeforeConversion(bool bytes, int value)
    {
        var options = bytes ? new LlmConversionOptions { MaxStreamAnnotationBytes = value } : new() { MaxStreamAnnotations = value };
        Assert.Throws<ArgumentOutOfRangeException>(() => new LlmProtocolStreamConverter(LlmProtocol.Responses, LlmProtocol.ChatCompletions, options));
        Assert.Throws<ArgumentOutOfRangeException>(() => LlmProtocolConverter.ConvertResponse(new(), LlmProtocol.Responses, LlmProtocol.ChatCompletions, options));
    }

    /// <summary>同协议复制仍保留完整原文，不把跨协议引用状态限制施加给原生调用。</summary>
    [Fact]
    public void NativeIdentityCopy_IsUnfilteredByConversionLimits()
    {
        var source = AnnotationStreamFixtures.Read(); var converter = new LlmProtocolStreamConverter(LlmProtocol.Responses, LlmProtocol.Responses,
            new() { MaxStreamAnnotationBytes = 1, MaxStreamAnnotations = 1 });
        foreach (var frame in source) CustomToolConversionTests.Equal(frame, JsonNode.Parse(Assert.Single(converter.Push(frame)).Data.GetRawText()));
        Assert.Empty(converter.Complete());
    }

    /// <summary>未知字段中的 false/null 仍参与原生快照比较，不能通过普通损耗许可改变引用。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public void UnknownAnnotationFields_AreSnapshottedAndCompared(bool changed)
    {
        var source = AnnotationStreamFixtures.Read();
        foreach (var citation in AnnotationStreamFixtures.Citations(source)) citation["future"] = CustomToolFixtures.Parse("""{"flag":false,"unset":null}""");
        if (changed)
        {
            source[^1]["response"]!["output"]![0]!["content"]![1]!["annotations"]![0]!["future"]!["unset"] = false;
            Assert.Throws<InvalidDataException>(() => AnnotationStreamFixtures.Run(source));
        }
        else
        {
            var result = AnnotationStreamFixtures.Run(source); CustomToolConversionTests.Equal(source[^1], JsonNode.Parse(result[^1].OriginalData.GetRawText()));
            Assert.Contains(result[^1].Diagnostics, d => d.Code == "unsupported_field");
        }
    }

    /// <summary>调用者修改已经推送的原生事件不会改变转换器持有的引用身份。</summary>
    [Fact]
    public void AnnotationEvent_IsClonedBeforeReturningFromPush()
    {
        var source = AnnotationStreamFixtures.Read(); var converter = new LlmProtocolStreamConverter(LlmProtocol.Responses, LlmProtocol.ChatCompletions, AnnotationStreamFixtures.Options);
        foreach (var frame in source)
        {
            converter.Push(frame);
            if (frame["type"]!.GetValue<string>() == "response.output_text.annotation.added") frame["annotation"]!["title"] = "caller-mutation";
        }
        Assert.Single(converter.Complete());
    }

    /// <summary>原生 annotation 事件的 null 保持显式损耗，并参与快照一致性而不是被当作缺失。</summary>
    [Fact]
    public void NullableAnnotation_RemainsDistinctFromMissingProperty()
    {
        var source = AnnotationStreamFixtures.Read(); source.Single(e => e["type"]!.GetValue<string>() == "response.output_text.annotation.added")["annotation"] = null;
        foreach (var snapshot in AnnotationStreamFixtures.Snapshots(source).Where(a => a.Count != 0)) snapshot[0] = null;
        var output = AnnotationStreamFixtures.Run(source); Assert.Contains(output[^1].Diagnostics, d => d.Code == "annotation_not_representable");
    }
}
