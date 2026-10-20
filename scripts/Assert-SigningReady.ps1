$ErrorActionPreference='Stop'
if($env:SIGNPATH_ENABLED -cne 'true'){throw 'Signing is disabled'}
if($env:SIGNPATH_APPROVED -cne 'true'){throw 'Provider approval has not been recorded'}
if($env:GITHUB_REPOSITORY -cne 'foreverflip/foreverflip-desktop' -or $env:GITHUB_REF -cne 'refs/heads/main' -or $env:GITHUB_EVENT_NAME -cne 'workflow_dispatch'){throw 'Signing requires a manual run from the approved repository main branch'}
if($env:GITHUB_SHA -cnotmatch '^[a-f0-9]{40}$'){throw 'Missing immutable source commit'}
$organization=[Guid]::Empty
if(-not [Guid]::TryParse($env:SIGNPATH_ORGANIZATION_ID,[ref]$organization) -or $organization -eq [Guid]::Empty){throw 'Missing or invalid SignPath organization ID'}
foreach($name in 'SIGNPATH_PROJECT_SLUG','SIGNPATH_APP_POLICY_SLUG','SIGNPATH_SETUP_POLICY_SLUG'){
 if([Environment]::GetEnvironmentVariable($name) -cnotmatch '^[A-Za-z0-9][A-Za-z0-9._-]{0,99}$'){throw "Missing or invalid signing configuration: $name"}
}
if($env:SIGNPATH_CERTIFICATE_SHA256 -cnotmatch '^[A-Fa-f0-9]{64}$'){throw 'The approved signing certificate SHA256 fingerprint is required'}
if([string]::IsNullOrWhiteSpace($env:SIGNPATH_API_TOKEN)){throw 'Environment-scoped signing token is required'}
Write-Output 'Manual signing configuration passed. This check does not contact the provider or sign files.'
