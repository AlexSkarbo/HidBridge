# HidBridge Identity / SSO Baseline (UA)

Дата: `2026-03-03`  
Статус: `baseline, login/logout verified locally`

## 1. Рішення

`HidBridge.ControlPlane.Web` **не є identity server** і не повинен ним ставати.

Правильна модель:
- окремий централізований `Identity Provider`
- усі клієнти HidBridge інтегруються з ним як `OIDC/OAuth clients`

Поточний dev baseline:
- `Keycloak`

## 2. Що повинен покривати централізований IdP

1. `SSO` для всіх типів клієнтів:
- web
- mobile
- desktop
- API
- service-to-service

2. Кілька способів входу:
- локальний `username/password`
- зовнішні `OIDC` провайдери:
  - Google
  - Microsoft
  - GitHub
  - інші

3. Єдиний контур claims / roles / tenant / organization

## 3. Цільова інтеграційна схема

```text
External IdPs (Google / Microsoft / GitHub / ...)
        ->
Central IdP (Keycloak / future HidBridge.Identity)
        ->
HidBridge.ControlPlane.Web
HidBridge mobile clients
HidBridge desktop clients
HidBridge APIs
Service principals
```

## 4. Чому не `HidBridge.ControlPlane.Web`

Причини:
1. web shell не повинен нести відповідальність за SSO всього продукту;
2. mobile/desktop/API потребують інші auth flows;
3. roles/tenant/org governance має бути централізованим;
4. зовнішні IdP треба підключати один раз у центрі, а не в кожному застосунку окремо.

## 5. Поточний dev baseline

Фактично підтверджено локально:
- вхід через Keycloak local username/password працює;
- вихід із `HidBridge.ControlPlane.Web` через Keycloak працює;
- вхід через Google broker у Keycloak працює;
- `HidBridge.ControlPlane.Web` лишається лише `OIDC client`, а не identity server.

Поточний фактичний claims baseline після ручного Keycloak mapping:
- `principal_id` може бути нормалізований до email;
- `tenant_id` і `org_id` можуть видаватися як стабільні static claims;
- `role` може видавати внутрішню роль HidBridge, наприклад `operator.viewer`;
- `sub` лишається джерелом `User ID`.

Важливе уточнення:
- поле `User added at` у web shell не з'являється автоматично тільки тому, що `Keycloak Admin UI` показує `Created at`;
- це поле треба окремо віддати в токен mapper-ом;
- якщо поточний `Keycloak UI` не дозволяє стабільно віддати `createdTimestamp`, значення `not available` є допустимим baseline.

Поточний enforcement baseline після інтеграції claims:
- `principal_id`, `tenant_id`, `org_id` і `operator.*` ролі прокидаються з web shell у `ControlPlane.Api`;
- session-scoped backend flows уже перевіряють tenant/org scope;
- при наявному caller context moderation paths додатково вимагають `operator.moderator` або `operator.admin`.


Інфраструктура:
- `docker-compose.identity.yml`
- `Platform/Identity/Keycloak/realm-import/hidbridge-dev-realm.json`

Realm:
- `hidbridge-dev`

Clients:
- `controlplane-web`
- `controlplane-api`

Локальний вхід:
- user: `operator.admin`
- password: `ChangeMe123!`

Технічна примітка для `ASP.NET Core 10`:
- при інтеграції з `Keycloak` у dev baseline слід вимкнути `Pushed Authorization Requests`
- інакше можливі помилки `invalid_request` на етапі challenge
- якщо провайдер повертає `invalid_scope`, baseline для web shell треба звести до:
  - `openid`
  - `email`
  - `roles`
- `profile` слід додавати тільки після окремої перевірки конкретного IdP-контуру

Зовнішні IdP:
- підключаються в Keycloak як external identity providers

## 6. Auth flows

### 6.1 Web

- `OIDC Authorization Code`
- server-side cookie session
- BFF-style shell

### 6.2 Mobile / Desktop

- `OIDC Authorization Code + PKCE`

### 6.3 API

- `JWT bearer tokens`

### 6.4 Service-to-service

- `client credentials`

## 7. Claims baseline

Поточний baseline claims:
- `sub`
- `principal_id`
- `name`
- `tenant_id`
- `org_id`
- `role`

## 8. Практичний висновок

1. `Google Sign-In` треба інтегрувати не напряму в `HidBridge.ControlPlane.Web`, а через Keycloak.
2. Локальний логін/пароль теж має жити в Keycloak.
3. `HidBridge.ControlPlane.Web` лишається `OIDC client`.

## 9. Наступні кроки

1. Підняти `docker-compose.identity.yml`
2. Перевірити локальний вхід через Keycloak
3. Підключити `HidBridge.ControlPlane.Web` до Keycloak
4. Додати Google як external IdP у Keycloak
5. Використати той самий onboarding pattern для Microsoft / GitHub / інших OIDC провайдерів
6. Після цього перейти до tenant/org-aware enforcement у Platform backend


Практичний runbook для Google через Keycloak:
- `Docs/SystemArchitecture/HidBridge_Google_Keycloak_Runbook_UA.md`


Практичний runbook для claim mapping у Keycloak:
- `Docs/SystemArchitecture/HidBridge_Keycloak_Claim_Mapping_Runbook_UA.md`


Click-by-click runbook для Keycloak UI claim mapping:
- `Docs/SystemArchitecture/HidBridge_Keycloak_UI_Claim_Mapping_Clickpath_UA.md`
