param(
 [Parameter(Mandatory)][string]$NativeSetup,
 [Parameter(Mandatory)][string]$OutputFile,
 [string]$Version=''
)
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$inputFile=Get-Item -LiteralPath $NativeSetup
if($inputFile.PSIsContainer -or ($inputFile.Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'A regular native Setup is required'}
if(-not $inputFile.FullName.StartsWith($root+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'Native Setup must stay inside the project'}
$output=[IO.Path]::GetFullPath($OutputFile)
if(-not $output.StartsWith($root+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'Setup output must stay inside the project'}
if(Test-Path -LiteralPath $output){throw 'Refusing to overwrite a Setup artifact'}
if(-not $Version){[xml]$project=Get-Content -LiteralPath "$root/desktop/ForeverFlip.Desktop.csproj" -Raw;$Version=[string]$project.Project.PropertyGroup.Version}
if($Version -notmatch '^\d+\.\d+\.\d+(-[A-Za-z0-9.-]+)?$'){throw 'Invalid Setup version'}
$stage=Join-Path $root ('artifacts/installer-wrapper-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $stage | Out-Null
$digest=(Get-FileHash -LiteralPath $inputFile.FullName -Algorithm SHA256).Hash
# No trimming or analyzer package download; the SDK's single-file bundler embeds
# the existing self-contained Windows runtime and the immutable native payload.
& dotnet publish "$root/desktop/ForeverFlip.Desktop.csproj" -c Release -r win-x64 --self-contained true --no-restore `
 "-p:InstallerPayload=$($inputFile.FullName)" "-p:InstallerPayloadSHA256=$digest" `
 '-p:AssemblyName=ForeverFlip-Setup' '-p:Product=ForeverFlip Desktop' "-p:Version=$Version" `
 '-p:IncludeSourceRevisionInInformationalVersion=false' '-p:PublishSingleFile=true' `
 '-p:EnableSingleFileAnalyzer=false' '-p:PublishTrimmed=false' '-p:IncludeNativeLibrariesForSelfExtract=true' `
 '-p:EnableCompressionInSingleFile=true' -o $stage
if($LASTEXITCODE){throw 'Protected Setup build failed'}
$wrapper=Join-Path $stage 'ForeverFlip-Setup.exe'
$check=Start-Process -FilePath $wrapper -ArgumentList '--verify-payload' -Wait -PassThru -WindowStyle Hidden
if($check.ExitCode -ne 0){throw 'Protected Setup payload verification failed'}
[IO.Directory]::CreateDirectory((Split-Path $output -Parent)) | Out-Null
[IO.File]::Move($wrapper,$output,$false)
Write-Output "Protected Setup prepared and embedded payload verified: $([IO.Path]::GetFileName($output))"
