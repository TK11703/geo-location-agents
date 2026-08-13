// Copyright (c) Microsoft. All rights reserved.

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
var specialistTools = Specialists.All
    .Select(specialist => (AITool)projectClient
        .AsAIAgent(new AgentReference(specialist.AgentName))
        .AsAIFunction(new AIFunctionFactoryOptions
        {
            Name = specialist.ToolName,
            Description = specialist.Description
        }))
    .ToList();

AIAgent agent = projectClient.AsAIAgent(
    model: deployment,
    instructions: OrchestratorInstructions.Text,
    name: "geo-orchestrator",
    description: "Answers geospatial questions by consulting weather, terrain, mobility, and location specialists.",
    tools: specialistTools);

var builder = AgentHost.CreateBuilder(args);
builder.Services.AddFoundryResponses(new SourceReportingAgent(agent));
builder.RegisterProtocol("responses", endpoints => endpoints.MapFoundryResponses());

var app = builder.Build();
app.Run();
