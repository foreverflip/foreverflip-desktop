$ErrorActionPreference='Stop'
$names=@('SIGNPATH_ENABLED','SIGNPATH_APPROVED','GITHUB_REPOSITORY','GITHUB_REF','GITHUB_EVENT_NAME','GITHUB_SHA','SIGNPATH_ORGANIZATION_ID','SIGNPATH_PROJECT_SLUG','SIGNPATH_APP_POLICY_SLUG','SIGNPATH_SETUP_POLICY_SLUG','SIGNPATH_CERTIFICATE_SHA256','SIGNPATH_API_TOKEN')
$saved=@{};foreach($name in $names){$saved[$name]=[Environment]::GetEnvironmentVariable($name)}
$script:checks=0
function Expect-Refusal([scriptblock]$Action,[string]$Message){
 $refused=$false
 try{& $Action | Out-Null}catch{if($_.Exception.Message -notlike ('*'+$Message+'*')){throw};$refused=$true}
 if(-not $refused){throw "Expected refusal: $Message"};$script:checks++
}
try{
 $values=@('true','true','foreverflip/foreverflip-desktop','refs/heads/main','workflow_dispatch',('a'*40),'11111111-1111-1111-1111-111111111111','desktop','app','setup',('A'*64),'synthetic-unit-fixture')
 for($i=0;$i -lt $names.Count;$i++){[Environment]::SetEnvironmentVariable($names[$i],$values[$i])}
 & "$PSScriptRoot/Assert-SigningReady.ps1" | Out-Null;$script:checks++
 foreach($case in @(
  @('SIGNPATH_ENABLED','false','Signing is disabled'),@('SIGNPATH_APPROVED','false','Provider approval'),
  @('GITHUB_REF','refs/heads/topic','manual run'),@('GITHUB_EVENT_NAME','pull_request','manual run'),
  @('GITHUB_REPOSITORY','someone/another-repo','manual run'),@('GITHUB_SHA','main','immutable source'),
  @('SIGNPATH_ORGANIZATION_ID','missing','organization ID'),@('SIGNPATH_PROJECT_SLUG','a/b','configuration'),
  @('SIGNPATH_CERTIFICATE_SHA256','','fingerprint'),@('SIGNPATH_API_TOKEN','','token')
 )){
  $before=[Environment]::GetEnvironmentVariable($case[0]);[Environment]::SetEnvironmentVariable($case[0],$case[1])
  Expect-Refusal {& "$PSScriptRoot/Assert-SigningReady.ps1"} $case[2]
  [Environment]::SetEnvironmentVariable($case[0],$before)
 }
 # Certificate fixtures remain in memory. No certificate is installed in a store,
 # no real artifact is signed, and mocked Valid status does not establish OS trust.
 $rsa=[Security.Cryptography.RSA]::Create(2048)
 $request=[Security.Cryptography.X509Certificates.CertificateRequest]::new('CN=SignPath Foundation',$rsa,[Security.Cryptography.HashAlgorithmName]::SHA256,[Security.Cryptography.RSASignaturePadding]::Pkcs1)
 $certificate=$request.CreateSelfSigned([DateTimeOffset]::Now.AddDays(-1),[DateTimeOffset]::Now.AddDays(1))
 $hasher=[Security.Cryptography.SHA256]::Create()
 try{$pin=([BitConverter]::ToString($hasher.ComputeHash($certificate.RawData))).Replace('-','')}finally{$hasher.Dispose()}
 $script:fixture=[pscustomobject]@{Status='Valid';SignerCertificate=$certificate;TimeStamperCertificate=$certificate}
 $fixtureReference=[ref]$script:fixture
 Set-Item Function:Get-AuthenticodeSignature -Value ({param([string]$LiteralPath);return $fixtureReference.Value}.GetNewClosure())
 & "$PSScriptRoot/Verify-Signatures.ps1" -Paths $PSCommandPath -ExpectedSignerSHA256 $pin | Out-Null;$script:checks++
 Expect-Refusal {& "$PSScriptRoot/Verify-Signatures.ps1" -Paths $PSCommandPath -ExpectedSignerSHA256 ('0'*64)} 'approved certificate'
 Expect-Refusal {& "$PSScriptRoot/Verify-Signatures.ps1" -Paths $PSCommandPath -ExpectedSignerSHA256 ''} 'fingerprint'
 $script:fixture.Status='HashMismatch'
 Expect-Refusal {& "$PSScriptRoot/Verify-Signatures.ps1" -Paths $PSCommandPath -ExpectedSignerSHA256 $pin} 'Invalid or missing'
 $script:fixture.Status='Valid';$script:fixture.TimeStamperCertificate=$null
 Expect-Refusal {& "$PSScriptRoot/Verify-Signatures.ps1" -Paths $PSCommandPath -ExpectedSignerSHA256 $pin} 'timestamp'
 $otherRequest=[Security.Cryptography.X509Certificates.CertificateRequest]::new('CN=Fake SignPath Foundation Publisher',$rsa,[Security.Cryptography.HashAlgorithmName]::SHA256,[Security.Cryptography.RSASignaturePadding]::Pkcs1)
 $other=$otherRequest.CreateSelfSigned([DateTimeOffset]::Now.AddDays(-1),[DateTimeOffset]::Now.AddDays(1))
 $script:fixture.SignerCertificate=$other;$script:fixture.TimeStamperCertificate=$certificate
 Expect-Refusal {& "$PSScriptRoot/Verify-Signatures.ps1" -Paths $PSCommandPath -ExpectedSignerSHA256 $pin} 'identity'
 Remove-Item Function:Get-AuthenticodeSignature
 Expect-Refusal {& "$PSScriptRoot/Verify-Signatures.ps1" -Paths $PSCommandPath -ExpectedSignerSHA256 $pin} 'Invalid or missing'
 Write-Output "Signing readiness/signature regressions passed: $script:checks. No provider request or signing performed."
}finally{
 foreach($name in $names){[Environment]::SetEnvironmentVariable($name,$saved[$name])}
 foreach($resource in @($other,$certificate,$rsa)){if($resource){$resource.Dispose()}}
}
