// Copyright (c) Microsoft. All rights reserved.

using System.ComponentModel.DataAnnotations;

namespace GeoLocation.Web.Services;

/// <summary>
/// Where the orchestrator is and what a token has to be minted for to reach it.
/// </summary>
/// <remarks>
/// Both values are addresses rather than behavior, because the orchestrator is reachable at two
/// different ones depending on how it was deployed: the API Management gateway when it is
/// self-hosted, and the Foundry agent endpoint when the Agent Service hosts it. The token audience
/// differs with it, so the pair always moves together.
/// </remarks>
public sealed class OrchestratorOptions
{
    public const string SectionName = "Orchestrator";

    /// <summary>
    /// Absolute URL that accepts an OpenAI Responses request.
    /// </summary>
    [Required(AllowEmptyStrings = false)]
    [Url]
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>
    /// Delegated scope the signed-in user's token is requested for.
    /// </summary>
    [Required(AllowEmptyStrings = false)]
    public string Scope { get; set; } = string.Empty;

    /// <summary>
    /// A cold model fanning out across five specialists runs well past a client's default timeout.
    /// The gateway gives up at 230 seconds, so waiting much longer than that only reports a failure
    /// the backend has already reported.
    /// </summary>
    [Range(30, 600)]
    public int TimeoutSeconds { get; set; } = 240;
}
