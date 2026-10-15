param(
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$projects = @(Get-ChildItem -LiteralPath (Join-Path $repositoryRoot 'tests') -Recurse -Filter '*.Tests.csproj' |
    Sort-Object FullName)
if ($projects.Count -eq 0) { throw 'No test projects found.' }

foreach ($project in $projects) {
    $arguments = @('test', '--project', $project.FullName, '-c', $Configuration, '--no-build')
    # 仅允许预留的 Video 测试项目为空，其余测试项目必须实际发现测试。
    if ($project.Name -eq 'Asgard.Loki.Video.Tests.csproj') {
        $arguments += @('--ignore-exit-code', '8')
    }
    & dotnet @arguments
    if ($LASTEXITCODE -ne 0) { throw "Tests failed: $($project.Name) (exit $LASTEXITCODE)" }
}
