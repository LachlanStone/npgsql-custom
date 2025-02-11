
###################
## Setup PostgreSQL
###################

Write-Host Enabling PostgreSQL prepared transactions...
Add-Content 'C:\Program Files\PostgreSQL\10\data\postgresql.conf' "`nmax_prepared_transactions = 10"

Write-Host Enabling PostgreSQL SSL...
Add-Content 'C:\Program Files\PostgreSQL\10\data\postgresql.conf' "`nssl = true"
Copy-Item .build\server.* "C:\Program Files\PostgreSQL\10\data"

$POSTGIS_EXE='postgis-bundle-pg10x64-setup-2.5.1-1.exe'
echo $POSTGIS_EXE

Write-Host Enabling PostGIS...
If (!(Test-Path $POSTGIS_EXE)) {
  Write-Host Downloading PostGIS...
  (New-Object Net.WebClient).DownloadFile("https://ftp.postgresql.org/pub/postgis/pg10/v2.5.3/win64/postgis-bundle-pg10x64-setup-2.5.3-2.exe", "$POSTGIS_EXE")
}
iex ".\$POSTGIS_EXE /S /D='C:\Program Files\PostgreSQL\10'"

########################
## Set version variables
########################

Set-Variable -Name TruncatedSha1 -Value $env:APPVEYOR_REPO_COMMIT.subString(0, 9)

if ($env:APPVEYOR_REPO_TAG -eq 'true' -and $env:APPVEYOR_REPO_TAG_NAME -match '^v\d+\.\d+\.\d+')
{
    Write-Host "Release tag detected ($env:APPVEYOR_REPO_TAG_NAME), no version suffix is set."
    Set-AppveyorBuildVariable -Name deploy_github_release -Value true
}
#elseif (Test-Path env:APPVEYOR_PULL_REQUEST_NUMBER)
#{
#    Set-AppveyorBuildVariable -Name deploy_myget_unstable -Value true
#    Set-Variable -Name VersionSuffix -Value "pr$($env:APPVEYOR_PULL_REQUEST_NUMBER).$($env:APPVEYOR_BUILD_NUMBER)+sha.$TruncatedSha1"
#    Write-Host "Pull request detected (#$env:APPVEYOR_PULL_REQUEST_NUMBER), setting version suffix to $VersionSuffix"
#    Set-AppveyorBuildVariable -Name VersionSuffix -Value $VersionSuffix
#}
else
{
    # Set which myget feed we deploy to
    if ($env:APPVEYOR_REPO_BRANCH.StartsWith("hotfix/")) {
        Set-AppveyorBuildVariable -Name deploy_myget_stable -Value true
    } else {
        Set-AppveyorBuildVariable -Name deploy_myget_unstable -Value true
    }

    Set-Variable -Name VersionSuffix -Value "ci.$($env:APPVEYOR_BUILD_NUMBER)+sha.$TruncatedSha1"
    Write-Host "Setting version suffix to $VersionSuffix"
    Set-AppveyorBuildVariable -Name VersionSuffix -Value $VersionSuffix
}
