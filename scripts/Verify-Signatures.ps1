param(
 [Parameter(Mandatory)][ValidateCount(1,8)][string[]]$Paths,
 [string]$ExpectedSignerSHA256=$env:SIGNPATH_CERTIFICATE_SHA256,
 [string]$ExpectedProductVersion='',
 [string]$ExpectedFileVersion=''
)
$ErrorActionPreference='Stop'
if($ExpectedSignerSHA256 -cnotmatch '^[A-Fa-f0-9]{64}$'){throw 'An explicitly approved certificate SHA256 fingerprint is required'}
foreach($path in $Paths){
 $file=Get-Item -LiteralPath $path
 if($file.PSIsContainer -or ($file.Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'A regular, unlinked signed file is required'}
 $signature=Get-AuthenticodeSignature -LiteralPath $path
 if($signature.Status -ne 'Valid'){throw "Invalid or missing Authenticode signature: $path"}
 if(-not $signature.TimeStamperCertificate){throw "Missing signing timestamp: $path"}
 if(-not $signature.SignerCertificate){throw 'Missing signing certificate'}
 $publisher=$signature.SignerCertificate.GetNameInfo([Security.Cryptography.X509Certificates.X509NameType]::SimpleName,$false)
 if(-not [string]::Equals($publisher,'SignPath Foundation',[StringComparison]::Ordinal)){throw "Unexpected signing identity: $path"}
 $hasher=[Security.Cryptography.SHA256]::Create()
 try{$fingerprint=([BitConverter]::ToString($hasher.ComputeHash($signature.SignerCertificate.RawData))).Replace('-','')}finally{$hasher.Dispose()}
 if(-not [string]::Equals($fingerprint,$ExpectedSignerSHA256,[StringComparison]::OrdinalIgnoreCase)){throw 'Signing certificate is not the approved certificate'}
 if($ExpectedProductVersion -or $ExpectedFileVersion){
  $metadata=[Diagnostics.FileVersionInfo]::GetVersionInfo($file.FullName)
  if($metadata.ProductName -cne 'ForeverFlip Desktop'){throw 'Unexpected signed product name'}
  if($ExpectedProductVersion -and $metadata.ProductVersion -cne $ExpectedProductVersion){throw 'Signed product version changed'}
  if($ExpectedFileVersion -and $metadata.FileVersion -cne $ExpectedFileVersion){throw 'Signed file version changed'}
 }
 Write-Output "Valid timestamped Foundation signature: $([IO.Path]::GetFileName($path))"
}
