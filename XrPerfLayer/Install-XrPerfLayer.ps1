<#
.SYNOPSIS
	Registers or unregisters the XrPerf OpenXR API layer (requires administrator rights).

.EXAMPLE
	.\Install-XrPerfLayer.ps1                      # register bin\x64\Release\XrPerfLayer.json
	.\Install-XrPerfLayer.ps1 -ManifestPath C:\Tools\XrPerf\XrPerfLayer.json
	.\Install-XrPerfLayer.ps1 -Uninstall
	.\Install-XrPerfLayer.ps1 -List
#>
[CmdletBinding()]
param(
	[string]$ManifestPath,
	[switch]$Uninstall,
	[switch]$List
)

$ErrorActionPreference = 'Stop'
if (-not $ManifestPath) { $ManifestPath = Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) '..\bin\x64\Release\XrPerfLayer.json' }
$keyPath = 'HKLM:\SOFTWARE\Khronos\OpenXR\1\ApiLayers\Implicit'
$layerFile = 'XrPerfLayer.json'

function Show-Layers {
	if (-not (Test-Path $keyPath)) { Write-Host 'No implicit OpenXR layers registered.'; return }
	$key = Get-Item $keyPath
	Write-Host 'Implicit OpenXR layers (loader order, first = closest to the app):'
	foreach ($name in $key.GetValueNames()) {
		$state = if ($key.GetValue($name) -eq 0) { 'enabled ' } else { 'disabled' }
		Write-Host "  [$state] $name"
	}
}

if ($List) { Show-Layers; return }

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
	[Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) { throw 'Please run this script as administrator.' }

if (-not (Test-Path $keyPath)) { New-Item -Path $keyPath -Force | Out-Null }
$key = Get-Item $keyPath

# Remove any previous registrations of this layer.
foreach ($name in $key.GetValueNames()) {
	if ([IO.Path]::GetFileName($name) -ieq $layerFile) {
		Remove-ItemProperty -Path $keyPath -Name $name
		Write-Host "Removed: $name"
	}
}

if (-not $Uninstall) {
	$resolved = (Resolve-Path $ManifestPath).Path
	if (-not (Test-Path (Join-Path (Split-Path $resolved) 'XrPerfLayer.dll'))) {
		throw "XrPerfLayer.dll not found next to $resolved"
	}
	# Register as first layer (closest to the app) so the real app render resolution is captured
	# before other layers (e.g. OpenXR Toolkit upscaling) change it. Re-add the others afterwards.
	$others = @()
	foreach ($name in $key.GetValueNames()) {
		if ([IO.Path]::GetFileName($name) -ine $layerFile) {
			$others += [pscustomobject]@{ Name = $name; Value = $key.GetValue($name) }
			Remove-ItemProperty -Path $keyPath -Name $name
		}
	}
	New-ItemProperty -Path $keyPath -Name $resolved -PropertyType DWord -Value 0 | Out-Null
	foreach ($o in $others) {
		New-ItemProperty -Path $keyPath -Name $o.Name -PropertyType DWord -Value $o.Value | Out-Null
	}
	Write-Host "Registered: $resolved"
}

Show-Layers
