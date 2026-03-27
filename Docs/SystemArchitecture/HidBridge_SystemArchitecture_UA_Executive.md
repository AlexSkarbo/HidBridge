# HidBridge — Executive Summary системної архітектури

Версія: `0.2-draft`  
Дата: `2026-03-19`  
Мова: `Українська`  
Базовий детальний документ: `Docs/SystemArchitecture/HidBridge_SystemArchitecture_UA.md`
Актуальний runtime baseline: `Docs/SystemArchitecture/HidBridge_Runtime_Flow_2026-03-19_UA.md`

---

## Оновлення `2026-03-19`

Для поточної ітерації зафіксовано:

1. канонічний runtime шлях через `Platform/` core stack + external edge agents;
2. server-side policy ownership для readiness/lease/SLO/security;
3. canonical CLI-first orchestration через `HidBridge.RuntimeCtl` (`RuntimeCtl ...` лише compatibility);
4. `exp-022` як integration-lab (не production dependency).

---

## 1. Для чого цей документ

Це коротка управлінсько-технічна версія архітектурного аналізу HidBridge.

Документ потрібен, щоб швидко узгодити:

1. Який реальний стан системи зараз.
2. Чому поточний підхід упирається в межі стабільності/розвитку.
3. Яку модульну архітектуру беремо як наступний стандарт.
4. Який план впровадження дає результат по KPI: **затримка, стабільність, якість, керування**.

---

## 2. Поточна ціль продукту

HidBridge має забезпечити агентless remote control:

- на Target PC нічого не встановлюється
- керування йде через USB HID (миша/клавіатура)
- відео+аудіо йдуть через low-latency стрімінг

Ключова бізнес-мета:

- оператор отримує відчуття «живого» контролю з мінімальною затримкою
- сесія стабільна на тривалих інтервалах
- якість відео достатня для практичної роботи

---

## 3. Що вже доведено на практиці

На основі `Tools`, `Firmware`, `exp-022`:

1. Базова архітектурна ідея працює end-to-end.
2. AV-стрім (WHEP/WHIP) робочий.
3. HID-транспортний контур робочий (команди доходять, ACK є).
4. Clock-based метод дозволяє вимірювати реальну E2E затримку.
5. На LAN досягається робочий рівень latency (практично ~150–240 ms у стабільних профілях).

---

## 4. Ключові проблеми AS-IS

## 4.1 Архітектурні

1. Змішання відповідальностей у runtime.
2. Недостатньо формалізовані контракти між модулями.
3. Неповна єдина телеметрія AV+HID.
4. Експериментальний контур (`exp-022`) частково використовується як production-подібний.

## 4.2 Технічні

1. ACK по UART не гарантує правильну дію на target (семантика report).
2. Сильна залежність від layout/report-id різних HID-пристроїв.
3. Агресивні low-latency профілі можуть погіршувати стабільність (фрізи/аудіо-дропи).
4. Windows `HttpListener` у тестовому контурі показав нестабільну операційну поведінку (URLACL/HTTP.sys/IP listen edge cases).

## 4.3 Операційні

1. Root-cause аналіз часто ручний і повільний.
2. Бракує стандартизованих acceptance-gates на рівні всієї системи.

---

## 5. Головний висновок

Проблема вже не у відсутності функцій.  
Проблема у відсутності жорстких модульних меж і контрактів.

Тому наступний крок — не “ще один patch”, а керований перехід до модульної архітектури.

---

## 6. Цільова архітектура (TO-BE, high-level)

Рекомендується 3-сервісна модель + спільні інфраструктурні модулі:

1. `Control Gateway` — прийом input подій (WS/DataChannel), валідація, ACK/NACK.
2. `HID Protocol + UART Service` — побудова report-ів, layout/descriptor fallback, UART v2 transport.
3. `Media Gateway` — capture/encode/publish, профілі, runtime metrics.
4. `Session Orchestrator` — lifecycle сесій, recovery policy, unified state.
5. `Telemetry/Diagnostics` — єдина timeline для AV+HID.

Схема:

```text
Browser Client
  -> Session Orchestrator API
  -> Control Gateway -> HID Protocol/UART -> RP2040 bridge -> Target PC
  -> Media Gateway -> WHIP/WHEP backend -> Browser playback
  -> Telemetry Service (from all components)
```

---

## 7. Що залишаємо, що міняємо

## 7.1 Залишаємо

1. Firmware-контур A_device/B_host як основне апаратне ядро.
2. UART v2 протокол (SLIP+CRC+HMAC) як базовий транспорт.
3. Практику profile-based AV tuning.

## 7.2 Міняємо

1. Виносимо HID runtime з `exp-022` у production-сервіс.
2. Виносимо media runtime у чіткий `Media Gateway`.
3. Робимо `exp-022` виключно інтеграційною лабораторією.
4. Переходимо на Kestrel-based hosting для control API.
5. Уніфікуємо schemas і error taxonomy.

---

## 8. Офіційні доповнення до архітектури (2026-02-27)

До базової архітектури офіційно додано 3 обов'язкові артефакти:

1. `Docs/SystemArchitecture/HidBridge_AgentContract_v1_UA.md`
2. `Docs/SystemArchitecture/schemas/HidBridge_AgentContract_v1_Message.schema.json`
3. `Docs/SystemArchitecture/HidBridge_ModuleArchitecture_Map_UA.md`
4. `Docs/SystemArchitecture/HidBridge_Clean_Modular_Agent_Architecture_UA.md`

Що це дає на практиці:

1. Єдине визначення `agent` (включно з agentless і connector-підходом).
2. Єдиний формат міжмодульних повідомлень (schema-first).
3. Єдину state machine і error taxonomy для control/media/HID контурів.
4. Формалізовану карту міграції поточних компонентів (`Tools`, `Firmware`, `exp-022`) у нову модульну платформу.

---

## 8. KPI та SLO для приймання

Рекомендовані цільові пороги (LAN baseline):

1. `Median visual latency <= 220 ms`
2. `P95 visual latency <= 320 ms`
3. `Freeze > 1s == 0` на 30 хв
4. `Audio dropouts` — не системні (допуск: 0 у контрольних прогонах)
5. `HID semantic success >= 99%` на scripted regression suite

Без виконання цих метрик реліз вважати неповним.

---

## 9. Пріоритетний план впровадження

## Етап 1 (P0): стабілізація контрактів і виділення HID

1. Freeze baseline параметрів та сценаріїв тесту.
2. Виділити `Control Gateway` + `HID Protocol/UART` у окремий сервіс.
3. Уніфікувати ACK/NACK та коди помилок.
4. Закрити layout/descriptor fallback до стійкого стану.

Результат: керування мишею/клавіатурою стабільне і передбачуване.

## Етап 2 (P1): модульний media runtime + спільна телеметрія

1. Виділити `Media Gateway` з керованими профілями.
2. Додати unified timeline AV+HID.
3. Додати автоматичний збір latency/freeze статистики.

Результат: рішення вимірюване, кероване, придатне до тривалих прогонів.

## Етап 3 (P2): оптимізація якості/латентності

1. Adaptive policy для профілів.
2. Device compatibility matrix розширити.
3. Полірування UX і діагностики для оператора.

Результат: production-ready система з контрольованим розвитком.

---

## 10. Основні ризики та пом'якшення

1. **Drift контрактів firmware/server**  
Пом'якшення: contract tests + golden frames + версіонування схем.

2. **Регресії latency при модульності**  
Пом'якшення: latency budget per hop + perf профайлінг кожного сервісу.

3. **HID несумісність на окремих девайсах**  
Пом'якшення: layout cache, descriptor parser, mapping packs, automated matrix.

4. **Операційна складність multi-process**  
Пом'якшення: Session Orchestrator + health probes + standardized restart policy.

---

## 11. Які рішення потрібно затвердити зараз

Рекомендується затвердити 5 рішень:

1. `exp-022` офіційно залишається тільки integration-lab.
2. Будуємо production на окремих сервісах (`Control`, `HID/UART`, `Media`, `Orchestrator`).
3. Приймаємо контрактний підхід (versioned schemas + taxonomy errors).
4. Приймаємо єдині acceptance KPI/SLO як release gate.
5. Закладаємо централізовану observability як обов'язкову функцію, не як “додаткову”.

---

## 12. Executive підсумок

HidBridge вже технічно життєздатний.  
Щоб перейти від “працює в експериментах” до “стабільно працює як продукт”, потрібен модульний перехід з чіткими контрактами.

Найкоротший шлях:

1. Спочатку стабільність і керованість HID.
2. Потім керований media runtime.
3. Потім оптимізація якості/затримки в межах формальних KPI.

Цей план мінімізує ризик і дає прогнозований результат по головній цілі:  
**максимально придатне remote control рішення з мінімальною затримкою, стабільним аудіо/відео і надійним керуванням.**

Оновлення стану на `2026-03-02`:

1. `P0–P2` уже закриті по baseline.
2. `P3` уже закритий по baseline:
   - sharing
   - invitations / approvals
   - command journal
   - unified timeline baseline
   - collaboration read models
3. `P4-1/P4-2` уже закриті по baseline:
   - policy-driven approvals
   - control arbitration
   - active controller lease
   - control ownership transitions
4. `P4-3` уже закритий по baseline:
   - inventory dashboard read models
   - audit dashboard read models
   - telemetry dashboard read models
5. `P4-4` уже закритий по baseline:
   - paged session projections
   - paged endpoint projections
   - filtered audit projections
   - filtered telemetry projections
6. `P4-5` уже закритий по baseline:
   - session replay bundle
   - archive diagnostics summary
   - archive audit/telemetry/command slices
7. Поточний робочий фокус:
   - thin operator UI shell
6. Затверджено порядок реалізації dashboard/UI:
   - спочатку backend projections
   - потім dashboard API
   - потім thin operator UI

Робоча UX-модель наступного етапу:

1. Інтерфейс типу operator console.
2. Основні екрани:
   - fleet overview
   - session details
   - audit dashboard
   - telemetry dashboard
3. UI має читати готові read models, а не збирати state з великої кількості сирих endpoint-ів у браузері.

## 9. Оновлення стану `2026-03-02` — Web / Identity / Tenant

1. `P4` data/read-model baseline уже реалізований.
2. Наступний фокус:
- thin operator UI shell
- `Blazor Web App`
- `Tailwind CSS` browser baseline
 - design baseline inherited from `landing/index.html`
 - localization baseline:
   - `en` default
   - `uk` secondary
   - browser locale auto-detect
   - manual override in settings
 - operator actions baseline уже реалізовано в `Session Details`
3. Security direction:
- `OIDC + cookie session`
- `RBAC + ABAC`
- tenant/org-aware enforcement after web shell baseline
- `HidBridge.ControlPlane.Web` не розглядається як identity server
- централізований SSO baseline: `Keycloak`
- `Identity & Access baseline` уже стартував у thin web shell:
  - cookie-backed session
  - optional `OIDC` challenge flow
  - baseline policies `OperatorViewer`, `OperatorModerator`, `OperatorAdmin`
  - tenant/org seams projected from claims


Оновлення web shell / Identity baseline:
- коли `Identity:Enabled=false`, development fallback identity тепер автентифікується автоматично без ручного login flow;
- ручний theme switch поки не вважається стабільним і винесений у backlog дизайну;
- у thin web shell уже додано authorization-aware UX:
  - current operator / roles / tenant / organization
  - пояснення доступності moderation/control дій
- `html, body` background зроблено напівпрозорим, щоб grid background із `landing/index.html` читався стабільно.


Оновлення стану `2026-03-03`:
- login/logout між `HidBridge.ControlPlane.Web` і `Keycloak` перевірено локально;
- Google federation через `Keycloak` перевірено;
- `Current operator` може відображатися як email;
- `User ID` відображається з `sub`;
- `User added at` лишається optional claim і без окремого mapper-а може бути `not available`;
- у backend стартував `tenant/org-aware enforcement` baseline для session-scoped collaboration/control/dispatch/read flows;
- при наявному caller context backend уже застосовує viewer/moderator operator-role gating.
- projections, dashboards і replay/archive diagnostics тепер теж tenant/org-scoped у service/query layer;
- `operator.admin` має cross-tenant override для читання і moderation-level query paths.
- smoke/integration path підтримує явну передачу caller scope через `X-HidBridge-*` headers;
- `403` відповіді API тепер повертають структуровані denial reasons із caller context і required roles/scope.
