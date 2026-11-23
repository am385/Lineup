# Publishing Lineup

Maintainer workflows for development builds, stable images and Windows release packages.

- [Publishing a Development Build](#publishing-a-development-build)
- [Publishing a Release](#publishing-a-release)

## Publishing a Development Build

Maintainers can open **Actions → Publish Lineup Dev → Run workflow**, select any branch, and start a development publication. The selected revision is tested before the workflow publishes the moving development images to Docker Hub and GitHub Container Registry:

```text
am385/lineup:dev
ghcr.io/am385/lineup:dev
```

Each run also publishes a traceable Docker tag in the form `dev-<run-number>-<short-sha>`. Development binaries use a version derived from `Directory.Build.props`, such as `3.0.0-beta-dev.42+abc1234`, without changing the release version in source control.

The workflow creates or updates the **Lineup Dev** GitHub prerelease under the moving `dev` tag. Its `Lineup-dev-win-x64.zip`, `Lineup-dev-win-arm64.zip`, and checksum assets are replaced on each successful publication. The workflow run retains versioned Windows packages and checksums as a 30-day Actions artifact, and the prerelease notes identify the exact source ref, commit, version, run, and traceable Docker tag.

Starting a newer development publication cancels an older in-progress development run so the older revision cannot overwrite the newer requested `dev` channel. Development builds are mutable and intended for testing; use a stable versioned release for production installations. The workflow requires the same `DOCKERHUB_USERNAME` and `DOCKERHUB_TOKEN` repository secrets as stable publication, while GHCR uses the workflow's built-in GitHub token.

## Publishing a Release

`Directory.Build.props` is the single source for Lineup's release version. Before publishing, update its `<Version>` value to the next stable semantic version.

Maintainers can then open **Actions → Publish Lineup Release → Run workflow** and select the commit or branch to publish. The workflow reads the application version, tests the selected revision, and publishes both the full version and major/minor tags to Docker Hub and GitHub Container Registry:

```text
am385/lineup:3.0.0
am385/lineup:3.0
ghcr.io/am385/lineup:3.0.0
ghcr.io/am385/lineup:3.0
```

The workflow also builds self-contained x64 and ARM64 Windows ZIPs with the matching pinned Jellyfin FFmpeg distribution. A manual run makes the ZIPs and `SHA256SUMS.txt` available as a workflow artifact but does not create a Git tag or GitHub Release. A pushed stable tag such as `v3.0.0` creates or updates the matching GitHub Release and attaches those Windows assets. Tag publication stops with an explicit error if the tag does not exactly match `v` followed by the version in `Directory.Build.props`.

Select **Also update the latest tag** only for the current stable release. Manual publication requires the `DOCKERHUB_USERNAME` and `DOCKERHUB_TOKEN` repository secrets; GHCR uses the workflow's built-in GitHub token. Manual publication does not create a Git tag or GitHub Release.

## Related documentation

[Project overview](../README.md) | [Contributor standards](../CONTRIBUTING.md)
