# Self-hosted Actions artifact results service

This fork adds a runner-host setting for redirecting JavaScript and Docker actions to a custom GitHub Actions Results API endpoint.

Set `ACTIONS_ARTIFACTS_RESULTS_URL_OVERRIDE` in the environment of the runner service to the externally reachable base URL, for example:

```sh
ACTIONS_ARTIFACTS_RESULTS_URL_OVERRIDE=https://artifacts.example.net/
```

Restart the runner after changing its service environment. Existing workflows and `actions/upload-artifact` steps do not need to change. The setting overrides only `ACTIONS_RESULTS_URL`; it does not replace GitHub's runtime token or reroute all GitHub Actions APIs.

The endpoint must implement the GitHub Actions Results API v4 protocol and support the exact artifact action version in use. This runner patch alone does not provide that service. Do not expose an untested artifact server directly to the public internet; use HTTPS and restrict access while validating compatibility.

When `actions/upload-artifact` runs with this override, the patched runner adds an **Artifacts** table to that step's GitHub summary using the `Name`, `Size`, and `Digest` columns. The row uses the action's `artifact-url` and `artifact-digest` outputs, and takes the uploaded archive size from the action's final-size log line. The external files still do not populate GitHub's built-in **Actions → Artifacts** collection, so the configured Results service must make the generated artifact URL reachable for the row's download link to work.


## Publishing to a GitHub Release

When running **Runner CD** manually from GitHub Actions, use the **push_to_release** checkbox to control publishing. Leave it unchecked to build and upload workflow artifacts without creating a GitHub Release; check it to create the release and publish the platform packages. Pushes that update `releaseVersion` on `main` or a `releases/*` branch continue to publish automatically.
