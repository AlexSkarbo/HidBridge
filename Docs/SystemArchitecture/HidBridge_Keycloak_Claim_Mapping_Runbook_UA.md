# HidBridge Keycloak Claim Mapping Runbook (UA)

Дата: `2026-03-03`  
Статус: `practical runbook`

## 1. Мета

Нормалізувати brokered users у `Keycloak`, щоб `HidBridge.ControlPlane.Web` і майбутні клієнти бачили не сирі external claims, а внутрішні claims HidBridge:
- `principal_id`
- `tenant_id`
- `org_id`
- внутрішні `role`

## 2. Поточний симптом

Без mapping Google login уже працює, але в UI видно:
- `principal` як `sub` або технічний UUID;
- `tenant/org` як `unassigned`;
- ролі `Keycloak` типу `offline_access`, `default-roles-*`, `uma_authorization` замість HidBridge ролей.

Це очікувано для першого federated login без normalization.

## 3. Мінімальний baseline

Для першого практичного етапу достатньо:
1. `principal_id = email`
2. `tenant_id = local-tenant`
3. `org_id = local-org`
4. додати хоча б одну внутрішню роль:
   - `operator.viewer`

## 4. Де це робиться

У `Keycloak Admin Console` для realm `hidbridge-dev`:
1. `Identity providers -> google`
2. `Mappers`
3. Додати потрібні mapper-и

## 5. Практичні mapper-и

### 5.1 principal_id з email

Створити mapper типу, який бере email з external identity і записує його в user attribute / token claim:
- user attribute: `principal_id`
- token claim: `principal_id`

Ціль: `HidBridge.ControlPlane.Web` має бачити людинозрозумілий operator principal.

### 5.2 tenant_id

Для dev baseline можна задати статичне значення:
- `tenant_id = local-tenant`

### 5.3 org_id

Для dev baseline можна задати статичне значення:
- `org_id = local-org`

### 5.4 roles

Рекомендований baseline:
- вручну або через group/role assignment дати користувачу:
  - `operator.viewer`

Пізніше:
- `operator.moderator`
- `operator.admin`

## 6. Практичний порядок

1. Увійти через Google хоча б один раз.
2. Переконатися, що користувач створився в `Keycloak`.
3. Відкрити користувача в `Users`.
4. Прописати або перевірити атрибути:
- `principal_id`
- `tenant_id`
- `org_id`
5. Призначити realm role:
- `operator.viewer`
6. Перелогінитися в `HidBridge.ControlPlane.Web`.

## 7. Що має бути після цього

У `Settings` / identity panel очікується:
- `Current operator` = email або інший нормалізований principal
- `Tenant` = `local-tenant`
- `Organization` = `local-org`
- `Roles` містить `operator.viewer` або іншу внутрішню роль HidBridge

## 8. Правило на майбутнє

Для production governance:
- зовнішній IdP не повинен напряму визначати внутрішні authorization policies HidBridge;
- `tenant/org/roles` мають контролюватися централізовано в `Keycloak` або майбутньому `HidBridge.Identity`.


Click-by-click runbook для Keycloak UI claim mapping:
- `Docs/SystemArchitecture/HidBridge_Keycloak_UI_Claim_Mapping_Clickpath_UA.md`
