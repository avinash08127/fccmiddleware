# Local Testing Guide

## Prerequisites

- Docker Desktop running
- .NET 9 SDK
- Node.js 20+
- Angular CLI (`npm i -g @angular/cli`)

## 1. Start Infrastructure (Postgres + Redis)

```bash
cd C:\Users\a0812\fccmiddleware
docker compose up -d
```

This starts:
- **Postgres 16** on `localhost:5432` (db: `fccmiddleware`, user/pass: `postgres/postgres`)
- **Redis 7** on `localhost:6379`

SQL migrations in `db/migrations/` auto-run on **first container creation** only (mounted to `/docker-entrypoint-initdb.d`).

## 2. Run Pending Migrations (if DB already existed)

If your Postgres volume already existed before `004-portal-users.sql` was added, you must run it manually:

```bash
docker exec -i fcc-postgres psql -U postgres -d fccmiddleware < db/migrations/004-portal-users.sql
```

This creates the `portal_roles`, `portal_users`, and `portal_user_legal_entities` tables, and seeds your admin account (`avinash.mishra@noobsplayground.com` as FccAdmin with access to all legal entities).

### Verify the seed worked

```bash
docker exec -it fcc-postgres psql -U postgres -d fccmiddleware -c "SELECT id, email, role_id, all_legal_entities FROM portal_users;"
```

You should see one row with your email, `role_id = 1` (FccAdmin), and `all_legal_entities = true`.

## 3. Start the .NET Backend

```bash
cd src/cloud/FccMiddleware.Api
dotnet run
```

The API starts on **http://localhost:5070** (defined in `Properties/launchSettings.json`, profile `http`).

No docker compose needed for the backend — `dotnet run` is sufficient. It connects to the Dockerized Postgres and Redis on localhost.

### Quick health check

```
GET http://localhost:5070/health
```

## 4. Start the Angular Portal

```bash
cd src/portal
npm install   # first time only
ng serve
```

Portal runs on **http://localhost:4200**. CORS is already configured on the backend to allow this origin.

## 5. Azure Entra App Registration Checklist

Before login will work, verify these settings in the Azure Portal for app `38261eab-f0e3-4aef-94e2-c61498745e99`:

| Setting | Required Value |
|---|---|
| **Authentication > SPA redirect URI** | `http://localhost:4200` |
| **Authentication > ID tokens** | Checked (implicit grant) |
| **Authentication > Access tokens** | Checked (implicit grant) |
| **Expose an API > Application ID URI** | `api://38261eab-f0e3-4aef-94e2-c61498745e99` |
| **Expose an API > Scope** | Add a scope (e.g. `access_as_user`) or rely on `.default` |
| **Token configuration > Optional claims** | Add `email` and `preferred_username` to ID token |
| **API permissions** | `openid`, `profile`, `email` (delegated, Microsoft Graph) |

**Important:** The portal requests the scope `38261eab-f0e3-4aef-94e2-c61498745e99/.default`. For this to work, the app must have an **Application ID URI** set (`api://38261eab-...`). If you haven't set this, either:
- Set it in Entra > App registrations > Expose an API, or
- The `.default` scope will fall back to the app's own client ID as audience

## 6. Login Flow

1. Open `http://localhost:4200`
2. You'll be redirected to Microsoft login (Entra)
3. Sign in with `avinash.mishra@noobsplayground.com`
4. After redirect back, the portal calls `GET /api/v1/admin/users/me`
5. The `PortalRoleEnrichmentMiddleware` looks up your email in `portal_users`, finds the FccAdmin record, and injects role claims
6. You get full admin access including the **User Management** screen

## 7. Troubleshooting

### "USER_NOT_PROVISIONED" (403)

The middleware can't find your email in `portal_users`. Causes:
- Migration 004 hasn't run — see step 2
- Email case mismatch — the lookup is case-insensitive but verify with the SQL query in step 2
- The Entra token doesn't include an email claim — check the token in browser DevTools (Application > Local Storage > msal keys) and ensure `preferred_username` or `email` is present

### CORS errors in browser console

The backend CORS policy only allows `http://localhost:4200`. If you're using a different port or `127.0.0.1`, update the origin in `Program.cs` line 67.

### JWT validation errors (401)

Check the backend console logs. Common issues:
- **"IDX10214: Audience validation failed"** — the Entra app doesn't have `Application ID URI` set, so the token audience doesn't match. Set `api://38261eab-f0e3-4aef-94e2-c61498745e99` in Entra portal under "Expose an API".
- **"IDX10205: Issuer validation failed"** — verify the tenant ID in `appsettings.Development.json` matches your Entra directory.

### Backend won't start

- Verify Postgres is running: `docker exec -it fcc-postgres pg_isready`
- Verify Redis is running: `docker exec -it fcc-redis redis-cli ping`
- If port 5070 is in use, kill the existing process or change the port in `launchSettings.json`

### Resetting the database

If you need a clean slate:

```bash
docker compose down -v   # removes volumes (all data lost)
docker compose up -d     # recreates with fresh migrations
```

## Architecture Summary

```
Browser (localhost:4200)
  │  MSAL redirect login
  │  Entra token attached via MsalInterceptor
  ▼
Angular Portal ──HTTP──▶ .NET API (localhost:5070)
                            │
                            ├── PortalRoleEnrichmentMiddleware
                            │     └── looks up email in portal_users
                            │     └── injects synthetic role claims
                            │
                            ├── Postgres (localhost:5432)
                            └── Redis (localhost:6379)
```
