param([switch]$Publish,[switch]$SkipTests)
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent;Push-Location $root
try{
 & "$PSScriptRoot/Audit-Source.ps1"
 & "$PSScriptRoot/New-Icons.ps1"
 & dotnet restore desktop-tests/ForeverFlip.Tests.csproj --locked-mode
 if($LASTEXITCODE){throw 'Locked restore failed'}
 & dotnet build desktop-tests/ForeverFlip.Tests.csproj -c Release --no-restore
 if($LASTEXITCODE){throw 'Build failed'}
 if(-not $SkipTests){& dotnet desktop-tests/bin/Release/net10.0-windows/ForeverFlip.Tests.dll;if($LASTEXITCODE){throw 'Tests failed'}}
 if($Publish){
  & dotnet publish desktop/ForeverFlip.Desktop.csproj -c Release -r win-x64 --self-contained true --no-restore -o artifacts/app
  if($LASTEXITCODE){throw 'Desktop publish failed'}
  if(Test-Path -LiteralPath artifacts/app/Assets/Addon){throw 'Addon source must not be bundled'}
  Copy-Item -LiteralPath LICENSE -Destination artifacts/app/LICENSE-FOREVERFLIP.txt
  Copy-Item -LiteralPath THIRD-PARTY-NOTICES.md -Destination artifacts/app/THIRD-PARTY-NOTICES-FOREVERFLIP.md
  Copy-Item -LiteralPath licenses -Destination artifacts/app/licenses -Recurse -Force
 }
}finally{Pop-Location}
