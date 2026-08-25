# Contributing

Thanks for considering a contribution. This is a small, single-maintainer
project — keep that in mind for how much process to expect.

## Before you start

For anything beyond a small fix, please open an issue first (use the
[feature request template](.github/ISSUE_TEMPLATE/feature_request.md) for
new tools/config, [bug report](.github/ISSUE_TEMPLATE/bug_report.md) for
defects) so we can agree on the approach before you invest time.

Found a security issue? See [SECURITY.md](SECURITY.md) instead — don't
open a public issue for that.

## Development setup

Requirements: .NET 8 SDK, Windows x64 (the project only builds/runs there
— see [docs/getting-started.md](docs/getting-started.md#requirements)).

```powershell
cd src/BeckhoffMcp.Server
dotnet build -c Release
dotnet publish -c Release -o publish   # single-file exe, for manual testing
```

There's no automated test suite (see [docs/getting-started.md](docs/getting-started.md#4-verify))
— verify manually against a real or simulated ADS target using an MCP
client, or via `beckhoff-mcp.exe` directly over stdio.

## Making a change

- Follow the existing style within the file you're touching (this codebase
  doesn't use a formatter/analyzer config beyond what `dotnet build`
  enforces).
- New tool → add it under `Tools/`, register the `[McpServerTool(Name =
  "beckhoff_...")]` attribute, and document it in
  [docs/tools-reference.md](docs/tools-reference.md).
- New/changed config key → document it in
  [docs/configuration.md](docs/configuration.md).
- Keep the docs in sync with the code in the same PR — a past cleanup had
  to fix several places where they'd drifted apart; the
  [PR template](.github/pull_request_template.md) has a checklist for
  this.

## Submitting

Open a PR against `main`. `.github/workflows/build.yml` runs a build +
single-file publish sanity check on every PR — make sure it's green.

## License

By contributing, you agree that your contributions will be licensed under
this project's [MIT License](LICENSE).
