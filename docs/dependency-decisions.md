# Dependency Decision Log

A record of *why* each third-party dependency was adopted, and what was
checked before adopting it — not wired into CI, pure documentation. Useful
for anyone (including future maintainers) auditing this project's supply
chain, and doubles as prep material for EU Cyber Resilience Act -style
component due-diligence.

Full package-source audit performed 2026-08-25: single NuGet source
(`nuget.org`, no custom feeds), every direct and transitive package's
owner checked via the NuGet API, and signatures verified with `dotnet
nuget verify` for the ones that carry a code-signing certificate.

| Package (version) | Purpose | Source/Owner | Risk | Decision | Decided by | Date | Next review |
|---|---|---|---|---|---|---|---|
| `Beckhoff.TwinCAT.Ads`, `.AdsOverMqtt`, `.ConfigurationProviders`, `.SystemServer`, `.TcpRouter`, `.Abstractions`, `.Server`, `.SymbolicServer` (7.0.317 / 7.0.172) | Core ADS communication with the PLC | NuGet owner `Beckhoff`; **author-signed** by "Beckhoff Automation GmbH & Co. KG" (verified via `dotnet nuget verify`), plus nuget.org repository signature | low — vendor-signed | adopted, no change | Claude (session with Pirmin Bleiker) | 2026-08-25 | on major version bump |
| `ModelContextProtocol`, `ModelContextProtocol.Core` (1.4.1) | MCP server/tool framework | NuGet owner `ModelContextProtocol`, verified, 25M+ downloads | low | adopted, no change | Claude | 2026-08-25 | — |
| `MQTTnet`, `MQTTnet.Extensions.ManagedClient` (4.3.7.1207) | `MQTTnet` used directly in `DiscoveryTools.cs` for `beckhoff_discover`'s own broker peer-scan (separate from `AdsOverMqtt`'s internal use of the same library for the ADS tunnel — confirmed via `dotnet nuget why`, not a redundant reference). `.ManagedClient` is a pure transitive of `AdsOverMqtt`, not used directly. | NuGet owner `chkr1011`; **author-signed** by ".NET Foundation" (verified). Not enrolled in NuGet's reserved-namespace "verified" badge, which is cosmetic, not a trust signal. | low | adopted, no change | Claude | 2026-08-25 | — |
| `Microsoft.Extensions.*`, `System.*` (29 transitive packages) | Hosting, config, logging plumbing | All `Microsoft`/`aspnet`/`dotnetframework`, verified | low | adopted, no change | Claude | 2026-08-25 | — |

Note: several transitive `Microsoft.Extensions.*` packages resolve to
version 10.x instead of 8.x on this machine because the .NET 10 SDK is
installed locally alongside the net8.0 target — cosmetic, not a security
finding; building with the matching 8.0 SDK would pin them to 8.x.
