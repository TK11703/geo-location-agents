// Copyright (c) Microsoft. All rights reserved.

namespace GeoLocation.Web.Services;

/// <summary>
/// The questions asked in one circuit, and the channel the navigation uses to ask one again.
/// </summary>
/// <remarks>
/// The history lives in the sidebar and the questions are answered by the page, which are separate
/// components with no parent between them, so the state and the request both pass through here.
/// </remarks>
public sealed class AskSession
{
    private readonly List<AskRecord> _history = [];

    public IReadOnlyList<AskRecord> History => _history;

    /// <summary>Raised when the history changed, so the sidebar can redraw.</summary>
    public event Action? HistoryChanged;

    /// <summary>Raised when a recorded question should be asked again.</summary>
    public event Func<string, Task>? Requested;

    public void Record(string question)
    {
        // Asking the same thing again moves it up rather than repeating it.
        _history.RemoveAll(entry => entry.Question == question);
        _history.Insert(0, new AskRecord(question, DateTimeOffset.Now));
        HistoryChanged?.Invoke();
    }

    public Task RequestAsync(string question) => Requested?.Invoke(question) ?? Task.CompletedTask;
}

/// <summary>A question that was asked, and when it was last asked.</summary>
public sealed record AskRecord(string Question, DateTimeOffset Asked);
