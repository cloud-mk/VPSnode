[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Output,
    [string] $DotNet = 'dotnet',
    [string] $Wix,
    [string] $Version = '0.1.10'
)

$ErrorActionPreference = 'Stop'
if ($Version -notmatch '^\d{1,3}\.\d{1,3}\.\d{1,5}$') { throw 'Version must use three numeric parts.' }
$Output = [IO.Path]::GetFullPath($Output)
$payload = Join-Path $Output 'app'
if (Test-Path -LiteralPath $payload) {
    throw "Choose a fresh build output directory. Found $payload"
}
New-Item -ItemType Directory -Path $payload -Force | Out-Null

function Invoke-Native([string] $File, [string[]] $Arguments) {
    & $File @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$File returned exit code $LASTEXITCODE" }
}

foreach ($project in @('VPSnode.Cli', 'VPSnode.Desktop')) {
    $projectPath = Join-Path $PSScriptRoot "src/$project/$project.csproj"
    Invoke-Native $DotNet @('publish', $projectPath, '-c', 'Release', '-r', 'win-x64',
        '--self-contained', 'true', '-o', (Join-Path $payload 'DLL'), "-p:PackageEntryDirectory=$payload",
        "-p:Version=$Version", '-p:DebugType=None')
}
foreach ($entry in 'VPSnode.exe', 'vpsnode-cli.exe') {
    Remove-Item -LiteralPath (Join-Path $payload "DLL/$entry")
}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'README.md') -Destination (Join-Path $payload 'README.md')
foreach ($document in 'README.en.md', 'LICENSE', 'NOTICE', 'CHANGELOG.md') {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $document) -Destination (Join-Path $payload $document)
}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'THIRD-PARTY-NOTICES.md') -Destination (Join-Path $payload 'THIRD-PARTY-NOTICES.md')
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'licenses') -Destination $payload -Recurse
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'docs') -Destination $payload -Recurse
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'examples') -Destination $payload -Recurse
# Publish does not copy runtime-pack license files; include the notices for the exact resolved versions.
$deps = Get-Content -LiteralPath (Join-Path $payload 'DLL/VPSnode.deps.json') -Raw | ConvertFrom-Json -AsHashtable
$assets = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'src/VPSnode.Desktop/obj/project.assets.json') -Raw | ConvertFrom-Json -AsHashtable
foreach ($library in $deps.libraries.Keys | Where-Object { $_.StartsWith('runtimepack.') }) {
    $package = $library.Substring('runtimepack.'.Length).ToLowerInvariant()
    $folder = $assets.packageFolders.Keys | ForEach-Object { Join-Path $_ $package } | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
    if (!$folder) { throw "Runtime package notices not found: $library" }
    $destination = Join-Path $payload ('licenses/' + $package.Replace('/', '-'))
    New-Item -ItemType Directory -Path $destination -Force | Out-Null
    Get-ChildItem -LiteralPath $folder -File | Where-Object Name -match '^(LICENSE|THIRD-PARTY-NOTICES)(\.|$)' |
        Copy-Item -Destination $destination
}
$zip = Join-Path $Output "VPSnode-$Version-win-x64.zip"
[IO.Compression.ZipFile]::CreateFromDirectory($payload, $zip, [IO.Compression.CompressionLevel]::Optimal, $false)

if ($Wix) {
    $fileList = Join-Path $Output 'PublishedFiles.wxs'
    $settings = [Xml.XmlWriterSettings]::new()
    $settings.Indent = $true
    $writer = [Xml.XmlWriter]::Create($fileList, $settings)
    $ns = 'http://wixtoolset.org/schemas/v4/wxs'
    function Write-WixElement([string] $Tag, [hashtable] $Attributes) {
        $writer.WriteStartElement($Tag, $ns)
        foreach ($name in $Attributes.Keys) { $writer.WriteAttributeString($name, [string]$Attributes[$name]) }
    }
    $components = [Collections.Generic.List[string]]::new()
    function Component-Guid([string] $Key) {
        $bytes = [Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes("VPSnode/MSI/$Key"))
        return [Guid]::new([byte[]]($bytes[0..15])).ToString('D')
    }
    function Write-Payload([string] $Folder) {
        $rel = [IO.Path]::GetRelativePath($payload, $Folder)
        $dirHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($rel))).Substring(0, 30)
        foreach ($file in Get-ChildItem -LiteralPath $Folder -File | Sort-Object Name) {
            $source = [IO.Path]::GetRelativePath($payload, $file.FullName)
            $id = 'f' + [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($source))).Substring(0, 30)
            Write-WixElement Component @{ Id = $id; Guid = (Component-Guid $id) }
            Write-WixElement File @{ Id = "p$id"; Source = $file.FullName }
            $writer.WriteEndElement()
            Write-WixElement RegistryValue @{ Root = 'HKCU'; Key = 'Software\VPSnode\Installer\Files'; Name = $id; Type = 'integer'; Value = 1; KeyPath = 'yes' }
            $writer.WriteEndElement()
            $writer.WriteEndElement()
            $components.Add($id)
        }
        $clean = "r$dirHash"
        Write-WixElement Component @{ Id = $clean; Guid = (Component-Guid $clean) }
        Write-WixElement RemoveFolder @{ Id = "c$dirHash"; On = 'uninstall' }
        $writer.WriteEndElement()
        Write-WixElement RegistryValue @{ Root = 'HKCU'; Key = 'Software\VPSnode\Installer\Directories'; Name = $clean; Type = 'integer'; Value = 1; KeyPath = 'yes' }
        $writer.WriteEndElement()
        $writer.WriteEndElement()
        $components.Add($clean)
        foreach ($child in Get-ChildItem -LiteralPath $Folder -Directory | Sort-Object Name) {
            $relative = [IO.Path]::GetRelativePath($payload, $child.FullName)
            $id = 'd' + [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($relative))).Substring(0, 30)
            Write-WixElement Directory @{ Id = $id; Name = $child.Name }
            Write-Payload $child.FullName
            $writer.WriteEndElement()
        }
    }
    try {
        Write-WixElement Wix @{}
        Write-WixElement Fragment @{}
        Write-WixElement DirectoryRef @{ Id = 'INSTALLFOLDER' }
        Write-Payload $payload
        $writer.WriteEndElement()
        Write-WixElement ComponentGroup @{ Id = 'PublishedFiles' }
        foreach ($component in $components) {
            Write-WixElement ComponentRef @{ Id = $component }
            $writer.WriteEndElement()
        }
        $writer.WriteEndElement()
        $writer.WriteEndElement()
        $writer.WriteEndElement()
    } finally { $writer.Dispose() }
    $msi = Join-Path $Output "VPSnode-$Version-win-x64.msi"
    Invoke-Native $Wix @('build', '-arch', 'x64', '-d', "Version=$Version", '-d', "IconPath=$(Join-Path $PSScriptRoot 'src/VPSnode.Desktop/Assets/VPSnode.ico')", '-out', $msi,
        (Join-Path $PSScriptRoot 'installer/VPSnode.wxs'), $fileList)
}
Get-ChildItem -LiteralPath $Output -File | Where-Object Extension -in '.msi', '.zip' | Sort-Object Name |
    ForEach-Object { "$((Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant())  $($_.Name)" } |
    Set-Content -LiteralPath (Join-Path $Output 'SHA256SUMS.txt') -Encoding ascii
