#Requires -Modules Microsoft.Online.SharePoint.PowerShell

# RUNBOOK_VERSION: 0.9.0
# Released in lockstep with the migration platform API; keep in sync with the
# repo-root VERSION file. The API exposes the expected value at GET /api/version
# (runbookVersion) so an operator can compare it against the deployed runbook.

<#
.SYNOPSIS
Azure Automation runbook that drives the SharePoint Online cross-tenant OneDrive
MnA cmdlets on behalf of the migration platform API.

.DESCRIPTION
Microsoft.Online.SharePoint.PowerShell is Windows-only and only reachable from a
Windows PowerShell host, so the Linux API container cannot run it directly.
This runbook is published to an Azure Automation account (Microsoft-managed
Windows sandbox). The API triggers it via the Azure REST API, passes in the
tenant's app-only certificate (as base64, or as a Key Vault reference), and
reads back a single JSON line from the job output stream.

Supported operations:
  Start                 — Start-SPOCrossTenantUserContentMove
  GetState              — Get-SPOCrossTenantUserContentMoveState (single user)
  GetStateBatch         — Get-SPOCrossTenantUserContentMoveState looped over a JSON array of UPNs
  StartSite             — Start-SPOCrossTenantSiteContentMove
  GetSiteState          — Get-SPOCrossTenantSiteContentMoveState (single site)
  GetSiteStateBatch     — Get-SPOCrossTenantSiteContentMoveState looped over a JSON array of site URLs
  Compatibility         — Get-SPOCrossTenantCompatibilityStatus
  GetCrossTenantHostUrl — Get-SPOCrossTenantHostUrl (the tenant's canonical cross-tenant host URL)
  SetCrossTenantRelationship — Set-SPOCrossTenantRelationship (MnA) + Test-SPOCrossTenantRelationship
                          (one side per invocation; the API calls once per tenant)
  UploadIdentityMap     — Add-SPOTenantIdentityMap (target tenant; overwrites the existing map)
  RequestPersonalSite   — Request-SPOPersonalSite (pre-provision OneDrive; chunked ≤200 UPNs per call)

Certificate sourcing (two paths):
  1. CertificatePfxBase64 / CertificatePassword job parameters (default).
  2. KeyVaultUrl + KeyVaultCertificateName — the runbook fetches the PFX secret
     itself using the Automation account's system-assigned managed identity
     (Connect-AzAccount -Identity). The PFX password is read from the secret
     named "<KeyVaultCertificateName>-password" when present. This keeps the
     PFX and its password out of the portal-visible job parameters. Requires
     the Az.Accounts and Az.KeyVault modules imported into the Automation
     account and the managed identity granted Key Vault secret read access
     (Key Vault Secrets User role or an access policy with Secret Get).

Output contract: exactly one line written with Write-Output containing a
single JSON value (object or array). The C# client parses stdout and fails if
it cannot find valid JSON. All diagnostic / progress messages use Write-Verbose
so they do not pollute the output stream.
#>

param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('Start', 'GetState', 'GetStateBatch', 'Compatibility', 'StartSite', 'GetSiteState', 'GetSiteStateBatch', 'GetCrossTenantHostUrl', 'UploadIdentityMap', 'RequestPersonalSite', 'SetCrossTenantRelationship')]
    [string] $Operation,

    [Parameter(Mandatory = $true)] [string] $TenantId,
    [Parameter(Mandatory = $true)] [string] $ClientId,

    # Certificate path 1: PFX passed inline (base64) with optional password.
    [Parameter(Mandatory = $false)] [string] $CertificatePfxBase64,
    [Parameter(Mandatory = $false)] [string] $CertificatePassword,

    # Certificate path 2: fetched from Key Vault by the runbook itself using
    # the Automation account's managed identity.
    [Parameter(Mandatory = $false)] [string] $KeyVaultUrl,
    [Parameter(Mandatory = $false)] [string] $KeyVaultCertificateName,

    [Parameter(Mandatory = $true)] [string] $SourceAdminUrl,

    # Start
    [Parameter(Mandatory = $false)] [string] $SourceUpn,
    [Parameter(Mandatory = $false)] [string] $TargetUpn,
    [Parameter(Mandatory = $false)] [string] $TargetCrossTenantHostUrl,

    # StartSite / GetSiteState
    [Parameter(Mandatory = $false)] [string] $SourceSiteUrl,
    [Parameter(Mandatory = $false)] [string] $TargetSiteUrl,

    # GetState / GetSiteState / GetStateBatch / GetSiteStateBatch / Compatibility
    [Parameter(Mandatory = $false)] [string] $PartnerCrossTenantHostUrl,

    # GetStateBatch / GetSiteStateBatch — base64-encoded JSON array of UPNs or site URLs
    [Parameter(Mandatory = $false)] [string] $IdentitiesJsonBase64,

    # UploadIdentityMap — base64-encoded CSV content
    [Parameter(Mandatory = $false)] [string] $IdentityMapCsvBase64,

    # RequestPersonalSite — base64-encoded JSON array of UPNs
    [Parameter(Mandatory = $false)] [string] $TargetUpnsJsonBase64,

    # SetCrossTenantRelationship — role of the PARTNER tenant relative to the
    # connected tenant (destination side passes 'Source'; source side passes 'Target').
    [Parameter(Mandatory = $false)] [ValidateSet('Source', 'Target')] [string] $PartnerRole
)

$ErrorActionPreference = 'Stop'
$VerbosePreference     = 'Continue'

# Parse a JSON array of strings into a List[string], reliably on PS 5.1.
# Confirmed live 2026-07-09: `@($json | ConvertFrom-Json)` yields a SINGLE-element
# wrapper around the real array (ConvertFrom-Json does not enumerate into the
# pipeline on 5.1), so `foreach` iterates once with the whole collection and
# typed -SourceUserPrincipalName/-SourceSiteUrl binding fails with
# "Cannot convert ... to System.String. Specified method is not supported."
function ConvertFrom-JsonStringArray([string] $Json) {
    $parsed = ConvertFrom-Json -InputObject $Json
    $list = New-Object 'System.Collections.Generic.List[string]'
    foreach ($item in @($parsed)) { $list.Add([string]$item) }
    return ,$list
}

# ── Resolve the app-only certificate ────────────────────────────────────────
if ([string]::IsNullOrEmpty($CertificatePfxBase64)) {
    if ([string]::IsNullOrEmpty($KeyVaultUrl) -or [string]::IsNullOrEmpty($KeyVaultCertificateName)) {
        throw "Either CertificatePfxBase64 or (KeyVaultUrl + KeyVaultCertificateName) must be provided."
    }

    Write-Verbose "Fetching certificate '$KeyVaultCertificateName' from Key Vault via managed identity."
    Import-Module Az.Accounts -ErrorAction Stop | Out-Null
    Import-Module Az.KeyVault -ErrorAction Stop | Out-Null
    Connect-AzAccount -Identity | Out-Null

    $vaultName = ([Uri]$KeyVaultUrl).Host.Split('.')[0]
    $CertificatePfxBase64 = Get-AzKeyVaultSecret -VaultName $vaultName -Name $KeyVaultCertificateName -AsPlainText
    if ([string]::IsNullOrEmpty($CertificatePfxBase64)) {
        throw "Key Vault secret '$KeyVaultCertificateName' in vault '$vaultName' is empty or missing."
    }

    try {
        $CertificatePassword = Get-AzKeyVaultSecret -VaultName $vaultName -Name "$KeyVaultCertificateName-password" -AsPlainText -ErrorAction Stop
    } catch {
        Write-Verbose "No '$KeyVaultCertificateName-password' secret found — assuming passwordless PFX."
        $CertificatePassword = $null
    }
}

$tempPfx = Join-Path $env:TEMP ("spo-{0}.pfx" -f ([guid]::NewGuid().ToString('N')))
[IO.File]::WriteAllBytes($tempPfx, [Convert]::FromBase64String($CertificatePfxBase64))

try {
    Import-Module Microsoft.Online.SharePoint.PowerShell -DisableNameChecking | Out-Null

    $certPassword = if ([string]::IsNullOrEmpty($CertificatePassword)) {
        New-Object System.Security.SecureString
    } else {
        ConvertTo-SecureString -String $CertificatePassword -AsPlainText -Force
    }

    Connect-SPOService `
        -Url              $SourceAdminUrl `
        -ClientId         $ClientId `
        -Tenant           $TenantId `
        -CertificatePath  $tempPfx `
        -CertificatePassword $certPassword | Out-Null

    try {
        switch ($Operation) {
            'Start' {
                if (-not $SourceUpn -or -not $TargetUpn -or -not $TargetCrossTenantHostUrl) {
                    throw "Start requires SourceUpn, TargetUpn, TargetCrossTenantHostUrl."
                }
                $r = Start-SPOCrossTenantUserContentMove `
                    -SourceUserPrincipalName  $SourceUpn `
                    -TargetUserPrincipalName  $TargetUpn `
                    -TargetCrossTenantHostUrl $TargetCrossTenantHostUrl
                $result = [pscustomobject]@{
                    JobId  = $SourceUpn
                    Status = if ($r -and $r.Status) { [string]$r.Status } else { 'Scheduled' }
                }
            }
            'GetState' {
                if (-not $SourceUpn -or -not $PartnerCrossTenantHostUrl) {
                    throw "GetState requires SourceUpn, PartnerCrossTenantHostUrl."
                }
                $s = Get-SPOCrossTenantUserContentMoveState `
                    -PartnerCrossTenantHostURL $PartnerCrossTenantHostUrl `
                    -SourceUserPrincipalName   $SourceUpn
                if ($null -eq $s) {
                    $result = $null
                } else {
                    $result = [pscustomobject]@{
                        JobId           = $SourceUpn
                        Status          = [string]$s.MoveState
                        ProgressPercent = if ($s.PSObject.Properties['PercentComplete']) { [int]$s.PercentComplete } else { 0 }
                        ErrorMessage    = if ($s.PSObject.Properties['ErrorMessage'])    { [string]$s.ErrorMessage }    else { $null }
                    }
                }
            }
            'GetStateBatch' {
                if (-not $IdentitiesJsonBase64 -or -not $PartnerCrossTenantHostUrl) {
                    throw "GetStateBatch requires IdentitiesJsonBase64, PartnerCrossTenantHostUrl."
                }
                $idsJson = [System.Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($IdentitiesJsonBase64))
                $ids = ConvertFrom-JsonStringArray $idsJson
                if (-not $ids -or $ids.Count -eq 0) {
                    throw "GetStateBatch received an empty identity list."
                }
                Write-Verbose "Querying user content move state for $($ids.Count) user(s)."
                $states = New-Object System.Collections.ArrayList
                foreach ($id in $ids) {
                    try {
                        $s = Get-SPOCrossTenantUserContentMoveState `
                            -PartnerCrossTenantHostURL $PartnerCrossTenantHostUrl `
                            -SourceUserPrincipalName   $id
                        if ($null -eq $s) {
                            [void]$states.Add([pscustomobject]@{ JobId = $id; Status = 'NotFound'; ProgressPercent = 0; ErrorMessage = $null })
                        } else {
                            [void]$states.Add([pscustomobject]@{
                                JobId           = $id
                                Status          = [string]$s.MoveState
                                ProgressPercent = if ($s.PSObject.Properties['PercentComplete']) { [int]$s.PercentComplete } else { 0 }
                                ErrorMessage    = if ($s.PSObject.Properties['ErrorMessage'])    { [string]$s.ErrorMessage }    else { $null }
                            })
                        }
                    } catch {
                        [void]$states.Add([pscustomobject]@{ JobId = $id; Status = 'Error'; ProgressPercent = 0; ErrorMessage = [string]$_.Exception.Message })
                    }
                }
                $result = $states
            }
            'StartSite' {
                if (-not $SourceSiteUrl -or -not $TargetSiteUrl -or -not $TargetCrossTenantHostUrl) {
                    throw "StartSite requires SourceSiteUrl, TargetSiteUrl, TargetCrossTenantHostUrl."
                }
                $r = Start-SPOCrossTenantSiteContentMove `
                    -SourceSiteUrl            $SourceSiteUrl `
                    -TargetSiteUrl            $TargetSiteUrl `
                    -TargetCrossTenantHostUrl $TargetCrossTenantHostUrl
                $result = [pscustomobject]@{
                    JobId  = $SourceSiteUrl
                    Status = if ($r -and $r.Status) { [string]$r.Status } else { 'Scheduled' }
                }
            }
            'GetSiteState' {
                if (-not $SourceSiteUrl -or -not $PartnerCrossTenantHostUrl) {
                    throw "GetSiteState requires SourceSiteUrl, PartnerCrossTenantHostUrl."
                }
                $s = Get-SPOCrossTenantSiteContentMoveState `
                    -PartnerCrossTenantHostURL $PartnerCrossTenantHostUrl `
                    -SourceSiteUrl             $SourceSiteUrl
                if ($null -eq $s) {
                    $result = $null
                } else {
                    $result = [pscustomobject]@{
                        JobId           = $SourceSiteUrl
                        Status          = [string]$s.MoveState
                        ProgressPercent = if ($s.PSObject.Properties['PercentComplete']) { [int]$s.PercentComplete } else { 0 }
                        ErrorMessage    = if ($s.PSObject.Properties['ErrorMessage'])    { [string]$s.ErrorMessage }    else { $null }
                    }
                }
            }
            'GetSiteStateBatch' {
                if (-not $IdentitiesJsonBase64 -or -not $PartnerCrossTenantHostUrl) {
                    throw "GetSiteStateBatch requires IdentitiesJsonBase64, PartnerCrossTenantHostUrl."
                }
                $idsJson = [System.Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($IdentitiesJsonBase64))
                $ids = ConvertFrom-JsonStringArray $idsJson
                if (-not $ids -or $ids.Count -eq 0) {
                    throw "GetSiteStateBatch received an empty identity list."
                }
                Write-Verbose "Querying site content move state for $($ids.Count) site(s)."
                $states = New-Object System.Collections.ArrayList
                foreach ($id in $ids) {
                    try {
                        $s = Get-SPOCrossTenantSiteContentMoveState `
                            -PartnerCrossTenantHostURL $PartnerCrossTenantHostUrl `
                            -SourceSiteUrl             $id
                        if ($null -eq $s) {
                            [void]$states.Add([pscustomobject]@{ JobId = $id; Status = 'NotFound'; ProgressPercent = 0; ErrorMessage = $null })
                        } else {
                            [void]$states.Add([pscustomobject]@{
                                JobId           = $id
                                Status          = [string]$s.MoveState
                                ProgressPercent = if ($s.PSObject.Properties['PercentComplete']) { [int]$s.PercentComplete } else { 0 }
                                ErrorMessage    = if ($s.PSObject.Properties['ErrorMessage'])    { [string]$s.ErrorMessage }    else { $null }
                            })
                        }
                    } catch {
                        [void]$states.Add([pscustomobject]@{ JobId = $id; Status = 'Error'; ProgressPercent = 0; ErrorMessage = [string]$_.Exception.Message })
                    }
                }
                $result = $states
            }
            'Compatibility' {
                if (-not $PartnerCrossTenantHostUrl) {
                    throw "Compatibility requires PartnerCrossTenantHostUrl."
                }
                $c = Get-SPOCrossTenantCompatibilityStatus `
                    -PartnerCrossTenantHostURL $PartnerCrossTenantHostUrl
                $statusText = if ($c -is [string]) { $c } elseif ($c.PSObject.Properties['CompatibilityStatus']) { [string]$c.CompatibilityStatus } elseif ($c.PSObject.Properties['Status']) { [string]$c.Status } elseif ($c) { [string]$c } else { 'Unknown' }
                $result = [pscustomobject]@{ Status = $statusText }
            }
            'GetCrossTenantHostUrl' {
                # Returns THIS tenant's canonical cross-tenant host URL — the value
                # partners must pass as -PartnerCrossTenantHostUrl / -TargetCrossTenantHostUrl.
                $u = Get-SPOCrossTenantHostUrl
                $urlText = if ($u -is [string]) { $u } elseif ($u.PSObject.Properties['CrossTenantHostUrl']) { [string]$u.CrossTenantHostUrl } elseif ($u.PSObject.Properties['Url']) { [string]$u.Url } elseif ($u) { [string]$u } else { $null }
                if ($urlText) { $urlText = $urlText.Trim().TrimEnd('/') }
                $result = [pscustomobject]@{ Url = $urlText }
            }
            'RequestPersonalSite' {
                if (-not $TargetUpnsJsonBase64) {
                    throw "RequestPersonalSite requires TargetUpnsJsonBase64."
                }
                $upnsJson = [System.Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($TargetUpnsJsonBase64))
                $upns = ConvertFrom-JsonStringArray $upnsJson
                if (-not $upns -or $upns.Count -eq 0) {
                    throw "RequestPersonalSite received empty UPN list."
                }
                # Request-SPOPersonalSite accepts at most 200 UPNs per invocation.
                $chunkSize = 200
                Write-Verbose "Requesting personal sites for $($upns.Count) user(s) in chunks of $chunkSize."
                for ($i = 0; $i -lt $upns.Count; $i += $chunkSize) {
                    $end = [Math]::Min($i + $chunkSize - 1, $upns.Count - 1)
                    # -UserEmails is typed List[string]; PowerShell cannot coerce the
                    # Object[] that ConvertFrom-Json yields into it ("Cannot convert ...
                    # Specified method is not supported."). Build an explicit
                    # List[string] so parameter binding succeeds.
                    $emails = New-Object 'System.Collections.Generic.List[string]'
                    foreach ($u in $upns[$i..$end]) { $emails.Add([string]$u) }
                    Write-Verbose "Requesting personal sites $($i + 1)-$($end + 1) of $($upns.Count)."
                    Request-SPOPersonalSite -UserEmails $emails | Out-Null
                }
                $result = [pscustomobject]@{ Status = 'Requested'; Count = $upns.Count }
            }
            'SetCrossTenantRelationship' {
                if (-not $PartnerRole -or -not $PartnerCrossTenantHostUrl) {
                    throw "SetCrossTenantRelationship requires PartnerRole, PartnerCrossTenantHostUrl."
                }
                # Verified working MnA sequence: Set-… then Test-… with identical
                # parameters. Test returns 'GoodToProceed' when the side is ready.
                Set-SPOCrossTenantRelationship `
                    -Scenario                  MnA `
                    -PartnerRole               $PartnerRole `
                    -PartnerCrossTenantHostUrl $PartnerCrossTenantHostUrl
                $t = Test-SPOCrossTenantRelationship `
                    -Scenario                  MnA `
                    -PartnerRole               $PartnerRole `
                    -PartnerCrossTenantHostUrl $PartnerCrossTenantHostUrl
                $statusText = if ($t -is [string]) { $t } elseif ($t -and $t.PSObject.Properties['Status']) { [string]$t.Status } elseif ($t) { [string]$t } else { 'Unknown' }
                $result = [pscustomobject]@{ Status = $statusText }
            }
            'UploadIdentityMap' {
                if (-not $IdentityMapCsvBase64) {
                    throw "UploadIdentityMap requires IdentityMapCsvBase64."
                }
                $tempCsv = Join-Path $env:TEMP ("identity-map-{0}.csv" -f ([guid]::NewGuid().ToString('N')))
                try {
                    $csvBytes = [Convert]::FromBase64String($IdentityMapCsvBase64)
                    [IO.File]::WriteAllBytes($tempCsv, $csvBytes)
                    Write-Verbose "Identity map CSV written to $tempCsv ($($csvBytes.Length) bytes)."
                    Add-SPOTenantIdentityMap -IdentityMapPath $tempCsv

                    # Add-SPOTenantIdentityMap reports per-row rejections ("can't be
                    # added. Check the format") on the console WITHOUT a terminating
                    # error, so a malformed CSV looks like success while the map stays
                    # empty. Verify the first user row actually landed before reporting
                    # Uploaded (cmdlet available in module 16.0.27xx+; skip if absent).
                    $csvLines = @((Get-Content $tempCsv) | Where-Object { $_ })
                    $firstUserRow = $csvLines | Where-Object { $_ -match '^"?User"?,' } | Select-Object -First 1
                    if ($firstUserRow -and (Get-Command Get-SPOTenantIdentityMappingUser -ErrorAction SilentlyContinue)) {
                        $srcUpn = (($firstUserRow -split ',')[2]).Trim('"')
                        $verified = $false
                        for ($i = 0; $i -lt 3 -and -not $verified; $i++) {
                            if ($i -gt 0) { Start-Sleep -Seconds 15 }
                            try {
                                if (Get-SPOTenantIdentityMappingUser -Field SourceUserKey -Value $srcUpn -ErrorAction Stop) { $verified = $true }
                            } catch { Write-Verbose "Identity map verify attempt $($i + 1) for [$srcUpn]: $_" }
                        }
                        if (-not $verified) {
                            throw "Identity map verification failed: no entry for source UPN [$srcUpn] after Add-SPOTenantIdentityMap — the rows were likely rejected silently (check column values; UserType must be 'RegularUser')."
                        }
                        Write-Verbose "Identity map verified: entry for [$srcUpn] exists on the target tenant."
                    }
                    $result = [pscustomobject]@{ Status = 'Uploaded'; Rows = $csvLines.Count }
                } finally {
                    if (Test-Path $tempCsv) { Remove-Item $tempCsv -Force -ErrorAction SilentlyContinue }
                }
            }
        }
    } finally {
        Disconnect-SPOService -ErrorAction SilentlyContinue | Out-Null
    }

    if ($null -eq $result) {
        Write-Output 'null'
    } elseif ($result -is [System.Collections.IEnumerable] -and $result -isnot [string] -and $result -isnot [pscustomobject]) {
        # Force array JSON even for single-element collections. PS 5.1 unwraps
        # single-element arrays even via -InputObject (confirmed live 2026-07-09:
        # a 1-user GetStateBatch emitted a bare object) — wrap defensively.
        $jsonOut = ConvertTo-Json -InputObject @($result) -Compress -Depth 5
        if (-not $jsonOut.TrimStart().StartsWith('[')) { $jsonOut = "[$jsonOut]" }
        Write-Output $jsonOut
    } else {
        Write-Output ($result | ConvertTo-Json -Compress -Depth 5)
    }
}
finally {
    if (Test-Path $tempPfx) { Remove-Item $tempPfx -Force -ErrorAction SilentlyContinue }
}
