# HidBridge Keycloak Claim Mapping Runbook (EN)

Date: `2026-03-11`  
Status: `practical runbook`

## 1. Goal

Normalize brokered users in `Keycloak` so `HidBridge.ControlPlane.Web` (and future clients) use HidBridge internal claims instead of raw external provider claims:
- `principal_id`
- `tenant_id`
- `org_id`
- internal `role`

## 2. Typical Symptom

Without claim normalization, Google login works but UI may show:
- principal as raw `sub` / technical UUID;
- tenant/org as `unassigned`;
- Keycloak technical roles (`offline_access`, `default-roles-*`, `uma_authorization`) instead of HidBridge operator roles.

This is expected for first federated login before mapping.

## 3. Minimal Baseline

For dev baseline, enough to set:
1. `principal_id = email` (or username if username is email)
2. `tenant_id = local-tenant`
3. `org_id = local-org`
4. assign at least one internal role:
   - `operator.viewer`

## 4. Where to Configure

In `Keycloak Admin Console`, realm `hidbridge-dev`:
1. `Identity providers -> google`
2. `Mappers`
3. Add/adjust required mappers

Also in:
- `Client scopes -> hidbridge-operator -> Mappers`
- `Client scopes -> hidbridge-caller-context-v2 -> Mappers`

## 5. Practical Mappings

### 5.1 `principal_id` from email/username

Map email/username from brokered identity into:
- user attribute: `principal_id`
- token claim: `principal_id`

### 5.2 `tenant_id`

For dev baseline, static value is fine:
- `tenant_id = local-tenant`

### 5.3 `org_id`

For dev baseline, static value is fine:
- `org_id = local-org`

### 5.4 roles

Assign at least:
- `operator.viewer`

Later (manual elevation):
- `operator.moderator`
- `operator.admin`

## 6. Practical Order

1. Log in via Google at least once.
2. Confirm user exists in Keycloak.
3. Open user in `Users`.
4. Verify/set attributes:
- `principal_id`
- `tenant_id`
- `org_id`
5. Assign realm role:
- `operator.viewer`
6. Log in again to `HidBridge.ControlPlane.Web`.

## 7. Expected Result

In `Settings` / identity panel:
- `Current operator` = normalized principal (typically email)
- `Tenant` = `local-tenant`
- `Organization` = `local-org`
- `Roles` include `operator.viewer` (or other HidBridge operator roles)

## 8. `User added at` Claim

`User added at` depends on one of these claims:
- `createdTimestamp`
- `created_at`
- `user_created_at`

Current baseline:
- Keycloak scope mapper includes `createdTimestamp`
- web OIDC mapping includes all three names above

After mapping updates:
1. `/auth/logout`
2. login again

## 9. Governance Rule

For production:
- external IdP should not directly define HidBridge authorization policy;
- `tenant/org/roles` should be governed centrally (Keycloak / future HidBridge Identity governance layer).

## 10. Related

- `Docs/SystemArchitecture/HidBridge_Keycloak_UI_Claim_Mapping_Clickpath_EN.md`
- `Docs/SystemArchitecture/HidBridge_Google_Keycloak_Runbook_EN.md`
- `Platform/Identity/Keycloak/README_EN.md`
