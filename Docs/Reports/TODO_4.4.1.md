# Дорожня карта 4.4.1

1. **Подвійні Pico (LS/FS):**
   - Виділити окрему плату для Low-Speed (TinyUSB у OPT_MODE_LOW_SPEED, виключені CDC/інші класові дескриптори).
   - Реалізувати мультиплексор через SPI або I²C: B_host (master) → LS/FS A_device (slaves). Протокол повинен дозволяти перемикання між пристроями без перезавантаження ПК.
   - На рівні програмного стеку: загальний `remote_storage`, але окремі інстанси TinyUSB/uart_transport для кожного каналу.

2. **Unit/Integration tests для string_manager:**
   - Покрити сценарії: кешований рядок, synthetic-перша відповідь, порожня-друга відповідь, idx>cache (eviction), timeout fallback.
   - Інтерфейс: створити lightweight harness, який мокне `send_frames` та `time_ms`; перевіряти, що `STRING_REQ` черга не зависає.

3. **Документація state-machine + автотести:**
   - Зафіксувати поточний `control_flow.md` у README/Docs, додати sequence diagram (A_device ↔ B_host ↔ PC) для CASES: boot, DEVICE_RESET, PF_UNMOUNT.
   - Написати smoke-тест скрипт (Python) який перевіряє, що під час логів `string idx>2` з’являється STALL і жоден `STRING_REQ` не шле реальні дані.

4. **Telemetry / Health-checks:**
   - A_device: лічильники GET_REPORT/STRING_REQ/STALL = expose через UART debug команду.
   - B_host: watchdog на `s_ctrl_irq_pending` (якщо READY не приходить 2 сек після PF_DESC_DONE → повторити DEVICE_RESET / репортити помилку).
