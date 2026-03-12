# HidBridge Identity Baseline — Keycloak (Dev)

Статус на `2026-03-03`:
- локальний login/password working baseline підтверджено;
- `HidBridge.ControlPlane.Web -> Keycloak` login/logout working baseline підтверджено;
- окремий `Valid post logout redirect URIs` у поточному локальному Keycloak-контурі не знадобився, але це версійно-залежна поведінка і її не слід вважати універсальною для всіх середовищ.

Ця тека містить dev-baseline для централізованого `SSO/Identity` контуру HidBridge.

## Призначення

`HidBridge.ControlPlane.Web` **не є identity server**.  
Він виступає лише як `OIDC client` для зовнішнього централізованого IdP.

У dev baseline центральним IdP виступає:
- `Keycloak`

Keycloak покриває:
- локальний вхід за `username/password`
- федерацію зовнішніх IdP:
  - `Google`
  - `Microsoft`
  - `GitHub`
  - інші `OIDC/SAML` провайдери
- SSO для:
  - web
  - mobile
  - desktop
  - API
  - service-to-service сценаріїв

## Файли

- `Platform/Identity/Keycloak/realm-import/hidbridge-dev-realm.json`
  - dev realm import
  - clients:
    - `controlplane-web`
    - `controlplane-api`
    - `controlplane-smoke`
  - realm roles:
    - `operator.viewer`
    - `operator.moderator`
    - `operator.admin`
  - dev local user:
    - `operator.admin / ChangeMe123!`
  - bearer-smoke users:
    - `operator.smoke.admin / ChangeMe123!`
    - `operator.smoke.viewer / ChangeMe123!`
    - `operator.smoke.foreign / ChangeMe123!`
  - `controlplane-smoke` is a `public` direct-grant dev client, so bearer-only smoke does not require a client secret

## Запуск

```bash
docker compose -f docker-compose.identity.yml up -d
```

Після старту:
- Keycloak admin console:
  - `http://127.0.0.1:18096/admin/`
- admin credentials:
  - `admin / admin`
- realm:
  - `hidbridge-dev`

## Dev OIDC settings for ControlPlane.Web

- authority:
  - `http://127.0.0.1:18096/realms/hidbridge-dev`
- client id:
  - `controlplane-web`
- client secret:
  - `hidbridge-web-dev-secret`

Важливо для `ASP.NET Core 10` OIDC client:
- для `Keycloak` у dev baseline потрібно вимкнути `Pushed Authorization Requests`
- у конфігу web shell:
  - `Identity:DisablePushedAuthorization=true`
- рекомендований baseline scopes для web shell:
  - `openid`
  - `email`
  - `roles`
- `profile` не слід примусово додавати в dev baseline, якщо провайдер повертає `invalid_scope`

## Google Sign-In

Google не підключається напряму до `HidBridge.ControlPlane.Web`.

Правильна схема:

```text
Google -> Keycloak -> HidBridge clients
```

Тобто:
1. у Google Cloud створюється `OAuth client`
2. він підключається як external IdP у Keycloak
3. усі HidBridge застосунки ходять уже в Keycloak

## Важливо

Цей baseline призначений для локальної розробки та інтеграційного тесту.

Для production потрібно окремо:
1. змінити всі dev secrets
2. винести конфіг у secret store
3. налаштувати HTTPS / reverse proxy
4. налаштувати tenant/org-aware claims governance

Примітка по realm import:
- якщо realm `hidbridge-dev` уже був імпортований раніше, нові smoke client/users не з'являться автоматично;
- для цього потрібно або:
  - перевидалити realm і переімпортувати його;
  - або додати `controlplane-smoke` та `operator.smoke.*` вручну в `Keycloak Admin UI`.

Примітка по logout callback:
- у поточному локальному контурі logout працює без окремого заповнення `Valid post logout redirect URIs`;
- якщо в іншому середовищі з'явиться `invalid redirect uri`, спочатку перевірити саме client logout settings у Keycloak і дозволені callback URI для `signout-callback-oidc`.

## Google як external IdP

Базовий напрямок:
- `Google -> Keycloak -> HidBridge clients`

Практичні артефакти:
- `Platform/Identity/Keycloak/providers/google-oidc.example.json`
- `Platform/Identity/Keycloak/providers/EXTERNAL_IDP_ONBOARDING_UA.md`

Для production/real secrets цей приклад не імпортується напряму без редагування. Спочатку створити client у Google Cloud, потім перенести `clientId` і `clientSecret` у Keycloak.


Практичний runbook для Google через Keycloak:
- `Docs/SystemArchitecture/HidBridge_Google_Keycloak_Runbook_UA.md`

Локальний файл для реальних Google credentials (не комітиться):
- `Platform/Identity/Keycloak/providers/google-oidc.local.json`
- можна створити з шаблону:
  - `Platform/Identity/Keycloak/providers/google-oidc.example.json`
- файл `*.local.json` доданий у `.gitignore`


Практичний runbook для claim mapping у Keycloak:
- `Docs/SystemArchitecture/HidBridge_Keycloak_Claim_Mapping_Runbook_UA.md`


Click-by-click runbook для Keycloak UI claim mapping:
- `Docs/SystemArchitecture/HidBridge_Keycloak_UI_Claim_Mapping_Clickpath_UA.md`


Скрипт нормалізації dev realm:
- `Platform/Identity/Keycloak/Sync-HidBridgeDevRealm.ps1`
- що робить:
  - перевіряє наявність realm `hidbridge-dev`
  - робить backup поточного стану realm
  - нормалізує realm settings
  - upsert-ить realm roles
  - upsert-ить `hidbridge-operator` client scope і mapper-и
  - upsert-ить clients:
    - `controlplane-web`
    - `controlplane-api`
    - `controlplane-smoke`
  - upsert-ить dev users:
    - `operator.admin`
    - `operator.smoke.admin`
    - `operator.smoke.viewer`
    - `operator.smoke.foreign`
  - гарантує групу `hidbridge-operators` з роллю `operator.viewer`

Приклад запуску:
```powershell
powershell -ExecutionPolicy Bypass -File Platform/Identity/Keycloak/Sync-HidBridgeDevRealm.ps1
```

Поточний локальний dev default для sync script:
- `admin / 1q-p2w0o`

Якщо bootstrap admin credentials відрізняються від цього значення:
```powershell
powershell -ExecutionPolicy Bypass -File Platform/Identity/Keycloak/Sync-HidBridgeDevRealm.ps1 `
  -AdminUser "<admin-user>" `
  -AdminPassword "<admin-password>"
```

Або через env vars:
```powershell
$env:HIDBRIDGE_KEYCLOAK_ADMIN_USER="<admin-user>"
$env:HIDBRIDGE_KEYCLOAK_ADMIN_PASSWORD="<admin-password>"
powershell -ExecutionPolicy Bypass -File Platform/Identity/Keycloak/Sync-HidBridgeDevRealm.ps1
```

Якщо треба зберегти користувачів у backup:
```powershell
powershell -ExecutionPolicy Bypass -File Platform/Identity/Keycloak/Sync-HidBridgeDevRealm.ps1 -IncludeUsersInBackup
```

Якщо треба одночасно синхронізувати external IdP (Google) під час sync:
```powershell
powershell -ExecutionPolicy Bypass -File Platform/Identity/Keycloak/Sync-HidBridgeDevRealm.ps1 `
  -ExternalProviderConfigPaths "Platform/Identity/Keycloak/providers/google-oidc.local.json"
```

Альтернатива через env:
```powershell
$env:HIDBRIDGE_KEYCLOAK_EXTERNAL_PROVIDER_CONFIGS="Platform/Identity/Keycloak/providers/google-oidc.local.json"
powershell -ExecutionPolicy Bypass -File Platform/Identity/Keycloak/Sync-HidBridgeDevRealm.ps1
```

## Безпечний realm reset

`Reset-HidBridgeDevRealm.ps1` тепер блокує reset за замовчуванням, якщо:
- import не містить `identityProviders`
- і не передані external provider configs

Тобто випадково "втратити Google/Microsoft/GitHub broker" під час reset більше не вийде.

Варіант reset зі збереженням Google provider:
```powershell
powershell -ExecutionPolicy Bypass -File Platform/Identity/Keycloak/Reset-HidBridgeDevRealm.ps1 `
  -ExternalProviderConfigPaths "Platform/Identity/Keycloak/providers/google-oidc.local.json"
```

Якщо потрібен саме чистий reset без external IdP:
```powershell
powershell -ExecutionPolicy Bypass -File Platform/Identity/Keycloak/Reset-HidBridgeDevRealm.ps1 -AllowIdentityProviderLoss
```

## Відновлення після "зник Google provider / зник Google user"

Симптом:
- у `Keycloak -> Identity providers` немає `google`
- federated Google-користувача немає в `Users`

Типова причина:
- виконувався `identity-reset`, а realm import не містив `identityProviders`

Що робити:
1. Створити локальний конфіг Google (з реальними credentials):
```powershell
Copy-Item Platform\Identity\Keycloak\providers\google-oidc.example.json Platform\Identity\Keycloak\providers\google-oidc.local.json
```
2. Заповнити в `google-oidc.local.json`:
- `clientId`
- `clientSecret`
3. Прогнати sync з external provider config:
```powershell
powershell -ExecutionPolicy Bypass -File Platform/Identity/Keycloak/Sync-HidBridgeDevRealm.ps1 `
  -ExternalProviderConfigPaths "Platform/Identity/Keycloak/providers/google-oidc.local.json"
```
4. Перевірити в Keycloak, що `Identity providers -> google` знову є й `Enabled`.
5. Зайти в `HidBridge.ControlPlane.Web` через Google один раз:
- federated user буде створений заново на першому успішному login.

Примітка:
- попередній federated user не "відновлюється магічно" після reset, якщо його не було в окремому realm export.

## Чому в Settings може бути "Користувача додано: недоступно"

Для цього поля потрібен claim `createdTimestamp` у токені/userinfo.

Нормалізація зараз включає:
- mapper `createdTimestamp` у `hidbridge-caller-context-v2`
- OIDC claim mapping у web shell (`createdTimestamp`, `created_at`, `user_created_at`)

Після sync потрібно:
1. `/auth/logout`
2. login знову

Інакше у cookie/session може лишатись старий набір claims.

## Troubleshooting (коротко)

- `400 Bad Request` на `identity-provider/instances`:
  - перевірити `google-oidc.local.json` (реальні `clientId/clientSecret`, коректний JSON)
- `409 Conflict` на `protocol-mappers/models`:
  - це дубль mapper-а; sync script обробляє як idempotent-case і продовжує
