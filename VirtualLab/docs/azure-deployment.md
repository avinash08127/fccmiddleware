# Virtual Lab Azure Deployment

This guide defines the deployment contract for `VL-4.2`: the Angular UI deploys to Azure Static Web Apps, the ASP.NET Core API deploys to Azure Web App, and each artifact can move independently as long as its environment configuration is updated.

## Deployment boundaries

- UI artifact: `VirtualLab/ui/virtual-lab/dist/virtual-lab/browser`
- API artifact: `dotnet publish` output from `VirtualLab/src/VirtualLab.Api`
- UI-to-API dependency: runtime configuration only
- API-to-database dependency: `VirtualLab__Persistence__*` app settings only

The UI build no longer hardcodes an Azure API hostname. Instead it reads `assets/config/runtime-config.json` at startup. That keeps the frontend build reproducible and lets you repoint the same built artifact to another API deployment without rebuilding Angular.

## Frontend: Azure Static Web Apps

### Build command

```bash
cd VirtualLab/ui/virtual-lab
npm ci
npm run build:azure
```

Publish the generated `dist/virtual-lab/browser` folder to Azure Static Web Apps.

### Required runtime config

Edit `public/assets/config/runtime-config.json` before deployment, or replace the built `assets/config/runtime-config.json` file in your release pipeline.

```json
{
  "environmentName": "production",
  "apiBaseUrl": "https://<your-api-app>.azurewebsites.net",
  "signalRHubUrl": ""
}
```

Notes:

- `apiBaseUrl` should be the API origin or reverse-proxy base path. Do not append `/api`.
- Leave `signalRHubUrl` empty to derive `<apiBaseUrl>/hubs/live`.
- Set `signalRHubUrl` explicitly only if SignalR is exposed on a different origin/path than the REST API.
- `staticwebapp.config.json` is included in the build output to handle Angular SPA routing and to prevent long-lived caching of `runtime-config.json`.

## Backend: Azure Web App

### Publish command

```bash
cd VirtualLab/src
dotnet publish VirtualLab.Api/VirtualLab.Api.csproj -c Release -o ../artifacts/virtual-lab-api
```

Deploy the published output from `VirtualLab/artifacts/virtual-lab-api` to Azure Web App.

The publish output includes `config/benchmark-seed.json`, so the API no longer depends on the repository layout after deployment.

### Required app settings

Set these Azure App Settings on the Web App:

| Setting | Required | Purpose |
|---------|----------|---------|
| `DOTNET_ENVIRONMENT` | Yes | Use `Production` for Azure Web App unless you intentionally load another appsettings environment. |
| `VirtualLab__Persistence__Provider` | Yes | `Sqlite` for single-instance/shared-demo environments, `SqlServer` for Azure SQL, or `PostgreSQL`/`Npgsql` for PostgreSQL. |
| `VirtualLab__Persistence__ConnectionString` | Yes | Database connection string for the selected provider. |
| `VirtualLab__Seed__ApplyOnStartup` | Yes | `false` for shared Azure environments unless you explicitly want startup reseeding. |
| `VirtualLab__Seed__ResetOnStartup` | No | Keep `false` outside disposable environments. |
| `VirtualLab__Cors__AllowedOrigins__0` | Yes for separate UI hosting | Azure Static Web Apps origin, for example `https://<your-ui-app>.azurestaticapps.net`. |
| `VirtualLab__Cors__AllowedOrigins__1` | Optional | Secondary UI hostname or custom domain. |
| `VirtualLab__Callbacks__DispatchBatchSize` | No | Callback worker batch size override. |
| `VirtualLab__Callbacks__WorkerPollIntervalMs` | No | Retry worker polling interval. |
| `VirtualLab__Callbacks__RequestTimeoutSeconds` | No | Callback dispatch timeout. |
| `VirtualLab__Callbacks__MaxRetryCount` | No | Maximum callback retries. |
| `VirtualLab__Callbacks__RetryDelaysSeconds__0..n` | No | Retry delay sequence. |

### Database choices

#### SQLite for local/dev and lightweight Azure demos

SQLite remains the default and is still the simplest local/dev option:

```text
VirtualLab__Persistence__Provider=Sqlite
VirtualLab__Persistence__ConnectionString=Data Source=<persistent-app-service-path>/virtual-lab.db
```

Use SQLite only when the Azure environment is effectively single-writer and disposable or low-concurrency.

#### Azure SQL for shared Azure environments

The API now supports SQL Server provider selection through configuration:

```text
VirtualLab__Persistence__Provider=SqlServer
VirtualLab__Persistence__ConnectionString=Server=tcp:<server>.database.windows.net,1433;Initial Catalog=<db>;Persist Security Info=False;User ID=<user>;Password=<password>;MultipleActiveResultSets=False;Encrypt=True;TrustServerCertificate=False;Connection Timeout=30;
```

This is the recommended path once the virtual lab becomes a shared integration environment or you need better concurrency, durability, and operational visibility than a file-backed SQLite database provides.

#### PostgreSQL for shared environments

The API now accepts `PostgreSQL`, `Postgres`, or `Npgsql` as the provider and can bootstrap a fresh PostgreSQL schema directly from the EF Core model:

```text
VirtualLab__Persistence__Provider=PostgreSQL
VirtualLab__Persistence__ConnectionString=Host=<host>;Port=5432;Database=<db>;Username=<user>;Password=<password>;SSL Mode=Require;Trust Server Certificate=false
```

Notes:

- PostgreSQL startup uses `EnsureCreated` for fresh environments because the checked-in migration set is still SQLite/SQL Server oriented.
- Use PostgreSQL for new deployments rather than trying to apply the existing migration history to an already-created schema.
- If you need migration-managed PostgreSQL upgrades later, add a provider-specific migration set before rolling schema changes across an existing PostgreSQL environment.

## Independent deployment checklist

- UI deploys independently as long as `assets/config/runtime-config.json` points at a reachable API origin.
- API deploys independently as long as CORS allows the current UI origin and the selected database settings are valid.
- The API does not need the Angular bundle.
- The UI does not need the API build output.
- SignalR uses the same runtime-config boundary as the REST API, so no extra frontend rebuild is required when the API moves.
