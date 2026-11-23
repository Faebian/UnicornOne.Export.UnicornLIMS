param(
    [switch]$Deploy,
    [ValidateSet('Debug','Release')]
    [string]$Configuration = 'Release',
    [string]$Destination = 'C:\UnicornOne\Plugins\Export'
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$project = Join-Path $root 'UnicornOne.Export.UnicornLIMS.csproj'
$required = @('C:\UnicornOne\UnicornOne.Abstractions.dll', 'C:\UnicornOne\Newtonsoft.Json.dll')
foreach ($dependency in $required) {
    if (-not (Test-Path -LiteralPath $dependency -PathType Leaf)) {
        throw "Missing build dependency: $dependency"
    }
}
$msbuild = $null
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (Test-Path $vswhere) {
    $msbuild = & $vswhere -latest -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
}
if (-not $msbuild) { $msbuild = (Get-Command msbuild.exe -ErrorAction SilentlyContinue).Source }
if (-not $msbuild) { throw 'MSBuild.exe not found; install VS2022 Build Tools and .NET 4.7.2 Developer Pack.' }
& $msbuild $project '/t:Build' "/p:Configuration=$Configuration" '/p:Platform=AnyCPU' '/verbosity:minimal' '/nologo'
if ($LASTEXITCODE -ne 0) { throw "MSBuild failed ($LASTEXITCODE)" }
$dll = Join-Path $root "bin\$Configuration\UnicornOne.Export.UnicornLIMS.dll"
if (-not (Test-Path $dll)) { throw "Missing built DLL: $dll" }
Write-Host "Built: $dll"
if ($Deploy) {
    New-Item -ItemType Directory -Force -Path $Destination | Out-Null
    Copy-Item -LiteralPath $dll -Destination (Join-Path $Destination 'UnicornOne.Export.UnicornLIMS.dll') -Force
    Write-Host "Deployed to: $Destination"
    Write-Host 'Explicitly enable this DLL under Config > Exports, then restart UnicornOne.'
}
