#include <stdint.h>
#include <stdbool.h>
#include <string.h>

#include "tusb.h"
#include "pico/stdlib.h"
#include "bsp/board.h"
#include "hardware/gpio.h"
#include "hardware/uart.h"
#include "hardware/irq.h"
#include "hardware/structs/uart.h"

#include "logging.h"
#include "proto_frame.h"
#include "uart_transport.h"
#include "proxy_config.h"
#include "remote_storage.h"

#ifndef INPUT_LOG_VERBOSE
#define INPUT_LOG_VERBOSE 0
#endif

typedef struct
{
    bool     pending;
    uint8_t  report_type;
    uint8_t  report_id;
    uint8_t* buffer;
    uint16_t buffer_max;
    uint16_t actual_len;
    bool     success;
} pending_get_report_t;

static pending_get_report_t s_get_report_sync;

static void remote_desc_reset(void);
static void remote_desc_reset_reports_and_config(void);
static void tinyusb_shutdown(void);
static void tinyusb_restart(void);
static void start_tinyusb_if_ready(void);
static void maybe_complete_descriptors(void);
static void handle_descriptor_frame(const proto_frame_t *f);
static void handle_control_frame(const proto_frame_t *f);
static void handle_device_reset_request(uint8_t reason);
static void handle_unmount_frame(void);
static void notify_host_ready(void);
static void process_proto_frames(void);
void hid_proxy_dev_service(void);
static void flush_pending_reports(void);
static bool request_string_descriptor(uint8_t index, uint16_t langid);
static bool wait_for_string_ready(remote_string_desc_t* entry,
                                  uint8_t index,
                                  uint16_t langid,
                                  uint32_t timeout_ms);
static void host_irq_init(void);
static void host_irq_pulse(void);

// Лічильники для моніторингу інпутів/дропів
static uint32_t s_input_received = 0;
static uint32_t s_input_dropped_not_ready = 0;
static uint32_t s_input_last_log_ms = 0;
static uint32_t s_input_last_ts_ms = 0;
static uint32_t s_input_min_delta_ms = UINT32_MAX;
static uint32_t s_input_max_delta_ms = 0;
static uint32_t s_latency_min_ms = UINT32_MAX;
static uint32_t s_latency_max_ms = 0;
static uint32_t s_host_time_offset_ms = 0;
static bool     s_host_time_offset_init = false;

typedef struct
{
    bool     valid;
    bool     has_id;
    uint8_t  report_id;
    uint8_t  data[64];
    uint16_t len;
} pending_report_t;

static pending_report_t s_pending_reports[CFG_TUD_HID];

static void remote_desc_reset(void)
{
    tinyusb_shutdown();
    remote_storage_init_defaults();
}

// Clear accumulated config and report descriptors without touching string cache.
static void remote_desc_reset_reports_and_config(void)
{
    s_remote_desc.config.len   = 0;
    s_remote_desc.config.valid = false;
    for (uint8_t i = 0; i < CFG_TUD_HID; i++)
    {
        s_remote_desc.reports[i].len   = 0;
        s_remote_desc.reports[i].valid = false;
        s_remote_desc.hid_itf_present[i] = false;
        s_remote_desc.hid_report_expected_len[i] = 0;
        s_remote_desc.report_has_id[i] = false;
    }
    s_remote_desc.descriptors_complete = false;
}

static void tinyusb_shutdown(void)
{
    if (s_remote_desc.usb_attached)
    {
        tud_disconnect();
        s_remote_desc.usb_attached = false;
    }

    if (s_remote_desc.tusb_initialized)
    {
        tud_deinit(BOARD_TUD_RHPORT);
        s_remote_desc.tusb_initialized = false;
    }

    s_remote_desc.ready_sent = false;
}

static void tinyusb_restart(void)
{
    tinyusb_shutdown();
    start_tinyusb_if_ready();
}

static void update_speed_from_device_desc(void)
{
    if (!s_remote_desc.device.valid ||
        s_remote_desc.device.len < sizeof(tusb_desc_device_t))
    {
        return;
    }

    tusb_desc_device_t const* desc =
        (tusb_desc_device_t const*)s_remote_desc.device.data;

    tusb_speed_t detected =
        (desc->bMaxPacketSize0 <= 8) ? TUSB_SPEED_LOW : TUSB_SPEED_FULL;
    tusb_speed_t effective = detected;

    if (detected == TUSB_SPEED_LOW)
    {
        // RP2040 device controller cannot operate as a real LS device.
        // Keep reporting the detected speed for diagnostics, but clamp the
        // TinyUSB runtime to Full-Speed so enumeration remains stable.
        effective = TUSB_SPEED_FULL;
        LOGI("[DEV] remote device speed detected: LOW (clamped to FULL)");
    }
    else
    {
        LOGI("[DEV] remote device speed detected: FULL");
    }

    if (s_remote_desc.usb_speed != effective)
    {
        s_remote_desc.usb_speed = effective;
        if (s_remote_desc.tusb_initialized)
        {
            LOGI("[DEV] reinitializing TinyUSB to match new speed setting");
            tinyusb_restart();
        }
    }
}

static void start_tinyusb_if_ready(void)
{
    static bool logged_missing_report = false;
    static bool logged_missing_device = false;
    static bool logged_missing_config = false;

    if (s_remote_desc.usb_attached) return;

    if (!remote_storage_reports_ready())
    {
        if (!logged_missing_report)
        {
            LOGW("[DEV] cannot start TinyUSB: report descriptor(s) missing");
            logged_missing_report = true;
        }
        return;
    }
    logged_missing_report = false;

    if (!s_remote_desc.device.valid)
    {
        if (!logged_missing_device)
        {
            LOGW("[DEV] cannot start TinyUSB: device descriptor missing");
            logged_missing_device = true;
        }
        return;
    }
    logged_missing_device = false;

    if (!s_remote_desc.config.valid)
    {
        if (!logged_missing_config)
        {
            LOGW("[DEV] cannot start TinyUSB: config descriptor missing");
            logged_missing_config = true;
        }
        return;
    }
    logged_missing_config = false;

    if (!s_remote_desc.tusb_initialized)
    {
        tusb_rhport_init_t dev_init = {
            .role  = TUSB_ROLE_DEVICE,
            .speed = s_remote_desc.usb_speed ? s_remote_desc.usb_speed
                                             : TUSB_SPEED_FULL
        };
        if (!tusb_init(BOARD_TUD_RHPORT, &dev_init))
        {
            LOGW("[DEV] tusb_init failed");
            return;
        }
        s_remote_desc.tusb_initialized = true;
        LOGI("[DEV] TinyUSB core initialized (speed=%s)",
             (dev_init.speed == TUSB_SPEED_LOW) ? "LOW" :
             (dev_init.speed == TUSB_SPEED_HIGH) ? "HIGH" : "FULL");
    }

    tud_connect();
    s_remote_desc.usb_attached = true;
    LOGI("[DEV] TinyUSB device stack started");

    // Повідомляємо хост, що можна приймати вхідні звіти.
    notify_host_ready();
}

static void maybe_complete_descriptors(void)
{
    if (s_remote_desc.descriptors_complete)
    {
        return;
    }

    // Need device and config first.
    if (!s_remote_desc.device.valid || !s_remote_desc.config.valid)
    {
        return;
    }

    // Keep HID bookkeeping fresh.
    remote_storage_update_string_allowlist();
    remote_storage_analyze_report_descriptors();

    if (remote_storage_reports_ready())
    {
        s_remote_desc.descriptors_complete = true;
        LOGI("[DEV] descriptor set considered complete (auto)");
        start_tinyusb_if_ready();
    }
}

static void handle_descriptor_frame(const proto_frame_t *f)
{
    switch (f->cmd)
    {
        case PF_DESC_DEVICE:
            // Новий цикл дескрипторів може прийти в будь-який момент — повністю
            // скидаємо стан і починаємо спочатку, щоб не зависати у змішаному наборі.
            if (s_remote_desc.usb_attached || s_remote_desc.descriptors_complete)
            {
                LOGW("[DEV] device descriptor ignored (active session)");
                break;
            }
            remote_desc_reset();
            LOGI("[DEV] starting new descriptor set");

            // Replace if duplicate arrives.
            s_remote_desc.device.len = 0;
            if (f->len > sizeof(s_remote_desc.device.data))
            {
                LOGW("[DEV] device descriptor too long len=%u", f->len);
                break;
            }
            memcpy(s_remote_desc.device.data, f->data, f->len);
            s_remote_desc.device.len   = f->len;
            s_remote_desc.device.valid = true;
            LOGI("[DEV] device descriptor chunk len=%u total=%u",
                 f->len, s_remote_desc.device.len);
            update_speed_from_device_desc();
            maybe_complete_descriptors();
            break;

        case PF_DESC_CONFIG:
        {
            if (s_remote_desc.usb_attached || s_remote_desc.descriptors_complete)
            {
                LOGW("[DEV] config descriptor chunk ignored (active session)");
                break;
            }
            // Accumulate chunks; guard buffer size.
            uint16_t base = s_remote_desc.config.len;
            // If уже маємо повний конфіг за wTotalLength – ігноруємо дублікати.
            if (base >= 4)
            {
                uint16_t target = (uint16_t)s_remote_desc.config.data[2] |
                                  ((uint16_t)s_remote_desc.config.data[3] << 8);
                if (target && base >= target)
                {
                    LOGW("[DEV] extra config chunk ignored (already have %u)", base);
                    break;
                }
            }
            if (base >= sizeof(s_remote_desc.config.data))
            {
                LOGW("[DEV] config descriptor buffer full, dropping chunk len=%u", f->len);
                remote_desc_reset();
                break;
            }

            uint8_t chunk[PROTO_MAX_PAYLOAD_SIZE];
            uint16_t cpy = f->len;
            if (base + cpy > sizeof(s_remote_desc.config.data))
            {
                cpy = (uint16_t)(sizeof(s_remote_desc.config.data) - base);
            }
            memcpy(chunk, f->data, cpy);

            // Zero iConfiguration and iInterface fields inside this chunk using absolute offsets.
            // uint16_t processed = 0;
            // while (processed + 1 < cpy)
            // {
            //     uint8_t bl = chunk[processed];
            //     uint8_t dt = chunk[processed + 1];
            //     if (bl < 2) break;
            //     uint16_t abs_off = (uint16_t)(base + processed);
            //     if (dt == TUSB_DESC_CONFIGURATION && (abs_off + 6) < (base + cpy))
            //     {
            //         chunk[processed + 6] = 0; // iConfiguration
            //     }
            //     // else if (dt == TUSB_DESC_INTERFACE && (abs_off + 5) < (base + cpy))
            //     // {
            //     //     chunk[processed + 5] = 0; // iInterface
            //     // }
            //     else if (dt == TUSB_DESC_INTERFACE &&
            //              bl >= sizeof(tusb_desc_interface_t) &&
            //              (processed + 8) < cpy)
            //     {
            //         chunk[processed + 8] = 0; // iInterface
            //     }

            //     processed = (uint16_t)(processed + bl);
            // }

            remote_desc_append(&s_remote_desc.config, chunk, cpy);
            // Trim до wTotalLength, якщо відомо.
            if (s_remote_desc.config.len >= 4)
            {
                uint16_t target = (uint16_t)s_remote_desc.config.data[2] |
                                  ((uint16_t)s_remote_desc.config.data[3] << 8);
                if (target && s_remote_desc.config.len > target)
                {
                    s_remote_desc.config.len = target;
                }
            }
            LOGI("[DEV] config descriptor chunk len=%u total=%u",
                 cpy, s_remote_desc.config.len);
            maybe_complete_descriptors();
            break;
        }

        case PF_DESC_REPORT:
            if (f->len < 1)
            {
                LOGW("[DEV] report descriptor frame too short");
                break;
            }
            else
            {
                if (s_remote_desc.usb_attached || s_remote_desc.descriptors_complete)
                {
                    LOGW("[DEV] report descriptor ignored itf=%u (active session)",
                         f->data[0]);
                    break;
                }
                uint8_t itf = f->data[0];
                if (itf >= CFG_TUD_HID)
                {
                    LOGW("[DEV] report descriptor itf=%u out of range, resync", itf);
                    remote_desc_reset();
                    break;
                }
                // Позначаємо інтерфейс як присутній навіть якщо HID дескриптор з конфіга ще не розібрали.
                s_remote_desc.hid_itf_present[itf] = true;
                remote_desc_append(&s_remote_desc.reports[itf],
                                   &f->data[1],
                                   (uint16_t)(f->len - 1));
                LOGI("[DEV] report descriptor chunk itf=%u len=%u total=%u",
                     itf, f->len - 1, s_remote_desc.reports[itf].len);
                maybe_complete_descriptors();
            }
            break;

        case PF_DESC_STRING:
            if (f->len >= 2)
            {
                uint8_t idx = f->data[0];
                uint16_t slen = f->len - 1;
                LOGI("[DEV] string descriptor frame idx=%u raw_len=%u", idx, slen);
                remote_string_desc_t* entry = remote_desc_get_string_entry(idx);
                if (entry)
                {
                    bool had_valid = entry->valid && entry->len;
                    if (slen == 0)
                    {
                        LOGW("[DEV] string descriptor len=0 ignored idx=%u (keep old=%u)",
                             idx, had_valid ? entry->len : 0);
                        // Do not mark as complete; keep waiting for a real payload.
                        break;
                    }

                    // Захищаємося від перезапису валідної строки укороченим кадром.
                    if (had_valid && slen < entry->len)
                    {
                        LOGW("[DEV] string descriptor idx=%u shorter (%u<%u), keeping existing",
                             idx, slen, entry->len);
                        entry->pending = false;
                        break;
                    }

                    remote_desc_store_string(idx,
                                             entry->langid,
                                             &f->data[1],
                                             slen);
                    LOGI("[DEV] string descriptor stored idx=%u len=%u", idx, entry->len);
                }
                else
                {
                    LOGW("[DEV] string descriptor idx=%u ignored (no entry)",
                         idx);
                }
            }
            else
            {
                LOGW("[DEV] string descriptor frame too short len=%u", f->len);
            }
            break;

        case PF_DESC_DONE:
            LOGI("[DEV] descriptor transmission complete (reset pending)");
            s_remote_desc.descriptors_complete = true;
            // Готуємося до нового READY після повного комплекту дескрипторів.
            s_remote_desc.ready_sent = false;
            remote_storage_analyze_report_descriptors();
            remote_storage_update_string_allowlist();
            maybe_complete_descriptors();
            start_tinyusb_if_ready();
            // Якщо стек уже запущений, одразу повідомляємо хост.
            if (s_remote_desc.usb_attached)
            {
                notify_host_ready();
            }
            break;

        default:
            LOGI("[DEV] descriptor cmd=%u len=%u (not handled yet)",
                 f->cmd, f->len);
            break;
    }
}

static void handle_control_frame(const proto_frame_t *f)
{
    switch (f->cmd)
    {
        case PF_CTRL_GET_REPORT:
            if (!s_get_report_sync.pending)
            {
                LOGW("[DEV] unexpected GET_REPORT response len=%u", f->len);
                return;
            }

            if (f->len < 3)
            {
                s_get_report_sync.pending = false;
                s_get_report_sync.success = false;
                LOGW("[DEV] GET_REPORT response too short");
                return;
            }

            uint8_t itf = f->data[0];
            uint8_t rtype = f->data[1];
            uint8_t rid   = f->data[2];

            // For now we expect instance 0 only; multi-itf support later
            (void)itf;

            if (rtype != s_get_report_sync.report_type ||
                rid   != s_get_report_sync.report_id)
            {
                LOGW("[DEV] GET_REPORT response mismatch type=%u id=%u",
                     rtype, rid);
                return;
            }

            uint16_t copy_len = f->len - 3;
            if (copy_len > s_get_report_sync.buffer_max)
            {
                copy_len = s_get_report_sync.buffer_max;
            }

            if (copy_len && s_get_report_sync.buffer)
            {
                memcpy(s_get_report_sync.buffer, &f->data[3], copy_len);
            }

            s_get_report_sync.actual_len = copy_len;
            s_get_report_sync.success    = true;
            s_get_report_sync.pending    = false;
            break;

        case PF_CTRL_DEVICE_RESET:
            handle_device_reset_request(f->len ? f->data[0] : 0);
            break;

        default:
            LOGW("[DEV] control cmd=%u len=%u ignored", f->cmd, f->len);
            break;
    }
}

static void handle_device_reset_request(uint8_t reason)
{
    LOGI("[DEV] DEVICE_RESET request reason=%u", reason);

    bool descriptors_ready = s_remote_desc.descriptors_complete;
    tinyusb_restart();
    if (!descriptors_ready)
    {
        LOGW("[DEV] descriptors incomplete, waiting for data before reattach");
    }
}

static void handle_unmount_frame(void)
{
    LOGI("[DEV] remote device unmounted");
    remote_desc_reset();
}

static void notify_host_ready(void)
{
    if (s_remote_desc.ready_sent) return;

    uint8_t buf[PROTO_MAX_FRAME_SIZE];
    int out = proto_build_ctrl_ready(buf, sizeof(buf));
    if (out <= 0)
    {
        LOGW("[DEV] failed to build READY control frame");
        return;
    }

    int wr = uart_transport_device_send(buf, (uint16_t)out);
    if (wr < 0)
    {
        LOGW("[DEV] failed to queue READY control frame (wr=%d)", wr);
        return;
    }

    s_remote_desc.ready_sent = true;
    host_irq_pulse();
    LOGI("[DEV] READY control frame queued");
}

// ------------------------------------------------------
// Initialization
// ------------------------------------------------------

void hid_proxy_dev_init(void)
{
    LOGI("[DEV] init");
    remote_desc_reset();

    // 1. Configure the transport: device side uses the dedicated UART link
    uart_transport_init_device();
    host_irq_init();
}

// ------------------------------------------------------
// Frame handling (A_device receives frames from B_host)
// ------------------------------------------------------

void hid_proxy_dev_task(void)
{
    hid_proxy_dev_service();
}

void hid_proxy_dev_service(void)
{
    process_proto_frames();
    flush_pending_reports();
}

static bool request_string_descriptor(uint8_t index, uint16_t langid)
{
    remote_string_desc_t* entry = remote_desc_get_string_entry(index);
    if (!entry || !entry->allow_fetch)
    {
        return false;
    }

    uint8_t ctrl_buf[PROTO_MAX_FRAME_SIZE];
    int out = proto_build_ctrl_string_req(index, langid, ctrl_buf, sizeof(ctrl_buf));
    if (out <= 0)
    {
        LOGW("[DEV] failed to build STRING_REQ frame idx=%u", index);
        return false;
    }

    int wr = uart_transport_device_send(ctrl_buf, (uint16_t)out);
    if (wr < 0)
    {
        LOGW("[DEV] failed to send STRING_REQ frame idx=%u (wr=%d out=%d)",
             index, wr, out);
        return false;
    }
    host_irq_pulse();

    entry->pending = true;
    entry->valid   = false;
    entry->len     = 0;
    entry->langid  = langid;

    LOGI("[DEV] STRING_REQ forwarded idx=%u lang=0x%04X", index, langid);
    return true;
}

static bool wait_for_string_ready(remote_string_desc_t* entry,
                                  uint8_t index,
                                  uint16_t langid,
                                  uint32_t timeout_ms)
{
    if (!entry)
    {
        return false;
    }

    if ((!entry->valid || (index != 0 && entry->langid != langid)) &&
        !entry->pending)
    {
        if (!request_string_descriptor(index, langid))
        {
            return false;
        }
    }

    uint32_t start_ms = board_millis();
    do
    {
        if (entry->valid &&
            entry->len &&
            (index == 0 || entry->langid == langid))
        {
            flush_pending_reports();
            return true;
        }

        hid_proxy_dev_service();
        tight_loop_contents();
    } while ((board_millis() - start_ms) < timeout_ms);

    return entry->valid &&
           entry->len &&
           (index == 0 || entry->langid == langid);
}

static void host_irq_init(void)
{
    gpio_init(PROXY_IRQ_PIN);
    gpio_set_dir(PROXY_IRQ_PIN, GPIO_OUT);
    gpio_put(PROXY_IRQ_PIN, 0);
}

static void host_irq_pulse(void)
{
    uart_hw_t* uart_hw = uart_get_hw(PROXY_UART_ID);
    uint32_t start = time_us_32();
    while ((uart_hw->fr & UART_UARTFR_BUSY_BITS) &&
           ((time_us_32() - start) < 200))
    {
        tight_loop_contents();
    }
    busy_wait_us_32(2);
    gpio_put(PROXY_IRQ_PIN, 1);
    busy_wait_us_32(2);
    gpio_put(PROXY_IRQ_PIN, 0);
}

static void flush_pending_reports(void)
{
    for (uint8_t itf = 0; itf < CFG_TUD_HID; itf++)
    {
        pending_report_t* p = &s_pending_reports[itf];
        if (!p->valid) continue;
        if (!tud_hid_ready()) break;

        if (!tud_hid_n_report(itf, p->has_id ? p->report_id : 0, p->data, p->len))
        {
            // Still busy, try later.
            continue;
        }
        p->valid = false;
    }
}

static void process_proto_frames(void)
{
    proto_frame_t f;
    uint8_t buf[PROTO_MAX_FRAME_SIZE];

    // IMPORTANT: keep UART processing bounded so we don't starve TinyUSB
    // enumeration/state-machine. When B_host starts sending PF_INPUT early
    // (before the PC finishes enumeration), a tight UART drain loop can
    // prevent `tud_task()` from running often enough and enumeration never
    // completes.
    const bool usb_enum_in_progress =
        s_remote_desc.usb_attached &&
        s_remote_desc.tusb_initialized &&
        !tud_hid_ready();

    const uint32_t budget_us =
        usb_enum_in_progress ? (uint32_t)PROXY_UART_RX_BUDGET_ENUM_US
                             : (uint32_t)PROXY_UART_RX_BUDGET_RUN_US;
    const uint32_t t_start_us = time_us_32();
    uint32_t frames_processed = 0;
    const uint32_t max_frames =
        usb_enum_in_progress ? (uint32_t)PROXY_UART_RX_MAX_FRAMES_ENUM
                             : (uint32_t)PROXY_UART_RX_MAX_FRAMES_RUN;

    int len;
    while ((len = uart_transport_recv_frame(buf, sizeof(buf))) > 0)
    {
        bool parsed = proto_parse(buf, (uint16_t)len, &f);
        if (parsed)
        {
            if (INPUT_LOG_VERBOSE)
            {
                LOGT("[DEV] frame type=0x%02X len=%u", f.type, f.len);
            }

            switch (f.type)
            {
                case PF_DESCRIPTOR:
                    handle_descriptor_frame(&f);
                    break;

                case PF_INPUT:
                    if (INPUT_LOG_VERBOSE)
                    {
                        LOGT("[DEV] PF_INPUT len=%u", f.len);
                    }
                    s_input_received++;

                    if (!s_remote_desc.usb_attached)
                    {
                        if (INPUT_LOG_VERBOSE)
                        {
                            LOGT("[DEV] HID stack not started yet, dropping input");
                        }
                        s_input_dropped_not_ready++;
                        break;
                    }

                    if (!s_remote_desc.descriptors_complete || !s_remote_desc.ready_sent)
                    {
                        if (INPUT_LOG_VERBOSE)
                        {
                            LOGT("[DEV] HID NOT READY (descriptors incomplete), dropping");
                        }
                        s_input_dropped_not_ready++;
                        break;
                    }

                    if (!tud_hid_ready())
                    {
                        if (INPUT_LOG_VERBOSE)
                        {
                            // Поки TinyUSB не готовий, просто ігноруємо трафік, щоб не заважати enumeration.
                            LOGT("[DEV] HID NOT READY (enumeration not complete), dropping");
                        }
                        s_input_dropped_not_ready++;
                        // LOGW("[DEV] HID NOT READY (enumeration not complete), sending anyway");
                        break;
                    }

                    if (f.len < 7)
                    {
                        LOGW("[DEV] PF_INPUT too short len=%u", f.len);
                        break;
                    }

                    // Лог інтервалів/дропів раз на ~500 подій або раз на 5 сек
                    uint32_t now_ms = board_millis();
                    if (s_input_last_ts_ms != 0)
                    {
                        uint32_t delta = now_ms - s_input_last_ts_ms;
                        if (delta < s_input_min_delta_ms) s_input_min_delta_ms = delta;
                        if (delta > s_input_max_delta_ms) s_input_max_delta_ms = delta;
                    }
                    s_input_last_ts_ms = now_ms;

                    uint32_t host_ts = (uint32_t)f.data[1] |
                                       ((uint32_t)f.data[2] << 8) |
                                       ((uint32_t)f.data[3] << 16) |
                                       ((uint32_t)f.data[4] << 24);
                    uint16_t seq = (uint16_t)f.data[5] | ((uint16_t)f.data[6] << 8);
                    (void)seq;
                    uint32_t latency;
                    uint32_t offset = now_ms - host_ts;
                    if (!s_host_time_offset_init)
                    {
                        s_host_time_offset_ms   = offset;
                        s_host_time_offset_init = true;
                    }
                    else
                    {
                        // Проста EMA, щоб вирівняти різницю годинників.
                        s_host_time_offset_ms = (s_host_time_offset_ms * 7 + offset) / 8;
                    }

                    if (now_ms >= host_ts + s_host_time_offset_ms)
                    {
                        latency = now_ms - (host_ts + s_host_time_offset_ms);
                    }
                    else
                    {
                        latency = 0;
                    }
                    if (latency < s_latency_min_ms) s_latency_min_ms = latency;
                    if (latency > s_latency_max_ms) s_latency_max_ms = latency;

                    if ((s_input_received % 500 == 0) ||
                        (now_ms - s_input_last_log_ms > 5000))
                    {
                        uint32_t min_d = (s_input_min_delta_ms == UINT32_MAX) ? 0 : s_input_min_delta_ms;
                        uint32_t min_lat = (s_latency_min_ms == UINT32_MAX) ? 0 : s_latency_min_ms;
                        LOGI("[DEV] PF_INPUT stats: received=%lu dropped_not_ready=%lu min_dt=%lu max_dt=%lu lat_min=%lu lat_max=%lu",
                             (unsigned long)s_input_received,
                             (unsigned long)s_input_dropped_not_ready,
                             (unsigned long)min_d,
                             (unsigned long)s_input_max_delta_ms,
                             (unsigned long)min_lat,
                             (unsigned long)s_latency_max_ms);
                        s_input_last_log_ms = now_ms;
                        s_input_min_delta_ms = UINT32_MAX;
                        s_input_max_delta_ms = 0;
                        s_latency_min_ms = UINT32_MAX;
                        s_latency_max_ms = 0;
                    }

                    uint8_t itf_id = f.data[0];
                    uint8_t report_id = 0;
                    uint8_t const* payload = f.data + 7;
                    uint16_t payload_len = f.len - 7;

                    bool has_id = remote_storage_report_has_id(itf_id);
                    if (has_id)
                    {
                        if (payload_len == 0)
                        {
                            LOGW("[DEV] report with ID flag but zero length");
                            break;
                        }
                        report_id = payload[0];
                        payload++;
                        payload_len--;
                    }

                    if (!tud_hid_n_report(itf_id, report_id, payload, payload_len))
                    {
                        pending_report_t* p = &s_pending_reports[itf_id];
                        if (payload_len <= sizeof(p->data))
                        {
                            p->valid    = true;
                            p->has_id   = has_id;
                            p->report_id = report_id;
                            p->len      = payload_len;
                            memcpy(p->data, payload, payload_len);
                            LOGT("[DEV] tud_hid_report busy, queued itf=%u len=%u", itf_id, payload_len);
                        }
                        else
                        {
                            LOGW("[DEV] tud_hid_report busy, drop itf=%u len=%u", itf_id, payload_len);
                        }
                    }
                    break;

                case PF_CONTROL:
                    handle_control_frame(&f);
                    break;

                case PF_UNMOUNT:
                    handle_unmount_frame();
                    break;

                default:
                    LOGI("[DEV] frame type=0x%02X ignored", f.type);
                    break;
            }
        }
        else
        {
            LOGW("[DEV] proto_parse failed len=%d", len);
        }

        start_tinyusb_if_ready();

        // Yield back to main loop regularly so `tud_task()` can run.
        frames_processed++;
        if (frames_processed >= max_frames)
        {
            break;
        }
        if ((time_us_32() - t_start_us) >= budget_us)
        {
            break;
        }
    }
}

bool hid_proxy_dev_usb_ready(void)
{
    return s_remote_desc.usb_attached;
}

// ------------------------------------------------------
// TinyUSB callbacks
// ------------------------------------------------------

void tud_mount_cb(void)
{
    LOGI("[DEV] tud_mount_cb (USB device mounted by host)");
    notify_host_ready();
}

void tud_umount_cb(void)
{
    LOGI("[DEV] tud_umount_cb (USB device unmounted by host)");
}

void tud_suspend_cb(bool remote_wakeup_en)
{
    (void)remote_wakeup_en;
    LOGI("[DEV] tud_suspend_cb");
}

void tud_resume_cb(void)
{
    LOGI("[DEV] tud_resume_cb");
}

void tud_hid_set_protocol_cb(uint8_t instance, uint8_t protocol)
{
    uint8_t buf[PROTO_MAX_FRAME_SIZE];
    int out = proto_build_ctrl_set_protocol(instance, protocol, buf, sizeof(buf));
    if (out <= 0)
    {
        LOGW("[DEV] failed to build SET_PROTOCOL control frame");
        return;
    }

    int wr = uart_transport_device_send(buf, (uint16_t)out);
    if (wr < 0)
    {
        LOGW("[DEV] failed to send SET_PROTOCOL frame (wr=%d out=%d)", wr, out);
    }
    else
    {
        host_irq_pulse();
        LOGI("[DEV] SET_PROTOCOL forwarded itf=%u protocol=%u", instance, protocol);
    }

    hid_proxy_dev_service();
}

bool tud_hid_set_idle_cb(uint8_t instance, uint8_t idle_rate)
{
    uint8_t buf[PROTO_MAX_FRAME_SIZE];
    int out = proto_build_ctrl_set_idle(instance, idle_rate, 0, buf, sizeof(buf));
    if (out <= 0)
    {
        LOGW("[DEV] failed to build SET_IDLE control frame");
        return false;
    }

    int wr = uart_transport_device_send(buf, (uint16_t)out);
    if (wr < 0)
    {
        LOGW("[DEV] failed to send SET_IDLE frame (wr=%d out=%d)", wr, out);
        hid_proxy_dev_service();
        return false;
    }

    host_irq_pulse();
    LOGI("[DEV] SET_IDLE forwarded itf=%u rate=%u", instance, idle_rate);
    hid_proxy_dev_service();
    return true;
}

// --------------------------------------------------------------------
// HID callbacks required by TinyUSB
// --------------------------------------------------------------------

// GET_REPORT (host requests report contents)
uint16_t tud_hid_get_report_cb(uint8_t instance,
                               uint8_t report_id,
                               hid_report_type_t report_type,
                               uint8_t* buffer,
                               uint16_t reqlen)
{
    if (s_get_report_sync.pending)
    {
        LOGW("[DEV] GET_REPORT request while previous pending");
        return 0;
    }

    LOGI("[DEV] GET_REPORT request type=%u id=%u len=%u",
         report_type, report_id, reqlen);

    uint8_t ctrl_buf[PROTO_MAX_FRAME_SIZE];
    int out = proto_build_ctrl_get_report(instance, report_type, report_id, reqlen,
                                          ctrl_buf, sizeof(ctrl_buf));
    if (out <= 0)
    {
        LOGW("[DEV] failed to build GET_REPORT control frame");
        return 0;
    }

    int wr = uart_transport_device_send(ctrl_buf, (uint16_t)out);
    if (wr < 0)
    {
        LOGW("[DEV] failed to send GET_REPORT frame (wr=%d out=%d)", wr, out);
        return 0;
    }
    host_irq_pulse();

    memset(&s_get_report_sync, 0, sizeof(s_get_report_sync));
    s_get_report_sync.pending    = true;
    s_get_report_sync.report_type = (uint8_t)report_type;
    s_get_report_sync.report_id   = report_id;
    s_get_report_sync.buffer      = buffer;
    s_get_report_sync.buffer_max  = reqlen;

    hid_proxy_dev_service();
    uint32_t start = time_us_32();
    while (s_get_report_sync.pending)
    {
        hid_proxy_dev_service();
        if ((time_us_32() - start) > 20000)
        {
            LOGW("[DEV] GET_REPORT timeout");
            s_get_report_sync.pending = false;
            s_get_report_sync.success = false;
            break;
        }
        tight_loop_contents();
    }

    if (s_get_report_sync.success)
    {
        LOGI("[DEV] GET_REPORT response len=%u", s_get_report_sync.actual_len);
        return s_get_report_sync.actual_len;
    }

    LOGW("[DEV] GET_REPORT failed");
    return 0;
}

// SET_REPORT (host sends OUT/Feature report)
// Currently ignored; future revisions can forward it to B_host over UART.
void tud_hid_set_report_cb(uint8_t instance,
                           uint8_t report_id,
                           hid_report_type_t report_type,
                           uint8_t const* buffer,
                           uint16_t bufsize)
{
    uint8_t buf[PROTO_MAX_FRAME_SIZE];
    int out = proto_build_ctrl_set_report(instance,
                                          (uint8_t)report_type,
                                          report_id,
                                          buffer,
                                          bufsize,
                                          buf,
                                          sizeof(buf));
    if (out <= 0)
    {
        LOGW("[DEV] failed to build SET_REPORT control frame");
        return;
    }

    int wr = uart_transport_device_send(buf, (uint16_t)out);
    if (wr < 0)
    {
        LOGW("[DEV] failed to send SET_REPORT frame (wr=%d out=%d)", wr, out);
    }
    else
    {
        host_irq_pulse();
        LOGI("[DEV] SET_REPORT forwarded itf=%u type=%u id=%u len=%u",
             instance, report_type, report_id, bufsize);
    }

    hid_proxy_dev_service();
}

bool hid_proxy_dev_get_string_descriptor(uint8_t index,
                                         uint16_t langid,
                                         uint8_t const** out_data,
                                         uint16_t *out_len)
{
    remote_string_desc_t* entry = remote_desc_get_string_entry(index);
    if (!entry)
    {
        return false;
    }

    uint16_t req_lang = (index == 0) ? 0 : langid;
    bool expect_remote = entry->allow_fetch;
    if (!wait_for_string_ready(entry, index, req_lang, 200))
    {
        // Якщо не готовий саме цей langid, але вже є валідний кеш — віддамо його.
        if (!(entry->valid && entry->len))
        {
            if (expect_remote)
            {
                LOGW("[DEV] string descriptor idx=%u lang=0x%04X not ready",
                     index,
                     langid);
            }
            return false;
        }
    }

    if (!entry->valid || entry->len == 0)
    {
        return false;
    }

    if (index > 2 && entry->len <= 2)
    {
        entry->valid = false;
        entry->allow_fetch = false;
        entry->len = 0;
        entry->langid = 0;
        return false;
    }

    if (out_data) *out_data = entry->data;
    if (out_len)  *out_len  = entry->len;
    return true;
}
