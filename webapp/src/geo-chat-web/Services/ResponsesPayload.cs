// Copyright (c) Microsoft. All rights reserved.

using System.Text;
using System.Text.Json;

namespace GeoLocation.Web.Services;

/// <summary>
/// Reads the answer out of an OpenAI Responses payload.
/// </summary>
/// <remarks>
/// Kept separate from the client, and free of any transport type, so the shape the orchestrator
/// actually returns can be asserted in a unit test rather than only observed at runtime. The
/// payload is walked rather than deserialized into a model: only the text is displayed, and a
/// protocol that grows a field is then not a breaking change for this app.
/// </remarks>
internal static class ResponsesPayload
{
    /// <summary>
    /// Concatenates every text fragment the response carries, in the order it was produced.
    /// </summary>
    /// <returns>The answer text, or an empty string when the response carried none.</returns>
    public static string ExtractText(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        if (root.ValueKind != JsonValueKind.Object)
        {
            return string.Empty;
        }

        // Present on the newer responses, and already the concatenation this method would build.
        if (root.TryGetProperty("output_text", out var outputText) && outputText.ValueKind == JsonValueKind.String)
        {
            var text = outputText.GetString();
            if (!string.IsNullOrWhiteSpace(text))
            {
                return text.Trim();
            }
        }

        if (!root.TryGetProperty("output", out var output) || output.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();

        foreach (var item in output.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object
                || !item.TryGetProperty("content", out var content)
                || content.ValueKind != JsonValueKind.Array)
            {
                // Reasoning and tool-call items carry no content array; they are not the answer.
                continue;
            }

            foreach (var part in content.EnumerateArray())
            {
                if (part.ValueKind == JsonValueKind.Object
                    && part.TryGetProperty("text", out var partText)
                    && partText.ValueKind == JsonValueKind.String
                    && partText.GetString() is { Length: > 0 } value)
                {
                    if (builder.Length > 0)
                    {
                        builder.Append('\n');
                    }

                    builder.Append(value);
                }
            }
        }

        return builder.ToString().Trim();
    }
}
