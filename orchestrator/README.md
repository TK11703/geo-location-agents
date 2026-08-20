# Geo Orchestrator

An agent that answers geospatial questions by fanning out to the specialist prompt agents and
merging their reports into one answer.

It runs in one of two places. In commercial Azure the Foundry Agent Service hosts it; everywhere else
it runs as an ordinary ASP.NET Core app on Linux App Service behind API Management, because Agent
Service hosting is commercial-only today. The binary is the same either way — AgentHost serves the
OpenAI Responses protocol at `/responses` whatever it is running on — so only the address callers use
changes. The root [README](../README.md) covers how the choice is made and how to override it.

Each specialist is registered as a tool rather than called on a fixed schedule, so the model decides
which domains a question touches and calls those in parallel. The specialists in turn reach the
backend through API Management, so this agent never talks to the function app directly.

Coordinates are the exception to the fan-out. Every specialist needs a latitude and longitude and
none of them can look one up, so when the question names a place instead of giving numbers the
orchestrator calls the place resolver first, on its own, and passes the coordinate it returns to
whichever specialists the question touches. That first hop is sequential because everything in the
second hop depends on its result.

```
geo-orchestrator (this project)
    -> place-resolver (prompt agent)          first, alone, only when a place was named
    -> weather / terrain / mobility / location specialists (prompt agents)
        -> APIM  ->  function app
```

A name that matches several real places is not resolved on the user's behalf. The resolver returns
`needs_input` with the candidates it found, and the orchestrator asks which was meant rather than
producing an accurate answer about the wrong Springfield.

## Why this is a separate azd project

The repository root is an azd project using the Bicep provider, which is what provisions the
function app and the APIM API. This one uses the `microsoft.foundry` provider. A single azd project
cannot do both, so the two live side by side and are deployed independently.

This only applies to the Agent Service path. On App Service the same source is published with
`dotnet publish` and a zip deploy, driven by [New-Deployment.ps1](../scripts/New-Deployment.ps1)
rather than by azd: azd deploys every service it is given on every run, and there is no conditional
form of that, so a service defined here would also be pushed on the runs where the Agent Service is
not the target.

## Deploying

Neither of these is normally run by hand — [New-Deployment.ps1](../scripts/New-Deployment.ps1) picks
the right one from the resolved host. Under `FoundryHosted`:

```pwsh
azd deploy geo-orchestrator --cwd orchestrator
```

Deploy the named service, and do not run `azd provision` here. The Foundry account, the project, and
both model deployments are created by the root project's Bicep, so provisioning from this side would
give those resources a second owner. The `ai-project` service in [azure.yaml](azure.yaml) carries
only the endpoint, so that azd can resolve the reference.

Deployment is a remote build from source — there is no Dockerfile.

Under `LinuxAppService` the App Service and its plan are created by the root project's Bicep, and
only the code is pushed:

```pwsh
dotnet publish src/geo-orchestrator/geo-orchestrator.csproj -c Release -o <staging>
az webapp deploy --name <app> --resource-group <rg> --type zip --src-path <staging>.zip
```

## Environment variables

| Variable | Where it comes from |
|----------|---------------------|
| `FOUNDRY_PROJECT_ENDPOINT` | `azure.yaml` when Foundry-hosted, app settings on App Service, `src/geo-orchestrator/.env` when local |
| `AZURE_AI_MODEL_DEPLOYMENT_NAME` | same |
| `AZURE_CLIENT_ID` | App Service only; names the user-assigned identity for `DefaultAzureCredential` |
| `PORT` | App Service only; AgentHost binds this and overrides `ASPNETCORE_URLS` to do it |

`.env` is excluded from deployment by [.azdignore](src/geo-orchestrator/.azdignore), so anything the
hosted container needs has to be declared in `azure.yaml`.

## Running locally

```pwsh
az login
azd ai agent run --cwd orchestrator
```

The host listens on `http://localhost:8088`. Managed identity is excluded from the credential chain
off Azure — see the comment in [Program.cs](src/geo-orchestrator/Program.cs) for why that is not
optional.

## Testing

[ask.ps1](ask.ps1) sends a single request in a fresh conversation, which `azd ai agent invoke
--new-session` does not do: it keeps the conversation, so a repeated question can be answered from
history without calling any tool.

```pwsh
./orchestrator/ask.ps1 -Message 'Conditions at 47.6062, -122.3321?'
./orchestrator/ask.ps1 -Message 'Conditions at 51.5072, -0.1276?' -Deployed
```

A Foundry-hosted agent is reached at `/agents/<name>/endpoint/protocols/openai/responses`. The
project-level `/openai/v1/responses` route used for prompt agents rejects it. On App Service the
address is `https://<gateway>/orchestrator/responses` instead, and the token is requested for the
orchestrator API's own audience rather than for Foundry. `ask.ps1` reads both out of the environment,
so the same command works against either host.

London is the useful test case: it is outside both the USGS elevation coverage and the United States
National Weather Service, so a correct answer reports the elevation gap and attributes the weather
alert to the worldwide feed.

Attribution is not the model's job. The specialists record the tools that answered in the `sources`
field of their report, and [SourceReporting.cs](src/geo-orchestrator/SourceReporting.cs) reads that
off the tool results and appends the resulting limitation as a `Source notes` block. Asking the model
to state it produced it in four runs out of five; deriving it in code produced it in eight out of
eight, once each, and in none of the runs against a point the National Weather Service covers.

The model is told not to describe where data came from, so the note has one author and one wording.
Anything that changes which tool implies which limitation belongs in `SourceNotices.All`, alongside
its tests.
