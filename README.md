# Minecraft Manager

Cross-platform Minecraft pack update manager with an integrated launcher. The application synchronizes explicitly managed files only, shows the complete update plan, requires confirmation before modification, and launches locally configured Minecraft runtimes with offline player profiles.

## Development

```powershell
$env:AVALONIA_TELEMETRY_OPTOUT = '1'
dotnet restore MinecraftManager.slnx
dotnet build MinecraftManager.slnx
dotnet test MinecraftManager.slnx
dotnet run --project src/MinecraftManager.App
```

Architecture is documented in [`docs/client-architecture.md`](docs/client-architecture.md), its [`docs/client-launcher-architecture.md`](docs/client-launcher-architecture.md) extension, and [`docs/server-architecture.md`](docs/server-architecture.md).

## Launcher

The **Launcher** tab is separate from pack **Updates**. Configure a Minecraft version, choose or create a local offline profile, repair the runtime explicitly, select a compatible Java installation, and press **PLAY** when readiness checks pass.

- Vanilla runtime metadata, libraries, assets, natives, and Java requirements are resolved from Mojang metadata and stored in a verified shared cache.
- Fabric and Quilt loader profile installation is supported through their HTTPS metadata services. Forge and NeoForge remain explicit unsupported configurations until their installer/processor pipeline is implemented; the UI does not advertise them.
- Offline profiles use Minecraft's deterministic `OfflinePlayer:<name>` UUID. They support single-player and compatible offline-mode servers only; they do not authenticate ownership, Realms, skins, or online-mode server access.
- The client never accepts executable paths, commands, player identities, JVM fragments, or environment variables from the managed server.
- Pack updates and runtime repair are separate. Neither operation silently runs when **PLAY** is clicked.

## Local pack format

Local sources use the same manifest and verification pipeline as remote sources:

```text
MyPack/
  manifest.json
  files/
    mods/example.jar
    config/example.json
```

The client can also open a `.7z` archive containing this exact structure. Choose
`7z archive` as the update source and select the archive file. The archive is
validated and expanded into the application cache before the normal review and
verified installation pipeline runs; 7-Zip does not need to be installed.

Manifest paths use `/`, remain relative to `files/`, declare SHA-256 and size, and must be inside an explicit managed scope. The pack source must not overlap the selected Minecraft installation. Files are staged and verified before the reviewed plan is applied.

## Safety and platform notes

- Updates never execute downloaded content and never apply without review and confirmation.
- Deletion is limited to files recorded in the prior managed ledger; saves, logs, screenshots, and common user settings are protected.
- Transaction journals and backups are stored in the per-user application-data directory. Interrupted mutations are offered for rollback at startup.
- Public remote sources and managed servers require HTTPS. Explicit private-LAN HTTP is available only for static, unauthenticated sources and remains insecure against local attackers.
- Managed credentials use Windows user-bound DPAPI storage. Managed mode fails closed on Linux until a Secret Service/libsecret adapter is installed; Local Folder and Static HTTP remain available.

## Server development

The server is a modular ASP.NET Core application backed by PostgreSQL and private S3-compatible storage. Configuration contains no committed secrets; set development values with user secrets as described in [`docs/server-operations.md`](docs/server-operations.md).

```powershell
dotnet restore src/MinecraftManager.Server.Api/MinecraftManager.Server.Api.csproj
dotnet build src/MinecraftManager.Server.Api/MinecraftManager.Server.Api.csproj
dotnet test tests/MinecraftManager.Server.UnitTests
dotnet test tests/MinecraftManager.Server.IntegrationTests
```

Run the EF migrations explicitly before starting against a new database. `docker-compose.server.yml` supplies PostgreSQL, MinIO, the API, and Caddy; copy the environment template outside source control and replace every placeholder first. The local pack-builder reads its administrator access token only from `MINECRAFT_MANAGER_ADMIN_TOKEN`.

## Publishing

Run `scripts/publish-client.ps1` to create self-contained output for `win-x64`, `win-arm64`, `linux-x64`, and `linux-arm64` under `artifacts/client/`. Packaging/signing installers is a separate release operation.
