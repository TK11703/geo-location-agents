# Azure Maps Functions

A .NET 10 Azure Functions v4 isolated-worker application that returns static PNG maps,
calculated routes, and point-level site data for a latitude and longitude.

Each endpoint is independent and does exactly one thing: it retrieves data from a single
upstream source and returns it. Nothing in this application decides whether a site is
reachable or serviceable. That determination is left to the caller, which is expected to
be a model or agent that selects the endpoints it needs and reasons over the combined
results.

## API

`GET /api/map`

| Parameter | Required | Description |
|-----------|----------|-------------|
| `city` | Conditional | City or place name. Do not combine with coordinates. |
| `latitude` | Conditional | Latitude from -90 to 90. Requires `longitude`. |
| `longitude` | Conditional | Longitude from -180 to 180. Requires `latitude`. |
| `width` | No | Image width from 80 to 2000. Default: 512. |
| `height` | No | Image height from 80 to 1500. Default: 512. |
| `zoom` | No | Zoom from 0 to 20. Default: 12. Do not combine with `radiusMeters`. |
| `radiusMeters` | No | Ground radius from 25 to 500000 to frame instead of a zoom level. Requires coordinates. |
| `mapType` | No | `road`, `dark`, or `satellite`. Default: `road`. Satellite supports zoom 0 to 19. |

For example, request satellite imagery for Seattle:

```text
http://localhost:7071/api/map?city=Seattle&mapType=satellite&zoom=10
```

Coordinates can be used instead of a city name. This requests close-in satellite imagery
of a specific point rather than a whole place:

```text
http://localhost:7071/api/map?latitude=47.6062&longitude=-122.3321&mapType=satellite&zoom=18
```

Supply `latitude` and `longitude` together, and do not combine them with `city`.

When the area of interest is known in meters rather than zoom levels, `radiusMeters` can
be used in place of `zoom`. It matches the `radiusMeters` parameter on the site data
endpoints, so the same value can frame an image and scope a traffic or elevation query:

```text
http://localhost:7071/api/map?latitude=47.6062&longitude=-122.3321&mapType=satellite&radiusMeters=250
```

The zoom level is derived so that the radius is visible in every direction, which means
the shorter side of the image spans at least twice the radius. Because zoom levels are
whole numbers, the derived level is rounded down and the resulting image usually covers
slightly more ground than requested. The two parameters are mutually exclusive, and
`radiusMeters` requires coordinates because the conversion depends on latitude; pair
`city` with `zoom` instead.

`POST /api/map` accepts the same values in a JSON body:

```json
{
  "city": "Seattle",
  "width": 640,
  "height": 480,
  "zoom": 10,
  "mapType": "satellite"
}
```

Or:

```json
{
  "latitude": 47.6062,
  "longitude": -122.3321
}
```

Successful responses have content type `image/png`. Invalid requests return JSON problem details. Unknown cities return `404`, and Azure Maps upstream failures return `502`.

Azure Maps Static Map supports road, dark grey, and satellite/aerial imagery. Terrain and 3D are not available from the static PNG API and are rejected as invalid map types.

## Route API

`GET /api/route`

| Parameter | Required | Description |
|-----------|----------|-------------|
| `originLatitude` | Yes | Origin latitude from -90 to 90. |
| `originLongitude` | Yes | Origin longitude from -180 to 180. |
| `destinationLatitude` | Yes | Destination latitude from -90 to 90. |
| `destinationLongitude` | Yes | Destination longitude from -180 to 180. |
| `travelMode` | No | `car`, `truck`, or `pedestrian`. Default: `car`. |
| `zoom` | No | Image zoom from 0 to 20. By default, the image is fitted to the route. |
| `output` | No | `map` returns a PNG; `details` returns route GeoJSON. Default: `map`. |
| `axleCount` | No | Known number of truck axles. Truck routes only. |
| `axleWeight` | No | Known weight per axle in kilograms. Truck routes only. |
| `height` | No | Known truck height in meters. Truck routes only. |
| `isVehicleCommercial` | No | Whether the truck is used commercially. Truck routes only. |
| `length` | No | Known truck length in meters. Truck routes only. |
| `loadType` | No | Known Azure Maps hazardous-cargo classification. May be repeated. Truck routes only. |
| `maxSpeed` | No | Known maximum speed in kilometers per hour. Truck routes only. |
| `weight` | No | Known gross weight in kilograms. Truck routes only. |
| `width` | No | Known truck width in meters. Truck routes only. |

For example, request a truck route from downtown Seattle to Seattle-Tacoma International Airport:

```text
http://localhost:7071/api/route?originLatitude=47.6062&originLongitude=-122.3321&destinationLatitude=47.4502&destinationLongitude=-122.3088&travelMode=truck&zoom=12
```

Known vehicle attributes can be supplied without guessing unknown values. For example,
this request identifies a commercial five-axle truck but leaves its weight and cargo unspecified:

```text
http://localhost:7071/api/route?originLatitude=47.6062&originLongitude=-122.3321&destinationLatitude=47.4502&destinationLongitude=-122.3088&travelMode=truck&axleCount=5&isVehicleCommercial=true
```

`POST /api/route` accepts the same values in a JSON body:

```json
{
  "originLatitude": 47.6062,
  "originLongitude": -122.3321,
  "destinationLatitude": 47.4502,
  "destinationLongitude": -122.3088,
  "travelMode": "truck",
  "zoom": 12,
  "vehicleSpec": {
    "axleCount": 5,
    "isVehicleCommercial": true
  }
}
```

Successful route responses have content type `image/png`. When `zoom` is omitted, the image is automatically framed around the route. Origin and destination pins are always included. Invalid requests return JSON problem details, and Azure Maps upstream failures return `502`.

To return route details instead of the PNG, set `output=details`:

```text
http://localhost:7071/api/route?originLatitude=47.6062&originLongitude=-122.3321&destinationLatitude=47.4502&destinationLongitude=-122.3088&travelMode=truck&axleCount=5&isVehicleCommercial=true&output=details
```

The details response is an Azure Maps GeoJSON `FeatureCollection`. Find the feature
whose `properties.type` is `RoutePath` for the full `MultiLineString` geometry,
`distanceInMeters`, `durationInSeconds`, traffic duration, arrival and departure times,
and route legs. Features whose `properties.type` is `ManeuverPoint` contain itinerary
information such as instructions, road names, signs, exits, and step ranges. GeoJSON
positions use `[longitude, latitude]` order.

An HTTP response can contain either the PNG or GeoJSON.

The optional `vehicleSpec` is partial: only include attributes that are known. Unknown
height, weight, axle weight, dimensions, speed, and cargo properties are omitted from
the Azure Maps request and therefore are not considered when calculating the route.
An 18-wheeler description alone should not be used to infer dimensions or weight because
actual configurations vary.

## Site Data Endpoints

These endpoints answer one question each about a single point. They are meant to be
called independently and in any combination. All of them accept `latitude` and
`longitude` as required query parameters and return JSON problem details on invalid
input.

### `GET /api/geocode/reverse`

Resolves a coordinate to a postal address. Useful for confirming a point corresponds to
a real addressable location rather than an unaddressed parcel or field.

```text
http://localhost:7071/api/geocode/reverse?latitude=47.6062&longitude=-122.3321
```

`hasAddressMatch` is `false` when Azure Maps returns no candidate. `isLikelyPrivateRoad`
is a heuristic, not an Azure Maps field: it is `true` when the address line contains a
word such as `private`, `pvt`, `trail`, `forest`, `unnamed`, `track`, or `easement`.
Treat it as a prompt to look closer, not as a fact.

### `GET /api/traffic/incidents`

Returns Azure Maps traffic incidents within a radius of the point, including closures,
construction, and hazards.

| Parameter | Required | Description |
|-----------|----------|-------------|
| `latitude` | Yes | Latitude from -90 to 90. |
| `longitude` | Yes | Longitude from -180 to 180. |
| `radiusMeters` | No | Search radius from 50 to 25000. Default: 2000. |

```text
http://localhost:7071/api/traffic/incidents?latitude=47.6062&longitude=-122.3321&radiusMeters=2000
```

Azure Maps takes a bounding box, so results are trimmed back to the requested radius and
sorted nearest first. Each incident includes its distance from the point in meters.
`incidentCount`, `roadClosureCount`, and `nearestIncidentMeters` summarize the list.
An empty result is a normal outcome, not an error.

### `GET /api/weather/conditions` and `GET /api/weather/alerts`

Return current conditions and severe weather alerts from Azure Maps for the point.

| Parameter | Required | Description |
|-----------|----------|-------------|
| `latitude` | Yes | Latitude from -90 to 90. |
| `longitude` | Yes | Longitude from -180 to 180. |
| `unit` | No | `metric` or `imperial`. Default: `metric`. |

```text
http://localhost:7071/api/weather/conditions?latitude=47.6062&longitude=-122.3321
http://localhost:7071/api/weather/alerts?latitude=47.6062&longitude=-122.3321
```

Both responses pass the Azure Maps payload through unchanged. These endpoints exist to
keep the subscription key server-side, not to reshape the data.

### `GET /api/alerts/nws`

Returns active National Weather Service alerts for the point, including evacuation
orders, flood warnings, and winter storm warnings.

```text
http://localhost:7071/api/alerts/nws?latitude=47.6062&longitude=-122.3321
```

The National Weather Service covers only the United States and its territories. Points
outside that area return `200` with `isWithinCoverage` set to `false` and a
`coverageNote` explaining why, so that a caller can tell "no coverage" apart from
"no alerts." `maxSeverity` is the highest of `Unknown`, `Minor`, `Moderate`, `Severe`,
and `Extreme` present in the results. `hasEvacuationOrder` is `true` when any alert
carries an `Evacuate` response.

The National Weather Service requires a contact User-Agent on every request. It is
supplied by `Nws__UserAgent` and there is no default; without it the endpoint returns
`503`.

### `GET /api/elevation`

Samples terrain elevation at the point and on a ring around it, which indicates how steep
the approach and the site itself are.

| Parameter | Required | Description |
|-----------|----------|-------------|
| `latitude` | Yes | Latitude from -90 to 90. |
| `longitude` | Yes | Longitude from -180 to 180. |
| `radiusMeters` | No | Ring radius from 10 to 5000. Default: 100. |
| `sampleCount` | No | Ring sample count from 4 to 16. Default: 8. |

```text
http://localhost:7071/api/elevation?latitude=47.6062&longitude=-122.3321&radiusMeters=100
```

Elevation comes from the USGS 3DEP point query service, which covers the United States;
the Azure Maps elevation service has been retired. `maxSlopePercent` is the steepest
grade between the center and any ring sample, derived from the elevation difference over
the ring radius. It is a straight-line approximation across open terrain, not a measured
road grade.

Points with no elevation data return `null` elevations rather than an error, so a
response where every sample is `null` means the location is outside coverage.

### Satellite imagery and truck reachability

These are already covered by the existing endpoints. Use
`GET /api/map?mapType=satellite&radiusMeters=250` for overhead imagery of a site, and
`GET /api/route?travelMode=truck&output=details` with and without a `vehicleSpec` to
compare a truck-legal path against an unrestricted one.

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- [Azure Functions Core Tools v4](https://learn.microsoft.com/azure/azure-functions/functions-run-local)
- [Azurite](https://learn.microsoft.com/azure/storage/common/storage-use-azurite) or another local Functions storage connection
- An [Azure Maps account](https://learn.microsoft.com/azure/azure-maps/quick-demo-map-app#create-an-azure-maps-account) and subscription key

## Run Locally

1. Copy `src/ERDC.Agents/local.settings.example.json` to `src/ERDC.Agents/local.settings.json`.
2. Replace `your-azure-maps-subscription-key` with an Azure Maps subscription key, and
  replace the contact address in `Nws__UserAgent` with your own.
3. Start Azurite.
4. Build and start the function host:

```powershell
Set-Location .\src\ERDC.Agents
dotnet run
```

Use [requests.http](requests.http) from VS Code or request a map directly:

```powershell
Invoke-WebRequest `
  -Uri "http://localhost:7071/api/map?city=Seattle" `
  -OutFile "seattle.png"
```

All endpoints enforce function-key authorization when deployed, except `/api/route`,
which is anonymous.

## Build And Test

```powershell
dotnet build .\ERDC.Agents.slnx
dotnet test .\ERDC.Agents.slnx
```

The tests mock upstream HTTP responses and do not require credentials or network calls.

## Configuration

| Setting | Required | Default | Purpose |
|---------|----------|---------|---------|
| `AzureMaps__SubscriptionKey` | Yes | None | Server-side Azure Maps authentication key. |
| `AzureMaps__Endpoint` | No | `https://atlas.microsoft.com` | Azure Maps endpoint, configurable for sovereign clouds or tests. |
| `Nws__UserAgent` | For `/api/alerts/nws` | None | Contact User-Agent required by the National Weather Service, in the form `AppName (contact@example.com)`. |
| `Nws__Endpoint` | No | `https://api.weather.gov` | National Weather Service endpoint, configurable for tests. |
| `Elevation__Endpoint` | No | `https://epqs.nationalmap.gov/v1/json` | USGS 3DEP point query endpoint, configurable for tests. |

Do not commit `local.settings.json`. The key is sent to Azure Maps in the `subscription-key` header and is never included in request URLs or API responses.

`local.settings.json` and IDE user secrets are local-development inputs; they are not included when the app is published. For a standalone local process, set the environment variables before starting the host:

```powershell
$env:AzureMaps__SubscriptionKey = "<your-key>"
$env:Nws__UserAgent = "ERDC.Agents (<your-contact>)"
dotnet run --project .\src\ERDC.Agents
```

For a deployed Function App, add both `AzureMaps__SubscriptionKey` and `Nws__UserAgent` under **Settings > Environment variables** in the Azure portal and restart the app. Neither is carried over from `local.settings.json` or user secrets, so a deployment that omits them will serve `503` from the endpoints that depend on them even though the same endpoints work locally. A missing setting is logged by the function and returns `503 Service Unavailable` without exposing configuration details to the caller.

All Azure Maps calls use the server-side subscription key, which never leaves the
Function App. The National Weather Service and USGS endpoints are public and require no
credentials.