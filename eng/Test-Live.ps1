param(
    [string]$Configuration = 'Release',
    [ValidateSet('Smoke', 'Parameters', 'All')]
    [string]$Suite = 'Smoke',
    [ValidateSet('Structured', 'Tools', 'ToolChoice', 'Reasoning', 'Limits', 'Replay')]
    [string]$Scenario
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$names = @(
    'ASGARD_LOKI_TEST_LIVE',
    'ASGARD_LOKI_TEST_VOLCENGINE_LLM_API_KEY',
    'ASGARD_LOKI_TEST_VOLCENGINE_LLM_CHAT_MODEL',
    'ASGARD_LOKI_TEST_VOLCENGINE_LLM_RESPONSES_MODEL'
)
# 已启动的终端可能尚未继承用户变量；仅补全本项目四个变量，进程值优先。
if ($IsWindows) {
    foreach ($name in $names) {
        if ($null -eq [Environment]::GetEnvironmentVariable($name, 'Process')) {
            [Environment]::SetEnvironmentVariable($name, [Environment]::GetEnvironmentVariable($name, 'User'), 'Process')
        }
    }
}
$project = Join-Path $repositoryRoot 'tests/Asgard.Loki.LLM.Volcengine.IntegrationTests/Asgard.Loki.LLM.Volcengine.IntegrationTests.csproj'
& dotnet build $project -c $Configuration -p:UseSharedCompilation=false -warnaserror
if ($LASTEXITCODE -ne 0) { throw "Live test build failed (exit $LASTEXITCODE)." }
$runnerArguments = @('-parallelMode', 'none', '-showLiveOutput')
if ($Scenario) {
    if ($Suite -eq 'Smoke') { throw '-Scenario requires -Suite Parameters or -Suite All.' }
    $runnerArguments += @('-trait', "Scenario=$Scenario")
} elseif ($Suite -ne 'All') { $runnerArguments += @('-trait', "Suite=$Suite") }
& dotnet run --project $project -c $Configuration --no-build -- @runnerArguments
if ($LASTEXITCODE -ne 0) { throw "Live test failed (exit $LASTEXITCODE)." }
