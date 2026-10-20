param(
    [Parameter(Mandatory)]
    [string]$Version,
    [string]$PackageDirectory = (Join-Path $PSScriptRoot '../artifacts/packages'),
    [switch]$Publish
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$repositoryRoot = Split-Path -Parent $PSScriptRoot
[xml]$properties = Get-Content -LiteralPath (Join-Path $repositoryRoot 'src/Directory.Build.props') -Raw
if ($Version -cne [string]$properties.Project.PropertyGroup.Version) { throw 'Version must match src/Directory.Build.props.' }

# 先检查完整产物集合，随后按照依赖方向发布；默认仅检查，不触发上传。
$packageIds = @(
    'Asgard.Loki.Abstractions',
    'Asgard.Loki.Infrastructure',
    'Asgard.Loki',
    'Asgard.Loki.LLM',
    'Asgard.Loki.Audio',
    'Asgard.Loki.Image',
    'Asgard.Loki.Embedding',
    'Asgard.Loki.Rerank',
    'Asgard.Loki.Video',
    'Asgard.Loki.LLM.Volcengine',
    'Asgard.Loki.LLM.OpenAI',
    'Asgard.Loki.LLM.Anthropic',
    'Asgard.Loki.LLM.Google',
    'Asgard.Loki.LLM.Ollama',
    'Asgard.Loki.LLM.Azure',
    'Asgard.Loki.LLM.GoogleVertex',
    'Asgard.Loki.LLM.AWS',
    'Asgard.Loki.LLM.Conversion',
    'Asgard.Loki.Audio.Volcengine',
    'Asgard.Loki.Video.Volcengine',
    'Asgard.Loki.Video.Ali',
    'Asgard.Loki.Video.Google',
    'Asgard.Loki.Video.GoogleVertex',
    'Asgard.Loki.Video.MiniMax',
    'Asgard.Loki.Video.Kling',
    'Asgard.Loki.Video.Vidu',
    'Asgard.Loki.Video.XAI',
    'Asgard.Loki.Video.Midjourney',
    'Asgard.Loki.Audio.OpenAI',
    'Asgard.Loki.Audio.MiniMax',
    'Asgard.Loki.Image.OpenAI',
    'Asgard.Loki.Embedding.OpenAI',
    'Asgard.Loki.Embedding.Google',
    'Asgard.Loki.Embedding.Ollama',
    'Asgard.Loki.Embedding.Jina',
    'Asgard.Loki.Rerank.Cohere',
    'Asgard.Loki.Rerank.Jina',
    'Asgard.Loki.Rerank.Ali',
    'Asgard.Loki.Rerank.SiliconFlow'
)
$expectedNames = @(foreach ($id in $packageIds) { "$id.$Version.nupkg"; "$id.$Version.snupkg" })
$actualNames = @(Get-ChildItem -LiteralPath $PackageDirectory -File |
    Where-Object { $_.Extension -in '.nupkg', '.snupkg' } | ForEach-Object Name)
if ((@($expectedNames | Sort-Object) -join '|') -cne (@($actualNames | Sort-Object) -join '|')) {
    throw "Expected exactly $($packageIds.Count) matching NuGet packages and symbol packages. Use a clean package directory."
}
foreach ($id in $packageIds) {
    foreach ($extension in @('nupkg', 'snupkg')) {
        $path = Join-Path $PackageDirectory "$id.$Version.$extension"
        $archive = [System.IO.Compression.ZipFile]::OpenRead((Resolve-Path -LiteralPath $path).Path)
        try {
            $manifests = @($archive.Entries | Where-Object { $_.FullName.EndsWith('.nuspec') })
            if ($manifests.Count -ne 1) { throw "Invalid package manifest: $path" }
            $reader = [System.IO.StreamReader]::new($manifests[0].Open())
            try { [xml]$manifest = $reader.ReadToEnd() } finally { $reader.Dispose() }
            $metadata = $manifest.SelectSingleNode("//*[local-name()='metadata']")
            if ($metadata.SelectSingleNode("*[local-name()='id']").InnerText -cne $id -or
                $metadata.SelectSingleNode("*[local-name()='version']").InnerText -cne $Version) {
                throw "Package identity or version mismatch: $path"
            }
        }
        finally { $archive.Dispose() }
    }
}
if (-not $Publish) {
    Write-Output "Validated $($packageIds.Count) packages and symbols for $Version; no upload requested."
    return
}
if ($env:GITHUB_ACTIONS -ne 'true' -or $env:GITHUB_EVENT_NAME -ne 'workflow_dispatch' -or
    $env:GITHUB_REF -ne 'refs/heads/main') { throw 'Publication is restricted to a manual GitHub Actions run on main.' }
if ([string]::IsNullOrWhiteSpace($env:NUGET_API_KEY)) { throw 'NuGet OIDC login did not provide a temporary API key.' }
foreach ($id in $packageIds) {
    $path = Join-Path $PackageDirectory "$id.$Version.nupkg"
    & dotnet nuget push $path --source 'https://api.nuget.org/v3/index.json' `
        --api-key $env:NUGET_API_KEY --symbol-api-key $env:NUGET_API_KEY --skip-duplicate
    if ($LASTEXITCODE -ne 0) { throw "Package publication failed: $id (exit $LASTEXITCODE)" }
}
