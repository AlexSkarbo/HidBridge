# HidBridge — системна архітектура (глибокий аналіз AS-IS + цільова модульна TO-BE)

Версія: `0.2-draft`  
Дата: `2026-03-19`  
Мова: `Українська`  

---

## Оновлення `2026-03-19` (runtime canonical flow)

Актуальний production/runtime baseline винесено в окремий документ:

- `Docs/SystemArchitecture/HidBridge_Runtime_Flow_2026-03-19_UA.md`

Ключова зміна:

1. runtime path канонізовано на `Platform/` стеку (core containers + external edge agents);
2. canonical launcher: `HidBridge.RuntimeCtl` (CLI-first); PowerShell `run.ps1 -Task ...` — compatibility only;
3. `WebRtcTests/exp-022-datachanneldotnet` зафіксовано як lab-only.

---

## 1. Мета документа

Цей документ фіксує повний технічний зріз платформи **HidBridge** на основі практичного досвіду з:

- `Firmware/` (RP2040 A_device + B_host)
- `Tools/` (HidControlServer, Shared/Core/Infrastructure, WebRtc helpers)
- `WebRtcTests/exp-022-datachanneldotnet/` (експериментальний інтеграційний стенд)

Ціль документа:

1. Дати **чітку модель поточної (AS-IS)** архітектури.
2. Виявити **реальні системні вузькі місця**, які підтверджені експериментами.
3. Сформувати **цільову модульну (TO-BE)** архітектуру для наступної ітерації.
4. Дати основу для деталізованої імплементації (контракти модулів, межі відповідальності, міграція).

---

## 2. Обсяг та джерела аналізу

### 2.1 Обсяг

Аналіз покриває:

- контрольний контур HID (клавіатура/миша)
- AV контур (capture -> encode -> publish -> browser playback)
- сигнальний/керуючий контур (WS/WebRTC signaling)
- діагностику, стійкість, латентність, якість

### 2.2 Ключові досліджені артефакти

#### Firmware

- `Firmware/src/common/proxy_config.h`
- `Firmware/src/common/proto_frame.h`
- `Firmware/src/common/proto_frame.c`
- `Firmware/src/common/uart_transport.h`
- `Firmware/src/common/uart_transport.c`
- `Firmware/src/B_host/control_uart.c`
- `Firmware/src/B_host/hid_proxy_host.c`
- `Firmware/src/B_host/hid_host.c`
- `Firmware/src/A_device/hid_proxy_dev.c`
- `Firmware/src/A_device/remote_storage.c`

#### Tools

- `Tools/HidBridge.sln`
- `Tools/Server/HidControlServer/Program.cs`
- `Tools/Server/HidControlServer/HidUartClient.cs`
- `Tools/Server/HidControlServer/Services/InputReportBuilder.cs`
- `Tools/Server/HidControlServer/Services/ReportLayoutService.cs`
- `Tools/Server/HidControlServer/README.md`
- `Tools/WebRtcControlPeer/*`
- `Tools/WebRtcVideoPeer/*`

#### EXP-022

- `WebRtcTests/exp-022-datachanneldotnet/Program.cs`
- `WebRtcTests/exp-022-datachanneldotnet/start.ps1`
- `WebRtcTests/exp-022-datachanneldotnet/stream_monitor.html`
- `WebRtcTests/exp-022-datachanneldotnet/clock_server.ps1`
- `WebRtcTests/exp-022-datachanneldotnet/RUNBOOK.md`
- `WebRtcTests/exp-022-datachanneldotnet/RESULT.md`

#### Супровідна документація

- `Docs/uart_control_protocol.md`
- `Docs/ws_control_protocol.md`
- `Docs/webrtc_control_peer.md`
- `Docs/webrtc_video_peer.md`
- `Docs/webrtc_signaling.md`
- `Docs/webrtc_signaling_paths.md`
- `Docs/EXP022_SYSTEM_CONCLUSIONS_UA.md`
- `Docs/EXP022_SRS_IMPLEMENTATION_PLAN_UA.md`
- `Docs/EXP022_BACKLOG_P0_P1_P2_UA.md`
- `Docs/SystemArchitecture/HidBridge_AgentContract_v1_UA.md`
- `Docs/SystemArchitecture/HidBridge_ModuleArchitecture_Map_UA.md`
- `Docs/SystemArchitecture/HidBridge_Clean_Modular_Agent_Architecture_UA.md`
- `Docs/SystemArchitecture/schemas/HidBridge_AgentContract_v1_Message.schema.json`

---

## 3. Системний контекст (що саме будуємо)

## 3.1 Бізнес-ціль платформи

HidBridge — це agentless remote control платформа, де цільовий ПК керується через USB HID, а відео/аудіо повертається через low-latency streaming.

Ключовий KPI продукту:

1. Мінімальна затримка взаємодії (операторський контроль у реальному часі).
2. Стабільність сесії (без фризів та обривів аудіо).
3. Прийнятна якість відео в межах latency budget.

## 3.2 Фізична топологія (реальна)

```text
[Client Browser]
   |  WHEP (video/audio) + WS/WebRTC control
[Control/Media Server]
   |  UART control v2
[RP2040 B_host] <== internal bridge ==> [RP2040 A_device]
   |                                           |
   | USB Host                                  | USB Device (emulation)
[Physical Keyboard/Mouse/Capture]              [Target PC]
```

## 3.3 Логічні площини

Система фактично має 3 незалежні площини:

1. `Media plane` — відео+аудіо.
2. `Control plane` — події миші/клавіатури.
3. `Diagnostics plane` — метрики, стан, логи, clock-sync.

Висновок з експериментів: проблеми в проді майже завжди виникають у місцях, де ці площини перетинаються без явних контрактів.

## 3.4 Поточний стан `2026-03-03`

Фактично підтверджено:
- `HidBridge.ControlPlane.Web -> Keycloak` login/logout працює;
- `Google -> Keycloak -> HidBridge.ControlPlane.Web` працює;
- `tenant_id`, `org_id`, `principal_id`, `role` можуть бути нормалізовані через Keycloak mapper-и;
- `User ID` у web shell береться з `sub`;
- `User added at` лишається optional claim і без окремого mapper-а може бути відсутнім;
- у backend стартував `tenant/org-aware enforcement` для session-scoped collaboration/control/dispatch/read flows.
- при наявному caller context backend додатково застосовує operator-role baseline:
  - `operator.viewer` для read/session/control базового доступу
  - `operator.moderator` / `operator.admin` для moderation flows
- tenant/org scope-check тепер застосовується також усередині query/read-model сервісів:
  - `ProjectionQueryService`
  - `OperationsDashboardReadModelService`
  - `ReplayArchiveDiagnosticsService`
- `operator.admin` має cross-tenant override на service/query рівні
- smoke/integration сценарії тепер можуть явно прокидати caller scope через `X-HidBridge-*` заголовки
- `403 Forbidden` у `ControlPlane.Api` тепер повертає структурований denial payload замість лише рядка помилки

---

## 4. Поточна архітектура (AS-IS)

## 4.1 Firmware шар (RP2040 pair)

## 4.1.1 B_host (USB host + control ingress)

Головна роль B_host:

- читати реальні HID report-и з фізичних USB HID девайсів
- отримувати зовнішні команди injection через `control UART`
- передавати report-и на A_device по внутрішньому high-speed UART bridge (`PF_INPUT`)
- обробляти control callbacks від A_device (`SET_REPORT`, `GET_REPORT`, `SET_IDLE`, `SET_PROTOCOL`)

Ключові модулі:

- `hid_host.c` — інтеграція з TinyUSB host callbacks
- `hid_proxy_host.c` — основний state machine для інтерфейсів, descriptor/layout інференсу, input gating
- `control_uart.c` — зовнішній UART v2 протокол (SLIP + CRC + HMAC)
- `descriptor_logger.c` / `string_manager.c` — оркестрація повного descriptor snapshot

Ключова поведінка:

- інтерфейсні селектори `0xFF` (перший mouse), `0xFE` (перший keyboard)
- підтримка `LIST_INTERFACES`, `GET_REPORT_DESC`, `GET_REPORT_LAYOUT`, `GET_DEVICE_ID`
- ACK/NACK на кожну control-команду з кодами помилок (`0x01..0x04`)
- двоключова HMAC валідація (bootstrap key + derived key)

## 4.1.2 A_device (USB device emulation)

Головна роль A_device:

- прийняти дескриптори від B_host
- зібрати повний descriptor set
- стартувати TinyUSB device stack тільки коли descriptor set валідний
- відтворювати HID device для Target PC
- приймати `PF_INPUT` і передавати в `tud_hid_n_report`

Ключові модулі:

- `hid_proxy_dev.c` — descriptor accumulation, start gating, control callbacks
- `remote_storage.c` — кеш дескрипторів/рядків/layout hints

Ключова поведінка:

- якщо descriptor set не повний — інпути не повинні пропихатись у USB device path
- при перепідключенні/новому descriptor cycle виконується reset стану
- є черга pending report-ів і retry логіка при тимчасовій недоступності TinyUSB endpoint

## 4.1.3 Common layer

- `proto_frame.*` — внутрішній протокол B_host<->A_device (`PF_DESCRIPTOR`, `PF_INPUT`, `PF_CONTROL`, `PF_UNMOUNT`)
- `uart_transport.*` — SLIP framing + RX/TX буфери + bounded processing budget
- `proxy_config.h` — весь низькорівневий конфіг (пін-аут, baud, flow control, HMAC key)

## 4.1.4 Важливий технічний висновок по firmware

Поточний firmware вже достатньо функціональний для production-ядра, але документація/контракт мають drift:

- `control_uart.h` має застарілий коментар про “no responses/ACKs”, тоді як `control_uart.c` реалізує повноцінний v2 request/response.

Це індикатор системної проблеми: контрактний drift між кодом і docs.

---

## 4.2 Tools шар (HidControl ecosystem)

## 4.2.1 Структура solution

`Tools/HidBridge.sln` вже містить фундамент для модульного дизайну:

- `Server/HidControlServer`
- `Shared/HidControl.Contracts`, `Shared/HidControl.ClientSdk`, `Shared/HidControl.Core`, `Shared/HidControl.UseCases`
- `Core/HidControl.Application`
- `Infrastructure/HidControl.Infrastructure`
- `Clients/Web`, `Clients/Desktop`, `Clients/Mobile`
- `Tests/HidControlServer.Tests`

Це сильна сторона: модульний skeleton вже присутній, навіть якщо runtime поки частково монолітний.

## 4.2.2 HidControlServer (фактично system hub)

`Tools/Server/HidControlServer/Program.cs` виконує роль composition root і одночасно orchestrator для:

- UART/HID control
- video ingestion/publishing
- WebRTC signaling room registry
- helper process supervision (`WebRtcControlPeer`, `WebRtcVideoPeer`)
- state persistence (`hidcontrol.state.json`, room registry)

Ключові підсистеми (по сервісам):

- `HidUartClient` — UART v2 transport + queue + trace + stats
- `InputReportBuilder`, `ReportLayoutService` — семантичне формування report-ів
- `WebRtcControlPeerSupervisor`, `WebRtcVideoPeerSupervisor` — helper lifecycle
- `VideoProfileService`, `FfmpegPipelineService`, `VideoInputService` — AV pipeline
- `DeviceDiagnostics`, `ServerEventLog`, `StreamDiagService` — діагностика

## 4.2.3 Сильні сторони Tools-реалізації

1. Реальний production-friendly UART клієнт з queue/backpressure.
2. Fallback-модель побудови report-ів: layout -> descriptor parser -> boot fallback.
3. Наявність супервізорів для зовнішніх WebRTC peer-процесів.
4. Хороший набір операційних скриптів у `Scripts/` для smoke/diag/full-test.

## 4.2.4 Проблема Tools-шару

Центр ваги (HidControlServer) занадто широкий:

- один процес відповідає і за control-plane, і за media-plane, і за signaling, і за persistence orchestration.

Тобто модулі в solution є, але runtime-ізоляція меж ще не доведена до кінця.

---

## 4.3 EXP-022 шар (інтеграційний стенд)

`WebRtcTests/exp-022-datachanneldotnet` виконує роль швидкого інкубатора/стенду.

## 4.3.1 Що реально реалізовано

### AV

- `dc-whip-poc` / `dc-whip-capture-poc`: WHIP publish через DataChannelDotnet peer
- H264 відео + Opus аудіо packetization
- low-latency профілі (`UltraLowLatency`, `ExtremeLowLatency`)

### HID

- `dc-hid-poc`: локальний WS endpoint (`/ws/control`) + bridge у DataChannel + UART inject
- JSON-команди з browser UI переводяться у UART v2 команди
- ACK/NACK повертаються назад у браузер

### UI

- `stream_monitor.html`: одночасно WHEP playback + HID control + clock iframe/fallback
- pointer lock, keyboard capture, `Arm Input`, status line (`hid: connected / error`)

### Utilities

- `clock_server.ps1`: lightweight TCP HTTP server для `/clock`, `/clock-sync`, `/health`
- `start.ps1`: режими запуску, env wiring, reproducible params

## 4.3.2 Сильні сторони EXP-022

1. Швидкість ітерації.
2. Видимі latency тести (clock overlay).
3. Реальні практичні сценарії edge-case (codec mismatch, listener bind issues, selector mismatch, report layout variability).

## 4.3.3 Обмеження EXP-022

1. Частина компонентів написана як експериментальний runtime, не як long-lived сервіс.
2. `HttpListener` WS path виявився чутливим до Windows HTTP.sys/URLACL/IP listen стану.
3. Архітектура зроблена для швидкої перевірки гіпотез, не для довгострокової операційної підтримки.

---

## 5. Контракти та протоколи (поточний стан)

## 5.1 UART control v2 (external -> B_host)

Транспорт:

- SLIP (`0xC0/0xDB` escaping)
- frame header: magic/version/flags/seq/cmd/len
- CRC16 + truncated HMAC16

Команди (важливі):

- `0x01` INJECT_REPORT
- `0x02` LIST_INTERFACES
- `0x04` GET_REPORT_DESC
- `0x05` GET_REPORT_LAYOUT
- `0x06` GET_DEVICE_ID

Семантика безпеки:

- bootstrap key дозволяє отримати `device_id`
- далі використовується derived key `HMAC(master_secret, device_id)`

Семантика помилок:

- `0x01` bad len
- `0x02` inject failed
- `0x03` descriptor missing
- `0x04` layout missing

Критичний практичний висновок:

- ACK `ok=true` означає прийом фрейма firmware-ом, але не гарантує семантично правильну дію на Target PC.

## 5.2 Internal PF protocol (B_host <-> A_device)

Внутрішній протокол використовується для:

- передачі descriptor frame-ів
- передачі input report frame-ів
- control callback bridge
- повідомлень про unmount/reset

Архітектурний плюс:

- control UART і internal PF вже розділені на дві різні площини, це правильно.

Архітектурний мінус:

- зовнішній stack не має єдиного normalized event model, що ускладнює end-to-end кореляцію.

## 5.3 WS control contract (browser -> server)

У `stream_monitor.html` і `dc-hid-poc` використовується JSON-модель:

- `mouse_move` / `mouse_wheel` / `mouse_button`
- `keyboard_text` / `keyboard_press` / `keyboard_shortcut`

Відповідь:

- `{ id, ok, error }`

Плюс:

- проста інтеграція з браузером.

Мінус:

- контракт не формалізований як версіонований schema package (потрібна версія протоколу, capability exchange, deterministic error taxonomy).

## 5.4 WebRTC signaling

У Tools:

- shared endpoint `/ws/webrtc`
- room namespace split (`control` vs `video`)
- per-room limit (MVP: 2 peers)

У exp-022:

- локальна in-process peer simulation DataChannelDotnet
- control WS поверх локального listener

Висновок:

- signaling частково консистентний, але в exp-022 і Tools використовується різна operational модель.

## 5.5 WHIP/WHEP media signaling

Практично підтверджено:

- SRS WHEP/WHIP працює стабільно за коректного SDP
- аудіо для publish має бути Opus (PCMU в offer викликав `no valid found opus payload type`)

Висновок:

- AV pipeline повинен мати жорстку preflight валідацію codec/profile до POST у WHIP.

---

## 6. Реальна поведінка системи (на основі експериментів)

## 6.1 Latency (візуальна)

На локальній LAN, за clock overlay методикою, отримувались затримки приблизно на рівні:

- ~150–240 ms у стабільних профілях

Важливо:

- “ultra aggressive” параметри не давали пропорційного виграшу в latency, але погіршували стабільність/якість.

## 6.2 Freeze/стабільність

Спостерігались періоди:

- фриз кадру
- зникнення аудіо
- стрибки якості при перепідключенні

Типові корені:

1. перевантаження encoder/capture path
2. агресивний low-latency без запасу по jitter
3. обриви/перезбірка WHEP peer session

## 6.3 HID семантика

Було підтверджено:

- транспортний шлях працює (велика кількість ACK)
- дії миші/клавіатури частково працюють

Ключова причина “частково”:

- гетерогенність report layout/report-id між пристроями
- необхідність стабільного fallback: `layout -> descriptor parse -> boot fallback`

## 6.4 Операційні проблеми Windows

Під час стендових запусків проявилась чутливість `HttpListener` до:

- URLACL
- HTTP.sys bindings
- IP listen list
- колізій listeners (включно з system process ownership)

Наслідок:

- endpoint формально “слухає порт”, але request/WebSocket може зависати без відповіді.

Архітектурний висновок:

- для production control-gateway бажано перейти на Kestrel/ASP.NET hosting модель (а не raw HttpListener).

---

## 7. Системні root-cause проблеми AS-IS

## 7.1 Змішання відповідальностей

Один runtime часто одночасно робить:

- медіа
- control translation
- signaling
- persistence orchestration
- diagnostics

Це ускладнює:

- fault isolation
- горизонтальне масштабування
- прогнозовану деградацію

## 7.2 Недостатня формалізація контрактів між шарами

Є контракти де-факто, але не всюди є контракти де-юре:

- WS control payload schema
- internal normalized event schema (AV+HID)
- unified error taxonomy

## 7.3 Неповна end-to-end observability

Є логи і локальні метрики, але немає єдиного telemetry envelope, який зшиває:

- Browser event timestamp
- WS ingress timestamp
- UART send/ack timestamp
- firmware apply timestamp
- media frame presentation timestamp

## 7.4 Трудомістка експлуатація

Багато операційної логіки “в голові” (manual runbook), а не в автоматизованій health policy.

---

## 8. Цільова модульна архітектура (TO-BE)

## 8.1 Архітектурні принципи

1. **Явне розділення площин**: Media, Control, Diagnostics.
2. **Контракт first**: усі міжмодульні інтерфейси версіоновані.
3. **Сервісна ізоляція деградації**: збій HID не валить AV і навпаки.
4. **Observability as feature**: телеметрія не “опція”, а частина контракту.
5. **Production runtime окремо від експериментів**: `exp-022` лишається лабораторією.

## 8.2 Контейнерний погляд (логічний)

```text
[Client Web App]
  -> [Session API / Orchestrator]
  -> [Media Gateway Service] ----> [SRS or Native WebRTC Media Backend]
  -> [Control Gateway Service] --> [HID Protocol Service] --> [UART Driver] --> [B_host]
  -> [Telemetry Service] <-------- (events from all modules)
```

## 8.3 Рекомендований набір модулів

## M1. Session Orchestrator

Відповідальність:

- життєвий цикл сесії
- state machine (Starting/Running/Degraded/Recovering/Stopped)
- policy-rules (retry, fallback profile, disarm on critical HID errors)

Контракт:

- `StartSession`, `StopSession`, `ApplyProfile`, `GetSessionState`

## M2. Media Gateway

Відповідальність:

- capture orchestration
- encode/publish профілі
- WHIP publish / WHEP subscription contract boundary

Контракт:

- `StartPublish(sessionId, profile)`
- `StopPublish(sessionId)`
- `GetMediaMetrics(sessionId)`

## M3. Control Gateway

Відповідальність:

- WS/WebRTC DataChannel ingest подій від клієнта
- валідація payload schema
- rate-limit/backpressure policy для input events

Контракт:

- `SubmitInputEvent`
- `Ack/Nack` з нормалізованими кодами

## M4. HID Protocol Service

Відповідальність:

- трансляція high-level input event у HID report
- layout/descriptor discovery
- fallback strategy

Контракт:

- `BuildMouseReport`
- `BuildKeyboardReport`
- `ResolveInterfaceSelector`

## M5. UART Driver Service

Відповідальність:

- transport v2 (SLIP/CRC/HMAC)
- queueing/retry/timeouts
- trace snapshot

Контракт:

- `SendCommand(cmd,payload)`
- `SendInject(report)`
- `GetStats/Trace`

## M6. Telemetry & Diagnostics Service

Відповідальність:

- прийом подій від усіх модулів
- зшивка у єдиний timeline
- SLI/SLO обчислення

Контракт:

- `EmitEvent`
- `QueryTimeline`
- `GetHealthSummary`

## M7. Config & State Authority

Відповідальність:

- єдине джерело істини для runtime state
- schema-модель для профілів і room bindings
- migration policy для state versions

---

## 9. Декомпозиція потоків у TO-BE

## 9.1 Media flow

1. Session Orchestrator обирає media profile.
2. Media Gateway піднімає capture+encode path.
3. Публікація в SRS WHIP (або native media backend).
4. Browser читає WHEP.
5. Media metrics летять у Telemetry Service.

## 9.2 HID flow

1. Browser генерує input event.
2. Control Gateway валідуює/нормалізує.
3. HID Protocol Service будує report (layout-aware).
4. UART Driver відправляє `INJECT_REPORT`.
5. ACK/NACK повертається через Gateway у Browser.
6. Event trace пишеться у Telemetry Service.

## 9.3 Recovery flow

1. Деградація (наприклад, повторні `ack_timeout` або media freeze).
2. Session Orchestrator переводить сесію в `Recovering`.
3. Застосовується fallback policy:
   - control: reset keyboard state, re-probe layout
   - media: conservative profile + republish
4. Після стабілізації сесія повертається в `Running`.

---

## 10. Контракти та версіонування (обов'язково для next-gen)

## 10.1 Control Event Schema v1

Кожне повідомлення повинно мати:

- `schemaVersion`
- `eventId`
- `sessionId`
- `sourceTsUtcMs`
- `type`
- `payload`

## 10.2 Ack/Nack Schema v1

Кожен reply:

- `eventId`
- `ok`
- `errorCode`
- `errorDetails`
- `serverTsUtcMs`
- `transportPath` (ws|dc)

## 10.3 UART Error Mapping

Нормалізована таблиця помилок:

- `UART_BAD_LEN`
- `UART_INJECT_FAILED`
- `UART_DESC_MISSING`
- `UART_LAYOUT_MISSING`
- `UART_TIMEOUT`
- `UART_HMAC_INVALID`

## 10.4 Media Health Schema

- `fps`
- `bitrateKbps`
- `rttMs`
- `nackRate`
- `pliRate`
- `freezeCount`
- `maxFreezeMs`
- `audioDropCount`

## 10.5 Нормативний Agent Contract v1

Для нової модульної архітектури зафіксований окремий нормований контракт:

- `Docs/SystemArchitecture/HidBridge_AgentContract_v1_UA.md`
- `Docs/SystemArchitecture/schemas/HidBridge_AgentContract_v1_Message.schema.json`

Що приймається як обов'язкове для нових компонентів:

1. Єдиний envelope повідомлень (`v`, `kind`, `messageId`, `traceId`, `correlationId`, `tenantId`, `endpointId`, `agentId`, `body`).
2. Єдина capability-модель для mixed-середовища (`installed`, `agentless`, `hid_bridge`, `media_gateway`).
3. Єдина state machine для `agent/session/command`.
4. Єдина error taxonomy (`domain+code+retryable`) з мапінгом UART error bytes `0x01..0x04`.

## 10.6 Нормативна карта модулів (AS-IS -> TO-BE)

Окремий документ з модульною декомпозицією і міграційною картою:

- `Docs/SystemArchitecture/HidBridge_ModuleArchitecture_Map_UA.md`

Цей документ вважається обов'язковим додатком до поточного архітектурного опису і використовується як робочий baseline для:

1. Формування структури нових проєктів у `Tools/Platform`, `Tools/Connectors`, `Tools/Shared`.
2. Винесення HID/UART runtime з експериментального й монолітного контурів у окремий connector.
3. Відділення `exp-022` як integration lab only від production runtime.

---

## 11. Нефункціональна архітектура

## 11.1 Latency budget

Цільове бюджетування затримки (LAN baseline):

1. Capture + encode: 40–90 ms
2. Network + relay: 20–60 ms
3. Browser decode + render: 40–90 ms

Сумарно (медіана): ~150–240 ms, що відповідає практичним вимірам зі стенду.

## 11.2 Throughput і backpressure

Розділити політики для типів input:

- mouse move: допускається controlled drop
- mouse click / keyboard press/release: drop заборонений, тільки retry/ordered

## 11.3 Надійність

- bounded queues на кожному ingress
- circuit-breaker на нестабільному UART
- idempotent stop/start для peer sessions

## 11.4 Безпека

- HMAC на UART (вже є)
- token/JWT на control ingress
- origin checks для browser WS
- ключі в secure storage, не в plain-text config для production

## 11.5 Observability

Обов'язковий мінімум:

- structured logs з `sessionId`, `eventId`, `module`
- latency histogram (p50/p95/p99)
- reasoned failures (machine-readable)

---

## 12. Рекомендована модульна структура репозиторію (next step)

```text
Tools/
  Core/
    HidBridge.Domain/
    HidBridge.Application/
    HidBridge.Contracts/
  Infrastructure/
    HidBridge.Uart/
    HidBridge.HidProtocol/
    HidBridge.MediaFfmpeg/
    HidBridge.Telemetry/
  Server/
    HidBridge.Orchestrator.Api/
    HidBridge.ControlGateway.Api/
    HidBridge.MediaGateway.Api/
  Clients/
    HidBridge.Web/
WebRtcTests/
  exp-022-datachanneldotnet/   (integration lab only)
Firmware/
  src/
Docs/
  SystemArchitecture/
```

Ключове правило:

- `exp-022` не має бути production runtime. Це лабораторний стенд для перевірки гіпотез.

---

## 13. Міграція AS-IS -> TO-BE (архітектурна)

## Фаза A — контрактна стабілізація

1. Заморозити `Control Event Schema`.
2. Заморозити `Ack/Nack Schema`.
3. Додати versioning для diagnostics payload.

## Фаза B — виділення Control Gateway

1. Винести WS ingest із експериментального runtime у окремий API сервіс.
2. Переключити browser monitor на цей сервіс.
3. Залишити `dc-hid-poc` тільки для regression/interop тестів.

## Фаза C — виділення HID Protocol Service

1. Винести `layout/descriptor/report build` в окрему бібліотеку.
2. Підключити її і в Tools, і в exp-022 для єдиної логіки.
3. Заборонити дублювання report-build кодів у різних процесах.

## Фаза D — виділення Media Gateway

1. Формалізувати profile registry.
2. Зробити deterministic start/stop/republish API.
3. Зшити media metrics у централізовану телеметрію.

## Фаза E — Orchestrator

1. Ввести session state machine.
2. Додати policy-based recovery.
3. Закрити інтеграційні acceptance gates.

---

## 14. Acceptance criteria для нової модульної архітектури

## 14.1 Функціональні

1. Browser може керувати target мишею/клавіатурою через єдиний control API.
2. AV стрім стабільно йде з аудіо і відео.
3. Session restart/stop deterministic і не залишає "сиріт".

## 14.2 Нефункціональні

1. `Median visual latency <= 220 ms` (LAN baseline).
2. `P95 <= 320 ms`.
3. `Freeze > 1s == 0` на 30-хв тесті.
4. `HID semantic success >= 99%` на scripted suite.

## 14.3 Операційні

1. Повний diag пакет формується за < 1 хв.
2. Root cause основних інцидентів визначається за < 5 хв.
3. Всі error codes мають однозначну класифікацію і рекомендацію дії.

---

## 15. Ризики цільової архітектури

## R1. Контрактний drift між firmware і server

Пом'якшення:

- contract tests на UART v2 + golden frames
- CI перевірка docs vs protocol constants

## R2. Латентність росте при надмірній модульності

Пом'якшення:

- zero-copy де можливо
- асинхронні bounded queues
- чіткий профайлінг на кожному hop

## R3. Різна поведінка HID devices

Пом'якшення:

- layout cache + descriptor fallback
- compatibility matrix
- per-device mapping packs

## R4. Операційна складність multi-process

Пом'якшення:

- централізований orchestrator
- health probes
- supervisor з restart backoff і jitter

---

## 16. Ключові архітектурні рішення (ADR-кандидати)

1. **ADR-001**: Поділ runtime на `Control Gateway`, `Media Gateway`, `Orchestrator`.
2. **ADR-002**: Єдина бібліотека `Hid Protocol Service` для побудови report-ів.
3. **ADR-003**: `exp-022` залишається тільки integration-lab.
4. **ADR-004**: Kestrel-based API hosting замість raw HttpListener для production control API.
5. **ADR-005**: Централізована telemetry timeline як обов'язковий системний модуль.

---

## 17. Мапінг існуючих артефактів у цільові модулі

| Поточний артефакт | Цільовий модуль |
|---|---|
| `Tools/Server/HidControlServer/HidUartClient.cs` | `Infrastructure/HidBridge.Uart` |
| `Tools/Server/HidControlServer/Services/InputReportBuilder.cs` | `Infrastructure/HidBridge.HidProtocol` |
| `Tools/Server/HidControlServer/Services/ReportLayoutService.cs` | `Infrastructure/HidBridge.HidProtocol` |
| `WebRtcTests/exp-022-datachanneldotnet/Program.cs` (`dc-hid-poc` частина) | `Server/HidBridge.ControlGateway.Api` |
| `WebRtcTests/exp-022-datachanneldotnet/Program.cs` (`dc-whip*` частина) | `Server/HidBridge.MediaGateway.Api` |
| `Tools/WebRtcVideoPeer` | `Infrastructure/HidBridge.MediaFfmpeg` runtime helper |
| `Tools/WebRtcControlPeer` | `Infrastructure/HidBridge.ControlRtcBridge` (опційно) |
| `Firmware/src/B_host/control_uart.c` | Firmware contract authority (не переноситься, але версіонується) |
| `Firmware/src/A_device/hid_proxy_dev.c` | Firmware contract authority (USB emulation) |

---

## 18. Підсумок

Поточна система вже довела життєздатність ключової ідеї:

- agentless HID control через RP2040 bridge
- low-latency AV контур через WebRTC stack
- практичний end-to-end стенд з вимірюваною затримкою

Але для наступного кроку потрібна не чергова "точкова правка", а архітектурний перехід:

1. Ясні модульні межі.
2. Контракти між модулями з версіонуванням.
3. Єдина телеметрія для AV + HID.
4. Відділення лабораторного контуру (`exp-022`) від production runtime.

Саме така модульна декомпозиція дасть одночасно:

- нижчу операційну крихкість,
- прогнозовану еволюцію функціоналу,
- контрольований шлях до кращої якості та нижчої затримки.

---

## 19. Статус доповнень 2026-02-27

У межах цієї версії архітектури офіційно додано:

1. `Agent Contract v1` як нормований міжмодульний контракт.
2. `JSON Schema` для валідації повідомлень контракту.
3. `Module Architecture Map` як карта міграції від поточного стану до цільової модульної платформи.
4. `Clean Modular Agent Architecture` як цільова референс-модель платформи класу “Meet + remote control + multi-agent”.

Ці артефакти мають використовуватись як базові архітектурні обмеження в усіх нових імплементаціях, що розвивають `Tools`, `Firmware` і `WebRtcTests/exp-022-datachanneldotnet`.

Стан реалізації на `2026-03-02`:

1. `Platform/` уже має робочі `File` і `SQL` persistence providers.
2. `P3` control-plane baseline уже реалізований:
   - `session_participants`
   - `session_shares`
   - invitations / approvals
   - command journal
   - unified timeline baseline
   - collaboration read models
3. `P4-1/P4-2` baseline уже реалізований:
   - policy-driven approvals
   - control arbitration
   - active controller lease
   - `request/grant/release/force-takeover` flow
   - control-aware command dispatch policy
   - control dashboard read model
4. `P4-3` dashboard baseline уже реалізований:
   - fleet inventory dashboard
   - audit diagnostics dashboard
   - telemetry diagnostics dashboard
5. `P4-4` optimized query projections baseline уже реалізований:
   - paged session projections
   - paged endpoint projections
   - filtered audit projections
   - filtered telemetry projections
6. `P4-5` replay/archive diagnostics baseline уже реалізований:
   - session replay bundle
   - archive summary
   - archive audit slices
   - archive telemetry slices
   - archive command slices
7. Поточний активний етап:
   - thin operator UI shell
8. Затверджено принцип реалізації dashboard/UI:
   - спочатку backend projections / read models
   - потім thin operator UI
   - UI не збирає стан з великої кількості raw endpoint-ів у браузері
   - UI читає готові dashboard-oriented query models

UI/UX напрямок наступного етапу:

1. Тип інтерфейсу:
   - operator console, а не marketing-style UI
2. Основні екрани:
   - Fleet Overview
   - Session Details
   - Audit Dashboard
   - Telemetry Dashboard
   - Control Operations
3. Основні UX-принципи:
   - висока щільність корисної інформації
   - мінімум анімацій і декоративності
   - явний показ:
     - owner
     - active controller
     - pending invitations / approvals
     - health / degraded / failed status
4. Архітектурний принцип:
   - спочатку query/projection layer
   - потім dashboard API
   - потім окремий web client shell

## 19. Оновлення стану `2026-03-02` — Web / Identity / Tenant

1. `P4` data/read-model частина фактично закрита по baseline:
- dashboards
- projections
- replay/archive diagnostics
2. Наступний практичний фокус:
- thin operator UI shell
- `Blazor Web App`
- `Tailwind CSS` browser baseline
- design baseline from `landing/index.html`
- localization baseline:
  - `en` default
  - `uk` secondary
  - browser locale auto-detection
  - manual locale override in settings
  - manual theme override in settings (`auto | light | dark`)
  - responsive operator layout aligned with `landing/index.html`
  - core layout styling no longer depends on a delayed Tailwind runtime refresh
- operator actions baseline:
   - request control
   - release control
   - force takeover
   - approve/decline invitation
3. Security direction зафіксовано:
- server-side web shell як BFF-style точка входу
- `OIDC + cookie session` для users
- `RBAC + ABAC` для operator/control дій
- `Tenant & Organization` enforcement після стабілізації web shell
- централізований SSO/Identity не повинен реалізовуватись усередині `HidBridge.ControlPlane.Web`
- роль централізованого IdP baseline зараз виконує `Keycloak`
- у thin web shell уже реалізовано baseline `Identity & Access`:
  - cookie-backed session
  - optional `OIDC` challenge flow
  - development operator fallback identity
  - policies `OperatorViewer`, `OperatorModerator`, `OperatorAdmin`
  - tenant/org seams projected from claims


Оновлення web shell / Identity baseline:
- коли `Identity:Enabled=false`, development fallback identity тепер автентифікується автоматично без ручного login flow;
- ручний theme switch лишається окремим відкритим дефектом web shell і винесений у дизайн-backlog;
- authorization-aware UX baseline already added to the shell:
  - current operator / roles / tenant / organization
  - moderation/control allow/deny reasoning in operator flows
- `html, body` background зроблено напівпрозорим, щоб grid background із `landing/index.html` читався стабільно.


Оновлення стану `2026-03-03`:
- login/logout між `HidBridge.ControlPlane.Web` і `Keycloak` перевірено локально;
- централізований SSO baseline підтверджено практично;
- наступний practical step: `Google` як перший external IdP у `Keycloak`, далі той самий onboarding pattern для інших провайдерів.

## 20. Оновлення цільової архітектури `2026-03-15` — Global Room-Centric Multi-Agent Fabric

Цей розділ фіксує обов'язкову цільову модель для наступної ітерації:  
`Agent = повноцінна кінцева точка`, `Adapter = транспортний посередник`, `Core = оркестратор`.

## 20.1 Нормативні інваріанти системи

1. `Room` є центральною realtime-сутністю співпраці, а не обгорткою навколо одного девайса.
2. Один `Agent` може обслуговувати `1..N` цільових пристроїв.
3. Один `Device` може бути прикріплений (`attachment`) до `1..N` кімнат.
4. В одній кімнаті одночасно можуть бути:
   - багато учасників;
   - багато пристроїв;
   - багато агентів/endpoint-ів.
5. Кімнат може бути багато; система має підтримувати одночасно багато активних room.
6. Географія не обмежується одним майданчиком: агенти і пристрої можуть бути в різних країнах/регіонах.
7. Перегляд і керування розділені:
   - багато учасників можуть дивитися;
   - active control на конкретний device визначається lease/policy.
8. `Core` не має напряму працювати з COM/HID/драйверами пристроїв.

## 20.2 Ролі компонентів (операційна модель)

### Agent (пристроє-орієнтований endpoint-додаток)

`Agent` — окремий повноцінний пристроє-орієнтований застосунок на edge-вузлі (на самому target device або на сусідньому host/gateway), який:

1. взаємодіє з фізичним пристроєм;
2. реалізує потрібні для конкретного device домени (media/control/telemetry/інше);
3. має локальний інтерфейс для налаштування/діагностики;
4. публікує capabilities/health/presence у Core;
5. виконує команди і повертає ACK/event telemetry.

### Adapter (посередник між Agent і Core)

`Adapter` — транспортний шар інтеграції між Agent і Core:

1. маршрутизація команд/подій;
2. protocol mapping (webrtc/ws/uart/gateway);
3. retry/backoff/queue semantics;
4. health reporting і reconnect;
5. без direct ownership бізнес-стану room/session/policy;
6. не замінює Agent і не реалізує повний device-facing функціонал.

### Core (Control Plane)

`Core` (`API + Web + Identity + Persistence`) відповідає за:

1. room/session/participant/device attachment lifecycle;
2. policy/lease/access enforcement;
3. transport route selection;
4. audit/telemetry storage;
5. UX orchestration для операторів.

## 20.3 Де повинні жити agent-застосунки та експерименти в репозиторії

Нормативна схема для коду:

1. Production device-oriented agents (окремі застосунки за типом цільового пристрою або доменом):
   - `Platform/Edge/Agents/HidBridge.Agent.DeviceHost.Pc`
   - `Platform/Edge/Agents/HidBridge.Agent.DeviceHost.Camera`
   - `Platform/Edge/Agents/HidBridge.Agent.DeviceHost.Robot`
   - `Platform/Edge/Agents/HidBridge.Agent.DeviceHost.Cnc`
   - `Platform/Edge/Agents/HidBridge.Agent.DeviceHost.Drone`
   - `Platform/Edge/Agents/HidBridge.Agent.DeviceHost.IoT`
   - `Platform/Edge/Agents/HidBridge.Agent.Common` (shared contracts/runtime parts)
2. Transport adapters:
   - `Platform/Edge/Adapters/HidBridge.Adapter.CoreBridge`
   - `Platform/Edge/Adapters/HidBridge.Adapter.Transport.WebRtc`
   - `Platform/Edge/Adapters/HidBridge.Adapter.Transport.Uart`
3. Експериментальні стенди/симулятори:
   - `WebRtcTests/exp-022-datachanneldotnet` (залишити як PoC/simulator, не production endpoint).

Поточний legacy adapter path на базі `webrtc-peer-adapter` розглядається як тимчасовий migration bridge і має бути замінений на довгоживучий сервіс у `Platform/Edge/Adapters/*`.

## 20.4 Deployment-модель (обов'язкова)

### Control Plane (containerized)

Запускається в Docker:

1. `HidBridge.ControlPlane.Api`
2. `HidBridge.ControlPlane.Web`
3. `Keycloak`
4. `PostgreSQL`
5. опційно event bus/cache (`Redis`/`NATS`) для масштабування relay/event path.

### Edge Plane (distributed)

Запускається поза Control Plane контейнерами, на edge вузлах:

1. один або кілька device-oriented agent apps (за типом device/доменом)
2. один або кілька transport adapters для зв'язку з Core
3. локальні device connectors (COM/HID/capture/camera/robot/CNC/drone APIs)

Це дозволяє масштабувати edge незалежно від Core та не блокувати API доступом до локального hardware.

## 20.5 End-to-end контур взаємодії учасника з target device

1. Учасник у Web UI входить у room.
2. Отримує/запитує control lease на конкретний device.
3. Команда йде: `Web -> API -> route resolver -> adapter -> agent -> device`.
4. ACK повертається: `device -> agent -> adapter -> API -> Web`.
5. Media/telemetry йдуть паралельно у room-context.
6. Для кожної команди є audit trail + error code + latency метрики.

## 20.6 Multi-room / multi-agent / global scaling вимоги

1. Agent registry має бути `tenant/org scoped`.
2. Route selection має враховувати:
   - endpoint capability;
   - online/offline/degraded health;
   - policy constraints;
   - requested transport provider.
3. Command delivery має бути:
   - idempotent;
   - retry-safe;
   - auditable;
   - deterministic по error semantics.
4. Session/read-model має явно показувати:
   - active controller;
   - room state reason;
   - transport health;
   - online peers/agents per endpoint.

## 20.7 Міграційний policy для exp-022 і script adapter

1. `exp-022` лишається тестовим симулятором для transport/ack перевірок.
2. Production потік не повинен залежати від `exp-022`.
3. Script adapter дозволений тільки як тимчасовий dev harness.
4. Після появи `Agent.Runtime + Adapter services`:
   - demo-flow/webrtc-stack мають запускати service-based edge runtime;
   - PoC scripts переводяться у compatibility profile або архівуються.

## 20.8 Definition of Done для цього архітектурного переходу

1. Учасник керує target device з Web UI без ручного запуску PS adapter.
2. Agent runtime працює як довгоживучий сервіс на віддаленому edge-хості.
3. API/Web працюють у Docker незалежно від edge-processes.
4. Multi-device в room і multi-room одночасно проходять e2e тести.
5. ACK/status semantics стабільні (`Applied/Rejected/Timeout`) без евристик.
6. Наявні спостережуваність та audit докази для transport/media/control path.
