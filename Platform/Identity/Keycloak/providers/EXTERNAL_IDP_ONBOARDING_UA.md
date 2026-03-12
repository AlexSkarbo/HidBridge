# External IdP Onboarding через Keycloak (UA)

## 1. Мета

Підключати будь-який зовнішній IdP до HidBridge однаково:
- не напряму в клієнти;
- тільки через централізований `Keycloak`.

## 2. Порядок

1. Створити application/client у зовнішнього провайдера.
2. Налаштувати redirect URI на Keycloak broker endpoint.
3. Додати external IdP у `Keycloak`.
4. Призначити claim / username / email mapping rules.
5. Перевірити login у `HidBridge.ControlPlane.Web`.

## 3. Google baseline

Для `Google` потрібен broker redirect URI виду:
- `http://127.0.0.1:18096/realms/hidbridge-dev/broker/google/endpoint`

Для production треба використовувати вже production public URL `Keycloak`.

Приклад конфігурації:
- `Platform/Identity/Keycloak/providers/google-oidc.example.json`

## 4. Інші провайдери

Той самий шаблон застосовується до:
- `Microsoft Entra ID`
- `GitHub`
- інших `OIDC` провайдерів
- `SAML` провайдерів

## 5. Правило governance

Зовнішній IdP відповідає за первинну identity, але:
- `tenant_id`
- `org_id`
- внутрішні `role`
- policy decisions

мають контролюватися централізовано в HidBridge IdP контурі.
