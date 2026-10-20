param([Parameter(Mandatory)][string]$File,[Parameter(Mandatory)][string]$Prefix)
$ErrorActionPreference='Stop'
if($Prefix -notmatch '^[A-Za-z]+$'){throw 'Invalid metadata prefix'}
$info=[Diagnostics.FileVersionInfo]::GetVersionInfo((Resolve-Path -LiteralPath $File).Path)
if($info.ProductName -ne 'ForeverFlip Desktop'){throw "Unexpected own-product metadata: $($info.ProductName)"}
foreach($field in 'ProductVersion','FileVersion'){
 $value=$info.$field
 if($value -notmatch '^\d+\.\d+\.\d+(\.\d+)?(-[A-Za-z0-9.-]+)?$'){throw 'Invalid product/version metadata'}
 Write-Output "$Prefix$field=$value"
}
