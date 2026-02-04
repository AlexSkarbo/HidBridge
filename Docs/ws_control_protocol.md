# WebSocket control protocol (draft)

Цей документ описує окремий канал керування (миша/клавіатура/геймпад) через WebSocket. Відео передається іншим каналом і тут не розглядається.

## Цілі

- мінімальна затримка, стабільність
- масштаб: багато клієнтів -> сервер -> багато B_host (керованих ПК)
- підтвердження доставки (ACK/NACK), контроль частоти
- сумісність з REST (спільні концепції itf/targetId)
- JWT для авторизації

## Транспорт

- WS endpoint: `ws(s)://SERVER/control`
- JWT:
  - нативні клієнти: `Authorization: Bearer <jwt>`
  - браузер: `?access_token=<jwt>` у URL
- Сервер може закрити WS при `401/403`.

## Формат повідомлень

Перший етап (v1) використовує JSON кадри. Пізніше можливий бінарний режим.

```json
{ "t": "hello", "v": 1, "client": "app", "targets": ["bhost-1"] }
```

```json
{ "t": "welcome", "v": 1, "sessionId": "abc", "maxRateHz": 250 }
```

```json
{ "t": "input", "seq": 123, "targetId": "bhost-1", "device": "mouse", "payload": { "dx": 1, "dy": 0, "wheel": 0, "buttons": 0 } }
```

```json
{ "t": "batch", "items": [ { "seq": 1, "device": "mouse", "payload": { "dx": 1, "dy": 0 } }, { "seq": 2, "device": "keyboard", "payload": { "type": "press", "usage": 4 } } ] }
```

```json
{ "t": "ack", "seq": 123 }
```

```json
{ "t": "nack", "seq": 123, "error": "busy" }
```

```json
{ "t": "ping", "ts": 123456 }
```

```json
{ "t": "pong", "ts": 123456 }
```

## Типи повідомлень

### hello (client -> server)

Поля:
- `v`: версія протоколу (1)
- `client`: ім'я клієнта
- `targets`: список бажаних `targetId` (необов'язково)

### welcome (server -> client)

Поля:
- `sessionId`: ідентифікатор сесії
- `maxRateHz`: рекомендована частота інпутів

### input (client -> server)

Поля:
- `seq`: монотонний лічильник
- `targetId`: ідентифікатор B_host (може бути опційним, якщо є дефолт)
- `device`: `mouse` | `keyboard` | `gamepad` (gamepad зарезервовано)
- `payload`: дані події (див. нижче)

Параметри mouse payload:
- `dx`, `dy`: int
- `wheel`: int
- `buttons`: int (bitmask)
- `itfSel`: byte (опційно)

Параметри keyboard payload:
- `type`: `down` | `up` | `press` | `report` | `type`
- `usage`: int (для down/up/press)
- `modifiers`: int (опційно)
- `keys`: int[] (для report)
- `text`: string (для type)
- `itfSel`: byte (опційно)

### batch (client -> server)

Поля:
- `items`: масив `input` об'єктів (до 64)

### ack/nack (server -> client)

- `ack` підтверджує доставку `seq`
- `nack` містить `error` (`busy`, `rate_limit`, `bad_payload`, `no_target`)

### ping/pong

Поля:
- `ts`: unix ms або монотонний лічильник клієнта

## Семантика доставки

- клієнт може посилати без очікування `ack`, але має тримати ковзне вікно (рекомендовано 64)
- при `nack` клієнт може повторити подію (якщо це має сенс)
- при `rate_limit` клієнт зменшує частоту

## Status channel

Окремий WS endpoint для статусів: `ws(s)://SERVER/status`

Сервер надсилає повідомлення:

```json
{ "t": "status", "ts": 123456, "uptimeMs": 1234, "hmacMode": "bootstrap", "uart": { ... }, "deviceId": "..." }
```

Клієнт може відправляти `ping`:

```json
{ "t": "ping", "ts": 123456 }
```

та отримує `pong`.

## Мапінг до REST

Внутрішньо сервер використовує ті ж механізми, що і REST:
- mouse move/wheel/buttons -> `/mouse/*`
- keyboard -> `/keyboard/*`

## Shortcut chords

Для “гарячих клавіш” можна відправляти chord рядком:

```json
{ "type": "keyboard.shortcut", "shortcut": "Ctrl+Alt+Del", "holdMs": 80, "itfSel": 2 }
```

`shortcut` підтримує `Ctrl/Alt/Shift/Win` + `F1..F24` + букви/цифри та часті назви (`Del`, `Tab`, `Enter`, `Esc`).

## Gamepad (резерв)

Тип `device=gamepad` зарезервовано, протокол буде розширено окремо.
