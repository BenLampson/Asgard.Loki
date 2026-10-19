using System.Text.Json.Nodes;
using Xunit;

namespace Asgard.Loki.LLM.Conversion.Tests;

/// <summary>验证字节范围、重复文本 part、来源顺序和完整响应的显式损耗边界。</summary>
public sealed class GeminiGroundingTests
{
    /// <summary>完整报文与独立 golden 比较；包含省略零、重复 URL、多来源和三个 Unicode 计量单位。</summary>
    [Theory] [MemberData(nameof(GeminiGroundingFixtures.Directions), MemberType = typeof(GeminiGroundingFixtures))]
    public void CompleteResponse_MatchesIndependentGolden(LlmProtocol target, LlmCitationIndexUnit unit)
    {
        var source = GeminiGroundingFixtures.Read(); var before = source.ToJsonString();
        var result = GeminiGroundingFixtures.Convert(source, target, GeminiGroundingFixtures.Options(unit));
        CustomToolConversionTests.Equal(GeminiGroundingFixtures.Read(target.ToString() + unit), CustomToolConversionTests.Read(result));
        Assert.Empty(result.Diagnostics); Assert.Equal(before, source.ToJsonString());
        CustomToolConversionTests.Equal(source, JsonNode.Parse(result.OriginalData.GetRawText()));
    }

    /// <summary>单位没有声明时维持原来的严格边界，损耗许可也不猜测 OpenAI 单位。</summary>
    [Theory] [InlineData(LlmProtocol.ChatCompletions)] [InlineData(LlmProtocol.Responses)]
    public void UnspecifiedUnit_DoesNotInventRanges(LlmProtocol target)
    {
        var source = GeminiGroundingFixtures.Read();
        var error = Assert.Throws<LlmConversionException>(() => GeminiGroundingFixtures.Convert(source, target, new()));
        Assert.Contains(error.Diagnostics, d => d.Code == "grounding_metadata_not_representable");
        var result = GeminiGroundingFixtures.Convert(source, target, new() { AllowLoss = true });
        Assert.DoesNotContain("url_citation", result.Data.GetRawText());
        Assert.Contains(result.Diagnostics, d => d.Code == "grounding_metadata_not_representable");
    }

    /// <summary>无效关联、切断 UTF-8 的偏移与不匹配文本不能因损耗许可而变成成功引用。</summary>
    [Theory]
    [InlineData("start-byte")] [InlineData("end-byte")] [InlineData("reversed")] [InlineData("past-end")]
    [InlineData("part")] [InlineData("negative")] [InlineData("fraction")] [InlineData("overflow")]
    [InlineData("null-index")] [InlineData("string-index")] [InlineData("source")] [InlineData("source-negative")]
    [InlineData("source-fraction")] [InlineData("text")] [InlineData("non-text-part")] [InlineData("oneof")]
    [InlineData("uri")] [InlineData("image-oneof")]
    public void InvalidRangeOrAssociation_IsHardFailure(string kind)
    {
        foreach (var allowLoss in new[] { false, true })
        {
            var source = GeminiGroundingFixtures.Read(); var metadata = GeminiGroundingFixtures.Metadata(source);
            var support = metadata["groundingSupports"]![0]!.AsObject(); var segment = support["segment"]!.AsObject();
            switch (kind)
            {
                case "image-oneof": metadata["groundingChunks"]![0]!["image"] = new JsonObject(); break;
                case "start-byte": segment["startIndex"] = 4; break;
                case "end-byte": segment["endIndex"] = 5; break;
                case "reversed": segment["startIndex"] = 10; segment["endIndex"] = 3; break;
                case "past-end": segment["endIndex"] = 99; break;
                case "part": segment["partIndex"] = 3; break;
                case "negative": segment["partIndex"] = -1; break;
                case "fraction": segment["startIndex"] = 0.5; break;
                case "overflow": segment["startIndex"] = 2147483648L; break;
                case "null-index": segment["startIndex"] = null; break;
                case "string-index": segment["startIndex"] = "3"; break;
                case "source": support["groundingChunkIndices"] = new JsonArray(2); break;
                case "source-negative": support["groundingChunkIndices"] = new JsonArray(-1); break;
                case "source-fraction": support["groundingChunkIndices"] = new JsonArray(0.5); break;
                case "text": segment["text"] = "wrong"; break;
                case "non-text-part": source["candidates"]![0]!["content"]!["parts"]![1] = CustomToolFixtures.Parse("""{"functionCall":{"id":"call_1","name":"lookup","args":{}}}"""); break;
                case "oneof": metadata["groundingChunks"]![0]!["retrievedContext"] = new JsonObject(); break;
                case "uri": metadata["groundingChunks"]![0]!["web"]!["uri"] = "file:///private/a"; break;
            }
            Assert.Throws<InvalidDataException>(() => GeminiGroundingFixtures.Convert(source, options: GeminiGroundingFixtures.Options() with { AllowLoss = allowLoss }));
        }
    }

    /// <summary>搜索展示、查询与置信分数都保留独立损耗记录，不能伪装搜索执行或静默消失。</summary>
    [Theory] [InlineData("webSearchQueries")] [InlineData("searchEntryPoint")] [InlineData("retrievalMetadata")]
    [InlineData("futureNull")] [InlineData("futureFalse")] [InlineData("confidenceScores")]
    public void UnrepresentableMetadata_RequiresSpecificConsent(string name)
    {
        var source = GeminiGroundingFixtures.Read(); var metadata = GeminiGroundingFixtures.Metadata(source);
        if (name == "confidenceScores") metadata["groundingSupports"]![0]![name] = new JsonArray(0.9, 0.8, 0.7);
        else metadata[name] = name switch { "webSearchQueries" => new JsonArray("test"), "futureNull" => null, "futureFalse" => JsonValue.Create(false), _ => new JsonObject { ["renderedContent"] = "<div>search</div>" } };
        var error = Assert.Throws<LlmConversionException>(() => GeminiGroundingFixtures.Convert(source));
        Assert.Contains(error.Diagnostics, d => d.Path.EndsWith("." + name, StringComparison.Ordinal));
        var result = GeminiGroundingFixtures.Convert(source, options: GeminiGroundingFixtures.Options() with { AllowLoss = true });
        Assert.Single(result.Diagnostics); Assert.Contains("url_citation", result.Data.GetRawText()); Assert.DoesNotContain("web_search_call", result.Data.GetRawText());
        CustomToolConversionTests.Equal(GeminiGroundingFixtures.Read("ResponsesUnicodeScalar"), CustomToolConversionTests.Read(result));
        CustomToolConversionTests.Equal(source, JsonNode.Parse(result.OriginalData.GetRawText()));
    }

    /// <summary>缺失来源详情、私有检索源、未关联来源或无来源的 support 不能宣称完整无损。</summary>
    [Theory] [InlineData("missing-title")] [InlineData("retrieved")] [InlineData("unused")] [InlineData("empty-indices")]
    public void UnrepresentableSource_IsDiagnosed(string kind)
    {
        var source = GeminiGroundingFixtures.Read(); var metadata = GeminiGroundingFixtures.Metadata(source);
        switch (kind)
        {
            case "missing-title": metadata["groundingChunks"]![0]!["web"]!.AsObject().Remove("title"); break;
            case "retrieved": metadata["groundingChunks"]![0] = CustomToolFixtures.Parse("""{"retrievedContext":{"uri":"gs://bucket/doc","title":"private"}}"""); break;
            case "unused": metadata["groundingChunks"]!.AsArray().Add(CustomToolFixtures.Parse("""{"web":{"uri":"https://unused.invalid/","title":"unused"}}""")); break;
            case "empty-indices": metadata["groundingSupports"]![0]!.AsObject().Remove("groundingChunkIndices"); break;
        }
        Assert.Throws<LlmConversionException>(() => GeminiGroundingFixtures.Convert(source));
        var result = GeminiGroundingFixtures.Convert(source, options: GeminiGroundingFixtures.Options() with { AllowLoss = true });
        Assert.Contains(result.Diagnostics, d => d.Code.StartsWith("grounding_", StringComparison.Ordinal));
    }

    /// <summary>显式空对象与不合法的 null、数组、support 对象保持不同，原生同协议仍完整复制。</summary>
    [Fact]
    public void EmptyMetadataAndNativeCopy_KeepTheirContracts()
    {
        var source = GeminiGroundingFixtures.Read(); source["candidates"]![0]!["groundingMetadata"] = new JsonObject();
        Assert.Empty(GeminiGroundingFixtures.Convert(source).Diagnostics);
        source["candidates"]![0]!["groundingMetadata"] = null;
        Assert.Throws<ArgumentException>(() => GeminiGroundingFixtures.Convert(source));
        var native = LlmProtocolConverter.ConvertResponse(source, LlmProtocol.Gemini, LlmProtocol.Gemini);
        CustomToolConversionTests.Equal(source, CustomToolConversionTests.Read(native)); Assert.Empty(native.Diagnostics);
    }

    /// <summary>不为没有引用结构的 Claude 目标发明字段，也不绕过 Gemini 签名硬边界。</summary>
    [Fact]
    public void OtherTargetAndOpaqueThought_StayUnsupported()
    {
        var source = GeminiGroundingFixtures.Read();
        var error = Assert.Throws<LlmConversionException>(() => GeminiGroundingFixtures.Convert(source, LlmProtocol.ClaudeMessages));
        Assert.Contains(error.Diagnostics, d => d.Code == "grounding_metadata_not_representable");
        source["candidates"]![0]!["content"]!["parts"]![0]!["thoughtSignature"] = "opaque";
        var opaque = Assert.Throws<LlmConversionException>(() => GeminiGroundingFixtures.Convert(source, options: GeminiGroundingFixtures.Options() with { AllowLoss = true }));
        Assert.Contains(opaque.Diagnostics, d => d.Code == "opaque_state");
    }

    /// <summary>原始 part 索引包括函数 part，引用定位不能误用文本序号，工具 ID 与参数不变。</summary>
    [Fact]
    public void FunctionBetweenTextParts_PreservesOriginalAssociation()
    {
        var source = GeminiGroundingFixtures.Read();
        source["candidates"]![0]!["content"]!["parts"]!.AsArray().Insert(1,
            CustomToolFixtures.Parse("""{"functionCall":{"id":"call_exact","name":"lookup","args":{"n":0}}}"""));
        var supports = GeminiGroundingFixtures.Metadata(source)["groundingSupports"]!.AsArray();
        supports[0]!["segment"]!["partIndex"] = 2; supports[2]!["segment"]!["partIndex"] = 3;
        var data = CustomToolConversionTests.Read(GeminiGroundingFixtures.Convert(source));
        var output = data["output"]!.AsArray(); Assert.Equal(3, output.Count);
        Assert.Equal("call_exact", output[1]!["call_id"]!.GetValue<string>());
        Assert.Equal("{\"n\":0}", output[1]!["arguments"]!.GetValue<string>());
        Assert.Equal(3, output[2]!["content"]![0]!["annotations"]!.AsArray().Count);
        Assert.Equal(1L, output[2]!["content"]![0]!["annotations"]![0]!["start_index"]!.GetValue<long>());
    }

    /// <summary>已有引用不能把截断响应改成成功完成，原有终态映射仍然生效。</summary>
    [Theory] [InlineData(LlmProtocol.ChatCompletions)] [InlineData(LlmProtocol.Responses)]
    public void IncompleteReply_KeepsItsTerminalState(LlmProtocol target)
    {
        var source = GeminiGroundingFixtures.Read(); source["candidates"]![0]!["finishReason"] = "MAX_TOKENS";
        var data = CustomToolConversionTests.Read(GeminiGroundingFixtures.Convert(source, target));
        Assert.Contains("url_citation", data.ToJsonString());
        if (target == LlmProtocol.ChatCompletions) Assert.Equal("length", data["choices"]![0]!["finish_reason"]!.GetValue<string>());
        else { Assert.Equal("incomplete", data["status"]!.GetValue<string>()); Assert.Equal("max_output_tokens", data["incomplete_details"]!["reason"]!.GetValue<string>()); }
    }
}
