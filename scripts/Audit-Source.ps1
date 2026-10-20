$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$allowed=@('desktop','desktop-tests','scripts','licenses','.github','.config')
$rootFiles=@('.gitignore','global.json','LICENSE','README.md','PRIVACY.md','CODE-SIGNING.md','SECURITY.md','THIRD-PARTY-NOTICES.md','NuGet.Config')
foreach($path in Get-ChildItem -LiteralPath $root -Force){
 if($path.Name -in @('.git','artifacts')){continue}
 if($path.PSIsContainer -and $path.Name -notin $allowed){throw "Unapproved source directory: $($path.Name)"}
 if(-not $path.PSIsContainer -and $path.Name -notin $rootFiles){throw "Unapproved source file: $($path.Name)"}
}
foreach($directory in $allowed){
 $base=Join-Path $root $directory;if(-not(Test-Path -LiteralPath $base)){continue}
 foreach($file in Get-ChildItem -LiteralPath $base -Recurse -File){
  $relative=$file.FullName.Substring($root.Length+1)
  if($relative -match '(^|[\\/])(bin|obj)([\\/]|$)'){continue}
  if($file.Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Linked source refused'}
  if($relative -match '(?i)(\.pfx$|\.p12$|\.key$|-private\.pem$|\.lua$|\.tga$|^.*\.env$|settings\.json$|credentials\.json$|token\.json$)'){throw "Private/unsupported file: $relative"}
  if($file.Extension -in @('.cs','.xaml','.csproj','.ps1','.json','.yml','.md','.pem','.txt','.manifest')){
   $text=Get-Content -LiteralPath $file.FullName -Raw
   if($text -match '-----BEGIN (RSA |EC |OPENSSH |ENCRYPTED )?PRIVATE KEY-----|(?i)D:[/\\]wowforever|C:[/\\]Users[/\\]Sam|192\.168\.\d|/mnt/\.ix-apps|\.codex[/\\]'){throw "Private source marker: $relative"}
  }
 }
}
Write-Output 'Desktop-only source allowlist and private-marker audit passed.'
