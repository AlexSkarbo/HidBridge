# HidBridge Keycloak UI Claim Mapping — Click-by-Click Runbook (UA)

Дата: `2026-03-03`  
Статус: `practical clickpath`

Пов'язані документи:
- `Docs/SystemArchitecture/HidBridge_Keycloak_Claim_Mapping_Runbook_UA.md`
- `Docs/SystemArchitecture/HidBridge_Identity_SSO_Baseline_UA.md`

## 1. Ціль

Налаштувати `Keycloak` так, щоб brokered Google user коректно відображався в `HidBridge.ControlPlane.Web`.

Після налаштування в `Settings` мають бути:
- `Current operator`: email користувача
- `User ID`: внутрішній `sub` / Keycloak user id
- `Tenant`: `local-tenant`
- `Organization`: `local-org`
- `Roles`: тільки внутрішні ролі HidBridge, наприклад `operator.viewer`
- `User added at`: дата створення користувача, якщо окремо змеплено claim

## 2. Поточний фактичний стан

У твоєму контурі вже працює:
- `Google -> Keycloak -> HidBridge.ControlPlane.Web`
- `login/logout`
- `tenant_id`
- `org_id`
- `operator.viewer`

Проблеми, які ще треба добити:
- старі mapper-и були створені як `User Attribute`
- тип mapper-а в цьому UI не змінюється "на місці"
- технічні ролі `offline_access`, `uma_authorization`, `default-roles-*` не треба сприймати як ролі HidBridge

## 3. Важлива особливість цього Keycloak UI

У твоїй версії `Keycloak`:
- може не бути окремого tab `Attributes` у картці користувача
- не можна змінити `Mapper type` у вже створеного mapper-а

Тому правильний шлях тут такий:
1. старий mapper **не видаляти**
2. старий mapper **деактивувати**
3. створити новий mapper з **іншою назвою mapper-а**
4. але з правильним `Token Claim Name`

Це нормальний і безпечний шлях.

## 4. Що вже є і що треба лишити

У `Client scopes -> hidbridge-operator -> Mappers` у тебе вже є:
- `tenant_id`
- `org_id`
- `principal_id`
- `role`

З них:
- `role` лишаємо
- `tenant_id`, `org_id`, `principal_id` треба виправити логічно

## 5. Як правильно виправити `tenant_id`

### 5.1 Деактивувати старий mapper

Відкрити існуючий mapper:
- `tenant_id`

Вимкнути в ньому:
- `Add to ID token`
- `Add to access token`
- `Add to userinfo`
- `Add to token introspection`

Після цього зберегти mapper.

### 5.2 Створити новий mapper

Шлях:
- `Client scopes`
- `hidbridge-operator`
- `Mappers`
- `Add mapper`

Параметри:
- `Mapper type`: `Hardcoded claim`
- `Name`: `tenant_id_static`
- `Token Claim Name`: `tenant_id`
- `Claim value`: `local-tenant`
- `Claim JSON Type`: `String`

Увімкнути:
- `Add to ID token`
- `Add to access token`
- `Add to userinfo`

## 6. Як правильно виправити `org_id`

### 6.1 Деактивувати старий mapper

Відкрити існуючий mapper:
- `org_id`

Вимкнути в ньому:
- `Add to ID token`
- `Add to access token`
- `Add to userinfo`
- `Add to token introspection`

### 6.2 Створити новий mapper

Параметри:
- `Mapper type`: `Hardcoded claim`
- `Name`: `org_id_static`
- `Token Claim Name`: `org_id`
- `Claim value`: `local-org`
- `Claim JSON Type`: `String`

Увімкнути:
- `Add to ID token`
- `Add to access token`
- `Add to userinfo`

## 7. Як правильно виправити `principal_id`

Тут не треба робити `Hardcoded claim`.

Потрібно, щоб `principal_id` дорівнював email користувача. У твоєму поточному кейсі `username` уже дорівнює email, тому правильний baseline:

### 7.1 Деактивувати старий mapper

Відкрити mapper:
- `principal_id`

Вимкнути:
- `Add to ID token`
- `Add to access token`
- `Add to userinfo`
- `Add to token introspection`

### 7.2 Створити новий mapper

Параметри:
- `Mapper type`: `User Property`
- `Name`: `principal_id_username`
- `User property`: `username`
- `Token Claim Name`: `principal_id`
- `Claim JSON Type`: `String`

Увімкнути:
- `Add to ID token`
- `Add to access token`
- `Add to userinfo`

Результат:
- `principal_id` у токені стане `alexandr.skarbo@gmail.com`
- `Current operator` у web shell теж стане email

## 8. Як залишити `User ID`

Поле `User ID` у web shell береться з:
- `sub`
- або внутрішнього `NameIdentifier`

Це внутрішній `Keycloak user id`, наприклад:
- `361abfa9-7370-41ba-a870-49ad18fc79f9`

Для цього нічого окремо мапити не треба.

## 9. Як додати `User added at`

Щоб у web shell з'явилось поле:
- `User added at`

потрібен окремий claim.

### 9.1 Спроба через `User Property`

У тому ж `hidbridge-operator` створити mapper:
- `Mapper type`: `User Property`
- `Name`: `user_created_at`
- `User property`: `createdTimestamp`
- `Token Claim Name`: `user_created_at`
- `Claim JSON Type`: `String`

Увімкнути:
- `Add to ID token`
- `Add to access token`
- `Add to userinfo`

### 9.2 Якщо `createdTimestamp` не підтримується цим UI

Тоді це поле поки залишиться як:
- `not available` / `недоступно`

І це нормально для першого baseline.

## 10. Як правильно налаштувати ролі HidBridge

### 10.1 Чому ти не бачив `operator.viewer`

На екрані призначення ролей у тебе був активний:
- `Filter by clients`

Через це ти дивився не на `realm roles`.

### 10.2 Що робити

У модальному вікні `Assign roles`:
1. прибрати `Filter by clients`
2. очистити пошук
3. шукати:
   - `operator.viewer`

### 10.3 Якщо ролі немає

Створи в:
- `Realm roles`

такі ролі:
- `operator.viewer`
- `operator.moderator`
- `operator.admin`

## 11. Як прив'язати роль до групи

Ти вже створив групу:
- `hidbridge-operators`

Це правильно.

Далі:
1. Відкрити `Groups`
2. Відкрити `hidbridge-operators`
3. Відкрити `Role mapping`
4. Додати realm role:
   - `operator.viewer`

Після цього:
1. Відкрити користувача
2. Відкрити `Groups`
3. Додати користувача в:
   - `hidbridge-operators`

## 12. Як налаштований mapper `role`

Mapper `role` уже є, і його треба лишити.

Його задача:
- віддати realm roles у токен

Саме через нього після входу web shell побачить:
- `operator.viewer`

## 13. Які ролі web shell має показувати

У токені можуть бути і технічні ролі Keycloak:
- `offline_access`
- `default-roles-hidbridge-dev`
- `uma_authorization`

Це нормально.

Але `HidBridge.ControlPlane.Web` має показувати користувачу тільки внутрішні ролі HidBridge:
- `operator.viewer`
- `operator.moderator`
- `operator.admin`

Тобто технічні ролі Keycloak не повинні трактуватися як ролі оператора.

## 14. Порядок дій без видалення існуючих mapper-ів

Ось правильний порядок для твого UI:

1. Старий `tenant_id` деактивувати
2. Створити `tenant_id_static` як `Hardcoded claim`
3. Старий `org_id` деактивувати
4. Створити `org_id_static` як `Hardcoded claim`
5. Старий `principal_id` деактивувати
6. Створити `principal_id_username` як `User Property -> username`
7. Перевірити або створити `Realm roles`
8. Дати `operator.viewer` групі `hidbridge-operators`
9. Додати Google user у групу `hidbridge-operators`
10. Перелогінитися через Google

## 15. Що перевірити після повторного логіну

У `Settings` очікується:
- `Current operator`: email
- `User ID`: Keycloak UUID
- `Tenant`: `local-tenant`
- `Organization`: `local-org`
- `Roles`: `operator.viewer`
- `User added at`: дата, якщо змеплено `createdTimestamp`; інакше `недоступно`

## 15.1 Фактичний робочий стан, який уже підтверджено

У поточному локальному контурі вже підтверджено:
- `Google -> Keycloak -> HidBridge.ControlPlane.Web` працює;
- `Current operator` уже може відображатися як email;
- `User ID` уже показується з `sub`;
- `Tenant` і `Organization` уже можуть приходити як `local-tenant` / `local-org`;
- `Roles` уже можуть показувати тільки `operator.viewer`.

Тобто для переходу до backend enforcement достатньо такого практичного стану:
1. `principal_id` віддати через `User Property -> username`;
2. `tenant_id` і `org_id` віддати через `Hardcoded claim`;
3. `operator.viewer` видати через realm role + group.

## 15.2 Що не треба робити

- не треба видаляти старі mapper-и;
- не треба намагатися змінювати `Mapper type` у вже створеного mapper-а;
- не треба створювати новий mapper з тим самим `Name`.

Правильний шлях:
1. старий mapper вимкнути;
2. новий mapper створити з іншим `Name`;
3. залишити правильний `Token Claim Name`.

## 15.3 `User added at` — чому зараз може бути `not available`

Поле `Created at` у `Keycloak Admin UI` не потрапляє в токен автоматично.

Для `HidBridge.ControlPlane.Web` воно з'явиться тільки якщо `Keycloak` реально віддає один із claims:
- `user_created_at`
- `createdTimestamp`
- `created_at`

Тому:
- наявність `Created at` в admin UI ще не означає, що web shell його побачить;
- без окремого mapper-а поле `User added at` цілком коректно лишається `not available`.

Якщо поточний `Keycloak UI` не дозволяє стабільно віддати `createdTimestamp` у токен, це треба вважати обмеженням поточного token-mapping baseline, а не багом web shell.

## 16. Що можна автоматизувати пізніше

Ручний baseline добрий для старту, але далі це краще автоматизувати.

### 16.1 `Identity providers -> google -> Mappers`

Тут можна автоматично переносити дані з Google профілю у локального brokered user.

Наприклад:
- email
- display name
- custom user attributes

### 16.2 `Default groups`

Правильний practical baseline:
- усіх нових brokered users класти в групу:
  - `hidbridge-operators`

Тоді роль `operator.viewer` буде приходити автоматично.

### 16.3 `First broker login flow`

Через `First broker login flow` можна:
- автоматично лінкувати користувача
- класти його в default group
- задавати первинні policy rules

### 16.4 Автоматичне role assignment

Базова модель безпеки:
- новий зовнішній користувач отримує тільки:
  - `operator.viewer`
- `operator.moderator` і `operator.admin` лишаються ручними

## 17. Рекомендований practical baseline саме для тебе зараз

Для твого поточного контуру це правильний мінімум:

1. `tenant_id` -> `Hardcoded claim(local-tenant)`
2. `org_id` -> `Hardcoded claim(local-org)`
3. `principal_id` -> `User Property(username)`
4. `role` -> лишити як є
5. `operator.viewer` -> видати через групу `hidbridge-operators`
6. Перелогінитися через Google

Це достатньо, щоб перейти до наступного етапу:
- `tenant/org-aware enforcement` у backend
