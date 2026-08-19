using MqttVision.Server.Infrastructure.Vision;

namespace MqttVision.Server.Tests.Infrastructure.Vision;

/// <summary>
/// PaddleOCR serving 响应解析的纯逻辑测试。无需起 HTTP 服务 —— 直接喂 JSON 字符串
/// 验证 basic-serving / high-stability(HPS 包裹)两种响应的解析分支。
/// </summary>
public class PaddleOcrServingResponseParserTests
{
    private const double MinimumScore = 0.8;

    [Fact]
    public void Basic_serving_rec_texts_with_score_above_threshold_is_recognized()
    {
        var body = """
                   {
                     "result": {
                       "rec_texts": ["001/1n-1D01"],
                       "rec_scores": [0.95]
                     }
                   }
                   """;

        var result = PaddleOcrServingTextRecognizer.ParseServingResponse(body, MinimumScore);

        result.Status.Should().Be("recognized");
        result.Text.Should().Be("001/1N-1D01");
        result.Confidence.Should().Be(0.95);
        result.ErrorMessage.Should().BeNull();
    }

    [Fact]
    public void Basic_serving_score_below_threshold_is_unrecognized()
    {
        var body = """
                   {
                     "result": {
                       "rec_texts": ["001/1n-1D01"],
                       "rec_scores": [0.4]
                     }
                   }
                   """;

        var result = PaddleOcrServingTextRecognizer.ParseServingResponse(body, MinimumScore);

        result.Status.Should().Be("unrecognized");
        result.Text.Should().Be("001/1N-1D01");
        result.Confidence.Should().Be(0.4);
        result.ErrorMessage.Should().Contain("below threshold");
    }

    [Fact]
    public void Basic_serving_plain_terminal_text_is_recognized_by_generic_parser()
    {
        // 通用响应解析器不再假设目标类型；端子编号由流水线按目标类型校验。
        var body = """
                   {
                     "result": {
                       "rec_texts": ["0011D01"],
                       "rec_scores": [0.99]
                     }
                   }
                   """;

        var result = PaddleOcrServingTextRecognizer.ParseServingResponse(body, MinimumScore);

        result.Status.Should().Be("recognized");
        result.Text.Should().Be("0011D01");
        result.Confidence.Should().Be(0.99);
        result.ErrorMessage.Should().BeNull();
    }

    [Fact]
    public void Basic_serving_normalizes_fullwidth_and_repeated_slashes()
    {
        // 全角 ／ 与重复斜杠应被归一化为单个 /,并大写。
        var body = """
                   {
                     "result": {
                       "rec_texts": ["001／／1n-1D01"],
                       "rec_scores": [0.9]
                     }
                   }
                   """;

        var result = PaddleOcrServingTextRecognizer.ParseServingResponse(body, MinimumScore);

        result.Status.Should().Be("recognized");
        result.Text.Should().Be("001/1N-1D01");
    }

    [Fact]
    public void Basic_serving_picks_highest_scoring_canonical_candidate()
    {
        // 多行结果时,优先选规范标记且得分最高者;此处低分行无 '-'/'/' 会被降权。
        var body = """
                   {
                     "result": {
                       "rec_texts": ["noise", "002/2n-2D02"],
                       "rec_scores": [0.99, 0.88]
                     }
                   }
                   """;

        var result = PaddleOcrServingTextRecognizer.ParseServingResponse(body, MinimumScore);

        result.Status.Should().Be("recognized");
        result.Text.Should().Be("002/2N-2D02");
        result.Confidence.Should().Be(0.88);
    }

    [Fact]
    public void Basic_serving_empty_rec_texts_yields_no_text()
    {
        var body = """
                   {
                     "result": {
                       "rec_texts": [],
                       "rec_scores": []
                     }
                   }
                   """;

        var result = PaddleOcrServingTextRecognizer.ParseServingResponse(body, MinimumScore);

        result.Status.Should().Be("no-text");
    }

    [Fact]
    public void Basic_serving_errorCode_is_treated_as_failure()
    {
        var body = """
                   {
                     "errorCode": 500,
                     "errorMsg": "model not loaded"
                   }
                   """;

        var result = PaddleOcrServingTextRecognizer.ParseServingResponse(body, MinimumScore);

        result.Status.Should().Be("failed");
        result.ErrorMessage.Should().Contain("model not loaded");
    }

    [Fact]
    public void High_stability_hps_wrapped_inner_json_is_parsed_like_basic()
    {
        // HPS(Triton)响应:outputs[].data[] 内嵌一个 JSON 字符串,内容即 basic-serving 的 result 结构。
        var innerJson = """
                        {"result":{"rec_texts":["003/3n-3D03"],"rec_scores":[0.91]}}
                        """;
        var body = $$"""
                     {
                       "outputs": [
                         {
                           "name": "output",
                           "data": [{{JsonEscape(innerJson)}}]
                         }
                       ]
                     }
                     """;

        var result = PaddleOcrServingTextRecognizer.ParseServingResponse(body, MinimumScore);

        result.Status.Should().Be("recognized");
        result.Text.Should().Be("003/3N-3D03");
        result.Confidence.Should().Be(0.91);
    }

    [Fact]
    public void OcrResults_array_with_prunedResult_is_parsed()
    {
        // 某些 PaddleX 版本把结果放进 ocrResults[].prunedResult。
        var body = """
                   {
                     "ocrResults": [
                       { "prunedResult": { "rec_texts": ["004/4n-4D04"], "rec_scores": [0.85] } }
                     ]
                   }
                   """;

        var result = PaddleOcrServingTextRecognizer.ParseServingResponse(body, MinimumScore);

        result.Status.Should().Be("recognized");
        result.Text.Should().Be("004/4N-4D04");
    }

    private static string JsonEscape(string value)
    {
        // 将内嵌 JSON 字符串转义为合法的 JSON 字符串字面量内容(含引号)。
        return System.Text.Json.JsonSerializer.Serialize(value);
    }
}
