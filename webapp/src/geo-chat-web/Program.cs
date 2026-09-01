// Copyright (c) Microsoft. All rights reserved.

using GeoLocation.Web.Components;
using GeoLocation.Web.Services;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Identity.Web;
using Microsoft.Identity.Web.UI;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOptions<OrchestratorOptions>()
    .Bind(builder.Configuration.GetSection(OrchestratorOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

var orchestrator = builder.Configuration.GetSection(OrchestratorOptions.SectionName).Get<OrchestratorOptions>()
    ?? new OrchestratorOptions();

// The user signs in here, and the same sign-in yields the token the orchestrator is called with.
// Asking for the downstream scope up front means the consent prompt happens once, at sign-in,
// rather than the first question failing on a missing grant.
builder.Services.AddAuthentication(OpenIdConnectDefaults.AuthenticationScheme)
    .AddMicrosoftIdentityWebApp(builder.Configuration.GetSection("AzureAd"))
    .EnableTokenAcquisitionToCallDownstreamApi([orchestrator.Scope])
    .AddInMemoryTokenCaches();

builder.Services.AddAuthorization(options =>
{
    // Nothing here is public: the whole point of the app is to call an API as the signed-in user.
    options.FallbackPolicy = options.DefaultPolicy;
});

builder.Services.AddHttpClient<OrchestratorClient>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(orchestrator.TimeoutSeconds);
});

// Scoped is the circuit, which is what the history and the sidebar both belong to.
builder.Services.AddScoped<AskSession>();

builder.Services.AddCascadingAuthenticationState();

// The sign-in and sign-out routes Microsoft.Identity.Web serves are MVC controllers, so the app
// needs a controller pipeline even though every page it owns is a Razor component.
builder.Services.AddControllersWithViews()
    .AddMicrosoftIdentityUI();

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents()
    .AddMicrosoftIdentityConsentHandler();

var app = builder.Build();

// App Service terminates TLS at the front end, so without this the app sees http and mints a
// redirect URI that Entra will not accept.
app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost
});

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

app.UseAuthentication();
app.UseAuthorization();

app.UseAntiforgery();

app.MapStaticAssets();
app.MapControllers();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
