# HidBridge Google Sign-In via Keycloak — Runbook (EN)

Date: `2026-03-11`  
Status: `practical runbook`

Related docs:
- `Platform/Identity/Keycloak/README.md`
- `Platform/Identity/Keycloak/README_EN.md`
- `Docs/SystemArchitecture/HidBridge_Google_Keycloak_Runbook_UA.md`

## Goal

Integrate Google Sign-In into HidBridge through Keycloak:

```text
Google -> Keycloak -> HidBridge.ControlPlane.Web
```

## Prerequisites

- `docker compose -f docker-compose.identity.yml up -d`
- Keycloak admin UI: `http://127.0.0.1:18096/admin/`
- realm: `hidbridge-dev`
- `HidBridge.ControlPlane.Web` uses OIDC (`Identity:Enabled=true`)

## Recommended Script-First Setup

1. Create local provider config:
```powershell
Copy-Item Platform\Identity\Keycloak\providers\google-oidc.example.json Platform\Identity\Keycloak\providers\google-oidc.local.json
```
2. Put real `clientId/clientSecret` into `google-oidc.local.json`.
3. Apply realm sync:
```powershell
powershell -ExecutionPolicy Bypass -File Platform/Identity/Keycloak/Sync-HidBridgeDevRealm.ps1 `
  -ExternalProviderConfigPaths "Platform/Identity/Keycloak/providers/google-oidc.local.json"
```

## Validate

1. Keycloak:
- `Identity providers -> google` exists and is enabled.
2. Web:
- login via Google succeeds;
- `/auth/status` shows external identity mode;
- `/settings` shows operator identity and roles.

## Recovery after Realm Reset

If Google provider disappears after reset:
1. Ensure `google-oidc.local.json` exists with real credentials.
2. Re-run:
```powershell
powershell -ExecutionPolicy Bypass -File Platform/Identity/Keycloak/Sync-HidBridgeDevRealm.ps1 `
  -ExternalProviderConfigPaths "Platform/Identity/Keycloak/providers/google-oidc.local.json"
```
3. Log in via Google again to recreate federated user.

## "User added at" Is Missing in Web Settings

Expected source:
- Keycloak user creation claims (`createdTimestamp` / `created_at` / `user_created_at`).

If Settings still shows unavailable:
1. run sync to normalize mappers;
2. `/auth/logout`;
3. login again.

## Common Errors

- `400 Bad Request` on identity provider create:
  - invalid `google-oidc.local.json` or invalid Google credentials.

- `409 Conflict` on protocol mappers:
  - mapper already exists (idempotent case). Current sync handles this and continues.
