<#
.SYNOPSIS
    Publishes a .NET project to a Linux App Service as a zip.

.DESCRIPTION
    Shared by the two apps that are not azd services: the self-hosted orchestrator and the web front
    end. Neither can be declared in azure.yaml, because azd deploys every service it is given on
    every run and both are conditional -- the orchestrator only exists on the App Service path, and
    the front end only when it was asked for.

.EXAMPLE
    ./scripts/Publish-AppService.ps1 -ProjectPath ./webapp/src/geo-chat-web/geo-chat-web.csproj `
        -ResourceGroup rg-geo-agents-dev -AppName web-geo-agents-dev-abcd1234
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ProjectPath,
    [Parameter(Mandatory)][string]$ResourceGroup,
    [Parameter(Mandatory)][string]$AppName
)

$ErrorActionPreference = 'Stop'

function Invoke-Native {
    param([string]$Command, [string[]]$Arguments)
    & $Command @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$Command $($Arguments -join ' ') failed with exit code $LASTEXITCODE." }
}

$publishDir = Join-Path ([IO.Path]::GetTempPath()) "$AppName-$([guid]::NewGuid().ToString('N').Substring(0, 8))"
$package = "$publishDir.zip"

try {
    Invoke-Native dotnet @('publish', $ProjectPath, '--configuration', 'Release', '--output', $publishDir)

    # Compress-Archive, and ZipFile.CreateFromDirectory on Windows PowerShell, separate nested entry
    # paths with backslashes. Linux Kudu unzips those as one long filename instead of a directory,
    # and then rsync cannot stat the result and fails the whole deployment. Naming the entries here
    # keeps the separator right no matter which shell runs the script.
    $archive = [System.IO.Compression.ZipFile]::Open($package, [System.IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($file in Get-ChildItem $publishDir -Recurse -File) {
            $entry = $file.FullName.Substring($publishDir.Length + 1).Replace('\', '/')
            [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $file.FullName, $entry) | Out-Null
        }
    }
    finally {
        $archive.Dispose()
    }

    # Without --clean, Kudu rsyncs over whatever is already in wwwroot, so a single undeletable
    # leftover file fails the whole deployment.
    Invoke-Native az @('webapp', 'deploy',
        '--resource-group', $ResourceGroup,
        '--name', $AppName,
        '--src-path', $package, '--type', 'zip', '--clean', 'true')
}
finally {
    Remove-Item $publishDir -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item $package -Force -ErrorAction SilentlyContinue
}
