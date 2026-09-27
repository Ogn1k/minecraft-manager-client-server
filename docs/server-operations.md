# Minecraft Manager Server Operations

## Local development

Set configuration through environment variables or .NET user secrets; `appsettings.json` deliberately contains no credentials.

```powershell
dotnet user-secrets set --project src/MinecraftManager.Server.Api "ConnectionStrings:Database" "Host=localhost;Database=minecraft_manager;Username=minecraft_manager;Password=..."
dotnet user-secrets set --project src/MinecraftManager.Server.Api "Authentication:SigningKey" "a-long-random-development-key"
dotnet user-secrets set --project src/MinecraftManager.Server.Api "Bootstrap:Token" "a-separate-long-one-time-bootstrap-token"
dotnet user-secrets set --project src/MinecraftManager.Server.Api "ObjectStorage:AccessKey" "..."
dotnet user-secrets set --project src/MinecraftManager.Server.Api "ObjectStorage:SecretKey" "..."
dotnet ef database update --project src/MinecraftManager.Server.Infrastructure --startup-project src/MinecraftManager.Server.Api
dotnet run --project src/MinecraftManager.Server.Api
```

Create the configured private object-storage bucket before uploading content. The service does not create or make buckets public automatically.
The first owner bootstrap request must present the configured one-time bootstrap token. Remove the setting after the owner exists; subsequent bootstrap requests are rejected by database state.

## Docker Compose

Copy `deploy/server.env.example` to a file outside source control, replace every value, install the Caddy local CA on clients (or configure a publicly trusted hostname), and run:

```text
docker compose --env-file <secret-env-file> -f docker-compose.server.yml up --build -d
```

Apply EF migrations as an explicit one-off release step before bringing up a new API version. Never run concurrent automatic migrations from API replicas.

## Network and TLS

Expose only the reverse proxy. Keep PostgreSQL and MinIO on the private container/LAN network. For LAN deployments prefer stable DNS and a private CA installed into each client OS trust store. Never disable client certificate verification. Permit WebSocket upgrades for `/hubs/client-events`.

## Backup and restore

Back up PostgreSQL and object storage together, recording a common timestamp. Use `pg_dump`/provider snapshots plus bucket versioning or provider replication. Restore both into an isolated network, apply only expected migrations, verify every published `file_blobs.storage_key` exists with the expected length, and smoke-test manifest plus download authorization before declaring the backup valid.

## Health and incidents

- `/health/live` checks the process only.
- `/health/ready` checks required server dependencies.
- Alert on authentication failures, outbox age/backlog, storage/DB errors, import failures, and stale deployments.
- Revoke a compromised machine or administrator/API credential immediately. Short-lived access tokens expire naturally; rotating refresh families prevent continued renewal.
- Logs must not contain authorization/cookie headers, registration/refresh credentials, upload bodies, presigned URLs, or client filesystem paths.
