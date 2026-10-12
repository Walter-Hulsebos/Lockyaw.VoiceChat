param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '../Runtime/Plugins/Windows/x86_64'),
    [string]$BuildDirectory = (Join-Path $env:USERPROFILE 'Documents/Codex/VoiceCapture-Build')
)
$ErrorActionPreference = 'Stop'
$visualStudioLocator = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
$installation = & $visualStudioLocator -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if ([string]::IsNullOrWhiteSpace($installation)) { throw 'Visual C++ x64 build tools are required.' }
$environmentScript = Join-Path $installation 'VC/Auxiliary/Build/vcvars64.bat'
New-Item -ItemType Directory -Force -Path $OutputDirectory, $BuildDirectory | Out-Null
$source = Join-Path $PSScriptRoot 'VoiceCapture.cpp'
$library = Join-Path ([System.IO.Path]::GetFullPath($OutputDirectory)) 'LockyawVoiceCapture.dll'
$objectFile = Join-Path ([System.IO.Path]::GetFullPath($BuildDirectory)) 'VoiceCapture.obj'
$importLibrary = Join-Path ([System.IO.Path]::GetFullPath($BuildDirectory)) 'LockyawVoiceCapture.lib'
$compileCommand = 'call "{0}" >nul && cl /nologo /O2 /MT /EHsc /std:c++17 /LD /W3 /Fo"{1}" "{2}" /link /OUT:"{3}" /IMPLIB:"{4}"' -f $environmentScript, $objectFile, $source, $library, $importLibrary
& $env:ComSpec /d /s /c $compileCommand
if ($LASTEXITCODE -ne 0) { throw 'Native capture compilation failed.' }
Get-FileHash -LiteralPath $library -Algorithm SHA256
