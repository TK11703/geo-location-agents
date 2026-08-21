// Copyright (c) Microsoft. All rights reserved.

using Azure.Core;

namespace GeoLocation.Orchestrator;

// The Foundry client libraries request https://ai.azure.com/.default with no option to change it.
// Sovereign tenants have no service principal behind that scope, and the managed identity endpoint
// reports the rejection as a credential failure rather than a scope failure, so the message names
// the wrong culprit. Rewriting the scope on the way through is the only seam available.
internal sealed class FoundryAudienceCredential(TokenCredential inner, string audience) : TokenCredential
{
    private const string CommercialScope = "https://ai.azure.com/.default";

    private readonly string _scope = $"{audience.TrimEnd('/')}/.default";

    public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
        inner.GetToken(Rewrite(requestContext), cancellationToken);

    public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
        inner.GetTokenAsync(Rewrite(requestContext), cancellationToken);

    private TokenRequestContext Rewrite(TokenRequestContext context)
    {
        if (!context.Scopes.Contains(CommercialScope, StringComparer.OrdinalIgnoreCase))
        {
            return context;
        }

        var scopes = context.Scopes
            .Select(scope => string.Equals(scope, CommercialScope, StringComparison.OrdinalIgnoreCase) ? _scope : scope)
            .ToArray();

        return new TokenRequestContext(
            scopes,
            context.ParentRequestId,
            context.Claims,
            context.TenantId,
            context.IsCaeEnabled);
    }
}
