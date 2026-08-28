// Copyright (c) Microsoft. All rights reserved.

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using Microsoft.Extensions.Options;
using Microsoft.Identity.Web;

namespace GeoLocation.Web.Services;

/// <summary>
/// Posts one question to the orchestrator as the signed-in user.
/// </summary>
/// <remarks>
/// The bearer token is the user's own, acquired for the orchestrator's audience rather than for
/// this app, which is what lets API Management authorize the person at the keyboard instead of the
/// web tier acting for everyone. Nothing here holds a key: the token comes from the cache
/// Microsoft.Identity.Web populated when the user signed in.
/// </remarks>
public sealed class OrchestratorClient(
    HttpClient httpClient,
    ITokenAcquisition tokenAcquisition,
    IOptions<OrchestratorOptions> options,
    ILogger<OrchestratorClient> logger)
{
    private readonly OrchestratorOptions _options = options.Value;

    /// <summary>
    /// Asks the orchestrator a question and returns the answer it produced.
    /// </summary>
    /// <remarks>
    /// Every call is a new conversation. The orchestrator stores nothing, so history shown in the
    /// browser is a record of what was asked rather than context the model is given.
    /// </remarks>
    public async Task<string> AskAsync(string question, ClaimsPrincipal user, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(question);
        ArgumentNullException.ThrowIfNull(user);

        var token = await tokenAcquisition.GetAccessTokenForUserAsync([_options.Scope], user: user)
            .ConfigureAwait(false);

        using var request = new HttpRequestMessage(HttpMethod.Post, _options.Endpoint)
        {
            Content = JsonContent.Create(new { input = question, store = false })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        HttpResponseMessage response;

        try
        {
            response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new OrchestratorException(
                $"The orchestrator did not answer within {_options.TimeoutSeconds} seconds.", ex);
        }
        catch (HttpRequestException ex)
        {
            throw new OrchestratorException($"Could not reach the orchestrator at {_options.Endpoint}.", ex);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                // The body of a failed call is a gateway or agent diagnostic, not something to put in
                // front of the user; it is logged instead and the status is reported on its own.
                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                logger.LogError("Orchestrator returned {StatusCode}: {Body}", (int)response.StatusCode, body);

                throw new OrchestratorException(response.StatusCode switch
                {
                    HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                        "The orchestrator rejected this account's token. Confirm the account has been granted access to the orchestrator API.",
                    HttpStatusCode.TooManyRequests =>
                        "The orchestrator is rate limited right now. Try again in a minute.",
                    _ => $"The orchestrator returned {(int)response.StatusCode} {response.ReasonPhrase}."
                });
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var answer = ResponsesPayload.ExtractText(json);

            return string.IsNullOrWhiteSpace(answer)
                ? "The orchestrator answered without any text."
                : answer;
        }
    }
}
