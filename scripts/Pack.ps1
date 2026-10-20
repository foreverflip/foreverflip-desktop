param([string]$AppDirectory='artifacts/app',[string]$Version='')
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent;Push-Location $root
try{
 if(-not $Version){[xml]$project=Get-Content desktop/ForeverFlip.Desktop.csproj -Raw;$Version=[string]$project.Project.PropertyGroup.Version}
 if($Version -notmatch '^\d+\.\d+\.\d+(-[A-Za-z0-9.-]+)?$'){throw 'Invalid package version'}
 $app=(Resolve-Path -LiteralPath $AppDirectory).Path
 if(-not $app.StartsWith($root+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'App path must be inside this repository'}
 if(Test-Path -LiteralPath (Join-Path $app 'Assets/Addon')){throw 'Proprietary addon bundle refused'}
 if(Test-Path -LiteralPath artifacts/releases){throw 'Refusing to replace existing immutable output; use a fresh build folder'}
 & dotnet tool restore
 if($LASTEXITCODE){throw 'Pinned Velopack tool restore failed'}
 & "$PSScriptRoot/New-Icons.ps1" -Version $Version
 # Own app binaries are signed before packaging. Third-party runtime/updater
 # binaries retain upstream signatures (or remain unsigned); do not re-sign them.
 & dotnet tool run vpk -- pack --packId ForeverFlip --packVersion $Version --packDir $app --mainExe ForeverFlip.exe --packAuthors ForeverFlip --packTitle 'ForeverFlip Desktop' --icon desktop/Assets/ForeverFlip-Setup.ico --splashImage artifacts/installer-art/Setup.png --outputDir artifacts/releases --releaseNotes desktop/RELEASE-NOTES.md --runtime win-x64
 if($LASTEXITCODE){throw 'Packaging failed'}
}finally{Pop-Location}
