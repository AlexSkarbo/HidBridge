# План Міграції HID Керування (з `exp-022` у production-сервіс)

## 1) Поточний стан

Експеримент `exp-022-datachanneldotnet` підтвердив працездатність повного ланцюга:

- Браузер (WebRTC клієнт) -> WebSocket керування
- процес `exp-022` -> UART (`COM6`, `3,000,000`)
- RP2040 B_host -> RP2040 A_device
- USB HID до цільового ПК

Відео/аудіо стрім працює.  
Керування мишею/клавіатурою працює частково (ще не повністю детерміновано для всіх форматів report/пристроїв).

## 2) Чому треба виносити з `exp`

Зараз у `exp-022` змішані:

- експериментальна AV-логіка
- експериментальна control-plane логіка
- HID транспорт + побудова HID report

Це ускладнює підтримку та валідацію.  
Production-поведінка HID потребує окремого сервісу зі стабільним API, діагностикою та матрицею сумісності.

## 3) Цільова архітектура

Розділити відповідальність:

- `exp-022`: лише AV-тестовий стенд (WHEP/WHIP, перевірка затримки)
- Новий сервіс (`HidBridgeControlService` або розширений `HidControlServer`): лише HID-керування

Цільовий шлях даних:

- Browser -> WebSocket (або gateway з DataChannel) -> HID-сервіс -> UART protocol -> RP2040 bridge -> Target PC

## 4) Обов’язковий функціонал нового HID-сервісу

### Транспорт і протокол

- Надійний UART framing (SLIP + CRC + HMAC)
- Резолв інтерфейсів (`0xFF` для mouse, `0xFE` для keyboard)
- Контроль timeout/retry/backpressure

### Побудова HID report

- Основне джерело: `GET_REPORT_LAYOUT (0x05)`
- Обов’язковий fallback: `GET_REPORT_DESC (0x04)` + parser descriptor
- Коректна обробка:
  - Report ID
  - Boot vs report protocol
  - Signed/unsigned поля осей
  - Різних довжин report на різних інтерфейсах

### Семантика input

- Миша: move, wheel, кнопки (`left/right/middle/back/forward`)
- Клавіатура: text, shortcuts, raw usage+modifiers, key down/up

## 5) API та діагностика

Потрібні явні діагностичні endpoint’и:

- `health` (стан транспорту/UART)
- snapshot активних інтерфейсів (`LIST_INTERFACES`)
- джерело layout для кожного інтерфейсу (`layout` vs `descriptor`)
- останні інжекти (hex + декодовані поля)
- лічильники command/ack/error

Це необхідно, щоб чітко розділяти:

- “транспорт працює” і
- “семантика HID інжекції коректна”

## 6) Матриця тестів і критерій готовності

Мінімальний критерій:

- 99%+ успішної семантично коректної інжекції у скриптових сценаріях.

Матриця:

- Миша: move/wheel/buttons + press/release/click
- Клавіатура: ASCII text, shortcuts (Ctrl/Alt/Shift/Meta), raw usages
- Цільові ОС: мінімум Windows baseline
- Різні варіанти descriptor/report-id
- Довгий прогін стабільності (30+ хв)

## 7) Кроки міграції

1. Заморозити HID-зміни в `exp-022` (feature freeze).
2. Створити окремий HID-сервіс (або відгалуження від `Tools/Server/HidControlServer`).
3. Винести UART + report-building у окремі модулі.
4. Додати descriptor fallback і кеш layout.
5. Додати діагностичні endpoint’и та структуровані логи.
6. Прогнати матрицю сумісності та виправити semantic mismatch.
7. Залишити `exp-022` як AV-стенд, що використовує endpoint нового HID-сервісу.

## 8) Definition of Done

Міграція завершена, коли:

- HID business-logic більше не живе в `exp-022`.
- Новий HID-сервіс проходить матрицю сумісності.
- Поведінка input відтворювана після рестартів і на довгих прогонах.
- За діагностикою причина збою локалізується менш ніж за 5 хвилин.
