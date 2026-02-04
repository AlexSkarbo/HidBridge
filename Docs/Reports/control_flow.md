# HID Proxy control flow (build 4.3.x → 4.4.1)

## Нотатки
- **Транспорт:** UART@1 MBaud + IRQ GPIO.
- **B_host** збирає дескриптори з реального HID і шле їх chunks A_device.
- **A_device** зберігає їх у `remote_storage` і оголошує себе через TinyUSB.

## Основні стани
```
┌────────────┐   PF_DESC_*   ┌─────────────────┐  DESCRIPTORS DONE + PF_CTRL_DEVICE_RESET
│  Idle/Init │──────────────►│   Capture mode   │─────────────────────────────────────────┐
└────────────┘                └─────────────────┘                                         │
        ▲                                                                                 │
        │ PF_UNMOUNT / remote reset                                                        │
        │                                                                                 ▼
 ┌────────────────┐  TinyUSB ready + PF_CTRL_READY  ┌────────────────────┐   STRING_REQ/IRQ   ┌────────────────────┐
 │ TinyUSB attach │────────────────────────────────►│  Ready/Streaming   │───────────────────►│  String fetch wait │
 └────────────────┘                                  └───────┬───────────┘                    └──────────┬─────────┘
        │ PF_CTRL_DEVICE_RESET / USB error                   │ PF_CTRL_SET_IDLE/GET_REPORT               │ timeout/fallback
        └─────────────────────────────────────────────────────┴───────────────────────────────────────────┘
```
- **Capture mode:** B_host шле PF_DESC_*; A_device приймає всі шматки, але TinyUSB ще не стартує.
- **TinyUSB attach:** A_device виконує `tud_connect()` лише після DEVICE_RESET від B_host. Це гарантує, що ПК бачить проксі лише коли усі дескриптори синхронізовані.
- **Ready/Streaming:** після `tud_mount_cb` A_device шле `PF_CTRL_READY`, B_host знімає паузу й починає репорти (HID IN).
- **String fetch wait:** коли TinyUSB просить рядок, A_device шле `PF_CTRL_STRING_REQ`, B_host або віддає кеш, або генерує фолбек/порожній дескриптор. Якщо `should_force_fallback(index)>2`, другий запит вертає пустий дескриптор, щоб Windows зупинилась.

## Input pipeline notes
- PF_INPUT передає весь HID-звіт. Якщо Remote HID опис містить Report ID (`0x85`), A_device відділяє перший байт як `report_id` і передає решту у `tud_hid_report(report_id, data, len-1)`.
- Після кожного PF_INPUT (навіть у стані паузи) B_host знову викликає `tuh_hid_receive_report()` — нові звіти від фізичної миші не блокуються.
- PROTO_MAX дозволяє звіти будь-якої довжини; UART транспортує їх без урізання.

## Контрольні кадри
| Cmd | Хто шле | Призначення |
|-----|---------|-------------|
| PF_CTRL_READY | A_device | TinyUSB змонтовано, можна відновлювати `tuh_hid_receive_report`. |
| PF_CTRL_DEVICE_RESET | B_host | Форсує `tinyusb_restart()` після завершення дескрипторів або при потрібному переenumeration. |
| PF_CTRL_SET_IDLE / GET_REPORT / SET_REPORT | A_device → B_host | Проксірування керуючих запитів від PC до реального HID. |
| PF_CTRL_STRING_REQ | A_device | TinyUSB просить рядок; B_host повертає PF_DESC_STRING chunk або фолбек. |

## Тайм-аути / IRQ
- A_device тримає IRQ лінію низькою і пульсує після кожного контрольного кадру (STRING_REQ, READY, GET_REPORT). Це розбуджує B_host навіть у стані `s_control_poll_enabled=false`.
- `STRING_REQ`: A_device повторює запит лише якщо `remote_string_desc_t.allow_fetch = true` і дані не отримані протягом ~200 мс (`wait_for_string_ready`). Після другого фонового виклику TinyUSB отримує STALL (через NULL).
- B_host в string_manager має fallback-таймаут 10 мс: якщо реальний рядок не приходить, одразу відправляє `IDX###` (перший раз) і пустий дескриптор (другий раз). Для idx>2 тепер одразу надсилається порожній дескриптор, а кеш позначається “synthetic sent”.

## Пункти для валідації
- READY → SET_IDLE/GET_REPORT: перевірити, що після будь-якого DEVICE_RESET B_host знову запускає `tuh_hid_receive_report` лише після READY.
- STRING_REQ → STALL: для індексів >2 A_device повинен одразу повертати NULL (TinyUSB -> STALL); у логах видно `string idx=… unsupported -> STALL`.
- RECOVERY: при PF_UNMOUNT (фізичний HID від’єднано) обидві сторони повертаються в Idle, `remote_desc_reset()` обнуляє allowlist й TinyUSB відключається.

## Як запускати string_manager harness
1. Потрібен host-компілятор (gcc/clang). Зібрати можна так:
   ```
   cd HidBridge
   gcc Firmware/tests/string_manager/string_manager_harness.c Firmware/B_host/string_manager.c \
       -IFirmware/tests/string_manager -IFirmware/B_host -IFirmware/common -o string_manager_harness
   ```
2. Запустити `./string_manager_harness` — побачите PASS/FAIL для кожного сценарію та короткий summary.
3. Щоб додати власний тест, допишіть функцію у `string_manager_harness.c` (використовуючи `harness_prepare_extra_string`, `string_manager_handle_ctrl_request`, `string_manager_task` тощо) і зареєструйте її в масиві `tests[]`.
