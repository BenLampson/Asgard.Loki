using System.Text.Json.Nodes;
using Xunit;

namespace Asgard.Loki.LLM.Conversion.Tests;

/// <summary>允许引用显示损耗时仍要验证原生索引、快照、字节范围和明确终态。</summary>
public sealed class AnnotationStreamIntegrityTests
{
    /// <summary>两个文本 part 的完整目标事件独立比较；没有凭空添加 Chat 引用 delta。</summary>
    [Fact]
    public void ValidNativeStream_MatchesAuthoredLossyGoldenAndPreservesSource()
    {
        var source = AnnotationStreamFixtures.Read(); var before = new JsonArray(source.Select(e => e.DeepClone()).ToArray()).ToJsonString();
        var actual = AnnotationStreamFixtures.Run(source); var expected = AnnotationStreamFixtures.Read("Chat");
        Assert.Equal(expected.Count, actual.Count);
        for (var i = 0; i < expected.Count; i++) CustomToolConversionTests.Equal(expected[i], JsonNode.Parse(actual[i].Data.GetRawText()));
        Assert.Equal(before, new JsonArray(source.Select(e => e.DeepClone()).ToArray()).ToJsonString());
        CustomToolConversionTests.Equal(source[^1], JsonNode.Parse(actual[^1].OriginalData.GetRawText()));
        Assert.Contains(actual[^1].Diagnostics, item => item.Code == "search_citations_not_representable");
        Assert.DoesNotContain(actual[^1].Diagnostics, item => item.Code == "unknown_event");
    }

    /// <summary>原生引用可乱序到达或先于后续文本完成；使用 annotation_index 而非到达次序关联。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public void AnnotationArrivalOrder_DoesNotChangeSnapshotIdentity(bool reversed)
    {
        var source = AnnotationStreamFixtures.TwoAnnotations(reversed); var output = AnnotationStreamFixtures.Run(source);
        Assert.Equal("stop", JsonNode.Parse(output[^1].Data.GetRawText())!["choices"]![0]!["finish_reason"]!.GetValue<string>());
    }

    /// <summary>部分原生传输只提供完成快照；没有观察到的增量不虚构，但已有内容和每个后续快照仍必须一致。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public void FirstAuthoritativeSnapshot_IsValidatedWithoutInventingEvents(bool finalOnly)
    {
        var source = AnnotationStreamFixtures.Read(); source.RemoveAll(e => e["type"]!.GetValue<string>() == "response.output_text.annotation.added"
            || finalOnly && e["type"]!.GetValue<string>() is "response.content_part.done" or "response.output_item.done");
        AnnotationStreamFixtures.Sequence(source); Assert.Equal(6, AnnotationStreamFixtures.Run(source).Count);
    }

    /// <summary>注解出现在初始内容快照时也占据原生索引，不能在后续重复占用。</summary>
    [Fact]
    public void InitialAnnotationSnapshot_IsTracked()
    {
        var source = AnnotationStreamFixtures.Read(); var annotation = source.Single(e => e["type"]!.GetValue<string>() == "response.output_text.annotation.added")["annotation"]!;
        source.Single(e => e["type"]!.GetValue<string>() == "response.content_part.added" && e["content_index"]!.GetValue<int>() == 1)["part"]!["annotations"]!.AsArray().Add(annotation.DeepClone());
        source.RemoveAll(e => e["type"]!.GetValue<string>() == "response.output_text.annotation.added"); AnnotationStreamFixtures.Sequence(source);
        Assert.Equal(6, AnnotationStreamFixtures.Run(source).Count);
    }

    /// <summary>明确的 incomplete 仍是合法不完整终态，保留 length 而非把引用流伪装成普通 stop。</summary>
    [Fact]
    public void IncompleteTerminal_PreservesLengthWithValidAnnotations()
    {
        var source = AnnotationStreamFixtures.Read(); source[^1]["type"] = "response.incomplete"; source[^1]["response"]!["status"] = "incomplete";
        source[^1]["response"]!["incomplete_details"] = CustomToolFixtures.Parse("""{"reason":"max_output_tokens"}""");
        source[^1]["response"]!["output"]![0]!["status"] = "incomplete";
        source.Single(e => Type(e) == "response.output_item.done")["item"]!["status"] = "incomplete";
        var result = AnnotationStreamFixtures.Run(source);
        Assert.Equal("length", JsonNode.Parse(result[^1].Data.GetRawText())!["choices"]![0]!["finish_reason"]!.GetValue<string>());
        CustomToolConversionTests.Equal(source[^1], JsonNode.Parse(result[^1].OriginalData.GetRawText()));
    }

    /// <summary>损耗同意不绕过这些原生关联和生命周期错误，且失败之后不能补发目标成功终态。</summary>
    [Theory]
    [InlineData("foreign-item")] [InlineData("foreign-output")] [InlineData("foreign-part")]
    [InlineData("missing-item")] [InlineData("missing-index")] [InlineData("negative-index")]
    [InlineData("fraction-index")] [InlineData("string-index")] [InlineData("sparse-index")]
    [InlineData("duplicate-index")] [InlineData("missing-annotation")] [InlineData("string-annotation")]
    [InlineData("invalid-type")] [InlineData("before-start")] [InlineData("before-part")]
    [InlineData("after-part")] [InlineData("after-item")] [InlineData("after-terminal")]
    [InlineData("part-changed")] [InlineData("part-omitted")] [InlineData("part-null")]
    [InlineData("item-changed")] [InlineData("item-omitted")] [InlineData("final-changed")] [InlineData("final-added")]
    [InlineData("terminal-missing")] [InlineData("snapshot-shifted-part")]
    public void InvalidNativeCitationState_NeverBecomesSuccessfulFinish(string kind)
    {
        var source = AnnotationStreamFixtures.Read(); var annotationEvent = source.Single(e => Type(e) == "response.output_text.annotation.added");
        var partDone = source.Single(e => Type(e) == "response.content_part.done" && e["content_index"]!.GetValue<int>() == 1);
        var itemDone = source.Single(e => Type(e) == "response.output_item.done");
        switch (kind)
        {
            case "foreign-item": annotationEvent["item_id"] = "foreign"; break;
            case "foreign-output": annotationEvent["output_index"] = 1; break;
            case "foreign-part": annotationEvent["content_index"] = 2; break;
            case "missing-item": annotationEvent.Remove("item_id"); break;
            case "missing-index": annotationEvent.Remove("annotation_index"); break;
            case "negative-index": annotationEvent["annotation_index"] = -1; break;
            case "fraction-index": annotationEvent["annotation_index"] = 0.5; break;
            case "string-index": annotationEvent["annotation_index"] = "0"; break;
            case "sparse-index": annotationEvent["annotation_index"] = int.MaxValue; break;
            case "duplicate-index": source.Insert(source.IndexOf(annotationEvent) + 1, annotationEvent.DeepClone().AsObject()); break;
            case "missing-annotation": annotationEvent.Remove("annotation"); break;
            case "string-annotation": annotationEvent["annotation"] = "bad"; break;
            case "invalid-type": annotationEvent["annotation"]!["type"] = false; break;
            case "before-start": source.Remove(annotationEvent); source.Insert(0, annotationEvent); break;
            case "before-part": source.Remove(annotationEvent); source.Insert(source.FindIndex(e => Type(e) == "response.content_part.added" && e["content_index"]!.GetValue<int>() == 1), annotationEvent); break;
            case "after-part": source.Remove(annotationEvent); source.Insert(source.IndexOf(partDone) + 1, annotationEvent); break;
            case "after-item": source.Remove(annotationEvent); source.Insert(source.IndexOf(itemDone) + 1, annotationEvent); break;
            case "after-terminal": source.Remove(annotationEvent); source.Add(annotationEvent); break;
            case "part-changed": partDone["part"]!["annotations"]![0]!["title"] = "changed"; break;
            case "part-omitted": partDone["part"]!.AsObject().Remove("annotations"); break;
            case "part-null": partDone["part"]!["annotations"] = null; break;
            case "item-changed": itemDone["item"]!["content"]![1]!["annotations"]![0]!["url"] = "https://other.invalid/"; break;
            case "item-omitted": itemDone["item"]!["content"]![1]!["annotations"]!.AsArray().Clear(); break;
            case "final-changed": source[^1]["response"]!["output"]![0]!["content"]![1]!["annotations"]![0]!["end_index"] = 4; break;
            case "final-added": source[^1]["response"]!["output"]![0]!["content"]![1]!["annotations"]!.AsArray().Add(annotationEvent["annotation"]!.DeepClone()); break;
            case "terminal-missing": source.RemoveAt(source.Count - 1); break;
            case "snapshot-shifted-part": itemDone["item"]!["content"]![0]!["annotations"]!.AsArray().Add(annotationEvent["annotation"]!.DeepClone()); itemDone["item"]!["content"]![1]!["annotations"]!.AsArray().Clear(); break;
        }
        AnnotationStreamFixtures.Sequence(source);
        var converter = new LlmProtocolStreamConverter(LlmProtocol.Responses, LlmProtocol.ChatCompletions, AnnotationStreamFixtures.Options);
        List<LlmConversionResult> emitted = [];
        Assert.Throws<InvalidDataException>(() => { foreach (var item in source) emitted.AddRange(converter.Push(item)); emitted.AddRange(converter.Complete()); });
        Assert.All(emitted, item => Assert.Null(JsonNode.Parse(item.Data.GetRawText())!["choices"]![0]!["finish_reason"]));
        Assert.Throws<InvalidOperationException>(() => converter.Complete());
    }

    /// <summary>所有快照都使用同一个非法范围时也必须失败，不能仅靠快照不一致误通过。</summary>
    [Theory] [InlineData("negative")] [InlineData("reverse")] [InlineData("past-text")]
    [InlineData("inside-surrogate")] [InlineData("fraction")] [InlineData("null")] [InlineData("missing")]
    public void UrlRange_IsValidatedEvenWhenEverySnapshotAgrees(string kind)
    {
        var source = AnnotationStreamFixtures.Read();
        foreach (var citation in AnnotationStreamFixtures.Citations(source))
            switch (kind)
            {
                case "negative": citation["start_index"] = -1; break;
                case "reverse": citation["start_index"] = 5; citation["end_index"] = 1; break;
                case "past-text": citation["end_index"] = 99; break;
                case "inside-surrogate": citation["start_index"] = 2; break;
                case "fraction": citation["end_index"] = 1.5; break;
                case "null": citation["start_index"] = null; break;
                case "missing": citation.Remove("end_index"); break;
            }
        Assert.Throws<InvalidDataException>(() => AnnotationStreamFixtures.Run(source));
    }

    /// <summary>原生函数输出项不能因为允许引用损耗而接收文本引用事件。</summary>
    [Fact]
    public void AnnotationCannotAttachToFunctionCall()
    {
        var converter = new LlmProtocolStreamConverter(LlmProtocol.Responses, LlmProtocol.ChatCompletions, AnnotationStreamFixtures.Options);
        foreach (var item in ConversionFixtures.Stream(LlmProtocol.Responses))
        {
            converter.Push(item);
            if (item["type"]?.GetValue<string>() != "response.output_item.added" || item["item"]?["type"]?.GetValue<string>() != "function_call") continue;
            var annotation = AnnotationStreamFixtures.Read().Single(e => Type(e) == "response.output_text.annotation.added");
            annotation["output_index"] = item["output_index"]!.DeepClone(); annotation["item_id"] = item["item"]!["id"]!.DeepClone(); annotation["sequence_number"] = int.MaxValue;
            var error = Assert.Throws<InvalidDataException>(() => converter.Push(annotation)); Assert.Contains("非消息", error.Message); return;
        }
        Assert.Fail("样本缺少原生函数项。");
    }

    /// <summary>只读取原生事件类型。</summary>
    private static string Type(JsonObject item) => item["type"]!.GetValue<string>();
}
