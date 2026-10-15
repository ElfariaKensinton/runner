# Self-hosted Actions artifact results service

This fork adds a runner-host setting that routes `actions/upload-artifact` and `actions/download-artifact` to a custom GitHub Actions Results API endpoint. It deliberately leaves cache-enabled actions (including `actions/cache` and `actions/setup-node`'s built-in cache) on the runner-provided endpoint because cache service v2 also uses `ACTIONS_RESULTS_URL`.

Set `ACTIONS_ARTIFACTS_RESULTS_URL_OVERRIDE` in the environment of the runner service to the externally reachable base URL, for example:

```sh
ACTIONS_ARTIFACTS_RESULTS_URL_OVERRIDE=https://artifacts.example.net/
```

Restart the runner after changing its service environment. Existing workflows and `actions/upload-artifact` steps do not need to change. The setting overrides only `ACTIONS_RESULTS_URL`; it does not replace GitHub's runtime token or reroute all GitHub Actions APIs.

The endpoint must implement the GitHub Actions Results API v4 protocol and support the exact artifact action version in use. This runner patch alone does not provide that service. The override is applied only to the two official artifact actions, not to other JavaScript or Docker actions. Do not expose an untested artifact server directly to the public internet; use HTTPS and restrict access while validating compatibility.

When `actions/upload-artifact` runs with this override, the patched runner adds an **Artifacts** table to that step's GitHub summary with `Name`, `Size`, and `Digest` columns. The artifact name is plain bold text (not a link); the last column contains a download icon linking directly to `{configured base URL}/artifacts/{artifact-id}/download`. The runner reads the ID and digest from `GITHUB_OUTPUT` and takes the uploaded archive size from the action's final-size log line. The self-hosted service must implement that direct-download route and return the artifact archive; the Results API v4 protocol itself does not define a browser page or this HTTP download route. The action's `artifact-url` output points to GitHub's native artifact route and is not used.


## Publishing to a GitHub Release

When running **Runner CD** manually from GitHub Actions, use the **push_to_release** checkbox to control publishing. Leave it unchecked to build and upload workflow artifacts without creating a GitHub Release; check it to create the release and publish the platform packages. Pushes that update `releaseVersion` on `main` or a `releases/*` branch continue to publish automatically.
