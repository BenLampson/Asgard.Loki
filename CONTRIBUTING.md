# Contributing to Asgard.Loki

Read [AGENTS.md](AGENTS.md) and the [architecture contract](docs/architecture.md) before changing public APIs or package boundaries. This repository contains .NET class libraries distributed as optional NuGet packages.

Use .NET 10 and C# 14. Keep namespaces under `Asgard`, use one type per C# file, Chinese XML documentation comments, UTF-8 and CRLF. Keep C# files within 400 lines. English is the primary documentation language; update the Chinese README when its information changes.

## Validate a change

```powershell
dotnet restore Asgard.Loki.slnx
dotnet build Asgard.Loki.slnx -c Release --no-restore -warnaserror
pwsh -File eng/Test.ps1
dotnet pack Asgard.Loki.slnx -c Release --no-build -o artifacts/packages
pwsh -File eng/Verify-PackageBoundaries.ps1
```

The test script requires tests in every test project except the reserved Video scaffold. Tests use xUnit v3, substitute HTTP handlers and `MemoryDistributedCache`. Unit tests must not require network, Redis or credentials. Report live-provider and Redis integration results separately.

Capability packages must remain independent. Put a provider in `Asgard.Loki.{Capability}.{Provider}` and reference only its own capability. Update architecture UML, package documentation and the support matrix with behavior changes. Preserve complete upstream data and LLM Session target binding. Do not add automatic generation retries or failover.

## Pull requests

Create a feature branch before editing code or documentation; maintainer branches use the `ben/` prefix. Submit a pull request into `main`. Direct pushes, force pushes and deletion of `main` are blocked, including for administrators. Both Windows and Linux CI checks must pass, the branch must be up to date, and review conversations must be resolved. No additional reviewer approval is currently required. Merge with squash.

Keep changes focused and explain the public behavior and validation. Never include secrets, private prompts or generated user media in examples, issues, tests or logs. Follow [SECURITY.md](SECURITY.md) when reporting a vulnerability.

Contributions are provided under this repository's [Apache-2.0 license](LICENSE).
