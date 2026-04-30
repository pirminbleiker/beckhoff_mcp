# Embedded ADS Router

Standalone TCP/IP ADS router for systems without TwinCAT installed. Hosts the
Beckhoff `AmsTcpIpRouter` (NuGet `Beckhoff.TwinCAT.Ads.TcpRouter`) plus the
`AdsRouterServer` (AmsPort 1) and `SystemServiceServer` (AmsPort 10000) so that
`pyads` clients can both communicate AND manage routes at runtime.

## Build

Requires .NET 8 SDK.

```bash
dotnet publish -c Release -o publish
```

Single-file self-contained `AdsRouter.exe` is produced under `publish/`.

## Run

```bash
./publish/AdsRouter.exe
```

Listens on `127.0.0.1:48898`.

## Configuration

Edit `appsettings.json` (lives next to the exe). Important fields:

- `AmsRouter.NetId` — local AMS net id (any unique 6-byte address; `127.0.0.1.1.1` is fine)
- `AmsRouter.TcpPort` — defaults to 48898
- `AmsRouter.RemoteConnections[]` — pre-configured PLC routes (optional; pyads can add at runtime)

Config can also be supplied via environment variables prefixed with `ADS_`,
e.g. `ADS_AmsRouter__NetId=10.0.0.1.1.1`.

## Adding routes

Either at startup via `appsettings.json`:

```json
"RemoteConnections": [
  {
    "Name": "MyPLC",
    "Address": "192.168.1.50",
    "NetId": "192.168.1.50.1.1",
    "Type": "TCP_IP"
  }
]
```

Or at runtime via `pyads.add_route_to_plc(...)`.
