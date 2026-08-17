// Copyright (c) Microsoft. All rights reserved.

using System.ComponentModel;
using Azure.AI.AgentServer.Core;
using Azure.AI.Extensions.OpenAI;
using Azure.AI.Projects;
using Azure.Core;
using Azure.Identity;
using DotNetEnv;
using ERDC.Agents.Orchestrator;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Foundry.Hosting;
using Microsoft.Extensions.AI;
using static System.FormattableString;

Env.NoClobber().TraversePath().Load();

var projectEndpoint = new Uri(Environment.GetEnvironmentVariable("FOUNDRY_PROJECT_ENDPOINT")
    ?? throw new InvalidOperationException("FOUNDRY_PROJECT_ENDPOINT environment variable is not set."));
var deployment = Environment.GetEnvironmentVariable("AZURE_AI_MODEL_DEPLOYMENT_NAME") ?? "gpt-4.1";

// Off Azure there is no instance metadata endpoint, and the probe for it fails with a socket error
// rather than a clean "unavailable". DefaultAzureCredential treats that as fatal and abandons the
// chain before it reaches the signed-in developer, so managed identity has to be excluded outright
// when running locally.
TokenCredential credential = FoundryEnvironment.IsHosted
    ? new DefaultAzureCredential()
    : new DefaultAzureCredential(new DefaultAzureCredentialOptions { ExcludeManagedIdentityCredential = true });

var projectClient = new AIProjectClient(projectEndpoint, credential);


// Each specialist is exposed as a tool rather than called on a fixed schedule, so the model decides
// which domains a question touches and issues those calls in parallel.
var specialistTools = Specialists.All.Select(CreateSpecialistTool).ToList();

AIAgent agent = projectClient.AsAIAgent(
    model: deployment,
    instructions: OrchestratorInstructions.Text,
    name: "geo-orchestrator",
    description: "Answers geospatial questions by resolving a named place to coordinates and then consulting weather, terrain, mobility, and location specialists.",
    tools: specialistTools);

var builder = AgentHost.CreateBuilder(args);
builder.Services.AddFoundryResponses(new SourceReportingAgent(agent));
builder.RegisterProtocol("responses", endpoints => endpoints.MapFoundryResponses());

var app = builder.Build();
app.Run();

// The coordinate is a required parameter rather than a value the instructions ask the model to
// remember to write into the message, so a call that skipped the resolver cannot be formed at all.
AITool CreateSpecialistTool(Specialist specialist)
{
    var specialistAgent = projectClient.AsAIAgent(new AgentReference(specialist.AgentName));
    var options = new AIFunctionFactoryOptions
    {
        Name = specialist.ToolName,
        Description = specialist.Description
    };

    return specialist.Input switch
    {
        SpecialistInput.PlaceName => AIFunctionFactory.Create(
            ([Description("The place name, street address, or landmark to resolve.")] string place) =>
                AskAsync(specialistAgent, place),
            options),

        SpecialistInput.Coordinate => AIFunctionFactory.Create(
            (double latitude, double longitude,
             [Description("The user's question, worded as they asked it.")] string question) =>
                AskAsync(specialistAgent, Invariant($"Latitude: {latitude}, Longitude: {longitude}. {question}")),
            options),

        SpecialistInput.CoordinatePair => AIFunctionFactory.Create(
            (double latitude, double longitude,
             [Description("Destination latitude. Supply only for a routing question.")] double? destinationLatitude,
             [Description("Destination longitude. Supply only for a routing question.")] double? destinationLongitude,
             [Description("The user's question, worded as they asked it.")] string question) =>
                AskAsync(specialistAgent, destinationLatitude is { } destLat && destinationLongitude is { } destLon
                    ? Invariant($"Origin latitude: {latitude}, Origin longitude: {longitude}. Destination latitude: {destLat}, Destination longitude: {destLon}. {question}")
                    : Invariant($"Latitude: {latitude}, Longitude: {longitude}. {question}")),
            options),

        _ => throw new ArgumentOutOfRangeException(nameof(specialist), specialist.Input, "Unknown specialist input.")
    };
}

static async Task<string> AskAsync(AIAgent specialistAgent, string message) =>
    (await specialistAgent.RunAsync(message)).Text;
