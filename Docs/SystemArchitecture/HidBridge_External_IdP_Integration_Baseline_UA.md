# HidBridge External IdP Integration Baseline (UA)

Дата: `2026-03-03`  
Статус: `baseline`

## 1. Мета

Зафіксувати базовий спосіб підключення зовнішніх провайдерів автентифікації до HidBridge через централізований `Keycloak`, а не напряму в окремі клієнти.

## 2. Принцип

```text
External IdP (Google / Microsoft / GitHub / ...)
    -> Keycloak
    -> HidBridge clients
```

Це дає:
- єдиний `SSO` контур;
- єдині role / tenant / organization claims;
- один шаблон інтеграції для web, mobile, desktop, API.

## 3. Перший practical baseline

Перший зовнішній IdP для baseline:
- `Google`

Причина:
- найпростіший старт для перевірки federated login;
- типовий OIDC сценарій;
- той самий підхід далі переноситься на `Microsoft`, `GitHub` та інші OIDC/SAML провайдери.

## 4. Що вже підтверджено

1. Локальний `username/password` у `Keycloak` працює.
2. `HidBridge.ControlPlane.Web -> Keycloak` login/logout працює.
3. `HidBridge.ControlPlane.Web` лишається `OIDC client`, а не identity server.

## 5. Що робимо далі

1. Створюємо `Google OAuth client` у Google Cloud.
2. Додаємо `Google` як external IdP у `Keycloak`.
3. Тестуємо login через Google у `HidBridge.ControlPlane.Web`.
4. Після цього документуємо той самий onboarding pattern для інших провайдерів.

## 6. Мінімальний mapping claims

На вході в `Keycloak` бажано нормалізувати до внутрішніх claims HidBridge:
- `sub`
- `principal_id`
- `name`
- `email`
- `tenant_id`
- `org_id`
- `role`

## 7. Важливе рішення

Tenant / organization / role governance не делегується зовнішньому IdP повністю.

Зовнішній IdP дає первинну identity, але нормалізація й політики доступу мають контролюватися центральним IdP контуром (`Keycloak` зараз, `future HidBridge.Identity` пізніше).

## 8. Практичні артефакти

- `Platform/Identity/Keycloak/providers/google-oidc.example.json`
- `Platform/Identity/Keycloak/providers/EXTERNAL_IDP_ONBOARDING_UA.md`


Практичний runbook для Google через Keycloak:
- `Docs/SystemArchitecture/HidBridge_Google_Keycloak_Runbook_UA.md`


Практичний runbook для claim mapping у Keycloak:
- `Docs/SystemArchitecture/HidBridge_Keycloak_Claim_Mapping_Runbook_UA.md`
