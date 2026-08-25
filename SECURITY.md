# Security Policy

## Reporting a vulnerability

Please **do not** open a public issue for security vulnerabilities.

Use [GitHub's private security advisory reporting](https://github.com/pirminbleiker/twincat-ads-mcp/security/advisories/new)
for this repository. That opens a private conversation with the
maintainer and lets you attach details/PoC without exposing them publicly
before a fix is out.

## Scope notes specific to this project

- `beckhoff_add_route` talks to the Windows Credential Manager (via
  CredUI) to read/store PLC login credentials. Credentials are never
  written to `appsettings.json`, logs, or the MCP's JSON-RPC responses —
  see [docs/tools-reference.md](docs/tools-reference.md#beckhoff_add_route--registering-a-backroute-windows-only)
  for the exact flow. If you find a path where a credential leaks through
  any of those channels, that's a vulnerability report, not a bug report.
- This server accepts read/write access to a running PLC. If you find a
  way for a tool call to affect a target beyond what its documented
  parameters describe (e.g. path traversal into unrelated ADS ports,
  unbounded/unauthenticated write access), please report it the same way.

## Supported versions

Only the latest release is supported. There is no LTS branch.
