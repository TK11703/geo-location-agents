using System.Runtime.CompilerServices;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace GeoLocation.Orchestrator;

/// <summary>
/// Adds the notices the specialists' sources oblige to every answer that owes one. The model writes
/// the analysis; whether a provenance limit reaches the user is not left to it.
/// </summary>
internal sealed class SourceReportingAgent(AIAgent inner) : DelegatingAIAgent(inner)
{
    protected override async Task<AgentResponse> RunCoreAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session,
        AgentRunOptions? options,
        CancellationToken cancellationToken)
    {
        var response = await base.RunCoreAsync(messages, session, options, cancellationToken);

        var notices = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var message in response.Messages)
        {
            Collect(message.Contents, notices);
        }

        if (Format(notices) is { } text)
        {
            response.Messages.Add(new ChatMessage(ChatRole.Assistant, text));
        }

        return response;
    }

    protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session,
        AgentRunOptions? options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var notices = new SortedSet<string>(StringComparer.Ordinal);

        await foreach (var update in base.RunCoreStreamingAsync(messages, session, options, cancellationToken))
        {
            Collect(update.Contents, notices);
            yield return update;
        }

        if (Format(notices) is { } text)
        {
            yield return new AgentResponseUpdate(ChatRole.Assistant, text);
        }
    }

    // Specialist reports come back as ordinary tool results, so reading them here keeps this to a
    // local variable. An earlier version collected them in an AsyncLocal from a wrapper around each
    // tool and silently held nothing: assigning an AsyncLocal inside an async iterator does not
    // survive, because every MoveNextAsync restores the caller's context.
    private static void Collect(IEnumerable<AIContent> contents, ISet<string> notices)
    {
        foreach (var content in contents)
        {
            if (content is FunctionResultContent result)
            {
                SourceNotices.AddFrom(result.Result?.ToString(), notices);
            }
        }
    }

    // Leads with a blank line because this arrives as its own message and a client that concatenates
    // them would otherwise run it into the last sentence of the answer.
    private static string? Format(IReadOnlyCollection<string> notices) =>
        notices.Count == 0
            ? null
            : "\n\nSource notes:\n" + string.Join("\n", notices.Select(notice => $"- {notice}"));
}
