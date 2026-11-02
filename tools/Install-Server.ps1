#Requires -RunAsAdministrator
#Requires -Version 5.1
<#
.SYNOPSIS
    Installs everything the WebText app needs on Windows Server 2019 (or newer) and deploys it to IIS,
    ready to use.

.DESCRIPTION
    Safe to run again: use the same command to update to a newer version or to repair the setup.

    1. Enables the IIS role and features (Windows Authentication, WebSockets, Application Initialization...).
    2. Downloads and installs the latest ASP.NET Core 10 Hosting Bundle (SHA-512 verified), unless it's current.
    3. Gets the app: builds it from the source (installing the .NET 10 SDK if needed), or uses an already
       published folder/zip passed with -PackagePath.
    0. Asks for the name of this installation (-AppName, e.g. TaxText); the site, folder and database are named after it.
    4. Copies it to -InstallPath (default C:\websites\<appname>), keeping an existing appsettings.json.
    5. Writes the database connection into appsettings.json (the only setting kept in a file).
    6. Creates the IIS app pool and site: No Managed Code, AlwaysRunning, no idle timeout, preload,
       Windows + Anonymous authentication, HTTPS binding with a certificate.
    7. Builds the database: creates it if missing, creates all tables from the app's own data model (or upgrades an
       older one), adds the starter templates, and gives the website's account read/write access. Uses your Windows
       account if it's a SQL administrator; otherwise asks for a SQL login (e.g. sa) or another Windows account.
    8. Opens the firewall for HTTPS (staff network only), starts the site and checks that it responds.
    9. Optionally (-CloudflareTunnelToken) installs Cloudflare's tunnel connector, cloudflared, as a Windows service,
       so Telnyx can reach the webhook through Cloudflare with NO inbound ports open on your firewall.

    Run it from an elevated PowerShell window. If your account isn't a SQL Server administrator, the script asks for
    one (or pass -SqlAdminCredential), or use -SkipSqlSetup and have your DBA run sql\create-database.sql.

.PARAMETER AppName
    The name of this installation of WebText, e.g. TaxText for an accountant, BillWatch for a legislative-monitoring
    client, RepairText for a computer repair shop. Asked for if not given. Letters, numbers, - and _ (2-40, starting
    with a letter). It becomes the default for the IIS site and app pool name, the install folder
    (C:\websites\<name>), the database name and the backup file names, and it's shown in the app's top bar (that
    display name can be changed later on the Settings page).
    It also keys this installation's encryption, so keep the same name when updating an existing installation.
    Several installations can run on one server, each with its own name and host name.

.PARAMETER SqlServer
    SQL Server name, e.g. SQL01, SQL01\SQLEXPRESS or SQL01,1433. Asked for if not given and not already configured.

.PARAMETER Database
    Database name. Default: the -AppName (asked for on a first install if not given). It's created if it doesn't exist.

.PARAMETER InstallPath
    Where the website files go. Default: C:\websites\<AppName in lower case>.

.PARAMETER PackagePath
    Where to get the app from. Any of:
      - the solution folder (contains src\WebText.Web\WebText.Web.csproj): it is built here. This is the default:
        the folder above this script.
      - a published folder (contains WebText.Web.dll), e.g. from 'dotnet publish src\WebText.Web -c Release -o publish'
      - a .zip of either of the above.

.PARAMETER HostName
    Name staff will use, e.g. texting.contoso.local. Default: this server's full DNS name. Used to pick or create
    the HTTPS certificate, and as the binding's host name when port 443 is already used by another site.

.PARAMETER CertificateThumbprint
    Thumbprint of an HTTPS certificate already in LocalMachine\My. If omitted, a valid certificate matching
    -HostName is used; if none exists, a self-signed one is created (replace it with a CA certificate later).

.PARAMETER HttpsPort
    Default 443.

.PARAMETER AppPoolCredential
    Run the app pool as a domain service account instead of the built-in ApplicationPoolIdentity.
    Example: -AppPoolCredential (Get-Credential CONTOSO\svc-webtext)

.PARAMETER HostingBundlePath
    Use an already-downloaded dotnet-hosting-10.0.x-win.exe (for servers without internet access).

.PARAMETER SqlAdminCredential
    A SQL Server administrator to build the database with, if the account running this script isn't one.
    A SQL login (e.g. sa): -SqlAdminCredential (Get-Credential sa)
    A Windows account:     -SqlAdminCredential (Get-Credential CONTOSO\sqladmin)
    If omitted, the script tries your own Windows account first and asks for credentials only if that isn't enough.
    Credentials are used only for the database setup and are never saved.

.PARAMETER SkipSqlSetup
    Don't touch SQL Server (no database creation or permission grants).

.PARAMETER CloudflareTunnelToken
    The token of a tunnel created in the Cloudflare dashboard (the long value after 'service install' in the
    command Cloudflare shows). Installs/updates cloudflared and runs it as a Windows service. Re-run with the token
    to update cloudflared (it doesn't update itself on Windows).

.PARAMETER SignIn
    How staff sign in: Windows (Active Directory, the default) or Local (user names, passwords and an authenticator
    app, managed on the app's Users page; use this when the server isn't on your domain or the app is hosted
    outside your network). Leave it out to keep whatever is set on the Settings page. Local needs -LocalAdminUser
    the first time.

.PARAMETER LocalAdminUser
    Creates (or recovers) a local administrator account, e.g. -LocalAdminUser admin. The script asks for its password.
    Running it again for an existing account resets the password, unlocks it, re-enables it and puts it back in the
    admin group: the way back in if every local admin is locked out.

.PARAMETER ResetLocalAdminAuthenticator
    With -LocalAdminUser: also remove that account's authenticator (lost phone). It sets up a new one at next sign-in.

.PARAMETER BackupFolder
    Prepares a folder for scheduled backups, e.g. -BackupFolder C:\txtappbackup: creates it and gives the website's
    account Modify permission (and only admins, SYSTEM and that account access). For a network share
    (\\server\share\folder) it prints the permissions to set on the file server. Then turn scheduled backups on
    under Settings in the app.

.PARAMETER PublicWebhookHost
    The public name Telnyx will call through Cloudflare, e.g. texthooks.yourfirm.com. Only used to print the exact
    Cloudflare settings, WAF rule and Telnyx webhook URL at the end.

.EXAMPLE
    .\tools\Install-Server.ps1 -AppName TaxText -SqlServer SQL01
    The installation for an accountant: IIS site and app pool "TaxText", files in C:\websites\taxtext, database TaxText.

.EXAMPLE
    .\tools\Install-Server.ps1 -AppName TaxText -SqlServer SQL01 -HostName texting.contoso.local -PackagePath D:\drop\publish

.EXAMPLE
    .\tools\Install-Server.ps1 -AppName TaxText -SqlServer SQL01 -CloudflareTunnelToken eyJhIjoi... -PublicWebhookHost texthooks.yourfirm.com

.EXAMPLE
    .\tools\Install-Server.ps1 -AppName RepairText -SqlServer .\SQLEXPRESS -SignIn Local -LocalAdminUser admin
    A server outside the domain: staff sign in with app accounts + authenticator codes. Creates the first admin.
#>
[CmdletBinding()]
param(
    [string]$AppName,
    [string]$SqlServer,
    [string]$Database,
    [string]$InstallPath,
    [string]$PackagePath = (Split-Path $PSScriptRoot -Parent),
    [string]$HostName,
    [string]$CertificateThumbprint,
    [int]$HttpsPort = 443,
    [string]$SiteName,
    [string]$AppPoolName,
    [System.Management.Automation.PSCredential]$AppPoolCredential,
    [string]$HostingBundlePath,
    [switch]$SkipSqlSetup,
    [System.Management.Automation.PSCredential]$SqlAdminCredential,
    [string]$CloudflareTunnelToken,
    [string]$PublicWebhookHost,
    [ValidateSet('Windows', 'Local')][string]$SignIn,
    [string]$LocalAdminUser,
    [switch]$ResetLocalAdminAuthenticator,
    [string]$BackupFolder
)

# The IIS tools (WebAdministration module and its IIS: drive) only work properly in Windows PowerShell 5.1.
# If this was started from PowerShell 7, run it again in Windows PowerShell with the same options.
if ($PSVersionTable.PSEdition -eq 'Core') {
    if ($AppPoolCredential -or $SqlAdminCredential) {
        throw ('Please run this script from Windows PowerShell (Start > Windows PowerShell > Run as administrator), ' +
               'not PowerShell 7, when using -AppPoolCredential or -SqlAdminCredential.')
    }
    $winPs = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    Write-Host 'This script needs Windows PowerShell 5.1 for the IIS tools. Re-starting it there...' -ForegroundColor Cyan
    $argList = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $PSCommandPath)
    foreach ($kv in $PSBoundParameters.GetEnumerator()) {
        if ($kv.Value -is [System.Management.Automation.SwitchParameter]) {
            if ($kv.Value.IsPresent) { $argList += "-$($kv.Key)" }
        } else {
            $argList += "-$($kv.Key)"; $argList += [string]$kv.Value
        }
    }
    & $winPs @argList
    exit $LASTEXITCODE
}

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'   # makes Invoke-WebRequest downloads much faster on PowerShell 5.1
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12

$DotnetMajor = '10.0'
$DotnetExe   = Join-Path $env:ProgramFiles 'dotnet\dotnet.exe'
$AncmDll     = Join-Path $env:ProgramFiles 'IIS\Asp.Net Core Module\V2\aspnetcorev2.dll'

# ---- Name of this installation (asked for first, because the other defaults come from it)
$namePattern = '^[A-Za-z][A-Za-z0-9_-]{1,39}$'
while (-not $AppName -or $AppName -notmatch $namePattern) {
    if ($AppName) { Write-Host "    '$AppName' can't be used: 2-40 letters, numbers, - or _, starting with a letter." -ForegroundColor Yellow }
    Write-Host ''
    Write-Host 'Name for this installation of WebText, e.g. TaxText, BillWatch or RepairText.' -ForegroundColor Cyan
    Write-Host 'It names the IIS site, folder, database and backups, and appears in the app. Use the same name when updating.'
    $AppName = (Read-Host 'Application name').Trim()
}
$appSlug = $AppName.ToLowerInvariant()
if (-not $InstallPath) { $InstallPath = "C:\websites\$appSlug" }
if (-not $SiteName)    { $SiteName = $AppName }
if (-not $AppPoolName) { $AppPoolName = $AppName }
if (-not $Database)    { $Database = $AppName }

# Updating an existing installation under a different name would make its saved keys unreadable.
$existingSettings = Join-Path $InstallPath 'appsettings.json'
if (Test-Path $existingSettings) {
    try { $existingName = (Get-Content $existingSettings -Raw | ConvertFrom-Json).Instance.Name } catch { $existingName = $null }
    if ($existingName -and $existingName -ne $AppName) {
        throw ("$InstallPath already holds the installation '$existingName'. Run again with -AppName $existingName to update it, " +
               "or choose a different -InstallPath for a new installation.")
    }
}

$WorkDir     = Join-Path $env:TEMP ("$AppName-install-" + (Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Path $WorkDir -Force | Out-Null

$logFile = Join-Path (Split-Path $InstallPath -Parent) ("$appSlug-install-" + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.log')
New-Item -ItemType Directory -Path (Split-Path $logFile -Parent) -Force | Out-Null
Start-Transcript -Path $logFile | Out-Null

function Write-Step([string]$text) { Write-Host ''; Write-Host "==> $text" -ForegroundColor Cyan }
function Write-Ok([string]$text)   { Write-Host "    $text" -ForegroundColor Green }
function Write-Info([string]$text) { Write-Host "    $text" }

# Runs an external program (cloudflared, appcmd, icacls...). Many of them write normal progress messages to the
# error stream; Windows PowerShell 5.1 with ErrorActionPreference=Stop would treat those as fatal, so collect
# everything as plain text instead and let the caller check the exit code.
$script:LastNativeOutput = @()
function Invoke-Native {
    param([Parameter(Mandatory = $true)][string]$FilePath, [string[]]$ArgumentList = @(), [switch]$ShowOutput)
    $old = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $lines = @(& $FilePath @ArgumentList 2>&1 | ForEach-Object { "$_" })
        $code = $LASTEXITCODE
    } finally { $ErrorActionPreference = $old }
    $script:LastNativeOutput = $lines
    if ($ShowOutput) { foreach ($l in $lines) { if ($l.Trim()) { Write-Host "    $l" } } }
    return $code
}

$warnings = New-Object System.Collections.Generic.List[string]
function Add-Warning([string]$text) { $warnings.Add($text); Write-Warning $text }

try {
# =====================================================================================================  checks
Write-Step 'Checking this server'
$os = Get-CimInstance Win32_OperatingSystem
$cs = Get-CimInstance Win32_ComputerSystem
if ($os.ProductType -eq 1) { throw 'This is a workstation, not Windows Server. Use tools\Setup-DevWorkstation.ps1 instead.' }
if ([int]$os.BuildNumber -lt 17763) { throw "Windows Server 2019 (build 17763) or newer is required. This is build $($os.BuildNumber)." }
Write-Ok "$($os.Caption) (build $($os.BuildNumber))"
if (-not $cs.PartOfDomain -and $SignIn -ne 'Local') {
    Add-Warning 'This server is not joined to a domain. Windows sign-in and the txtAppUser/txtAppAdmin group checks need it. Use -SignIn Local -LocalAdminUser <name> for app-hosted accounts instead.'
} else { Write-Ok "Domain: $($cs.Domain)" }
$isCore = (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion').InstallationType -eq 'Server Core'
if (-not $HostName) { $HostName = if ($cs.PartOfDomain) { "$($env:COMPUTERNAME).$($cs.Domain)".ToLower() } else { $env:COMPUTERNAME.ToLower() } }
Write-Ok "Host name: $HostName"

# =====================================================================================================  IIS
Write-Step 'Installing IIS and required features'
$features = @(
    'Web-Server', 'Web-WebServer', 'Web-Common-Http', 'Web-Default-Doc', 'Web-Static-Content', 'Web-Http-Errors',
    'Web-Health', 'Web-Http-Logging', 'Web-Http-Tracing', 'Web-Security', 'Web-Filtering', 'Web-Windows-Auth',
    'Web-App-Dev', 'Web-WebSockets', 'Web-AppInit', 'Web-Mgmt-Tools', 'Web-Scripting-Tools'
)
if (-not $isCore) { $features += 'Web-Mgmt-Console' }
$missing = @($features | Where-Object { -not (Get-WindowsFeature -Name $_).Installed })
if ($missing.Count -gt 0) {
    Write-Info ('Adding: ' + ($missing -join ', '))
    $r = Install-WindowsFeature -Name $missing
    if (-not $r.Success) { throw 'Install-WindowsFeature failed.' }
    if ($r.RestartNeeded -eq 'Yes') {
        Write-Warning 'Windows needs a restart to finish installing IIS. Restart, then run this script again with the same parameters.'
        return
    }
}
Write-Ok 'IIS features installed'
Set-Service W3SVC -StartupType Automatic; Set-Service WAS -StartupType Automatic
Start-Service WAS, W3SVC
Import-Module WebAdministration
$appcmd = Join-Path $env:windir 'System32\inetsrv\appcmd.exe'

# =====================================================================================================  .NET downloads
$script:releaseInfo = $null
function Get-DotnetRelease {
    if ($script:releaseInfo) { return $script:releaseInfo }
    $urls = @(
        "https://builds.dotnet.microsoft.com/dotnet/release-metadata/$DotnetMajor/releases.json",
        "https://dotnetcli.blob.core.windows.net/dotnet/release-metadata/$DotnetMajor/releases.json"
    )
    foreach ($u in $urls) {
        try {
            $json = Invoke-RestMethod -Uri $u -UseBasicParsing -TimeoutSec 60
            $script:releaseInfo = $json.releases | Select-Object -First 1
            return $script:releaseInfo
        } catch { Write-Info "Couldn't read $u ($($_.Exception.Message))" }
    }
    return $null
}

function Save-VerifiedFile($fileInfo, [string]$label) {
    $dest = Join-Path $WorkDir (Split-Path $fileInfo.url -Leaf)
    Write-Info "Downloading $label..."
    Invoke-WebRequest -Uri $fileInfo.url -OutFile $dest -UseBasicParsing
    $actual = (Get-FileHash -Path $dest -Algorithm SHA512).Hash
    if ($actual -ne $fileInfo.hash.ToUpper()) { throw "Checksum mismatch for $label. The download was not used." }
    Write-Ok "$label downloaded and verified"
    return $dest
}

function Invoke-Installer([string]$path, [string]$label) {
    Write-Info "Installing $label (this takes a few minutes)..."
    $p = Start-Process -FilePath $path -ArgumentList '/install', '/quiet', '/norestart' -Wait -PassThru
    # 0 = OK, 3010 = OK but restart recommended, 1638 = a newer version is already installed
    if ($p.ExitCode -eq 3010) { Add-Warning "$label installed; Windows recommends a restart when convenient." }
    elseif ($p.ExitCode -ne 0 -and $p.ExitCode -ne 1638) { throw "$label installer failed with exit code $($p.ExitCode)." }
    Write-Ok "$label installed"
}

function Get-InstalledVersions([string]$folder) {
    $dir = Join-Path $env:ProgramFiles "dotnet\$folder"
    if (-not (Test-Path $dir)) { return @() }
    return @(Get-ChildItem $dir -Directory | Where-Object { $_.Name -like "$DotnetMajor.*" } |
             ForEach-Object { try { [version]($_.Name -replace '-.*$', '') } catch { $null } } | Where-Object { $_ })
}

# =====================================================================================================  Hosting Bundle
Write-Step 'ASP.NET Core 10 Hosting Bundle'
$installedAsp = Get-InstalledVersions 'shared\Microsoft.AspNetCore.App' | Sort-Object -Descending | Select-Object -First 1
if ($HostingBundlePath) {
    if (-not (Test-Path $HostingBundlePath)) { throw "Hosting Bundle not found: $HostingBundlePath" }
    Invoke-Installer $HostingBundlePath 'ASP.NET Core Hosting Bundle'
    $bundleInstalled = $true
} else {
    $rel = Get-DotnetRelease
    if ($rel) {
        $latest = [version]$rel.'aspnetcore-runtime'.version
        if ($installedAsp -and $installedAsp -ge $latest -and (Test-Path $AncmDll)) {
            Write-Ok "Already up to date ($installedAsp)"
            $bundleInstalled = $false
        } else {
            $file = $rel.'aspnetcore-runtime'.files | Where-Object { $_.name -eq 'dotnet-hosting-win.exe' } | Select-Object -First 1
            if (-not $file) { throw 'Could not find the Hosting Bundle in the .NET release information.' }
            $exe = Save-VerifiedFile $file "ASP.NET Core Hosting Bundle $latest"
            Invoke-Installer $exe "ASP.NET Core Hosting Bundle $latest"
            $bundleInstalled = $true
        }
    } elseif ($installedAsp -and (Test-Path $AncmDll)) {
        Add-Warning "Couldn't check for a newer Hosting Bundle (no internet?). Using installed $installedAsp."
        $bundleInstalled = $false
    } else {
        throw ("Couldn't download the Hosting Bundle. On a PC with internet, download 'Hosting Bundle' for ASP.NET Core 10.0 " +
               "from https://dotnet.microsoft.com/download/dotnet/10.0, copy it here and re-run with -HostingBundlePath <file>.")
    }
}
if (-not (Test-Path $AncmDll)) { throw 'The ASP.NET Core Module is not registered with IIS. Run the Hosting Bundle installer again and choose Repair.' }
if ($bundleInstalled) {
    Write-Info 'Restarting IIS so it picks up the ASP.NET Core Module...'
    Invoke-Native 'net.exe' @('stop', 'was', '/y') | Out-Null
    Invoke-Native 'net.exe' @('start', 'w3svc') | Out-Null
}

# =====================================================================================================  get the app
Write-Step 'Preparing the application files'
$src = (Resolve-Path $PackagePath).Path
if ($src -like '*.zip') {
    $unz = Join-Path $WorkDir 'package'
    Expand-Archive -Path $src -DestinationPath $unz -Force
    $src = $unz
}
# Source code wins if both are present (the solution folder may contain an old local build under bin\).
$csproj = Get-ChildItem -Path $src -Filter 'WebText.Web.csproj' -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1
$publishedDll = Get-ChildItem -Path $src -Filter 'WebText.Web.dll' -Recurse -ErrorAction SilentlyContinue |
                Where-Object { $_.DirectoryName -notmatch '\\obj\\' -and (Test-Path (Join-Path $_.DirectoryName 'web.config')) } |
                Select-Object -First 1

if ($csproj) {
    # Build from source. Needs the .NET 10 SDK and access to nuget.org.
    if (-not (Get-InstalledVersions 'sdk')) {
        $rel = Get-DotnetRelease
        if (-not $rel) { throw 'The .NET 10 SDK is needed to build from source, and it could not be downloaded. Publish on another PC and use -PackagePath <publish folder>.' }
        $file = $rel.sdk.files | Where-Object { $_.name -eq 'dotnet-sdk-win-x64.exe' } | Select-Object -First 1
        $exe = Save-VerifiedFile $file ".NET SDK $($rel.sdk.version)"
        Invoke-Installer $exe ".NET SDK $($rel.sdk.version)"
    }
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'; $env:DOTNET_NOLOGO = '1'
    $publishDir = Join-Path $WorkDir 'publish'
    Write-Info "Building $($csproj.FullName)..."
    $oldEap = $ErrorActionPreference; $ErrorActionPreference = 'Continue'   # build warnings go to stderr
    try { & $DotnetExe publish $csproj.FullName -c Release -o $publishDir --nologo; $buildCode = $LASTEXITCODE }
    finally { $ErrorActionPreference = $oldEap }
    if ($buildCode -ne 0) {
        throw ('Build failed (see the errors above). If it reports NU1903/NU1904, a package has a newly published security ' +
               'vulnerability: update its version in WebText.Web.csproj.')
    }
    $appFiles = $publishDir
    Write-Ok 'Built'
} elseif ($publishedDll) {
    $appFiles = $publishedDll.DirectoryName
    Write-Ok "Using published files in $appFiles"
} else {
    throw "No WebText.Web.csproj or published WebText.Web.dll found under $PackagePath."
}
if (-not (Test-Path (Join-Path $appFiles 'web.config'))) { throw "web.config is missing from $appFiles. Is this a published build?" }

# =====================================================================================================  app pool
Write-Step "IIS app pool '$AppPoolName'"
$poolPath = "IIS:\AppPools\$AppPoolName"
if (-not (Test-Path $poolPath)) { New-WebAppPool -Name $AppPoolName | Out-Null; Write-Info 'Created' }
Set-ItemProperty $poolPath -Name managedRuntimeVersion -Value ''                       # No Managed Code
Set-ItemProperty $poolPath -Name managedPipelineMode -Value 'Integrated'
Set-ItemProperty $poolPath -Name startMode -Value 'AlwaysRunning'                      # background sender keeps running
Set-ItemProperty $poolPath -Name processModel.idleTimeout -Value ([TimeSpan]::Zero)
Set-ItemProperty $poolPath -Name processModel.loadUserProfile -Value $true
Set-ItemProperty $poolPath -Name recycling.periodicRestart.time -Value ([TimeSpan]::Zero)
# Recycle once a night at 3 AM instead of every 29 hours (which could land in the middle of the day).
Clear-WebConfiguration -PSPath 'MACHINE/WEBROOT/APPHOST' -Filter "system.applicationHost/applicationPools/add[@name='$AppPoolName']/recycling/periodicRestart/schedule" -ErrorAction SilentlyContinue
Add-WebConfigurationProperty -PSPath 'MACHINE/WEBROOT/APPHOST' -Filter "system.applicationHost/applicationPools/add[@name='$AppPoolName']/recycling/periodicRestart/schedule" -Name '.' -Value @{ value = '03:00:00' }
if ($AppPoolCredential) {
    Set-ItemProperty $poolPath -Name processModel -Value @{
        identityType = 3; userName = $AppPoolCredential.UserName; password = $AppPoolCredential.GetNetworkCredential().Password }
    $poolAccount = $AppPoolCredential.UserName
} else {
    Set-ItemProperty $poolPath -Name processModel.identityType -Value 4                # ApplicationPoolIdentity
    $poolAccount = "IIS AppPool\$AppPoolName"
}
Write-Ok "Configured (runs as $poolAccount)"

# =====================================================================================================  backup folder
if ($BackupFolder) {
    Write-Step "Backup folder $BackupFolder"
    if ($BackupFolder -match '^[A-Za-z]:' -and (Get-CimInstance Win32_LogicalDisk -Filter "DeviceID='$($BackupFolder.Substring(0,2))'" -ErrorAction SilentlyContinue).DriveType -eq 4) {
        Add-Warning "$($BackupFolder.Substring(0,2)) is a mapped network drive; the website can't see mapped drives. Use the \\server\share path instead."
    } elseif ($BackupFolder.StartsWith('\\')) {
        $computerAccount = "$((Get-CimInstance Win32_ComputerSystem).Domain.Split('.')[0])\$env:COMPUTERNAME`$"
        $who = if ($AppPoolCredential) { $AppPoolCredential.UserName } else { $computerAccount }
        Write-Info "Network share: on the file server, give $who Change (share) and Modify (NTFS) permission on $BackupFolder."
        Write-Info "(The built-in app pool account reaches the network as this server's computer account, $computerAccount.)"
    } else {
        New-Item -ItemType Directory -Path $BackupFolder -Force | Out-Null
        # Backups hold (encrypted) client data and credentials: only admins, SYSTEM and the website's account.
        Invoke-Native 'icacls.exe' @($BackupFolder, '/inheritance:r', '/grant:r', '*S-1-5-32-544:(OI)(CI)F', '*S-1-5-18:(OI)(CI)F',
            "${poolAccount}:(OI)(CI)M", '/Q') | Out-Null
        Write-Ok "Created; $poolAccount can write backups there (Administrators and SYSTEM have full control; nobody else has access)"
    }
    Write-Info 'Turn on scheduled backups in the app: Settings -> Scheduled backups (choose days, time and a backup password).'
}

# =====================================================================================================  files
Write-Step "Copying the website to $InstallPath"
New-Item -ItemType Directory -Path $InstallPath -Force | Out-Null
$siteExists = Test-Path "IIS:\Sites\$SiteName"
$offline = Join-Path $InstallPath 'app_offline.htm'
if ($siteExists -and (Test-Path (Join-Path $InstallPath 'WebText.Web.dll'))) {
    # Take the running app offline so its files can be replaced.
    Set-Content -Path $offline -Value '<html><body style="font-family:Segoe UI;padding:40px"><h2>Updating...</h2><p>Back in a minute.</p></body></html>'
    Start-Sleep -Seconds 5
}
$rc = Invoke-Native 'robocopy.exe' @($appFiles, $InstallPath, '/MIR', '/R:5', '/W:3', '/NFL', '/NDL', '/NJH', '/NJS', '/NP',
    '/XF', 'appsettings.json', 'appsettings.Development.json', 'app_offline.htm', '/XD', 'logs')
if ($rc -ge 8) { throw "Copying files failed (robocopy exit code $rc): $($script:LastNativeOutput -join ' ')" }
$settingsFile = Join-Path $InstallPath 'appsettings.json'
if (-not (Test-Path $settingsFile)) { Copy-Item (Join-Path $appFiles 'appsettings.json') $settingsFile }
New-Item -ItemType Directory -Path (Join-Path $InstallPath 'logs') -Force | Out-Null
Write-Ok 'Files copied'

# Permissions: the app pool account reads the site, writes only to logs\.
if ((Invoke-Native 'icacls.exe' @($InstallPath, '/grant', "${poolAccount}:(OI)(CI)RX", '/T', '/C', '/Q')) -ne 0) {
    Add-Warning "Couldn't grant $poolAccount read access to ${InstallPath}: $($script:LastNativeOutput -join ' ')" }
if ((Invoke-Native 'icacls.exe' @((Join-Path $InstallPath 'logs'), '/grant', "${poolAccount}:(OI)(CI)M", '/T', '/C', '/Q')) -ne 0) {
    Add-Warning "Couldn't grant $poolAccount write access to the logs folder: $($script:LastNativeOutput -join ' ')" }
Write-Ok "Permissions granted to $poolAccount"

# =====================================================================================================  appsettings.json
Write-Step 'Database server and name'
$cfg = Get-Content $settingsFile -Raw | ConvertFrom-Json
# Settings files from older versions name the connection differently: adopt it under the current name.
$currentCs = $cfg.ConnectionStrings.WebText
if (-not $currentCs) {
    $other = $cfg.ConnectionStrings.PSObject.Properties | Where-Object { $_.Name -ne 'Sqlite' -and $_.Value } | Select-Object -First 1
    if ($other) {
        $currentCs = $other.Value
        $cfg.ConnectionStrings.PSObject.Properties.Remove($other.Name)
    }
}
if (-not $cfg.ConnectionStrings.PSObject.Properties['WebText']) { $cfg.ConnectionStrings | Add-Member -NotePropertyName WebText -NotePropertyValue '' }
if (-not $cfg.PSObject.Properties['Instance']) { $cfg | Add-Member -NotePropertyName Instance -NotePropertyValue ([pscustomobject]@{ Name = $AppName }) }
$cfg.Instance.Name = $AppName
$alreadyConfigured = $currentCs -and $currentCs -notmatch 'YOUR-SQL-SERVER'
$curServer = $null; $curDb = $null
if ($alreadyConfigured) {
    if ($currentCs -match '(?i)(Server|Data Source)\s*=\s*([^;]+)') { $curServer = $Matches[2].Trim() }
    if ($currentCs -match '(?i)(Database|Initial Catalog)\s*=\s*([^;]+)') { $curDb = $Matches[2].Trim() }
}
$serverGiven = $PSBoundParameters.ContainsKey('SqlServer')
$dbGiven = $PSBoundParameters.ContainsKey('Database')
if (-not $serverGiven) {
    if ($curServer) { $SqlServer = $curServer }
    else { while (-not $SqlServer) { $SqlServer = (Read-Host 'SQL Server name (e.g. SQL01, SQL01\SQLEXPRESS or SQL01,1433)').Trim() } }
}
if (-not $dbGiven) {
    if ($curDb) { $Database = $curDb }
    elseif (-not $curServer) {
        $answer = (Read-Host "Database name [$Database]").Trim()
        if ($answer) { $Database = $answer }
    }
}
if ($Database -notmatch '^[\w\-\. ]{1,128}$') { throw "Database name '$Database' contains characters that aren't allowed." }
$cfg.ConnectionStrings.WebText = "Server=$SqlServer;Database=$Database;Trusted_Connection=True;TrustServerCertificate=True;MultipleActiveResultSets=False;Application Name=$AppName"
$cfg.Database.Provider = 'SqlServer'
$cfg | ConvertTo-Json -Depth 10 | Set-Content -Path $settingsFile -Encoding UTF8
Write-Ok "Installation: $AppName   Server: $SqlServer   Database: $Database"
Write-Info "The website connects with Windows authentication as its app pool account (no password stored)."

# Which Windows account SQL Server will see when the website connects:
$sqlHost = ($SqlServer -split '\\')[0] -replace '^tcp:', '' -replace ',\d+$', ''
$isLocalSql = @('.', 'localhost', '(local)', '127.0.0.1', $env:COMPUTERNAME, $HostName, "$($env:COMPUTERNAME).$($cs.Domain)") -contains $sqlHost
if ($AppPoolCredential) { $sqlLogin = $AppPoolCredential.UserName }
elseif ($isLocalSql)    { $sqlLogin = "IIS APPPOOL\$AppPoolName" }
else {
    # A remote SQL Server sees the built-in app pool identity as this computer's domain account, DOMAIN\SERVER$.
    try {
        $sid = (New-Object System.Security.Principal.NTAccount("$($env:COMPUTERNAME)`$")).Translate([System.Security.Principal.SecurityIdentifier])
        $sqlLogin = $sid.Translate([System.Security.Principal.NTAccount]).Value
    } catch { $sqlLogin = "$($env:USERDOMAIN)\$($env:COMPUTERNAME)`$" }
}

# =====================================================================================================  SQL Server
function Invoke-DatabaseSetup {
    <# Runs 'dotnet WebText.Web.dll --setup-database' (Data\DatabaseSetup.cs): creates the database and tables from
       the app's own data model, applies upgrades, adds the starter templates and grants $sqlLogin read/write.
       Auth: Current = this Windows account; Sql = SQL Server login; Windows = another Windows account. #>
    param([string]$Auth, [System.Management.Automation.PSCredential]$Credential)

    $setupDir = Join-Path $InstallPath ('logs\dbsetup-' + (Get-Date -Format 'yyyyMMddHHmmss'))
    New-Item -ItemType Directory -Path $setupDir -Force | Out-Null
    $settings = [ordered]@{
        server   = $SqlServer
        database = $Database
        appLogin = $sqlLogin
        auth     = $(if ($Auth -eq 'Sql') { 'Sql' } else { 'Integrated' })
        sqlUser  = $(if ($Auth -eq 'Sql') { $Credential.UserName } else { $null })
        signIn   = $(if ($SignIn) { $SignIn } else { $null })
        displayName = $AppName                          # only used if no name has been set on the Settings page
        localAdmin = $(if ($LocalAdminUser) { $LocalAdminUser } else { $null })
        localAdminPasswordHash = $localAdminHash        # a salted PBKDF2 hash, not the password; the file is deleted below
        resetLocalAdminAuthenticator = [bool]$ResetLocalAdminAuthenticator
    }
    $jsonPath = Join-Path $setupDir 'setup.json'
    $settings | ConvertTo-Json | Set-Content -Path $jsonPath -Encoding UTF8      # never contains a password
    if ($localAdminHash) { Invoke-Native 'icacls.exe' @($jsonPath, '/inheritance:r', '/grant:r', "*S-1-5-32-544:F", "$($env:USERNAME):F", '/Q') | Out-Null }
    $outFile = Join-Path $setupDir 'out.txt'; $errFile = Join-Path $setupDir 'err.txt'
    $argLine = "`"$(Join-Path $InstallPath 'WebText.Web.dll')`" --setup-database `"$jsonPath`""

    try {
        if ($Auth -eq 'Windows') {
            # Run the setup as the other Windows account, so SQL Server sees that account.
            Invoke-Native 'icacls.exe' @($setupDir, '/grant', "$($Credential.UserName):(OI)(CI)M", '/Q') | Out-Null
            $p = Start-Process -FilePath $DotnetExe -ArgumentList $argLine -Credential $Credential -LoadUserProfile `
                     -WorkingDirectory $InstallPath -Wait -PassThru -RedirectStandardOutput $outFile -RedirectStandardError $errFile
        } else {
            if ($Auth -eq 'Sql') { $env:WEBTEXT_SETUP_SQLPASSWORD = $Credential.GetNetworkCredential().Password }
            $p = Start-Process -FilePath $DotnetExe -ArgumentList $argLine -NoNewWindow `
                     -WorkingDirectory $InstallPath -Wait -PassThru -RedirectStandardOutput $outFile -RedirectStandardError $errFile
        }
        foreach ($f in $outFile, $errFile) {
            if (Test-Path $f) { Get-Content $f | Where-Object { $_.Trim() } | ForEach-Object {
                $color = if ($_ -match '^\s*OK') { 'Green' } elseif ($_ -match '^\s*!!|Unhandled|Exception') { 'Yellow' } else { 'Gray' }
                Write-Host $_ -ForegroundColor $color } }
        }
        return [int]$p.ExitCode
    } catch {
        Write-Host "    !!  Couldn't run the database setup: $($_.Exception.Message)" -ForegroundColor Yellow
        return 1
    } finally {
        Remove-Item Env:\WEBTEXT_SETUP_SQLPASSWORD -ErrorAction SilentlyContinue
        Remove-Item $setupDir -Recurse -Force -ErrorAction SilentlyContinue
    }
}

# Local admin account: ask for its password here, and pass the database step only a salted hash of it.
$localAdminHash = $null
if ($LocalAdminUser -and -not $SkipSqlSetup) {
    Write-Step "Local admin account '$LocalAdminUser'"
    Write-Info 'Choose its password (at least 12 characters). At first sign-in it also sets up an authenticator app.'
    while ($true) {
        $p1 = Read-Host "    Password for $LocalAdminUser" -AsSecureString
        $p2 = Read-Host '    Type it again' -AsSecureString
        $plain1 = [Runtime.InteropServices.Marshal]::PtrToStringBSTR([Runtime.InteropServices.Marshal]::SecureStringToBSTR($p1))
        $plain2 = [Runtime.InteropServices.Marshal]::PtrToStringBSTR([Runtime.InteropServices.Marshal]::SecureStringToBSTR($p2))
        if ($plain1 -ne $plain2) { Write-Host '    The passwords don''t match. Try again.' -ForegroundColor Yellow; continue }
        if ($plain1.Length -lt 12) { Write-Host '    Use at least 12 characters.' -ForegroundColor Yellow; continue }
        break
    }
    $old = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
    try { $localAdminHash = ($plain1 | & $DotnetExe (Join-Path $InstallPath 'WebText.Web.dll') --hash-password 2>$null | Select-Object -First 1) }
    finally { $ErrorActionPreference = $old; $plain1 = $null; $plain2 = $null }
    if (-not $localAdminHash -or $localAdminHash.Length -lt 40) { throw 'Could not prepare the local admin password.' }
    Write-Ok 'Password set'
} elseif ($SignIn -eq 'Local' -and -not $SkipSqlSetup) {
    Write-Info 'Local sign-in: if no local admin exists yet, add -LocalAdminUser <name> (the database step will say so).'
}

if ($SkipSqlSetup) {
    Write-Step 'Database setup skipped (-SkipSqlSetup)'
    Write-Info "Have your DBA create [$Database], run sql\create-database.sql in it, and grant $sqlLogin db_datareader + db_datawriter."
} else {
    Write-Step "Building the database [$Database] on $SqlServer"
    Write-Info "The website's own account will be: $sqlLogin"

    # Who does the setup: -SqlAdminCredential if given, otherwise this Windows account first.
    if ($SqlAdminCredential) {
        $auth = if ($SqlAdminCredential.UserName -match '\\|@') { 'Windows' } else { 'Sql' }
        $cred = $SqlAdminCredential
    } else {
        $auth = 'Current'; $cred = $null
    }

    $dbReady = $false
    for ($attempt = 1; $attempt -le 5; $attempt++) {
        $code = Invoke-DatabaseSetup -Auth $auth -Credential $cred
        if ($code -eq 0) { $dbReady = $true; break }
        if ($code -ne 2 -and $code -ne 3) {
            Add-Warning 'Database setup failed (see the messages above). Fix the problem and run this script again.'
            break
        }

        # 2 = sign-in refused, 3 = signed in but not allowed. Ask for an administrator account.
        Write-Host ''
        Write-Host '    Setting up the database needs a SQL Server administrator: an account that can create databases' -ForegroundColor Yellow
        Write-Host "    and logins on $SqlServer (sysadmin, or dbcreator + securityadmin). Your credentials are used only" -ForegroundColor Yellow
        Write-Host '    for this setup and are not saved anywhere.' -ForegroundColor Yellow
        $choice = ''
        while ($choice -notin 'S', 'W', 'Q') {
            $choice = (Read-Host '    Use a [S]QL Server login (e.g. sa), a different [W]indows account, or [Q]uit database setup?').Trim().ToUpper()
        }
        if ($choice -eq 'Q') { break }
        if ($choice -eq 'S') {
            $cred = Get-Credential -Message "SQL Server login with administrator rights on $SqlServer (for example: sa)"
            $auth = 'Sql'
        } else {
            $cred = Get-Credential -Message "Windows account that is a SQL Server administrator on $SqlServer (DOMAIN\user)"
            $auth = 'Windows'
        }
        if (-not $cred) { break }
    }
    if (-not $dbReady) {
        Add-Warning ("The database isn't set up yet. Re-run this script (it will ask for an administrator account), " +
                     "or have your DBA create [$Database], run sql\create-database.sql and grant $sqlLogin db_datareader + db_datawriter.")
    }
}

# =====================================================================================================  certificate
Write-Step 'HTTPS certificate'
if ($CertificateThumbprint) {
    $cert = Get-Item "Cert:\LocalMachine\My\$($CertificateThumbprint -replace '\s', '')" -ErrorAction SilentlyContinue
    if (-not $cert) { throw "Certificate $CertificateThumbprint not found in LocalMachine\My." }
} else {
    $now = Get-Date
    $cert = Get-ChildItem Cert:\LocalMachine\My | Where-Object {
        $_.HasPrivateKey -and $_.NotAfter -gt $now.AddDays(14) -and $_.NotBefore -le $now -and
        ($_.DnsNameList.Unicode -contains $HostName) -and
        ($_.EnhancedKeyUsageList.Count -eq 0 -or $_.EnhancedKeyUsageList.ObjectId -contains '1.3.6.1.5.5.7.3.1')
    } | Sort-Object NotAfter -Descending | Select-Object -First 1
    if (-not $cert) {
        $cert = New-SelfSignedCertificate -DnsName $HostName, $env:COMPUTERNAME -CertStoreLocation Cert:\LocalMachine\My `
                    -FriendlyName "$AppName self-signed ($HostName)" -NotAfter (Get-Date).AddYears(2)
        Add-Warning ("Created a SELF-SIGNED certificate for $HostName. Browsers will warn until you replace it with a certificate " +
                     "from your internal CA: re-run this script with -CertificateThumbprint <thumbprint>.")
    }
}
Write-Ok "$($cert.Subject)  (thumbprint $($cert.Thumbprint), expires $($cert.NotAfter.ToString('d')))"

# =====================================================================================================  site
Write-Step "IIS site '$SiteName'"
# Use a host-name binding (SNI) only if another site already listens on this port without one.
$otherOnPort = Get-WebBinding -Protocol https -Port $HttpsPort | Where-Object {
    ($_.ItemXPath -notmatch "@name='$([regex]::Escape($SiteName))'") -and ($_.bindingInformation -match "^\*?:${HttpsPort}:$")
}
$hostHeader = if ($otherOnPort) { $HostName } else { '' }
$sslFlags = if ($hostHeader) { 1 } else { 0 }

if (-not $siteExists) {
    New-Website -Name $SiteName -PhysicalPath $InstallPath -ApplicationPool $AppPoolName -Port $HttpsPort -Ssl `
                -HostHeader $hostHeader -SslFlags $sslFlags | Out-Null
    # New-Website adds no extra bindings, but remove any default HTTP one if present.
    Get-WebBinding -Name $SiteName -Protocol http -ErrorAction SilentlyContinue | Remove-WebBinding -ErrorAction SilentlyContinue
    Write-Info 'Created'
} else {
    Set-ItemProperty "IIS:\Sites\$SiteName" -Name physicalPath -Value $InstallPath
    Set-ItemProperty "IIS:\Sites\$SiteName" -Name applicationPool -Value $AppPoolName
    if (-not (Get-WebBinding -Name $SiteName -Protocol https -Port $HttpsPort -HostHeader $hostHeader)) {
        New-WebBinding -Name $SiteName -Protocol https -Port $HttpsPort -HostHeader $hostHeader -SslFlags $sslFlags
    }
}
# Attach the certificate (replacing a different one if this is a re-run).
$sslPath = if ($hostHeader) { "IIS:\SslBindings\!$HttpsPort!$hostHeader" } else { "IIS:\SslBindings\0.0.0.0!$HttpsPort" }
$existingSsl = Get-Item $sslPath -ErrorAction SilentlyContinue
if ($existingSsl -and $existingSsl.Thumbprint -ne $cert.Thumbprint) { Remove-Item $sslPath -Force; $existingSsl = $null }
if (-not $existingSsl) {
    $binding = Get-WebBinding -Name $SiteName -Protocol https -Port $HttpsPort -HostHeader $hostHeader | Select-Object -First 1
    $binding.AddSslCertificate($cert.Thumbprint, 'My')
}
Set-ItemProperty "IIS:\Sites\$SiteName" -Name applicationDefaults.preloadEnabled -Value $true
# IIS request logs: leave out the query string. VoIP.ms delivers incoming texts (which a client could fill with an SSN)
# and the callback key in the query string, and IIS logs are outside the app's SSN redaction.
$logFlags = (Get-ItemProperty "IIS:\Sites\$SiteName" -Name logFile.logExtFileFlags).ToString()
if ($logFlags -match 'UriQuery') {
    $newFlags = (($logFlags -split ',') | ForEach-Object { $_.Trim() } | Where-Object { $_ -and $_ -ne 'UriQuery' }) -join ','
    Set-ItemProperty "IIS:\Sites\$SiteName" -Name logFile.logExtFileFlags -Value $newFlags
}
Write-Ok 'IIS logs: query strings are not recorded (they can contain message text)'
Write-Ok ("https://{0}{1}/" -f $HostName, $(if ($HttpsPort -ne 443) { ":$HttpsPort" } else { '' }))

# Authentication: Windows for staff, Anonymous only so Telnyx can reach the webhook (the app enforces the rest).
# The app's web.config sets these, so allow it to for this site (IIS locks them by default).
foreach ($section in 'windowsAuthentication', 'anonymousAuthentication') {
    $rc = Invoke-Native $appcmd @('unlock', 'config', "$SiteName/", "-section:system.webServer/security/authentication/$section", '-commit:apphost')
    if ($rc -ne 0) { Invoke-Native $appcmd @('unlock', 'config', "-section:system.webServer/security/authentication/$section") | Out-Null }
    Set-WebConfigurationProperty -PSPath 'MACHINE/WEBROOT/APPHOST' -Location $SiteName `
        -Filter "system.webServer/security/authentication/$section" -Name enabled -Value $true
}
Write-Ok 'Windows + Anonymous authentication enabled'

# =====================================================================================================  firewall
Write-Step 'Firewall'
$fwName = "WebText HTTPS ($HttpsPort)"
if (-not (Get-NetFirewallRule -DisplayName $fwName -ErrorAction SilentlyContinue)) {
    New-NetFirewallRule -DisplayName $fwName -Direction Inbound -Protocol TCP -LocalPort $HttpsPort -Action Allow -Profile Domain, Private | Out-Null
}
Write-Ok "Inbound TCP $HttpsPort allowed"

# =====================================================================================================  Cloudflare Tunnel
$cfExe = @("${env:ProgramFiles(x86)}\cloudflared\cloudflared.exe", "$env:ProgramFiles\cloudflared\cloudflared.exe") |
         Where-Object { Test-Path $_ } | Select-Object -First 1
if ($CloudflareTunnelToken) {
    Write-Step 'Cloudflare Tunnel connector (cloudflared)'
    $msi = Join-Path $WorkDir 'cloudflared-windows-amd64.msi'
    Write-Info 'Downloading the latest cloudflared from Cloudflare''s GitHub releases...'
    Invoke-WebRequest -Uri 'https://github.com/cloudflare/cloudflared/releases/latest/download/cloudflared-windows-amd64.msi' -OutFile $msi -UseBasicParsing
    $sig = Get-AuthenticodeSignature -FilePath $msi
    if ($sig.Status -ne 'Valid' -or $sig.SignerCertificate.Subject -notmatch 'Cloudflare') {
        throw "The cloudflared installer's digital signature didn't check out ($($sig.Status), $($sig.SignerCertificate.Subject)). Not installed."
    }
    Write-Ok "Signature valid ($($sig.SignerCertificate.Subject -replace ',.*$', ''))"
    $p = Start-Process msiexec.exe -ArgumentList '/i', "`"$msi`"", '/qn', '/norestart' -Wait -PassThru
    if ($p.ExitCode -ne 0 -and $p.ExitCode -ne 3010) { throw "cloudflared installer failed (exit code $($p.ExitCode))." }
    $cfExe = @("${env:ProgramFiles(x86)}\cloudflared\cloudflared.exe", "$env:ProgramFiles\cloudflared\cloudflared.exe") |
             Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $cfExe) { throw 'cloudflared.exe not found after installing.' }
    Invoke-Native $cfExe @('--version') | Out-Null
    Write-Ok "Installed $($script:LastNativeOutput -join ' ')"

    # Service command line, exactly as 'cloudflared service install <token>' would write it.
    # The token is never printed or logged; any cloudflared output is shown with it masked.
    $imagePath = "`"$cfExe`" tunnel run --token $CloudflareTunnelToken"
    $svcKey = 'HKLM:\SYSTEM\CurrentControlSet\Services\Cloudflared'
    function Get-MaskedOutput { $script:LastNativeOutput | ForEach-Object { $_.Replace($CloudflareTunnelToken, '<token>') } | Where-Object { $_.Trim() } }

    $markedMsg = ('Windows has the old cloudflared service "marked for deletion", so it can''t be changed or recreated yet. ' +
                  'Restart the server (or close every Services, Task Manager and Event Viewer window), then run this script again.')
    # A service that is half-removed carries DeleteFlag=1 until every handle to it is closed.
    if ((Get-ItemProperty -Path $svcKey -Name DeleteFlag -ErrorAction SilentlyContinue).DeleteFlag -eq 1) { throw $markedMsg }

    $svc = Get-Service cloudflared -ErrorAction SilentlyContinue
    if ($svc) {
        # Update the existing service in place (Cloudflare's documented way to change the token) - no uninstall needed.
        Write-Info 'Updating the existing cloudflared service with this tunnel token...'
        Stop-Service cloudflared -Force -ErrorAction SilentlyContinue
        Set-ItemProperty -Path $svcKey -Name ImagePath -Value $imagePath
    } else {
        Write-Info 'Installing the cloudflared service...'
        $rc = Invoke-Native $cfExe @('service', 'install', $CloudflareTunnelToken)
        if ($rc -ne 0 -or -not (Get-Service cloudflared -ErrorAction SilentlyContinue)) {
            $out = @(Get-MaskedOutput)
            foreach ($l in $out) { Write-Host "    cloudflared: $l" -ForegroundColor Yellow }
            if ($out -match 'marked for deletion') { throw $markedMsg }
            # Fall back to creating the service directly with the same command line.
            Write-Info "cloudflared's own service installer failed (exit code $rc); creating the service directly instead..."
            if (Test-Path $svcKey) { Set-ItemProperty -Path $svcKey -Name ImagePath -Value $imagePath }
            else { New-Service -Name 'Cloudflared' -DisplayName 'Cloudflared agent' -BinaryPathName $imagePath -StartupType Automatic | Out-Null }
        }
    }

    try { Set-Service cloudflared -StartupType Automatic -ErrorAction Stop }
    catch { if ($_.Exception.Message -match 'marked for deletion') { throw $markedMsg } else { throw } }
    Invoke-Native 'sc.exe' @('failure', 'cloudflared', 'reset=', '86400', 'actions=', 'restart/60000/restart/60000/restart/60000') | Out-Null
    try { Start-Service cloudflared -ErrorAction Stop } catch {
        if ($_.Exception.Message -match 'marked for deletion|1072') { throw $markedMsg }
        Add-Warning "The cloudflared service didn't start: $($_.Exception.Message)"
    }
    Start-Sleep -Seconds 8
    $svc = Get-Service cloudflared -ErrorAction SilentlyContinue
    if ($svc -and $svc.Status -eq 'Running') {
        Write-Ok 'cloudflared service running (outbound-only connection to Cloudflare; needs outbound TCP/UDP 7844 and TCP 443)'
        Write-Info 'In Cloudflare (Zero Trust > Networks > Tunnels) the tunnel should now show as Healthy.'
    } else {
        $evt = Get-WinEvent -FilterHashtable @{ LogName = 'Application'; StartTime = (Get-Date).AddMinutes(-5) } -MaxEvents 20 -ErrorAction SilentlyContinue |
               Where-Object { $_.ProviderName -match 'cloudflared' -or $_.Message -match 'cloudflared' } | Select-Object -First 3
        foreach ($e in $evt) { Write-Host "    Event log: $($e.Message.Replace($CloudflareTunnelToken, '<token>'))" -ForegroundColor Yellow }
        Add-Warning ("cloudflared isn't running (status: $(if ($svc) { $svc.Status } else { 'missing' })). Check the tunnel token " +
                     "(copy it again from Cloudflare) and that outbound TCP/UDP 7844 to Cloudflare is allowed, then run this script again.")
    }
} elseif ($cfExe -and (Get-Service cloudflared -ErrorAction SilentlyContinue)) {
    Write-Step 'Cloudflare Tunnel connector (cloudflared)'
    Invoke-Native $cfExe @('--version') | Out-Null
    Write-Ok "Already installed: $($script:LastNativeOutput -join ' '); service $((Get-Service cloudflared).Status). Re-run with -CloudflareTunnelToken to update it."
}

# =====================================================================================================  start + check
Write-Step 'Starting the website'
if (Test-Path $offline) { Remove-Item $offline -Force }
if ((Get-WebAppPoolState -Name $AppPoolName).Value -ne 'Started') { Start-WebAppPool -Name $AppPoolName }
else { Restart-WebAppPool -Name $AppPoolName }
if ((Get-WebsiteState -Name $SiteName).Value -ne 'Started') { Start-Website -Name $SiteName }
Start-Sleep -Seconds 5

# Try the names the binding should answer to. A host-name (SNI) binding only answers to its own name.
$portPart = $(if ($HttpsPort -ne 443) { ":$HttpsPort" } else { '' })
$checkHosts = if ($hostHeader) { @($HostName) } else { @('localhost', $env:COMPUTERNAME, $HostName) | Select-Object -Unique }
[Net.ServicePointManager]::ServerCertificateValidationCallback = { $true }   # local check only; cert may be self-signed
$status = $null; $detail = ''; $checkUrl = ''
foreach ($h in $checkHosts) {
    $checkUrl = "https://$h$portPart/"
    for ($i = 1; $i -le 3 -and -not $status; $i++) {
        try {
            $resp = Invoke-WebRequest -Uri $checkUrl -UseBasicParsing -UseDefaultCredentials -TimeoutSec 60
            $status = [int]$resp.StatusCode
        } catch {
            if ($_.Exception.Response) { $status = [int]$_.Exception.Response.StatusCode }
            else {
                $inner = $_.Exception.InnerException
                $detail = "$checkUrl -> $($_.Exception.Message)$(if ($inner) { ' ' + $inner.Message })"
                Start-Sleep -Seconds 4
            }
        }
    }
    if ($status) { break }
    Write-Info "No answer from $checkUrl"
}
[Net.ServicePointManager]::ServerCertificateValidationCallback = $null

if (-not $status) {
    # Couldn't even complete the HTTPS handshake: show what usually explains it.
    Write-Host '    Diagnostics:' -ForegroundColor Yellow
    Get-WebBinding | ForEach-Object {
        $site = if ($_.ItemXPath -match "@name='([^']+)'") { $Matches[1] } else { '?' }
        Write-Host ("      binding  {0,-20} {1,-6} {2}  sslFlags={3}" -f $site, $_.protocol, $_.bindingInformation, $_.sslFlags) -ForegroundColor Yellow
    }
    Invoke-Native 'netsh.exe' @('http', 'show', 'sslcert') | Out-Null
    $sslLines = $script:LastNativeOutput | Where-Object { $_ -match 'IP:port|Hostname:port|Certificate Hash' }
    if ($sslLines) { foreach ($l in $sslLines) { Write-Host "      sslcert  $($l.Trim())" -ForegroundColor Yellow } }
    else { Write-Host '      sslcert  (no HTTPS certificate bindings at all - the certificate is not attached)' -ForegroundColor Yellow }
    try {
        $ips = @(Resolve-DnsName $HostName -ErrorAction Stop | Where-Object { $_.IPAddress } | ForEach-Object { $_.IPAddress })
        $mine = @(Get-NetIPAddress -ErrorAction SilentlyContinue | ForEach-Object { $_.IPAddress })
        $local = @($ips | Where-Object { $mine -contains $_ -or $_ -like '127.*' })
        Write-Host "      DNS      $HostName -> $($ips -join ', ')$(if (-not $local) { '   (NOT this server: add an internal DNS record)' })" -ForegroundColor Yellow
    } catch { Write-Host "      DNS      $HostName doesn't resolve (add an internal DNS record)" -ForegroundColor Yellow }
}

switch ($status) {
    200     { Write-Ok "The site is up: $checkUrl answered 200 OK." }
    403     { Write-Ok "The site is up. It answered 'Access denied' because $env:USERDOMAIN\$env:USERNAME isn't in txtAppUser/txtAppAdmin, which is expected if you're not." }
    401     { Write-Ok 'The site is up (401 from this server is normal: Windows blocks sign-in to itself by name). Test from a workstation.' }
    default {
        Add-Warning "The site didn't respond as expected (status: $status $detail)."
        $evt = Get-WinEvent -FilterHashtable @{ LogName = 'Application'; ProviderName = 'IIS AspNetCore Module V2'; StartTime = (Get-Date).AddMinutes(-10) } -MaxEvents 3 -ErrorAction SilentlyContinue
        foreach ($e in $evt) { Write-Host "    Event log: $($e.Message)" -ForegroundColor Yellow }
        Write-Info "For details, set stdoutLogEnabled=""true"" in $InstallPath\web.config, browse to the site, then read $InstallPath\logs."
    }
}

# =====================================================================================================  AD groups
foreach ($g in 'txtAppUser', 'txtAppAdmin') {
    try { [void](New-Object System.Security.Principal.NTAccount($g)).Translate([System.Security.Principal.SecurityIdentifier]) }
    catch { Add-Warning "AD group '$g' wasn't found. Create it (or set different group names on the Settings page)." }
}

# =====================================================================================================  summary
Write-Step 'Done'
Write-Host @"
    Website:     https://$HostName$(if ($HttpsPort -ne 443) { ":$HttpsPort" })/
    Files:       $InstallPath
    App pool:    $AppPoolName (runs as $poolAccount)
    Database:    $SqlServer / $Database
    Install log: $logFile

    Next:
      1. Make sure staff can resolve $HostName (DNS) and add the site to the Local Intranet zone by group policy.
      2. Put yourself in txtAppAdmin, sign out of Windows and back in (or sign in with the local admin account), open
         the site -> Settings, choose the texting provider (Telnyx or VoIP.ms) and enter its details.
         Click 'Check the connection'.
      3. Let Telnyx reach the webhook through Cloudflare Tunnel (see "Cloudflare Tunnel" below and the README).
"@
# ---- Cloudflare settings to enter (printed so they can be copied exactly)
$pub = if ($PublicWebhookHost) { $PublicWebhookHost } else { '<your public webhook name, e.g. texthooks.yourfirm.com>' }
$telnyxIps = @(
    '192.76.120.128/29', '64.16.239.40/31', '64.16.239.49/32', '64.16.239.50/32', '64.16.239.67/32', '64.16.239.72/32',
    '64.16.239.73/32', '192.76.120.136/29', '64.16.239.99/32', '64.16.239.100/32', '64.16.239.114/31', '192.76.120.144/29',
    '64.16.239.2/31', '64.16.239.83/32', '64.16.239.84/32', '192.76.120.160/29', '185.246.41.0/29', '185.246.41.8/29',
    '185.246.41.16/29', '185.246.41.24/29', '103.115.244.0/29', '103.115.244.8/29', '103.115.244.32/29')
$waf = "(http.host eq `"$pub`" and (not ip.src in {$($telnyxIps -join ' ')} or not (http.request.method eq `"POST`" and http.request.uri.path eq `"/api/webhooks/telnyx`")))"
# VoIP.ms doesn't publish its callback IPs; its callbacks are GETs to /api/webhooks/voipms/<secret key>/...
$wafVoipMs = "(http.host eq `"$pub`" and not ((http.request.method eq `"POST`" and http.request.uri.path eq `"/api/webhooks/telnyx`" and ip.src in {$($telnyxIps -join ' ')}) or (http.request.method eq `"GET`" and http.request.uri.path eq `"/api/webhooks/voipms`")))"
Write-Host @"

    Cloudflare Tunnel (Telnyx -> Cloudflare -> this server; no inbound ports):
      Tunnel route (Cloudflare dashboard > Tunnels > your tunnel > published application route):
        Hostname:  $pub
        Path:      ^/api/webhooks/telnyx$        (VoIP.ms: ^/api/webhooks/(telnyx|voipms)$ )
        Service:   HTTPS  localhost:$HttpsPort
        Additional application settings > TLS:  No TLS Verify = ON$(if ($hostHeader) { "   Origin Server Name = $hostHeader" })$(if ($hostHeader) { "
        Additional application settings > HTTP: HTTP Host Header = $hostHeader" })
      WAF custom rule (Security > WAF > Custom rules), action Block, expression:
        Telnyx:   $waf
        VoIP.ms (also allows the VoIP.ms callback URL):
                  $wafVoipMs
      Telnyx messaging profile webhook URL:
        https://$pub/api/webhooks/telnyx
      VoIP.ms: the SMS URL Callback is shown on the app's Settings page once VoIP.ms is selected.
"@
"# Telnyx`r`n$waf`r`n`r`n# VoIP.ms (and Telnyx)`r`n$wafVoipMs" | Set-Content -Path (Join-Path (Split-Path $InstallPath -Parent) "$appSlug-cloudflare-waf-rule.txt") -Encoding ASCII
Write-Info "(The WAF expression was also saved to $(Join-Path (Split-Path $InstallPath -Parent) "$appSlug-cloudflare-waf-rule.txt") for copy/paste.)"

if ($warnings.Count -gt 0) {
    Write-Host ''
    Write-Host 'Warnings:' -ForegroundColor Yellow
    foreach ($w in $warnings) { Write-Host "  - $w" -ForegroundColor Yellow }
}
}
finally {
    Stop-Transcript | Out-Null
    Remove-Item $WorkDir -Recurse -Force -ErrorAction SilentlyContinue
}
