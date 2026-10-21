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
    # 每个普通测试项目必须实际发现并执行测试。
    & dotnet @arguments
    if ($LASTEXITCODE -ne 0) { throw "Tests failed: $($project.Name) (exit $LASTEXITCODE)" }
}
