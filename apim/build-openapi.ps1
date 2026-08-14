# Merges the per-specialist specs into one document for APIM import.
# Foundry consumes specs/*.json individually; APIM needs a single API at one path.
#
# This document describes the backend, not the gateway: it is what APIM imports to learn the
# operations it will forward. The specialists' own copies point at the gateway instead.
param(
    [string]$SpecsDir = (Join-Path $PSScriptRoot '..\specs'),
    [string]$OutFile = (Join-Path $PSScriptRoot 'geo-api.openapi.json')
)

$ErrorActionPreference = 'Stop'

$config = & (Join-Path $PSScriptRoot '..\scripts\Get-AzdConfig.ps1') -Require FUNCTION_APP_API_URL

# APIM's importer rejects 3.1 union types, so rewrite type:[X,"null"] as type:X + nullable:true.
function Convert-NullableType {
    param($Node)

    if ($Node -is [object[]]) {
        foreach ($item in $Node) { Convert-NullableType $item }
        return
    }
    if ($Node -isnot [pscustomobject]) { return }

    $typeProp = $Node.PSObject.Properties['type']
    if ($typeProp -and $typeProp.Value -is [object[]]) {
        $types = @($typeProp.Value)
        if ($types -contains 'null') {
            $Node.type = @($types | Where-Object { $_ -ne 'null' })[0]
            $Node | Add-Member -NotePropertyName nullable -NotePropertyValue $true -Force
        }
    }

    foreach ($prop in $Node.PSObject.Properties) { Convert-NullableType $prop.Value }
}

$merged = [ordered]@{
    openapi = '3.0.3'
    info    = [ordered]@{
        title       = 'ERDC Agents Geo API'
        version     = '1.0.0'
        description = 'Weather, terrain, mobility, and location endpoints consumed by Foundry specialist agents.'
    }
    servers = @(@{ url = $config.FUNCTION_APP_API_URL })
    paths   = [ordered]@{}
    components = [ordered]@{}
}

foreach ($file in Get-ChildItem (Join-Path $SpecsDir '*.json') | Sort-Object Name) {
    $doc = Get-Content $file.FullName -Raw | ConvertFrom-Json

    foreach ($path in $doc.paths.PSObject.Properties) {
        if ($merged.paths.Contains($path.Name)) {
            throw "Duplicate path '$($path.Name)' found in $($file.Name)."
        }
        $merged.paths[$path.Name] = $path.Value
    }

    if ($doc.components) {
        foreach ($section in $doc.components.PSObject.Properties) {
            if (-not $merged.components.Contains($section.Name)) {
                $merged.components[$section.Name] = [ordered]@{}
            }
            foreach ($entry in $section.Value.PSObject.Properties) {
                $existing = $merged.components[$section.Name][$entry.Name]
                if ($existing) {
                    # Per-domain wording differs (e.g. "weather provider" vs "mapping provider");
                    # only the shape matters here since APIM never shows these to the model.
                    $a = ($existing | Select-Object -ExcludeProperty description | ConvertTo-Json -Depth 30 -Compress)
                    $b = ($entry.Value | Select-Object -ExcludeProperty description | ConvertTo-Json -Depth 30 -Compress)
                    if ($a -ne $b) { throw "Conflicting definitions for $($section.Name)/$($entry.Name) in $($file.Name)." }
                    continue
                }
                $merged.components[$section.Name][$entry.Name] = $entry.Value
            }
        }
    }
}

$merged | ConvertTo-Json -Depth 30 | Set-Content $OutFile -Encoding utf8

$doc = Get-Content $OutFile -Raw | ConvertFrom-Json
Convert-NullableType $doc
$doc | ConvertTo-Json -Depth 30 | Set-Content $OutFile -Encoding utf8

"Wrote $OutFile ($($merged.paths.Count) paths, $($merged.components.schemas.Count) schemas)"
