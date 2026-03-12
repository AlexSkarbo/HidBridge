# HidBridge Google Sign-In через Keycloak — Runbook (UA)

Дата: `2026-03-03`  
Статус: `practical runbook`

Пов'язані документи:
- `Docs/SystemArchitecture/HidBridge_Identity_SSO_Baseline_UA.md`
- `Docs/SystemArchitecture/HidBridge_External_IdP_Integration_Baseline_UA.md`
- `Platform/Identity/Keycloak/README.md`
- `Platform/Identity/Keycloak/providers/EXTERNAL_IDP_ONBOARDING_UA.md`

## 1. Ціль

Підключити `Google Sign-In` до HidBridge **через Keycloak**, а не напряму в `HidBridge.ControlPlane.Web`.

Схема:

```text
Google -> Keycloak -> HidBridge.ControlPlane.Web
```

## 2. Передумови

Потрібно, щоб уже працювало:
- `docker compose -f docker-compose.identity.yml up -d`
- `Keycloak`: `http://127.0.0.1:18096/admin/`
- realm: `hidbridge-dev`
- локальний login/logout для `controlplane-web`
- `HidBridge.ControlPlane.Web` запускається з `Identity:Enabled=true`

## 3. Що створюємо в Google Cloud

У Google Cloud потрібен `OAuth 2.0 Client ID` типу `Web application`.

Практично це робиться в:
- `Google Cloud Console -> APIs & Services -> Credentials`

Потрібно мати:
- `Client ID`
- `Client Secret`

## 4. Redirect URI для Google

У Google OAuth client треба додати **рівно** broker endpoint Keycloak:

- `http://127.0.0.1:18096/realms/hidbridge-dev/broker/google/endpoint`

Якщо хочеш працювати через інший hostname або reverse proxy, тоді в Google треба вказувати вже **публічний URL Keycloak broker endpoint**, а не URL `ControlPlane.Web`.

Ключове правило:
- redirect URI у Google веде в `Keycloak`
- redirect URI у `controlplane-web` веде в `HidBridge.ControlPlane.Web`

Це два різні рівні.

## 5. Налаштування Google consent screen

У Google Cloud також треба налаштувати OAuth consent screen:
- application name
- support email
- developer contact email
- test users, якщо app у тестовому режимі

Для локального dev baseline зазвичай достатньо:
- External app
- додати свій Google account у `Test users`

## 6. Додавання Google в Keycloak

У `Keycloak Admin Console`:

1. Вибрати realm `hidbridge-dev`
2. Перейти в `Identity providers`
3. Додати провайдер `Google`
4. Внести:
- `Client ID`
- `Client Secret`
5. Зберегти

Якщо в твоїй версії Keycloak немає окремого social preset або потрібен ручний OIDC baseline, використовуй шаблон:
- `Platform/Identity/Keycloak/providers/google-oidc.example.json`

Практичний script-first варіант (рекомендовано для dev):
1. створити локальний файл:
   - `Platform/Identity/Keycloak/providers/google-oidc.local.json`
2. заповнити `clientId/clientSecret`
3. виконати:
```powershell
powershell -ExecutionPolicy Bypass -File Platform/Identity/Keycloak/Sync-HidBridgeDevRealm.ps1 `
  -ExternalProviderConfigPaths "Platform/Identity/Keycloak/providers/google-oidc.local.json"
```

## 7. Мінімальні перевірки в Keycloak

Після додавання Google перевірити:
- провайдер `google` увімкнений
- broker alias = `google`
- перший login flow = `first broker login`
- email trust policy відповідає dev-потребам

## 8. Мапінг користувача і claims

На першому етапі достатньо, щоб через Google приходили:
- `sub`
- `name`
- `email`

Далі в Keycloak треба нормалізувати або добудувати внутрішні claims HidBridge:
- `principal_id`
- `tenant_id`
- `org_id`
- `role`

Практичний baseline:
- зовнішній Google login дає первинну identity
- внутрішні ролі і tenant/org governance не беремо напряму з Google
- це керується в Keycloak/HidBridge identity контурі

## 9. Перевірка входу в HidBridge.ControlPlane.Web

1. Запустити `HidBridge.ControlPlane.Web`
2. Відкрити `http://localhost:18110`
3. Натиснути `Sign in`
4. На сторінці Keycloak вибрати Google login
5. Пройти Google authentication
6. Переконатися, що після повернення в web shell:
- користувач автентифікований
- `auth/status` показує коректний provider
- відображаються operator identity fields

## 10. Smoke checklist

Після інтеграції Google перевірити:
1. login через локальний `username/password`
2. login через Google
3. logout після локального login
4. logout після Google login
5. `GET /auth/status`
6. доступ до operator screens після login
7. заборона доступу без login

## 11. Типові проблеми

### 11.1 `invalid_scope`

Причина:
- занадто агресивний scope set

Baseline для web shell:
- `openid`
- `email`
- `roles`

`profile` додавати тільки після перевірки конкретного provider flow.

### 11.2 `invalid_request` на OIDC challenge

Причина:
- `Pushed Authorization Requests` у dev-контурі

Що робити:
- `Identity:DisablePushedAuthorization=true`

### 11.3 `invalid redirect uri`

Перевіряти окремо:
- redirect URI у Google -> має вести в `Keycloak broker endpoint`
- redirect URI у Keycloak client `controlplane-web` -> має вести в `HidBridge.ControlPlane.Web`

### 11.4 browser console шум типу `metadata.js` / `content.js`

Зазвичай це браузерні розширення, не HidBridge.

### 11.5 Після reset зник Google provider

Причина:
- realm було пересоздано з import без `identityProviders`.

Відновлення:
```powershell
powershell -ExecutionPolicy Bypass -File Platform/Identity/Keycloak/Sync-HidBridgeDevRealm.ps1 `
  -ExternalProviderConfigPaths "Platform/Identity/Keycloak/providers/google-oidc.local.json"
```

Далі:
1. перевірити `Identity providers -> google`
2. зробити новий Google login (federated user створиться знову)

### 11.6 У web Settings: "Користувача додано: недоступно"

Причина:
- у поточній web session ще старі claims без `createdTimestamp`.

Що робити:
1. `/auth/logout`
2. login знову
3. перевірити `/settings`

## 12. Що далі після Google

Після успішної інтеграції Google робимо те саме для:
- `Microsoft Entra ID`
- `GitHub`
- інших `OIDC/SAML` провайдерів

Після цього наступний етап платформи:
- `tenant/org-aware enforcement` у backend
- auth propagation у mobile/desktop/API

## 13. Джерела

- Google Cloud OAuth clients: https://cloud.google.com/docs/authentication/oauth-overview
- Keycloak main site / identity brokering overview: https://www.keycloak.org/
