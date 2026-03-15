# ADR-001: Realtime Transport Abstraction (Room-Centric Fabric)

Дата: 2026-03-15  
Статус: Accepted

## Контекст

Поточна реалізація командного шляху у Platform працює через конкретний UART-connector.  
Це не покриває цільову архітектуру Room-Centric Hybrid Realtime Fabric, де transport має бути взаємозамінним (`UART`, `WebRTC DataChannel`, далі `WebSocket/Gateway Relay`).

## Рішення

Вводимо єдиний transport-контракт:

- `IRealtimeTransport`
- `IRealtimeTransportFactory`
- `RealtimeTransportProvider` (`Uart`, `WebRtcDataChannel`)
- уніфіковані типи повідомлень: `command`, `ack`, `event`, `heartbeat`
- уніфікована класифікація transport-помилок: `timeout`, `disconnected`, `rejected`, `protocol_error`

Базові інваріанти:

1. `Room` є головним контекстом координації; transport не володіє room/session бізнес-правилами.
2. `Device` є глобальною сутністю; маршрут управління визначається route-context, а не hardcoded UART-path.
3. `TransportHub`/factory не залежить від одного провайдера.
4. Auth/scope/policy перевірки залишаються у ControlPlane/Session-layer, не дублюються у transport.

## Наслідки

Позитивні:

- Можна додавати `WebRTC DataChannel` без переписування бізнес-логіки сесій.
- Легше підтримувати mixed topology (`direct`, `gateway`, `local connector`).
- Діагностика стає прозорішою: видно активний transport provider у runtime.

Компроміси:

- Зростає кількість інтерфейсів/адаптерів.
- Потрібен окремий MVP для signaling та WebRTC adapter.

## Межі ADR

Цей ADR фіксує лише transport-контракт і provider-selection у command-path.  
Він не визначає повний signaling protocol і не описує медіа-шар (`SFU/P2P`) детально.

## План імплементації

1. `IRealtimeTransport` + factory + `Uart` adapter (default).
2. Інтеграція в dispatch pipeline без регресій `demo-flow`.
3. Додавання `WebRtcDataChannel` provider як MVP adapter.
4. Розширення diagnostics/ops-status transport-метаданими.
