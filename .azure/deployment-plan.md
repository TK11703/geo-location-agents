# Azure Functions Map Image API Plan

> **Status:** Completed (application only)

Generated: 2026-08-10

---

## 1. Project Overview

**Goal:** Create a C# Azure Functions application targeting .NET 10. The HTTP API accepts either a city name or latitude/longitude coordinates and returns a static PNG map image from Azure Maps.

**Path:** New project

**Scope:** Application only. This task will not generate infrastructure or provision Azure resources.

---

## 2. Requirements

| Attribute | Value |
|-----------|-------|
| Classification | POC |
| Scale | Small |
| Budget | Cost-optimized |
| Runtime | Azure Functions v4, C# isolated worker, .NET 10 (GA) |
| API authorization | Function key |
| Subscription | Not applicable - no Azure deployment requested |
| Location | Not applicable - no Azure deployment requested |
| Compliance | No special requirements specified |

---

## 3. API Contract

**Route:** `api/map`

**Methods:** `GET`, `POST`

**GET examples:**
- `GET /api/map?city=Seattle`
- `GET /api/map?latitude=47.6062&longitude=-122.3321`

**POST examples:**
- `{ "city": "Seattle" }`
- `{ "latitude": 47.6062, "longitude": -122.3321 }`

Optional rendering values: `width`, `height`, and `zoom`, constrained to Azure Maps limits.

Exactly one location form is required: either a non-empty city or both coordinates. Invalid input returns an RFC 7807-style JSON error. A successful request returns the upstream bytes as `image/png`.

---

## 4. Architecture

| Component | Technology | Responsibility |
|-----------|------------|----------------|
| HTTP function | .NET 10 isolated worker | Parse GET/POST input, validate it, and return PNG/error responses |
| Map service | Typed `HttpClient` | Geocode city names and request static map images |
| Azure Maps Search | REST API `2026-01-01` | Resolve a city to longitude/latitude |
| Azure Maps Render | REST API `2024-04-01` | Render a road map with a pushpin |
| Configuration | Environment/local settings | Supply `AZURE_MAPS_SUBSCRIPTION_KEY`; secrets remain uncommitted |

The application-only scope uses an Azure Maps subscription key through a server-side header. A future deployment should replace this with managed identity and Azure Maps RBAC.

---

## 5. Provisioning Limit Checklist

No resources will be provisioned, so subscription quota and regional capacity checks are not applicable.

| Resource Type | Number to Deploy | Quota Check |
|---------------|------------------|-------------|
| None | 0 | Not applicable |

---

## 6. Files to Generate

| Path | Purpose |
|------|---------|
| `ERDC.Agents.slnx` | Solution definition |
| `src/ERDC.Agents/ERDC.Agents.csproj` | .NET 10 isolated worker project |
| `src/ERDC.Agents/Program.cs` | Dependency injection and worker startup |
| `src/ERDC.Agents/Functions/GetMapFunction.cs` | GET/POST HTTP endpoint |
| `src/ERDC.Agents/Models/MapRequest.cs` | Request model and rendering defaults |
| `src/ERDC.Agents/Services/AzureMapsService.cs` | Geocoding and static map integration |
| `src/ERDC.Agents/host.json` | Functions host configuration |
| `src/ERDC.Agents/local.settings.example.json` | Secret-free local configuration template |
| `tests/ERDC.Agents.Tests/*` | Focused request validation and service tests |
| `.gitignore` | Exclude secrets and build output |
| `README.md` | Setup, configuration, local run, and sample requests |

Exact package versions will be selected from current stable NuGet releases compatible with .NET 10 and verified by restore/build.

---

## 7. Execution Checklist

### Planning
- [x] Analyze workspace
- [x] Gather requirements
- [x] Confirm .NET 10 isolated-worker support
- [x] Confirm current Azure Maps API contracts
- [x] Define application architecture and API contract
- [x] User approved this plan

### Implementation
- [x] Fetch the current C# HTTP Functions template baseline
- [x] Generate the .NET 10 application and tests
- [x] Implement GET/POST parsing and validation
- [x] Implement Azure Maps geocoding and PNG rendering
- [x] Add local configuration and usage documentation

### Validation
- [x] Restore dependencies
- [x] Build the solution in Release configuration
- [x] Run 13 automated tests
- [x] Verify formatting
- [x] Verify there are no relevant editor diagnostics

### Validation Evidence

| Check | Result |
|-------|--------|
| `dotnet test .\ERDC.Agents.slnx --no-restore --configuration Release` | Passed: 13 tests, 0 failures |
| `dotnet format .\ERDC.Agents.slnx --verify-no-changes --no-restore` | Passed |
| VS Code diagnostics for `src` and `tests` | No errors |
| NuGet vulnerability audit | Not completed: configured Visual Studio package feed DNS lookup failed |

---

## 8. Completion

The approved application-only scope is complete. Azure infrastructure validation and deployment were not requested.
