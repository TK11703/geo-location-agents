# Geo Orchestrator

A hosted agent that answers geospatial questions by fanning out to the four specialist prompt agents
and merging their reports into one answer.

Each specialist is registered as a tool rather than called on a fixed schedule, so the model decides
which domains a question touches and calls those in parallel. The specialists in turn reach the
backend through API Management, so this agent never talks to the function app directly.

```
geo-orchestrator (hosted, this project)
    -> weather / terrain / mobility / location specialists (prompt agents)
        -> APIM  ->  function app
```

## Why this is a separate azd project

The repository root is an azd project using the Bicep provider, which is what provisions the
function app and the APIM API. This one uses the `microsoft.foundry` provider. A single azd project
cannot do both, so the two live side by side and are deployed independently.

## Deploying

```pwsh
azd deploy geo-orchestrator --cwd orchestrator
```

Deploy the named service, and do not run `azd provision` here. The Foundry account, the project, and
both model deployments are created by the root project's Bicep, so provisioning from this side would
give those resources a second owner. The `ai-project` service in [azure.yaml](azure.yaml) carries
only the endpoint, so that azd can resolve the reference.

Deployment is a remote build from source — there is no Dockerfile.

## Environment variables

| Variable | Where it comes from |
|----------|---------------------|
| `FOUNDRY_PROJECT_ENDPOINT` | `azure.yaml` when hosted, `src/geo-orchestrator/.env` when local |
| `AZURE_AI_MODEL_DEPLOYMENT_NAME` | same |

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

A hosted agent is reached at `/agents/<name>/endpoint/protocols/openai/responses`. The project-level
`/openai/v1/responses` route used for prompt agents rejects it.

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
