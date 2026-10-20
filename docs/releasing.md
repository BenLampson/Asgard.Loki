# Releasing NuGet packages

All thirty-six packages share the version in `src/Directory.Build.props`. The package IDs and allowed dependencies are checked by `eng/Verify-PackageBoundaries.ps1`. Each package includes its README, Apache-2.0 license expression, author and repository metadata.

## Prepare

1. Update the shared version and all affected support documentation. Treat public contract changes as compatibility decisions.
2. Run the commands in [CONTRIBUTING.md](../CONTRIBUTING.md). Require the Windows and Linux CI jobs to pass for the exact release commit.
3. Inspect the thirty-six `.nupkg` and `.snupkg` files attached to that CI run. Confirm supported APIs, limitations, dependency boundaries and the declared license. Package names do not imply that an upstream account or model is available.
4. Record live-provider and Redis integration checks separately; unit tests do not establish real-account compatibility.

## Publish

Publishing is a separate maintainer action. This repository does not automatically publish on a push or tag. The manually dispatched [Publish NuGet workflow](../.github/workflows/publish.yml) defaults to `verify`, which authenticates without uploading packages. No long-lived NuGet API key is stored in the repository or required for CI.

The reusable [CI workflow](../.github/workflows/ci.yml) runs for pull requests into `main` or as part of this explicitly invoked release workflow. It has no push trigger or standalone manual entry point. Day-to-day development checkpoints are validated locally; do not start GitHub Actions for each commit. PR and release validation both retain the Windows/Linux build, offline tests, pack and package checks. Creating a tag or GitHub release does not bypass the explicit NuGet mode/version confirmation.

## Trusted publishing configuration

The nuget.org policy must use repository owner `BenLampson`, repository `Asgard.Loki`, workflow filename `publish.yml`, and environment `nuget`. Select package owner `BenLampson`, allow new packages and new versions, and use glob `Asgard.Loki*` to include the entry package and extensions. The policy must be saved on nuget.org; GitHub settings cannot create it.

GitHub environment `nuget` accepts deployments only from branch `main` and defines the non-secret variable `NUGET_USER=BenLampson`. The job has `id-token: write` and uses the pinned official `NuGet/login` action to obtain a temporary API key. The repository allows that specific action revision in addition to GitHub-owned actions. When Dependabot updates it, update the repository action allowlist to match before merging.

Reference: [NuGet trusted publishing](https://learn.microsoft.com/en-us/nuget/nuget-org/trusted-publishing).

## Verify or publish

1. Open **Actions → Publish NuGet → Run workflow** and select branch `main`.
2. Keep mode `verify` to run Windows/Linux validation, verify all thirty-six package and symbol identities, and test OIDC login. This does not publish packages.
3. To release, explicitly select mode `publish` and enter the exact version from `src/Directory.Build.props`. A mismatch fails before login or publication.

The workflow reuses CI at the selected commit and downloads packages from that same run. Both platforms must pass before the environment job can authenticate. `eng/Publish-Packages.ps1` verifies the complete thirty-six-package set and publishes Abstractions and Infrastructure first, then the entry and capabilities, then providers. `dotnet nuget push` also uploads the matching symbol packages. Existing versions are skipped to allow recovery from a partially completed publication; versions are immutable, so changed contents require a new version. NuGet indexing and symbol validation may complete after uploads finish.

OIDC verification confirms identity and policy matching; it does not reserve package names or establish ownership of an existing package ID. Before the first publication, confirm the IDs are available or owned by the selected NuGet account. A successful local protocol test does not establish live-provider compatibility.

Create an annotated `v{version}` tag and GitHub release only after deciding to release. Include package versions, supported capabilities, compatibility changes, validation and limitations. Do not describe a scaffold or local protocol test as a live upstream implementation.
