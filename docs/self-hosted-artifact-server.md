# Self-hosted Actions artifact results service

This fork adds a runner-host setting for redirecting JavaScript and Docker actions to a custom GitHub Actions Results API endpoint.

Set `ACTIONS_ARTIFACTS_RESULTS_URL_OVERRIDE` in the environment of the runner service to the externally reachable base URL, for example:

```sh
ACTIONS_ARTIFACTS_RESULTS_URL_OVERRIDE=https://artifacts.example.net/
```

Restart the runner after changing its service environment. Existing workflows and `actions/upload-artifact` steps do not need to change. The setting overrides only `ACTIONS_RESULTS_URL`; it does not replace GitHub's runtime token or reroute all GitHub Actions APIs.

The endpoint must implement the GitHub Actions Results API v4 protocol and support the exact artifact action version in use. This runner patch alone does not provide that service. Do not expose an untested artifact server directly to the public internet; use HTTPS and restrict access while validating compatibility.

When `actions/upload-artifact` runs with this override, the patched runner adds a **Browse and download artifacts** link to that step's summary. The link opens the local server's `/artifacts` page. These external files do not populate GitHub's built-in **Actions → Artifacts** collection; GitHub's public artifact REST API supports listing, downloading, retrieving, and deleting artifacts, but not registering a file stored on an external server.
