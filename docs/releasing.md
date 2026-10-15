# Releasing NuGet packages

All ten packages share the version in `src/Directory.Build.props`. The package IDs and allowed dependencies are checked by `eng/Verify-PackageBoundaries.ps1`. Each package includes its README, Apache-2.0 license expression, author and repository metadata.

## Prepare

1. Update the shared version and all affected support documentation. Treat public contract changes as compatibility decisions.
2. Run the commands in [CONTRIBUTING.md](../CONTRIBUTING.md). Require the Windows and Linux CI jobs to pass for the exact release commit.
3. Inspect the ten `.nupkg` and `.snupkg` files attached to that CI run. Confirm supported APIs, limitations, dependency boundaries and the declared license. Package names do not imply that an upstream account or model is available.
4. Record live-provider and Redis integration checks separately; unit tests do not establish real-account compatibility.

## Publish

Publishing is a separate maintainer action. This repository does not automatically publish on a push or tag. No NuGet credentials are stored in the repository or required for CI.

Before the first publication, the owner must configure a nuget.org account, reserve or verify access to the package IDs, and provide an appropriately scoped publishing credential or configure nuget.org trusted publishing. That account-side setup is not supplied by GitHub repository access.

Publish the validated CI packages as one version set. Publish abstractions first, then the entry and capability packages, then providers. Publish symbol packages alongside their matching packages. NuGet versions are immutable: fix a bad release with a new version and unlist an affected version when appropriate.

Create an annotated `v{version}` tag and GitHub release only after deciding to release. Include package versions, supported capabilities, compatibility changes, validation and limitations. Do not describe a scaffold or local protocol test as a live upstream implementation.
