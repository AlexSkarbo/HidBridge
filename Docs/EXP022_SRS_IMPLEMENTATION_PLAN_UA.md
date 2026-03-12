# EXP-022 Next-Gen Remote Control Platform
## SRS + Покроковий детальний план імплементації (UA)

Версія: `0.1-draft`  
Дата: `2026-02-25`

---

## 1. Вступ

### 1.1 Призначення
Визначити production-рішення на базі EXP-022 для:
- низької затримки відео
- стабільного аудіо
- керування мишею/клавіатурою в реальному часі
- вимірюваної та відтворюваної якості/затримки

Документ побудований у стилі Wiegers: бізнес-вимоги, користувацькі вимоги, функціональні вимоги, нефункціональні атрибути, інтерфейси, обмеження, верифікація, план впровадження.

### 1.2 Межі системи
Топологія:

`Браузер-клієнт <-> Control/Media сервіси <-> Capture + RP2040 HID Bridge <-> Target PC`

Поза scope:
- хмарна multi-tenant SaaS платформа
- повна система акаунтів/ідентифікації
- native mobile клієнти

### 1.3 Терміни
- E2E latency: візуальна затримка за синхронізованим годинником.
- HID semantic success: цільова дія виконана на target (а не тільки UART ACK).
- Profile: зафіксований набір AV параметрів.

---

## 2. Бізнес-вимоги

### BR-1: Керованість
Оператор має керувати target PC з прийнятною реакцією.

### BR-2: Стабільність
Сесія має бути придатною для тривалої роботи (>=30 хв) без критичних фризів.

### BR-3: Якість
Базова якість 1080p при low-latency обмеженнях.

### BR-4: Діагностованість
Команда має швидко локалізувати проблеми по телеметрії/логах.

### Метрики успіху
- Median visual latency у LAN <= 200 ms (ціль baseline)
- P95 latency <= 300 ms
- Freeze >1s: 0 на 30-хв прогоні
- HID semantic success >= 99% у скриптовій валідації

---

## 3. Зацікавлені сторони та класи користувачів

### Зацікавлені сторони
- Власник продукту/системи
- Інженер стрімінгу
- Інженер firmware/HID
- QA/тест-оператор

### Класи користувачів
- Оператор: стартує сесії, керує target.
- QA: виконує матрицю тестів та перевіряє KPI.
- Розробник: дебажить транспорт, report-формати та профілі тюнінгу.

---

## 4. Користувацькі вимоги (Use Cases)

### UC-1 Запуск сесії
1. Оператор запускає HID-сервіс і AV publish.
2. Відкриває web monitor.
3. Натискає Play і Connect HID.
4. Вмикає Arm Input і керує target.

### UC-2 Валідація low-latency
1. Відкриває clock overlay на target і на client.
2. Система дає заміри візуальної затримки.
3. Оператор порівнює профілі A/B/C.

### UC-3 Діагностика відмови
1. Оператор бачить фриз/відсутність input.
2. Відкриває diagnostics.
3. Визначає клас проблеми: AV / transport / HID semantic mismatch.

---

## 5. Функціональні вимоги

## 5.1 AV-сервіс
- FR-AV-1: Приймати capture video+audio, публікувати через WHIP.
- FR-AV-2: Підтримувати профілі (`balanced`, `low-latency`, `stability`).
- FR-AV-3: Експортувати runtime-метрики (fps, keyframe interval, rtt, nack/pli, drops).
- FR-AV-4: Бажано підтримати безпечне runtime-перемикання профілів.

## 5.2 HID-сервіс
- FR-HID-1: Приймати команди через WS (`/ws/control`).
- FR-HID-2: Мапити події керування у UART HID inject.
- FR-HID-3: Підтримувати `0xFF mouse` і `0xFE keyboard` селектори.
- FR-HID-4: Будувати report на основі `GET_REPORT_LAYOUT (0x05)`.
- FR-HID-5: Мати fallback `GET_REPORT_DESC (0x04)` parser при відсутньому/неповному layout.
- FR-HID-6: Віддавати ACK/NACK з класифікованими помилками.

## 5.3 Web-клієнт / Monitor
- FR-UI-1: Відтворення WHEP-потоку.
- FR-UI-2: Connect/disconnect HID каналу.
- FR-UI-3: Pointer lock + захоплення клавіатури з явним arm/disarm.
- FR-UI-4: Показ live-статусу: connected, armed, errors, last ACK.
- FR-UI-5: Показ короткої AV/HID діагностики.

## 5.4 Diagnostics API
- FR-DIAG-1: `/health` для AV і HID сервісів.
- FR-DIAG-2: `/diag/interfaces` (mounted/active HID інтерфейси).
- FR-DIAG-3: `/diag/layouts` (джерело: layout/descriptor + деталі).
- FR-DIAG-4: `/diag/reports/last` (hex + decoded fields).
- FR-DIAG-5: Лічильники: commands, acks, errors, timeouts, retries.

---

## 6. Нефункціональні вимоги

### NFR-LAT-1 (Latency)
- Утримувати цільовий latency envelope для LAN baseline.

### NFR-STAB-1 (Stability)
- Без фризів >1s у 30-хв тесті.
- Автовідновлення після транзитних збоїв транспорту.

### NFR-REL-1 (Reliability)
- Детермінована політика retry/timeout/backpressure.

### NFR-OBS-1 (Observability)
- Структуровані логи з correlation ID.
- Єдина timeline подій AV + HID.

### NFR-SEC-1 (Security)
- HMAC-захист UART-контролю.
- За потреби origin/token перевірка WS endpoint.

---

## 7. Обмеження та припущення

### Обмеження
- Поточний firmware UART protocol є базовим контрактом.
- RP2040 bridge апаратно не змінюємо на першому етапі.
- Деплой спочатку в межах локальної LAN.

### Припущення
- Capture-пристрій може видавати стабільний frame cadence.
- Target OS коректно обробляє стандартні HID usage.

---

## 8. Зовнішні інтерфейси

## 8.1 UART Protocol
- Команди inject/list/layout/descriptor/device-id.
- Помилки мають прозоро відображатися в UI.

## 8.2 WebSocket Control Contract
- Mouse: move, wheel, button down/up.
- Keyboard: text, usage press, shortcut.
- Відповідь: `{id, ok, error}`.

## 8.3 WHEP/WHIP
- AV-транспорт через SRS з negotiated codec.

---

## 9. Покроковий план імплементації

## Фаза 0 — Freeze baseline (1-2 дні)
1. Заморозити поточні EXP-022 коміти/параметри.
2. Зафіксувати baseline KPI.
3. Підготувати labels у трекері: `av`, `hid`, `latency`, `quality`, `diag`.

Результат: відтворюваний baseline + скрипти старту.

## Фаза 1 — Розділення сервісів (3-5 днів)
1. Винести HID-логіку з exp-022 в окремий сервіс.
2. Залишити exp-022 як інтеграційний стенд (AV + UI).
3. Формалізувати контракти UI <-> HID service.

Результат: незалежний HID процес з WS endpoint і UART pipeline.

## Фаза 2 — Доведення HID семантики (4-7 днів)
1. Завершити сумісність парсингу layout.
2. Додати fallback через descriptor parser.
3. Додати per-interface cache і invalidation.
4. Впровадити чітку taxonomy помилок.

Результат: стабільна семантика mouse/keyboard на відомих пристроях.

## Фаза 3 — AV framework для тюнінгу (4-6 днів)
1. Реєстр профілів A/B/C + runtime switch.
2. AV telemetry endpoints + periodic snapshots.
3. Автоматичний збір latency samples.

Результат: вимірюваний цикл тюнінгу з порівнянням профілів.

## Фаза 4 — Єдина діагностика (3-4 дні)
1. Об’єднана timeline AV + HID.
2. Sampled log декодування report.
3. Експорт діагностичного пакету в 1 клік.

Результат: практичний “5-хв root cause”.

## Фаза 5 — Валідація та hardening (5-8 днів)
1. Прогін матриці сумісності (30 хв на профіль).
2. Фікс top-регресій.
3. Фіналізація production default profile.

Результат: release candidate з доказом KPI.

---

## 10. Верифікація та приймання

### Тестові набори
- TS-1 HID semantic tests (scripted)
- TS-2 AV continuity tests
- TS-3 Latency benchmark
- TS-4 Endurance (30-60 хв)

### Acceptance Gates
- AG-1 Усі P0/P1 дефекти закриті.
- AG-2 KPI виконано у 3 послідовних прогонах.
- AG-3 Перевірено recovery-поведінку (restart/network blips).

---

## 11. Ризики та пом’якшення

- R-1 Jitter capture-пристрою -> fallback до консервативного профілю.
- R-2 Edge-cases descriptor -> parser + raw inject debug.
- R-3 CPU overload -> profile caps + telemetry alarms.
- R-4 Приховані packet loss -> expose NACK/PLI/RTT + adaptive profile.

---

## 12. Стратегія релізу

1. Внутрішня beta у LAN-лабораторії.
2. Контрольований pilot на цільових робочих місцях.
3. Фіксація production profile + change-control на тюнінг.

---

## 13. Матриця трасування (коротко)

| Бізнес-вимога | Функціональні/NFR |
|---|---|
| BR-1 Керованість | FR-HID-1..6, FR-UI-1..5 |
| BR-2 Стабільність | FR-AV-3..4, NFR-STAB-1 |
| BR-3 Якість | FR-AV-1..4, NFR-LAT-1 |
| BR-4 Діагностованість | FR-DIAG-1..5, NFR-OBS-1 |

