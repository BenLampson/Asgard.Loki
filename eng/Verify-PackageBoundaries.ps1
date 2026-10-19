param(
    [string]$PackageDirectory = (Join-Path $PSScriptRoot '../artifacts/packages')
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem

# 该检查独立于单元测试，验证实际 NuGet 包而非仅检查项目声明。
$repositoryRoot = Split-Path -Parent $PSScriptRoot
[xml]$libraryProperties = Get-Content -LiteralPath (Join-Path $repositoryRoot 'src/Directory.Build.props') -Raw
$version = [string]$libraryProperties.Project.PropertyGroup.Version
$expectedDependencies = [ordered]@{
    'Asgard.Loki.Abstractions' = @()
    'Asgard.Loki' = @('Asgard.Loki.Abstractions')
    'Asgard.Loki.LLM' = @('Asgard.Loki.Abstractions', 'Microsoft.Extensions.Caching.Abstractions')
    'Asgard.Loki.Audio' = @('Asgard.Loki.Abstractions')
    'Asgard.Loki.Image' = @('Asgard.Loki.Abstractions')
    'Asgard.Loki.Embedding' = @('Asgard.Loki.Abstractions')
    'Asgard.Loki.Rerank' = @('Asgard.Loki.Abstractions')
    'Asgard.Loki.Embedding.OpenAI' = @('Asgard.Loki.Embedding', 'Microsoft.Extensions.Http')
    'Asgard.Loki.Embedding.Google' = @('Asgard.Loki.Embedding', 'Microsoft.Extensions.Http')
    'Asgard.Loki.Embedding.Ollama' = @('Asgard.Loki.Embedding', 'Microsoft.Extensions.Http')
    'Asgard.Loki.Embedding.Jina' = @('Asgard.Loki.Embedding', 'Microsoft.Extensions.Http')
    'Asgard.Loki.Rerank.Cohere' = @('Asgard.Loki.Rerank', 'Microsoft.Extensions.Http')
    'Asgard.Loki.Rerank.Jina' = @('Asgard.Loki.Rerank', 'Microsoft.Extensions.Http')
    'Asgard.Loki.Rerank.Ali' = @('Asgard.Loki.Rerank', 'Microsoft.Extensions.Http')
    'Asgard.Loki.Video' = @('Asgard.Loki.Abstractions')
    'Asgard.Loki.LLM.Volcengine' = @('Asgard.Loki.LLM', 'Microsoft.Extensions.Http')
    'Asgard.Loki.LLM.OpenAI' = @('Asgard.Loki.LLM', 'Microsoft.Extensions.Http')
    'Asgard.Loki.LLM.Anthropic' = @('Asgard.Loki.LLM', 'Microsoft.Extensions.Http')
    'Asgard.Loki.LLM.Google' = @('Asgard.Loki.LLM', 'Microsoft.Extensions.Http')
    'Asgard.Loki.LLM.Conversion' = @('Asgard.Loki.LLM')
    'Asgard.Loki.Audio.Volcengine' = @('Asgard.Loki.Audio', 'Microsoft.Extensions.Http')
    'Asgard.Loki.Image.OpenAI' = @('Asgard.Loki.Image', 'Microsoft.Extensions.Http')
}

foreach ($packageId in $expectedDependencies.Keys) {
    $packagePath = Join-Path $PackageDirectory "$packageId.$version.nupkg"
    if (-not (Test-Path -LiteralPath $packagePath)) { throw "Missing package: $packagePath" }
    $archive = [System.IO.Compression.ZipFile]::OpenRead((Resolve-Path -LiteralPath $packagePath).Path)
    try {
        $manifest = @($archive.Entries | Where-Object { $_.FullName.EndsWith('.nuspec') })
        if ($manifest.Count -ne 1) { throw "Invalid nuspec count: $packageId" }
        $reader = [System.IO.StreamReader]::new($manifest[0].Open())
        try { [xml]$nuspec = $reader.ReadToEnd() } finally { $reader.Dispose() }
        $actual = @($nuspec.SelectNodes("//*[local-name()='dependency']") |
            ForEach-Object { $_.GetAttribute('id') } | Sort-Object -Unique)
        $expected = @($expectedDependencies[$packageId] | Sort-Object -Unique)
        if (($actual -join '|') -ne ($expected -join '|')) {
            throw "Unexpected dependencies for ${packageId}: $($actual -join ', ')"
        }
        if ($null -eq $archive.GetEntry('README.md')) { throw "Missing package README: $packageId" }
        if ($null -eq $archive.GetEntry('LICENSE') -or $null -eq $archive.GetEntry('NOTICE')) {
            throw "Missing license or notice file: $packageId"
        }
        if ($null -eq $archive.GetEntry("lib/net10.0/$packageId.dll")) { throw "Missing assembly: $packageId" }
        $metadata = $nuspec.SelectSingleNode("//*[local-name()='metadata']")
        $license = $metadata.SelectSingleNode("*[local-name()='license']")
        if ($null -eq $license -or $license.GetAttribute('type') -ne 'expression' -or $license.InnerText -ne 'Apache-2.0') {
            throw "Invalid license metadata: $packageId"
        }
        $repository = $metadata.SelectSingleNode("*[local-name()='repository']")
        if ($null -eq $repository -or $repository.GetAttribute('type') -ne 'git' -or
            $repository.GetAttribute('url') -ne 'https://github.com/BenLampson/Asgard.Loki') {
            throw "Invalid repository metadata: $packageId"
        }
        $authors = $metadata.SelectSingleNode("*[local-name()='authors']")
        if ($null -eq $authors -or $authors.InnerText -ne 'BenLampson') { throw "Invalid author: $packageId" }
        Write-Output "$packageId -> $($actual -join ', ')"
    }
    finally { $archive.Dispose() }
}

Write-Output "Verified $($expectedDependencies.Count) package boundaries for $version."
