# HidBridge Identity Baseline — Keycloak (Dev, EN)

Status as of `2026-03-11`:
- local login/password baseline is working;
- `HidBridge.ControlPlane.Web -> Keycloak` login/logout is working;
- `Google -> Keycloak -> HidBridge.ControlPlane.Web` federation baseline is restored and documented.

## Purpose

`HidBridge.ControlPlane.Web` is an OIDC client, not an identity server.

Centralized dev IdP:
- `Keycloak`

This baseline covers:
- local username/password auth;
- external IdP federation (Google, Microsoft, GitHub, generic OIDC/SAML);
- bearer-smoke users/clients for protected API checks.

## Key Files

- Realm import:
  - `Platform/Identity/Keycloak/realm-import/hidbridge-dev-realm.json`
- Realm sync script:
  - `Platform/Identity/Keycloak/Sync-HidBridgeDevRealm.ps1`
- Safe reset script:
  - `Platform/Identity/Keycloak/Reset-HidBridgeDevRealm.ps1`
- Google provider template:
  - `Platform/Identity/Keycloak/providers/google-oidc.example.json`
- Local Google provider config (not committed):
  - `Platform/Identity/Keycloak/providers/google-oidc.local.json`
- Claim mapping runbook:
  - `Docs/SystemArchitecture/HidBridge_Keycloak_Claim_Mapping_Runbook_EN.md`
- UI clickpath runbook:
  - `Docs/SystemArchitecture/HidBridge_Keycloak_UI_Claim_Mapping_Clickpath_EN.md`

## Run Keycloak

```bash
docker compose -f docker-compose.identity.yml up -d
```

Defaults:
- admin console: `http://127.0.0.1:18096/admin/`
- realm: `hidbridge-dev`
- current local admin default used by scripts: `admin / 1q-p2w0o`

## Sync (Idempotent)

Basic:
```powershell
powershell -ExecutionPolicy Bypass -File Platform/Identity/Keycloak/Sync-HidBridgeDevRealm.ps1
```

With external provider config (recommended when using Google):
```powershell
powershell -ExecutionPolicy Bypass -File Platform/Identity/Keycloak/Sync-HidBridgeDevRealm.ps1 `
  -ExternalProviderConfigPaths "Platform/Identity/Keycloak/providers/google-oidc.local.json"
```

Via env:
```powershell
$env:HIDBRIDGE_KEYCLOAK_EXTERNAL_PROVIDER_CONFIGS="Platform/Identity/Keycloak/providers/google-oidc.local.json"
powershell -ExecutionPolicy Bypass -File Platform/Identity/Keycloak/Sync-HidBridgeDevRealm.ps1
```

Notes:
- mapper creation is idempotent; `409 Conflict` on existing mapper is handled as "already exists";
- sync now normalizes mappers even when client scopes already exist.

## Safe Realm Reset

`Reset-HidBridgeDevRealm.ps1` now protects against accidental external IdP loss.

By default, reset is blocked when:
- import has no `identityProviders`;
- and no external provider config is supplied.

Reset while restoring Google provider:
```powershell
powershell -ExecutionPolicy Bypass -File Platform/Identity/Keycloak/Reset-HidBridgeDevRealm.ps1 `
  -ExternalProviderConfigPaths "Platform/Identity/Keycloak/providers/google-oidc.local.json"
```

Intentionally clean reset (remove external IdPs):
```powershell
powershell -ExecutionPolicy Bypass -File Platform/Identity/Keycloak/Reset-HidBridgeDevRealm.ps1 -AllowIdentityProviderLoss
```

## Recovery: "Google provider/user disappeared"

Symptoms:
- `Keycloak -> Identity providers` has no `google`;
- federated Google user is missing in `Users`.

Typical cause:
- realm reset/recreate from import without external IdP definitions.

Recovery steps:
1. Create local Google config from template:
```powershell
Copy-Item Platform\Identity\Keycloak\providers\google-oidc.example.json Platform\Identity\Keycloak\providers\google-oidc.local.json
```
2. Put real values into `google-oidc.local.json`:
- `clientId`
- `clientSecret`
3. Run sync with external provider config:
```powershell
powershell -ExecutionPolicy Bypass -File Platform/Identity/Keycloak/Sync-HidBridgeDevRealm.ps1 `
  -ExternalProviderConfigPaths "Platform/Identity/Keycloak/providers/google-oidc.local.json"
```
4. Verify in Keycloak: `Identity providers -> google` is enabled.
5. Log in to `HidBridge.ControlPlane.Web` via Google once:
- federated user is re-created on first successful login.

Important:
- old federated user records cannot be restored automatically unless they were saved in a dedicated full realm export.

## "User added at: not available" in Web Settings

That field depends on user creation claims.

Current baseline:
- Keycloak mapper `createdTimestamp` is included in `hidbridge-caller-context-v2`;
- web OIDC maps `createdTimestamp`, `created_at`, `user_created_at`.

After sync you must:
1. `/auth/logout`
2. log in again

Otherwise the current web cookie/session may still hold old claims.

## Troubleshooting

- `400 Bad Request` on `/identity-provider/instances`:
  - check `google-oidc.local.json` validity and real credentials;
  - check JSON shape and URLs.

- `409 Conflict` on `/protocol-mappers/models`:
  - duplicate mapper already exists;
  - sync now treats this as idempotent and continues.

- `CreatedAt` missing in web while visible in Keycloak UI:
  - run sync to ensure mapper exists;
  - logout/login to refresh cookie claims.
