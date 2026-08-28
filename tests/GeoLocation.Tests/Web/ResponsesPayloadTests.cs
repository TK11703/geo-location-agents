using GeoLocation.Web.Services;

namespace GeoLocation.Tests.Web;

public class ResponsesPayloadTests
{
    [Fact]
    public void Reads_the_text_out_of_an_output_item()
    {
        const string json = """
        {
          "output": [
            { "content": [ { "type": "output_text", "text": "Clear, 14 C." } ] }
          ]
        }
        """;

        Assert.Equal("Clear, 14 C.", ResponsesPayload.ExtractText(json));
    }

    [Fact]
    public void Prefers_the_flattened_output_text_when_the_response_carries_one()
    {
        const string json = """
        {
          "output_text": "Clear, 14 C.",
          "output": [
            { "content": [ { "type": "output_text", "text": "ignored" } ] }
          ]
        }
        """;

        Assert.Equal("Clear, 14 C.", ResponsesPayload.ExtractText(json));
    }

    [Fact]
    public void Joins_every_fragment_in_the_order_it_was_produced()
    {
        const string json = """
        {
          "output": [
            { "content": [ { "type": "output_text", "text": "Conditions." } ] },
            { "content": [ { "type": "output_text", "text": "Source notes." } ] }
          ]
        }
        """;

        Assert.Equal("Conditions.\nSource notes.", ResponsesPayload.ExtractText(json));
    }

    [Fact]
    public void Skips_items_that_carry_no_content()
    {
        // Reasoning and tool-call items sit in the same array as the answer.
        const string json = """
        {
          "output": [
            { "type": "reasoning", "summary": [] },
            { "type": "function_call", "name": "weather-specialist" },
            { "type": "message", "content": [ { "type": "output_text", "text": "Clear." } ] }
          ]
        }
        """;

        Assert.Equal("Clear.", ResponsesPayload.ExtractText(json));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{ "output": [] }""")]
    [InlineData("""{ "output_text": "   " }""")]
    [InlineData("[]")]
    public void Returns_empty_when_the_response_carries_no_text(string json)
    {
        Assert.Equal(string.Empty, ResponsesPayload.ExtractText(json));
    }
}
