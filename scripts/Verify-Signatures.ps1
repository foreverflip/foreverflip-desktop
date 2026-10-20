param([Parameter(Mandatory)][string[]]$Paths)
$ErrorActionPreference='Stop'
foreach($path in $Paths){
 $signature=Get-AuthenticodeSignature -LiteralPath $path
 if($signature.Status -ne 'Valid'){throw "Invalid or missing Authenticode signature: $path"}
 if(-not $signature.TimeStamperCertificate){throw "Missing signing timestamp: $path"}
 if($signature.SignerCertificate.Subject -notmatch 'SignPath Foundation'){throw "Unexpected signing identity: $path"}
 Write-Output "Valid timestamped Foundation signature: $([IO.Path]::GetFileName($path))"
}
