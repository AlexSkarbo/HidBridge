# HidBridge Keycloak UI Claim Mapping — Click-by-Click Runbook (EN)

Date: `2026-03-11`  
Status: `practical clickpath`

Related docs:
- `Docs/SystemArchitecture/HidBridge_Keycloak_Claim_Mapping_Runbook_EN.md`
- `Docs/SystemArchitecture/HidBridge_Google_Keycloak_Runbook_EN.md`
- `Platform/Identity/Keycloak/README_EN.md`

## 1. Goal

Configure `Keycloak` so a brokered Google user is rendered correctly in `HidBridge.ControlPlane.Web`.

Expected in `Settings`:
- `Current operator`: user email
- `User ID`: internal `sub` / Keycloak user id
- `Tenant`: `local-tenant`
- `Organization`: `local-org`
- `Roles`: HidBridge roles like `operator.viewer`
- `User added at`: shown when creation claim is mapped

## 2. Important UI Constraints

In some Keycloak versions:
- no dedicated `Attributes` tab in user profile;
- existing mapper `Mapper type` cannot be changed in place.

Safe approach:
1. do not delete old mapper;
2. disable old mapper token outputs;
3. create a new mapper with a new mapper name;
4. keep the correct token claim name.

## 3. Scope Mappers to Maintain

In `Client scopes -> hidbridge-operator -> Mappers` and/or `hidbridge-caller-context-v2`:
- `tenant_id`
- `org_id`
- `principal_id`
- `role`
- `createdTimestamp` (for user created date)

## 4. Fix `tenant_id`

1. Open old mapper `tenant_id`.
2. Disable token outputs:
- `Add to ID token`
- `Add to access token`
- `Add to userinfo`
- `Add to token introspection`
3. Create new mapper:
- `Mapper type`: `Hardcoded claim`
- `Name`: `tenant_id_static`
- `Token Claim Name`: `tenant_id`
- `Claim value`: `local-tenant`
- `Claim JSON Type`: `String`
- enable ID token / access token / userinfo

## 5. Fix `org_id`

1. Open old mapper `org_id`.
2. Disable token outputs.
3. Create new mapper:
- `Mapper type`: `Hardcoded claim`
- `Name`: `org_id_static`
- `Token Claim Name`: `org_id`
- `Claim value`: `local-org`
- `Claim JSON Type`: `String`
- enable ID token / access token / userinfo

## 6. Fix `principal_id`

For Google baseline, use username/email from user profile.

1. Open old mapper `principal_id`.
2. Disable token outputs.
3. Create new mapper:
- `Mapper type`: `User Property`
- `Name`: `principal_id_username`
- `User property`: `username`
- `Token Claim Name`: `principal_id`
- `Claim JSON Type`: `String`
- enable ID token / access token / userinfo

Result:
- `principal_id` becomes a human-readable value (email in this setup).

## 7. Keep `User ID` (`sub`)

`User ID` in web shell should come from:
- `sub` (or internal `NameIdentifier`)

No extra custom mapper is required for this.

## 8. Enable `User added at`

Preferred claim:
- `createdTimestamp`

Mapper setup:
- `Mapper type`: `User Property`
- `Name`: `createdTimestamp`
- `User property`: `createdTimestamp`
- `Token Claim Name`: `createdTimestamp`
- `Claim JSON Type`: `long`
- enable ID token / access token / userinfo

Web shell can also read:
- `created_at`
- `user_created_at`

## 9. Role Assignment

### 9.1 Realm roles

Ensure these realm roles exist:
- `operator.viewer`
- `operator.moderator`
- `operator.admin`

### 9.2 Group baseline

Use group:
- `hidbridge-operators`

Group role mapping:
- assign `operator.viewer`

Add Google user to:
- `hidbridge-operators`

## 10. Keep `role` Mapper

`role` mapper should stay enabled and emit realm roles to tokens.

The web shell should present HidBridge operator roles, not raw Keycloak technical roles.

## 11. Validation Sequence

1. Apply mapper changes.
2. `/auth/logout`
3. Login via Google again.
4. Check `Settings`:
- `Current operator` is email
- `User ID` is Keycloak UUID
- `Tenant` / `Organization` are populated
- `Roles` contain `operator.viewer` (or higher)
- `User added at` is shown if creation claim mapper is active

## 12. Common Pitfalls

- Trying to edit mapper type in place (often unsupported).
- Creating a new mapper with the same mapper name as old one.
- Assuming Keycloak UI `Created at` automatically appears in token claims.

## 13. Practical Dev Baseline (Recommended)

1. `tenant_id` -> hardcoded `local-tenant`
2. `org_id` -> hardcoded `local-org`
3. `principal_id` -> user property `username`
4. keep `role` mapper
5. assign `operator.viewer` through `hidbridge-operators`
6. logout/login

This is sufficient to continue with backend tenant/org-aware enforcement and protected API flows.
