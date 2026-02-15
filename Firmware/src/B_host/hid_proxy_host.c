#include "hid_proxy_host.h"

#include "hid_host.h"
#include "uart_transport.h"
#include "proto_frame.h"
#include "logging.h"
#include "bsp/board.h"
#include "hardware/gpio.h"
#include "proxy_config.h"
#include "descriptor_logger.h"
#include "string_manager.h"
#include "tusb.h"

#include <string.h>
#include "pico/time.h"
#include <limits.h>
#include <limits.h>

#ifndef INPUT_LOG_VERBOSE
#define INPUT_LOG_VERBOSE 0
#endif

	typedef struct
	{
	    bool     active;
	    uint8_t  dev_addr;
	    uint8_t  itf;
	    uint8_t  protocol;
	    uint8_t  itf_protocol; // bInterfaceProtocol (keyboard/mouse/other)
	    uint8_t  inferred_type; // bit0=keyboard, bit1=mouse (from report descriptor)
	    bool     mounted;
	    bool     input_paused;
	    bool     input_pending;
	    bool     input_started;
    bool     input_ready;
    uint32_t input_arm_count;
    uint32_t input_count;
    uint32_t input_skipped_not_ready;
    uint32_t input_last_ts_ms;
    uint32_t input_last_log_ms;
    uint32_t input_min_delta_ms;
    uint32_t input_max_delta_ms;
    uint16_t input_seq;
    uint32_t send_min_us;
    uint32_t send_max_us;
    bool     protocol_report_set;
    bool     protocol_boot_supported;
    uint8_t  protocol_attempts; // attempts to switch to REPORT
} host_itf_state_t;

static host_itf_state_t s_itf[CFG_TUH_HID];

static bool s_wait_ready_ack = false;
static bool s_control_poll_enabled = false;
static volatile bool s_ctrl_irq_pending = false;
static bool s_irq_callback_installed = false;

#define GET_REPORT_BUF_SIZE 64

typedef struct
{
    bool     active;
    uint8_t  itf;
    uint8_t  report_type;
    uint8_t  report_id;
    uint16_t requested_len;
} pending_get_report_t;

static pending_get_report_t s_ctrl_get_report;
static uint8_t              s_ctrl_get_report_buf[GET_REPORT_BUF_SIZE];
static uint64_t             s_ready_retry_deadline = 0;
static uint8_t              s_ready_retry_count    = 0;

static bool send_descriptor_frames(uint8_t cmd, const uint8_t* data, uint16_t len);
static bool send_descriptor_done(void);
static void send_unmount_frame(void);
static bool send_device_reset_command(uint8_t reason);
static void ensure_input_streaming(void);
static void log_input_state(void);
static void set_report_protocol_once(host_itf_state_t* hs);
static void maybe_switch_to_report_protocol(host_itf_state_t* hs, uint16_t report_len);
static bool fetch_control_frame(proto_frame_t* frame);
static bool process_control_frames(void);
static void handle_ctrl_ready(void);
static void handle_ctrl_set_protocol(uint8_t itf, uint8_t protocol);
static void handle_ctrl_set_idle(uint8_t itf, uint8_t duration, uint8_t report_id);
static void handle_ctrl_set_report(uint8_t const* payload, uint16_t len);
static void handle_ctrl_get_report_request(uint8_t const* payload, uint16_t len);
static void send_get_report_response(uint8_t report_type, uint8_t report_id,
                                     uint8_t const* data, uint16_t len);
static bool send_set_idle_request(uint8_t itf, uint8_t duration, uint8_t report_id);
static void control_irq_handler(uint gpio, uint32_t events);

static host_itf_state_t* alloc_slot(uint8_t dev_addr, uint8_t itf);
static host_itf_state_t* ensure_slot_for_itf(uint8_t itf);
static host_itf_state_t* ensure_slot_for_dev_itf(uint8_t dev_addr, uint8_t itf);
static host_itf_state_t* ensure_slot_for_dev_itf(uint8_t dev_addr, uint8_t itf);
static host_itf_state_t* find_slot_by_itf(uint8_t itf);

#define REPORT_DESC_MAX 256
static uint8_t  s_report_desc[CFG_TUH_HID][REPORT_DESC_MAX];
static uint16_t s_report_desc_len[CFG_TUH_HID];
static uint8_t  s_report_desc_trunc[CFG_TUH_HID];

static uint8_t infer_hid_type_from_report_desc(uint8_t const* desc, uint16_t len)
{
    if (!desc || len < 2) return 0;

    uint8_t inferred = 0;
    uint16_t usage_page = 0;

    uint16_t i = 0;
    while (i < len)
    {
        uint8_t b = desc[i++];
        if (b == 0xFE)
        {
            if (i + 1 >= len) break;
            uint8_t data_len = desc[i++];
            i++; // long item tag
            if (i + data_len > len) break;
            i += data_len;
            continue;
        }

        uint8_t size = b & 0x03;
        if (size == 3) size = 4;
        uint8_t type = (b >> 2) & 0x03;
        uint8_t tag  = (b >> 4) & 0x0F;

        uint32_t data = 0;
        if (i + size > len) break;
        for (uint8_t n = 0; n < size; n++)
        {
            data |= ((uint32_t)desc[i + n]) << (8u * n);
        }
        i += size;

        if (type == 1 && tag == 0x0) // Global: Usage Page
        {
            usage_page = (uint16_t)data;
        }
        else if (type == 2 && tag == 0x0) // Local: Usage
        {
            uint16_t usage = (uint16_t)data;
            if (usage_page == 0x01) // Generic Desktop
            {
                if (usage == 0x02) inferred |= 0x02; // Mouse
                if (usage == 0x06) inferred |= 0x01; // Keyboard
            }
        }
    }

    return inferred;
}

void hid_proxy_host_update_inferred_type(uint8_t itf, uint8_t const* desc, uint16_t len)
{
    host_itf_state_t* hs = find_slot_by_itf(itf);
    if (!hs || !hs->active || !hs->mounted)
    {
        return;
    }

    uint8_t inferred = infer_hid_type_from_report_desc(desc, len);
    if (inferred)
    {
        hs->inferred_type |= inferred;
    }
}

void hid_proxy_host_store_report_desc(uint8_t itf, uint8_t const* desc, uint16_t len)
{
    if (itf >= CFG_TUH_HID || !desc || len == 0) return;

    uint16_t copy_len = len;
    uint8_t trunc = 0;
    if (copy_len > REPORT_DESC_MAX)
    {
        copy_len = REPORT_DESC_MAX;
        trunc = 1;
    }

    memcpy(s_report_desc[itf], desc, copy_len);
    s_report_desc_len[itf] = len;
    s_report_desc_trunc[itf] = trunc;
}

uint16_t hid_proxy_host_get_report_desc(uint8_t itf, uint8_t* out, uint16_t max_len, bool* truncated)
{
    if (truncated) *truncated = false;
    if (itf >= CFG_TUH_HID || !out || max_len == 0) return 0;

    uint16_t len = s_report_desc_len[itf];
    if (!len) return 0;

    uint16_t copy_len = len;
    if (copy_len > max_len) copy_len = max_len;
    memcpy(out, s_report_desc[itf], copy_len);

    if (truncated)
    {
        *truncated = s_report_desc_trunc[itf] || (copy_len < len);
    }
    return len;
}

typedef struct
{
    uint8_t report_id;
    uint16_t total_bits;
    uint8_t has_buttons;
    uint8_t buttons_offset_bits;
    uint8_t buttons_count;
    uint8_t buttons_size_bits;
    uint8_t has_x;
    uint8_t x_offset_bits;
    uint8_t x_size_bits;
    uint8_t x_signed;
    uint8_t has_y;
    uint8_t y_offset_bits;
    uint8_t y_size_bits;
    uint8_t y_signed;
    uint8_t has_wheel;
    uint8_t wheel_offset_bits;
    uint8_t wheel_size_bits;
    uint8_t wheel_signed;
    uint8_t has_keyboard;
} report_layout_entry_t;

static int32_t hid_read_signed(uint32_t data, uint8_t size)
{
    if (size == 1)
    {
        return (int8_t)data;
    }
    if (size == 2)
    {
        return (int16_t)data;
    }
    if (size == 4)
    {
        return (int32_t)data;
    }
    return (int32_t)data;
}

static report_layout_entry_t* find_layout_entry(report_layout_entry_t* entries, size_t max_entries, uint8_t report_id, bool create)
{
    for (size_t i = 0; i < max_entries; i++)
    {
        if (entries[i].report_id == report_id) return &entries[i];
    }
    if (create)
    {
        for (size_t i = 0; i < max_entries; i++)
        {
            if (entries[i].report_id == 0xFF)
            {
                entries[i].report_id = report_id;
                return &entries[i];
            }
        }
    }
    return NULL;
}

static void build_usage_list(uint16_t* out, uint8_t* out_count,
                             uint16_t const* usage_list, uint8_t usage_count,
                             int16_t usage_min, int16_t usage_max, uint8_t report_count)
{
    uint8_t count = 0;
    if (usage_count)
    {
        for (uint8_t i = 0; i < usage_count && count < report_count; i++)
        {
            out[count++] = usage_list[i];
        }
    }
    else if (usage_min >= 0 && usage_max >= usage_min)
    {
        for (int16_t u = usage_min; u <= usage_max && count < report_count; u++)
        {
            out[count++] = (uint16_t)u;
        }
    }
    *out_count = count;
}

bool hid_proxy_host_get_report_layout(uint8_t itf, uint8_t report_id, hid_report_layout_t* out)
{
    if (!out) return false;

    uint8_t desc[REPORT_DESC_MAX];
    bool truncated = false;
    uint16_t len = hid_proxy_host_get_report_desc(itf, desc, sizeof(desc), &truncated);
    if (!len) return false;

    report_layout_entry_t entries[8];
    for (size_t i = 0; i < TU_ARRAY_SIZE(entries); i++)
    {
        memset(&entries[i], 0, sizeof(entries[i]));
        entries[i].report_id = 0xFF;
    }

    uint16_t usage_page = 0;
    uint32_t report_size = 0;
    uint32_t report_count = 0;
    int32_t logical_min = 0;
    int16_t usage_min = -1;
    int16_t usage_max = -1;
    uint16_t usage_list[16];
    uint8_t usage_list_count = 0;
    uint8_t cur_report_id = 0;

    uint16_t i = 0;
    while (i < len)
    {
        uint8_t b = desc[i++];
        if (b == 0xFE)
        {
            if (i + 1 >= len) break;
            uint8_t data_len = desc[i++];
            i++; // long item tag
            if (i + data_len > len) break;
            i += data_len;
            continue;
        }

        uint8_t size = b & 0x03;
        if (size == 3) size = 4;
        uint8_t type = (b >> 2) & 0x03;
        uint8_t tag  = (b >> 4) & 0x0F;

        if (i + size > len) break;
        uint32_t data = 0;
        for (uint8_t n = 0; n < size; n++)
        {
            data |= ((uint32_t)desc[i + n]) << (8u * n);
        }
        i += size;

        if (type == 1) // Global
        {
            if (tag == 0x0) usage_page = (uint16_t)data; // Usage Page
            else if (tag == 0x7) report_size = data;     // Report Size
            else if (tag == 0x9) report_count = data;    // Report Count
            else if (tag == 0x8) cur_report_id = (uint8_t)data; // Report ID
            else if (tag == 0x1) logical_min = hid_read_signed(data, size); // Logical Min
        }
        else if (type == 2) // Local
        {
            if (tag == 0x0) // Usage
            {
                if (usage_list_count < TU_ARRAY_SIZE(usage_list))
                {
                    usage_list[usage_list_count++] = (uint16_t)data;
                }
            }
            else if (tag == 0x1) usage_min = (int16_t)data; // Usage Min
            else if (tag == 0x2) usage_max = (int16_t)data; // Usage Max
        }
        else if (type == 0) // Main
        {
            if (tag == 0x08) // Input
            {
                if (report_size == 0 || report_count == 0)
                {
                    usage_list_count = 0;
                    usage_min = usage_max = -1;
                    continue;
                }

                report_layout_entry_t* entry = find_layout_entry(entries, TU_ARRAY_SIZE(entries), cur_report_id, true);
                if (!entry)
                {
                    usage_list_count = 0;
                    usage_min = usage_max = -1;
                    continue;
                }

                uint16_t start_offset = entry->total_bits;
                entry->total_bits = (uint16_t)(entry->total_bits + (uint16_t)(report_size * report_count));

                uint16_t usages[16];
                uint8_t usage_count = 0;
                build_usage_list(usages, &usage_count, usage_list, usage_list_count, usage_min, usage_max, (uint8_t)report_count);

                bool is_constant = (data & 0x01u) != 0;
                if (!is_constant)
                {
                    for (uint8_t idx = 0; idx < report_count; idx++)
                    {
                        uint16_t usage = (idx < usage_count) ? usages[idx] : 0;
                        uint16_t bit_offset = (uint16_t)(start_offset + (uint16_t)(idx * report_size));

                        if (usage_page == 0x09 && !entry->has_buttons)
                        {
                            entry->has_buttons = 1;
                            entry->buttons_offset_bits = (uint8_t)bit_offset;
                            entry->buttons_count = (uint8_t)((report_count > 8) ? 8 : report_count);
                            entry->buttons_size_bits = (uint8_t)report_size;
                        }
                        else if (usage_page == 0x01)
                        {
                            if (usage == 0x30 && !entry->has_x)
                            {
                                entry->has_x = 1;
                                entry->x_offset_bits = (uint8_t)bit_offset;
                                entry->x_size_bits = (uint8_t)report_size;
                                entry->x_signed = (logical_min < 0) ? 1 : 0;
                            }
                            else if (usage == 0x31 && !entry->has_y)
                            {
                                entry->has_y = 1;
                                entry->y_offset_bits = (uint8_t)bit_offset;
                                entry->y_size_bits = (uint8_t)report_size;
                                entry->y_signed = (logical_min < 0) ? 1 : 0;
                            }
                            else if (usage == 0x38 && !entry->has_wheel)
                            {
                                entry->has_wheel = 1;
                                entry->wheel_offset_bits = (uint8_t)bit_offset;
                                entry->wheel_size_bits = (uint8_t)report_size;
                                entry->wheel_signed = (logical_min < 0) ? 1 : 0;
                            }
                        }
                        else if (usage_page == 0x07)
                        {
                            entry->has_keyboard = 1;
                        }
                    }
                }

                usage_list_count = 0;
                usage_min = usage_max = -1;
            }
            else if (tag == 0x09 || tag == 0x0B) // Output/Feature
            {
                usage_list_count = 0;
                usage_min = usage_max = -1;
            }
        }
    }

    report_layout_entry_t* selected = NULL;
    if (report_id != 0)
    {
        selected = find_layout_entry(entries, TU_ARRAY_SIZE(entries), report_id, false);
    }
    else
    {
        for (size_t n = 0; n < TU_ARRAY_SIZE(entries); n++)
        {
            report_layout_entry_t* e = &entries[n];
            if (e->report_id == 0xFF) continue;
            if (e->has_x && e->has_y)
            {
                selected = e;
                break;
            }
        }
        if (!selected)
        {
            for (size_t n = 0; n < TU_ARRAY_SIZE(entries); n++)
            {
                report_layout_entry_t* e = &entries[n];
                if (e->report_id == 0xFF) continue;
                if (e->has_keyboard)
                {
                    selected = e;
                    break;
                }
            }
        }
    }

    if (!selected) return false;

    memset(out, 0, sizeof(*out));
    out->itf = itf;
    out->report_id = selected->report_id;
    out->flags = 0;
    if (selected->has_buttons) out->flags |= 0x01;
    if (selected->has_wheel) out->flags |= 0x02;
    if (selected->has_x) out->flags |= 0x04;
    if (selected->has_y) out->flags |= 0x08;

    bool has_mouse = selected->has_x && selected->has_y;
    bool has_keyboard = selected->has_keyboard;
    out->layout_kind = has_mouse && has_keyboard ? 3 : (has_mouse ? 1 : (has_keyboard ? 2 : 0));

    out->buttons_offset_bits = selected->buttons_offset_bits;
    out->buttons_count = selected->buttons_count;
    out->buttons_size_bits = selected->buttons_size_bits ? selected->buttons_size_bits : 1;
    out->x_offset_bits = selected->x_offset_bits;
    out->x_size_bits = selected->x_size_bits;
    out->x_signed = selected->x_signed;
    out->y_offset_bits = selected->y_offset_bits;
    out->y_size_bits = selected->y_size_bits;
    out->y_signed = selected->y_signed;
    out->wheel_offset_bits = selected->wheel_offset_bits;
    out->wheel_size_bits = selected->wheel_size_bits;
    out->wheel_signed = selected->wheel_signed;

    out->kb_report_len = (uint8_t)((selected->total_bits + 7u) / 8u);
    out->kb_has_report_id = selected->report_id ? 1 : 0;
    return true;
}

uint8_t hid_proxy_host_first_dev_addr(void)
{
    for (size_t i = 0; i < TU_ARRAY_SIZE(s_itf); i++)
    {
        if (s_itf[i].active && s_itf[i].mounted)
        {
            return s_itf[i].dev_addr;
        }
    }
    return 0;
}

static host_itf_state_t* find_slot(uint8_t dev_addr, uint8_t itf)
{
    for (size_t i = 0; i < TU_ARRAY_SIZE(s_itf); i++)
    {
        if (s_itf[i].active &&
            s_itf[i].dev_addr == dev_addr &&
            s_itf[i].itf == itf)
        {
            return &s_itf[i];
        }
    }
    return NULL;
}

static host_itf_state_t* find_slot_by_itf(uint8_t itf)
{
    for (size_t i = 0; i < TU_ARRAY_SIZE(s_itf); i++)
    {
        if (s_itf[i].active && s_itf[i].itf == itf)
        {
            return &s_itf[i];
        }
    }
    return NULL;
}

// Ensure there is a slot for a given interface; used when callbacks for that
// interface не приходять від TinyUSB, але контрольні кадри/запити вже йдуть.
static host_itf_state_t* ensure_slot_for_itf(uint8_t itf)
{
    host_itf_state_t* hs = find_slot_by_itf(itf);
    if (hs) return hs;

    uint8_t dev_addr = hid_proxy_host_first_dev_addr();
    if (!dev_addr)
    {
        return NULL;
    }

    return ensure_slot_for_dev_itf(dev_addr, itf);
}

static host_itf_state_t* alloc_slot(uint8_t dev_addr, uint8_t itf)
{
    host_itf_state_t* existing = find_slot(dev_addr, itf);
    if (existing) return existing;

    for (size_t i = 0; i < TU_ARRAY_SIZE(s_itf); i++)
    {
        if (!s_itf[i].active)
        {
            memset(&s_itf[i], 0, sizeof(s_itf[i]));
            s_itf[i].active   = true;
            s_itf[i].dev_addr = dev_addr;
            s_itf[i].itf      = itf;
            s_itf[i].input_paused = true;
            s_itf[i].input_ready = false;
            s_itf[i].input_count = 0;
            s_itf[i].input_skipped_not_ready = 0;
            s_itf[i].input_last_ts_ms = 0;
            s_itf[i].input_last_log_ms = 0;
            s_itf[i].input_min_delta_ms = UINT32_MAX;
            s_itf[i].input_max_delta_ms = 0;
            s_itf[i].input_seq = 0;
            s_itf[i].send_min_us = UINT32_MAX;
            s_itf[i].send_max_us = 0;
            return &s_itf[i];
        }
    }
    return NULL;
}

static host_itf_state_t* ensure_slot_for_dev_itf(uint8_t dev_addr, uint8_t itf)
{
    host_itf_state_t* hs = find_slot_by_itf(itf);
    if (hs) return hs;

    hs = alloc_slot(dev_addr, itf);
    if (hs)
    {
        hs->mounted  = true;
        hs->input_paused = true;
        hs->input_ready  = false;
        hs->protocol = HID_PROTOCOL_REPORT;
        hs->protocol_report_set = true;
        hs->protocol_boot_supported = true;
        LOGW("[B] created slot for itf=%u dev=%u (no mount callback)", itf, dev_addr);
    }
    return hs;
}

void hid_proxy_host_ensure_slot(uint8_t dev_addr, uint8_t itf)
{
    (void)ensure_slot_for_dev_itf(dev_addr, itf);
}

static bool any_active_for_dev(uint8_t dev_addr)
{
    for (size_t i = 0; i < TU_ARRAY_SIZE(s_itf); i++)
    {
        if (s_itf[i].active && s_itf[i].dev_addr == dev_addr)
        {
            return true;
        }
    }
    return false;
}


static bool host_send_descriptor_frames(uint8_t cmd, const uint8_t* data, uint16_t len)
{
    return send_descriptor_frames(cmd, data, len);
}

static uint32_t host_time_ms(void)
{
    return board_millis();
}

void hid_proxy_host_init(void)
{
    memset(s_itf, 0, sizeof(s_itf));
    s_control_poll_enabled = false;
    s_ctrl_irq_pending = false;

    string_manager_ops_t string_ops = {
        .send_frames = host_send_descriptor_frames,
        .time_ms     = host_time_ms,
    };
    string_manager_init(&string_ops);
    descriptor_logger_ops_t logger_ops = {
        .send_descriptor_frames = host_send_descriptor_frames,
        .send_descriptor_done   = send_descriptor_done,
    };
    descriptor_logger_init(&logger_ops);

    gpio_init(PROXY_IRQ_PIN);
    gpio_set_dir(PROXY_IRQ_PIN, GPIO_IN);
    gpio_pull_down(PROXY_IRQ_PIN);

    if (!s_irq_callback_installed)
    {
        gpio_set_irq_enabled_with_callback(PROXY_IRQ_PIN,
                                           GPIO_IRQ_EDGE_RISE,
                                           true,
                                           control_irq_handler);
        s_irq_callback_installed = true;
    }
    else
    {
        gpio_set_irq_enabled(PROXY_IRQ_PIN, GPIO_IRQ_EDGE_RISE, true);
    }

    LOGI("[B] proxy host init");
}

void hid_proxy_host_task(void)
{
    process_control_frames();
    if (!s_control_poll_enabled)
    {
        s_ctrl_irq_pending = false;
    }

    string_manager_task();
    ensure_input_streaming();
}

void hid_proxy_host_on_mount(uint8_t dev_addr,
                             uint8_t instance,
                             uint8_t const* desc_report,
                             uint16_t desc_len)
{
    if (instance >= CFG_TUH_HID)
    {
        LOGW("[B] HID mount skipped itf=%u (beyond CFG_TUH_HID=%u)",
             instance,
             CFG_TUH_HID);
        return;
    }

    host_itf_state_t* hs = alloc_slot(dev_addr, instance);
    if (!hs)
    {
        LOGW("[B] no free slot for dev=%u itf=%u", dev_addr, instance);
        return;
    }
    hs->mounted  = true;
    hs->input_started = false;
    hs->input_ready   = false;
    hs->input_count   = 0;
    hs->input_skipped_not_ready = 0;
    hs->input_last_ts_ms = 0;
    hs->input_last_log_ms = 0;
    hs->input_min_delta_ms = UINT32_MAX;
    hs->input_max_delta_ms = 0;
    hs->input_seq = 0;
	    hs->send_min_us = UINT32_MAX;
	    hs->send_max_us = 0;
    hs->protocol = HID_PROTOCOL_BOOT;
    hs->itf_protocol = 0;
    hs->inferred_type = 0;
    hs->protocol_report_set     = false;
    hs->protocol_boot_supported = false;
    hs->protocol_attempts       = 0;

    tuh_itf_info_t info;
	    if (tuh_hid_itf_get_info(dev_addr, instance, &info))
	    {
	        uint8_t proto = info.desc.bInterfaceProtocol;
	        hs->itf_protocol = proto;
	        hs->protocol_boot_supported = (proto == HID_ITF_PROTOCOL_KEYBOARD ||
	                                       proto == HID_ITF_PROTOCOL_MOUSE);
	    }

    LOGI("[B] HID mount dev=%u itf=%u desc_len=%u",
         dev_addr,
         instance,
         desc_len);

    string_manager_reset();
    descriptor_logger_start(dev_addr, desc_report, desc_len);
    hs->inferred_type = infer_hid_type_from_report_desc(desc_report, desc_len);

    hs->input_pending = false;

    // Не форвардимо репорт-дескриптор одразу з mount — дочекаємося повного
    // комплекту з descriptor_logger після конфіг/додаткових fetch, щоб
    // черговість device/config/HID залишалась коректною на A_device.
}

void hid_proxy_host_on_unmount(uint8_t dev_addr, uint8_t instance)
{
    LOGI("[B] HID unmount dev=%u itf=%u", dev_addr, instance);
    send_unmount_frame();
    host_itf_state_t* hs = find_slot(dev_addr, instance);
	    if (hs)
	    {
	        hs->input_paused   = true;
	        hs->input_started  = false;
	        hs->input_ready    = false;
	        hs->input_pending  = false;
	        hs->protocol_report_set     = false;
	        hs->protocol_boot_supported = false;
	        hs->protocol_attempts       = 0;
	        hs->itf_protocol = 0;
	        hs->mounted = false;
	        hs->active  = false;
	    }
    if (instance < CFG_TUH_HID)
    {
        s_report_desc_len[instance] = 0;
        s_report_desc_trunc[instance] = 0;
    }

    s_wait_ready_ack = false;
    s_control_poll_enabled = false;
    descriptor_logger_reset();
    string_manager_reset();
}

void hid_proxy_host_on_report(uint8_t dev_addr, uint8_t instance,
                              uint8_t const* report, uint16_t len)
{
    uint8_t buf[PROTO_MAX_FRAME_SIZE];

    host_itf_state_t* hs = find_slot(dev_addr, instance);
    if (!hs || !hs->mounted)
    {
        return;
    }

    if (INPUT_LOG_VERBOSE)
    {
        LOGT("[B] on_report dev=%u itf=%u paused=%u wait_ready=%u len=%u",
             hs->dev_addr,
             hs->itf,
             hs->input_paused ? 1 : 0,
             s_wait_ready_ack ? 1 : 0,
             len);
    }

    uint32_t now_ms = board_millis();
    uint32_t t_start_us = time_us_32();
    hs->input_count++;
    if (hs->input_last_ts_ms != 0)
    {
        uint32_t delta = now_ms - hs->input_last_ts_ms;
        if (delta < hs->input_min_delta_ms) hs->input_min_delta_ms = delta;
        if (delta > hs->input_max_delta_ms) hs->input_max_delta_ms = delta;
    }
    hs->input_last_ts_ms = now_ms;

    maybe_switch_to_report_protocol(hs, len);

    if (hs->input_paused || s_wait_ready_ack)
    {
        hs->input_skipped_not_ready++;
        if (INPUT_LOG_VERBOSE)
        {
            LOGW("[B] skipping input frame (not ready) itf=%u len=%u", hs->itf, len);
        }
        goto restart_receive;
    }
    if (!hs->input_ready)
    {
        hs->input_skipped_not_ready++;
        if (INPUT_LOG_VERBOSE)
        {
            LOGW("[B] skipping input frame (READY not acked) itf=%u len=%u", hs->itf, len);
        }
        goto restart_receive;
    }

    int out = proto_build_input(hs->itf, now_ms, hs->input_seq++, report, len, buf, sizeof(buf));
    if (out > 0)
    {
        int wr = uart_transport_send(buf, (uint16_t)out);
        if (wr < 0)
        {
            LOGW("[B] UART send input frame failed wr=%d out=%d", wr, out);
        }
        else
        {
            if (INPUT_LOG_VERBOSE)
            {
                LOGT("[B] input frame sent len=%d", out);
            }
        }
        uint32_t t_end_us = time_us_32();
        uint32_t send_us = t_end_us - t_start_us;
        if (send_us < hs->send_min_us) hs->send_min_us = send_us;
        if (send_us > hs->send_max_us) hs->send_max_us = send_us;
    }
    else
    {
        LOGW("[B] proto_build_input failed len=%u", len);
    }

restart_receive:
    if (!tuh_hid_receive_report(hs->dev_addr, hs->itf))
    {
        LOGW("[B] tuh_hid_receive_report() failed after report");
        hs->input_pending = false;
        hs->input_started = false;
    }
    else
    {
        hs->input_started = true;
        hs->input_pending = true;
    }

    if ((hs->input_count % 500 == 0) || (now_ms - hs->input_last_log_ms > 5000))
    {
        uint32_t min_d = (hs->input_min_delta_ms == UINT32_MAX) ? 0 : hs->input_min_delta_ms;
        uint32_t min_send = (hs->send_min_us == UINT32_MAX) ? 0 : hs->send_min_us;
        LOGI("[B] input stats itf=%u cnt=%lu skipped=%lu min_dt=%lu max_dt=%lu send_min_us=%lu send_max_us=%lu",
             hs->itf,
             (unsigned long)hs->input_count,
             (unsigned long)hs->input_skipped_not_ready,
             (unsigned long)min_d,
             (unsigned long)hs->input_max_delta_ms,
             (unsigned long)min_send,
             (unsigned long)hs->send_max_us);
        hs->input_last_log_ms = now_ms;
        hs->input_min_delta_ms = UINT32_MAX;
        hs->input_max_delta_ms = 0;
        hs->send_min_us = UINT32_MAX;
        hs->send_max_us = 0;
    }
}

void tuh_hid_get_report_complete_cb(uint8_t dev_addr,
                                    uint8_t instance,
                                    uint8_t report_id,
                                    uint8_t report_type,
                                    uint16_t len)
{
    if (s_ctrl_get_report.active &&
        (s_ctrl_get_report.itf != instance))
    {
        LOGW("[B] GET_REPORT complete wrong itf=%u expected=%u", instance, s_ctrl_get_report.itf);
        return;
    }
    if (len > s_ctrl_get_report.requested_len)
    {
        len = s_ctrl_get_report.requested_len;
    }

    LOGI("[B] GET_REPORT complete type=%u id=%u len=%u",
         report_type,
         report_id,
         len);

    send_get_report_response(report_type,
                             report_id,
                             (len ? s_ctrl_get_report_buf : NULL),
                             len);
    s_ctrl_get_report.active = false;
}

static bool fetch_control_frame(proto_frame_t* frame)
{
    uint8_t buf[PROTO_MAX_FRAME_SIZE];
    int len = uart_transport_recv_frame(buf, sizeof(buf));
    if (len <= 0) return false;

    if (!proto_parse(buf, (uint16_t)len, frame))
    {
        LOGW("[B] control frame CRC/parse failed len=%d", len);
        return false;
    }

    LOGI("[B] control frame type=0x%02X cmd=%u len=%u",
         frame->type,
         frame->cmd,
         frame->len);
    return true;
}

static bool process_control_frames(void)
{
    bool handled = false;
    proto_frame_t frame;

    while (fetch_control_frame(&frame))
    {
        handled = true;
        if (frame.type != PF_CONTROL)
        {
            LOGW("[B] unexpected frame type=0x%02X", frame.type);
            continue;
        }

        switch (frame.cmd)
        {
            case PF_CTRL_READY:
                handle_ctrl_ready();
                break;

            case PF_CTRL_SET_PROTOCOL:
                if (frame.len >= 2)
                {
                    handle_ctrl_set_protocol(frame.data[0], frame.data[1]);
                }
                else
                {
                    LOGW("[B] SET_PROTOCOL frame too short");
                }
                break;

            case PF_CTRL_SET_IDLE:
                if (frame.len >= 3)
                {
                    handle_ctrl_set_idle(frame.data[0], frame.data[1], frame.data[2]);
                }
                else
                {
                    LOGW("[B] SET_IDLE frame too short");
                }
                break;

            case PF_CTRL_SET_REPORT:
                if (frame.len >= 3)
                {
                    handle_ctrl_set_report(frame.data, frame.len);
                }
                else
                {
                    LOGW("[B] SET_REPORT frame too short");
                }
                break;

            case PF_CTRL_GET_REPORT:
                if (frame.len >= 5)
                {
                    handle_ctrl_get_report_request(frame.data, frame.len);
                }
                else
                {
                    LOGW("[B] GET_REPORT frame too short");
                }
                break;

            case PF_CTRL_STRING_REQ:
                string_manager_handle_ctrl_request(frame.data, frame.len);
                break;

            default:
                LOGW("[B] unknown control cmd=%u len=%u", frame.cmd, frame.len);
                break;
        }
    }

    return handled;
}

static void handle_ctrl_ready(void)
{
    // Завжди реагуємо на READY, навіть якщо флаг уже скинуто.
    s_wait_ready_ack = false;
    s_ready_retry_deadline = 0;
    for (size_t i = 0; i < TU_ARRAY_SIZE(s_itf); i++)
    {
        if (s_itf[i].active && s_itf[i].mounted)
        {
            s_itf[i].input_paused = false;
            s_itf[i].input_ready  = true;
            if (s_itf[i].protocol_boot_supported && !s_itf[i].protocol_report_set)
            {
                set_report_protocol_once(&s_itf[i]);
            }
        }
    }
    s_control_poll_enabled = false;
    s_ctrl_irq_pending = false;

    LOGI("[B] READY ack received");
    ensure_input_streaming();
}

static void handle_ctrl_set_protocol(uint8_t itf, uint8_t protocol)
{
    host_itf_state_t* hs = find_slot_by_itf(itf);
    if (hs)
    {
        hs->protocol = protocol;
    }

    if (!hs || !hs->mounted)
    {
        LOGW("[B] SET_PROTOCOL ignored (no device/itf)");
        return;
    }

    if (tuh_hid_set_protocol(hs->dev_addr, itf, protocol))
    {
        LOGI("[B] SET_PROTOCOL forwarded itf=%u protocol=%u", itf, protocol);
        if (protocol == HID_PROTOCOL_REPORT)
        {
            hs->protocol_report_set  = true;
            hs->protocol_attempts    = 1;
        }
    }
    else
    {
        LOGW("[B] tuh_hid_set_protocol failed itf=%u protocol=%u", itf, protocol);
    }
}

static void handle_ctrl_set_idle(uint8_t itf, uint8_t duration, uint8_t report_id)
{
    host_itf_state_t* hs = ensure_slot_for_itf(itf);
    if (!hs || !hs->mounted)
    {
        LOGW("[B] SET_IDLE ignored (no device/itf)");
        return;
    }

    if (send_set_idle_request(itf, duration, report_id))
    {
        LOGI("[B] SET_IDLE forwarded itf=%u duration=%u rid=%u",
             itf,
             duration,
             report_id);
    }
    else
    {
        LOGW("[B] tuh_hid_set_idle failed itf=%u duration=%u rid=%u",
             itf,
             duration,
             report_id);
    }
}

static void handle_ctrl_set_report(uint8_t const* payload, uint16_t len)
{
    if (!len)
    {
        return;
    }

    if (len < 3)
    {
        LOGW("[B] SET_REPORT payload too short len=%u", len);
        return;
    }

    uint8_t itf         = payload[0];
    host_itf_state_t* hs = ensure_slot_for_itf(itf);
    if (!hs || !hs->mounted)
    {
        LOGW("[B] SET_REPORT ignored wrong itf=%u", itf);
        return;
    }

    uint8_t report_type = payload[1];
    uint8_t report_id   = payload[2];
    uint16_t report_len = len - 3;

    if (!tuh_hid_set_report(hs->dev_addr,
                            itf,
                            report_id,
                            report_type,
                            (void*)(uintptr_t)(payload + 3),
                            report_len))
    {
        LOGW("[B] tuh_hid_set_report failed itf=%u type=%u id=%u len=%u",
             itf,
             report_type,
             report_id,
             report_len);
    }
    else
    {
        LOGI("[B] SET_REPORT forwarded itf=%u type=%u id=%u len=%u",
             itf,
             report_type,
             report_id,
             report_len);
    }
}

static void handle_ctrl_get_report_request(uint8_t const* payload, uint16_t len)
{
    if (len < 5)
    {
        LOGW("[B] GET_REPORT payload too short len=%u", len);
        return;
    }

    uint8_t itf = payload[0];
    host_itf_state_t* hs = ensure_slot_for_itf(itf);
    if (!hs || !hs->mounted)
    {
        LOGW("[B] GET_REPORT ignored wrong itf=%u", itf);
        return;
    }

    if (s_ctrl_get_report.active)
    {
        LOGW("[B] GET_REPORT request already active");
        return;
    }

    s_ctrl_get_report.active        = true;
    s_ctrl_get_report.itf           = itf;
    s_ctrl_get_report.report_type   = payload[1];
    s_ctrl_get_report.report_id     = payload[2];
    s_ctrl_get_report.requested_len = (uint16_t)payload[3] | ((uint16_t)payload[4] << 8);

    if (!tuh_hid_get_report(hs->dev_addr,
                            itf,
                            s_ctrl_get_report.report_id,
                            s_ctrl_get_report.report_type,
                            s_ctrl_get_report_buf,
                            sizeof(s_ctrl_get_report_buf)))
    {
        LOGW("[B] tuh_hid_get_report failed type=%u id=%u len=%u",
             s_ctrl_get_report.report_type,
             s_ctrl_get_report.report_id,
             s_ctrl_get_report.requested_len);
        s_ctrl_get_report.active = false;
    }
    else
    {
        LOGI("[B] GET_REPORT forwarded itf=%u type=%u id=%u len=%u",
             itf,
             s_ctrl_get_report.report_type,
             s_ctrl_get_report.report_id,
             s_ctrl_get_report.requested_len);
    }
}

static bool send_set_idle_request(uint8_t itf, uint8_t duration, uint8_t report_id)
{
    host_itf_state_t* hs = find_slot_by_itf(itf);
    if (!hs || !hs->mounted)
    {
        return false;
    }

    tuh_itf_info_t info;
    if (!tuh_hid_itf_get_info(hs->dev_addr, itf, &info))
    {
        return false;
    }

    tusb_control_request_t const request = {
        .bmRequestType_bit = {
            .recipient = TUSB_REQ_RCPT_INTERFACE,
            .type      = TUSB_REQ_TYPE_CLASS,
            .direction = TUSB_DIR_OUT
        },
        .bRequest = HID_REQ_CONTROL_SET_IDLE,
        .wValue   = tu_htole16(((uint16_t)duration << 8) | report_id),
        .wIndex   = tu_htole16(info.desc.bInterfaceNumber),
        .wLength  = 0
    };

    tuh_xfer_t xfer = {
        .daddr       = hs->dev_addr,
        .ep_addr     = 0,
        .setup       = &request,
        .buffer      = NULL,
        .complete_cb = NULL,
        .user_data   = 0
    };

    return tuh_control_xfer(&xfer);
}

static void send_get_report_response(uint8_t report_type, uint8_t report_id,
                                     uint8_t const* data, uint16_t len)
{
    uint8_t buf[PROTO_MAX_FRAME_SIZE];
    int out = proto_build_ctrl_get_report_resp(s_ctrl_get_report.itf,
                                               report_type,
                                               report_id,
                                               data,
                                               len,
                                               buf,
                                               sizeof(buf));
    if (out <= 0)
    {
        LOGW("[B] proto_build_ctrl_get_report_resp failed len=%u", len);
        return;
    }

    int wr = uart_transport_send(buf, (uint16_t)out);
    if (wr < 0)
    {
        LOGW("[B] UART send GET_REPORT response failed wr=%d out=%d", wr, out);
    }
    else
    {
        LOGI("[B] GET_REPORT response sent len=%d", out);
    }
}

static void ensure_input_streaming(void)
{
    if (s_wait_ready_ack && s_ready_retry_deadline)
    {
        uint64_t now = to_ms_since_boot(get_absolute_time());
        if (now >= s_ready_retry_deadline)
        {
            s_ready_retry_count++;
            if (s_ready_retry_count > 5)
            {
                LOGW("[B] READY ack timeout exceeded, forcing UNMOUNT/RESET");
                s_wait_ready_ack = false;
                s_control_poll_enabled = false;
                send_unmount_frame();
                send_device_reset_command(PF_RESET_REASON_REENUMERATE);
                s_ready_retry_deadline = 0;
                return;
            }

            LOGW("[B] READY ack timeout, re-sending descriptor DONE (retry %u)",
                 s_ready_retry_count);
            s_ready_retry_deadline = now + 300;
            send_descriptor_done();
            return;
        }
    }

    for (size_t i = 0; i < TU_ARRAY_SIZE(s_itf); i++)
    {
        host_itf_state_t* hs = &s_itf[i];
        if (!hs->active || !hs->mounted) continue;
        if (hs->input_paused || s_wait_ready_ack)
        {
            hs->input_started = false;
            continue;
        }
        if (hs->input_pending) continue;

        if (!tuh_hid_receive_report(hs->dev_addr, hs->itf))
        {
            LOGW("[B] tuh_hid_receive_report() failed to start/continue input");
            hs->input_started = false;
        }
        else
        {
            LOGI("[B] input stream armed (dev=%u itf=%u)", hs->dev_addr, hs->itf);
            hs->input_arm_count++;
            hs->input_started = true;
            hs->input_pending = true;
        }
    }
}

static void log_input_state(void)
{
    for (size_t i = 0; i < TU_ARRAY_SIZE(s_itf); i++)
    {
        host_itf_state_t* hs = &s_itf[i];
        if (!hs->active) continue;
        LOGT("[B] input state dev=%u itf=%u mounted=%u paused=%u ready=%u wait_ready=%u started=%u arms=%lu cnt=%lu skip=%lu",
             hs->dev_addr,
             hs->itf,
             hs->mounted ? 1 : 0,
             hs->input_paused ? 1 : 0,
             hs->input_ready ? 1 : 0,
             s_wait_ready_ack ? 1 : 0,
             hs->input_started ? 1 : 0,
             (unsigned long)hs->input_arm_count,
             (unsigned long)hs->input_count,
             (unsigned long)hs->input_skipped_not_ready);
    }
}

static void set_report_protocol_once(host_itf_state_t* hs)
{
    if (!hs || !hs->mounted || hs->protocol_report_set)
    {
        return;
    }

    if (!hs->protocol_boot_supported)
    {
        LOGT("[B] skip protocol switch (boot not supported)");
        return;
    }

    if (hs->protocol_attempts >= 2)
    {
        return;
    }

    hs->protocol_attempts++;

    if (tuh_hid_set_protocol(hs->dev_addr, hs->itf, HID_PROTOCOL_REPORT))
    {
        hs->protocol        = HID_PROTOCOL_REPORT;
        hs->protocol_report_set = true;
        LOGI("[B] HID protocol REPORT set dev=%u itf=%u", hs->dev_addr, hs->itf);
    }
    else
    {
        LOGW("[B] HID protocol REPORT set failed dev=%u itf=%u (attempt %u)",
             hs->dev_addr,
             hs->itf,
             (unsigned)hs->protocol_attempts);
    }
}

static void maybe_switch_to_report_protocol(host_itf_state_t* hs, uint16_t report_len)
{
    if (!hs || !hs->mounted)
    {
        return;
    }

    if (hs->protocol == HID_PROTOCOL_REPORT || report_len > 3)
    {
        hs->protocol_report_set = true;
        return;
    }

    if (!hs->protocol_boot_supported)
    {
        return;
    }

    if (!hs->protocol_report_set && hs->protocol_attempts < 2)
    {
        set_report_protocol_once(hs);
    }
}

static void control_irq_handler(uint gpio, uint32_t events)
{
    (void)events;
    if (gpio == PROXY_IRQ_PIN)
    {
        s_ctrl_irq_pending = true;
    }
}

static bool send_descriptor_frames(uint8_t cmd, const uint8_t* data, uint16_t len)
{
    // Для рядків надсилаємо одним кадром (якщо влазить), щоб не обрізати payload.
    if (cmd == PF_DESC_STRING)
    {
        if (len + 1 > PROTO_MAX_PAYLOAD_SIZE)
        {
            LOGW("[B] string descriptor too long len=%u", len);
            return false;
        }

        uint8_t buf[PROTO_MAX_FRAME_SIZE];
        int out = proto_build_descriptor(cmd, data, len, buf, sizeof(buf));
        if (out <= 0)
        {
            LOGW("[B] proto_build_descriptor failed cmd=%u len=%u", cmd, len);
            return false;
        }

        int wr = uart_transport_send(buf, (uint16_t)out);
        if (wr < 0)
        {
            LOGW("[B] UART send descriptor failed cmd=%u wr=%d out=%d", cmd, wr, out);
            return false;
        }
        return true;
    }

    if (cmd == PF_DESC_REPORT)
    {
        LOGI("[B] sending report descriptor itf=%u total_len=%u", data ? data[0] : 0, len);
    }

    const uint16_t chunk_max = 48; // дрібніші шматки знижують ризик переповнення UART RX

    // Для PF_DESC_REPORT перший байт data — itf_id; кожен кадр має його містити.
    uint8_t itf_id = 0;
    const uint8_t* desc_ptr = data;
    uint16_t desc_len = len;
    if (cmd == PF_DESC_REPORT)
    {
        if (len == 0)
        {
            LOGW("[B] PF_DESC_REPORT len=0");
            return false;
        }
        itf_id = data[0];
        desc_ptr = data + 1;
        desc_len = (uint16_t)(len - 1);
    }

    uint16_t offset = 0;
    while (offset < desc_len)
    {
        uint16_t chunk = desc_len - offset;
        if (chunk > chunk_max)
        {
            chunk = chunk_max;
        }

        uint8_t payload[PROTO_MAX_PAYLOAD_SIZE];
        uint16_t payload_len = chunk;
        const uint8_t* payload_src = desc_ptr + offset;

        if (cmd == PF_DESC_REPORT)
        {
            // Додаємо itf_id перед кожним шматком дескриптора.
            payload[0] = itf_id;
            memcpy(&payload[1], payload_src, chunk);
            payload_len = (uint16_t)(chunk + 1);
            payload_src = payload;
        }

        uint8_t buf[PROTO_MAX_FRAME_SIZE];
        int out = proto_build_descriptor(cmd, payload_src, payload_len, buf, sizeof(buf));
        if (out <= 0)
        {
            LOGW("[B] proto_build_descriptor failed cmd=%u chunk=%u", cmd, chunk);
            return false;
        }

        bool sent = false;
        for (int attempt = 0; attempt < 3 && !sent; attempt++)
        {
        int wr = uart_transport_send(buf, (uint16_t)out);
        if (wr < 0)
        {
            LOGW("[B] UART send descriptor failed cmd=%u wr=%d out=%d attempt=%d",
                 cmd, wr, out, attempt + 1);
            sleep_ms(1);
        }
        else
        {
            if (cmd == PF_DESC_REPORT)
            {
                LOGI("[B] sent report chunk itf=%u off=%u size=%u payload_len=%u",
                     itf_id, offset, chunk, payload_len);
            }
            sent = true;
        }
    }
    if (!sent) return false;

    offset += chunk;
        sleep_ms(2);
    }

    return true;
}

static bool send_descriptor_done(void)
{
    uint8_t buf[PROTO_MAX_FRAME_SIZE];
    int out = proto_build_descriptor(PF_DESC_DONE, NULL, 0, buf, sizeof(buf));
    if (out <= 0)
    {
        LOGW("[B] proto_build_descriptor DONE failed");
        return false;
    }

    bool sent = false;
    for (int attempt = 0; attempt < 3 && !sent; attempt++)
    {
        int wr = uart_transport_send(buf, (uint16_t)out);
        if (wr < 0)
        {
            LOGW("[B] UART send descriptor DONE failed wr=%d out=%d attempt=%d",
                 wr, out, attempt + 1);
            sleep_ms(1);
        }
        else
        {
            sent = true;
        }
    }
    if (!sent) return false;

    s_wait_ready_ack = true;
    s_ready_retry_deadline = to_ms_since_boot(get_absolute_time()) + 300; // 300ms до повтору
    s_ready_retry_count    = 0;
    for (size_t i = 0; i < TU_ARRAY_SIZE(s_itf); i++)
    {
        if (s_itf[i].active)
        {
            s_itf[i].input_paused = true;
            s_itf[i].input_ready  = false;
        }
    }
    s_control_poll_enabled = true;
    LOGI("[B] Descriptor transmission complete");

    return true;
}

static void send_unmount_frame(void)
{
    uint8_t buf[PROTO_MAX_FRAME_SIZE];
    int out = proto_build_unmount(buf, sizeof(buf));
    if (out > 0)
    {
        int wr = uart_transport_send(buf, (uint16_t)out);
        if (wr < 0)
        {
            LOGW("[B] UART send UNMOUNT failed wr=%d out=%d", wr, out);
        }
        else
        {
            LOGI("[B] UNMOUNT frame sent");
        }
    }
    else
    {
        LOGW("[B] proto_build_unmount failed");
    }
}

bool hid_proxy_host_request_device_reset(uint8_t reason)
{
    return send_device_reset_command(reason);
}

size_t hid_proxy_host_list_interfaces(hid_proxy_itf_info_t* out, size_t max_entries)
{
    if (!out || max_entries == 0) return 0;

    size_t written = 0;
    for (size_t i = 0; i < TU_ARRAY_SIZE(s_itf); i++)
    {
        if (!s_itf[i].active) continue;
        if (written >= max_entries) break;

        out[written].dev_addr    = s_itf[i].dev_addr;
        out[written].itf         = s_itf[i].itf;
        out[written].itf_protocol= s_itf[i].itf_protocol;
        out[written].protocol    = s_itf[i].protocol;
        out[written].inferred_type = s_itf[i].inferred_type;
        out[written].active      = s_itf[i].active ? 1 : 0;
        out[written].mounted     = s_itf[i].mounted ? 1 : 0;
        written++;
    }
    return written;
}

static host_itf_state_t* find_first_protocol(uint8_t itf_protocol)
{
    for (size_t i = 0; i < TU_ARRAY_SIZE(s_itf); i++)
    {
        host_itf_state_t* hs = &s_itf[i];
        if (!hs->active || !hs->mounted) continue;
        if (hs->itf_protocol == itf_protocol)
        {
            return hs;
        }
    }
    return NULL;
}

bool hid_proxy_host_inject_report(uint8_t itf_sel, uint8_t const* report, uint16_t len)
{
    if (!report || len == 0)
    {
        return false;
    }

    host_itf_state_t* hs = NULL;
    if (itf_sel == 0xFF)
    {
        hs = find_first_protocol(HID_ITF_PROTOCOL_MOUSE);
    }
    else if (itf_sel == 0xFE)
    {
        hs = find_first_protocol(HID_ITF_PROTOCOL_KEYBOARD);
    }
    else
    {
        hs = find_slot_by_itf(itf_sel);
    }

    if (!hs || !hs->active || !hs->mounted)
    {
        return false;
    }

    if (hs->input_paused || s_wait_ready_ack || !hs->input_ready)
    {
        hs->input_skipped_not_ready++;
        return false;
    }

    uint8_t buf[PROTO_MAX_FRAME_SIZE];
    uint32_t now_ms = board_millis();
    int out = proto_build_input(hs->itf, now_ms, hs->input_seq++, report, len, buf, sizeof(buf));
    if (out <= 0)
    {
        return false;
    }

    int wr = uart_transport_send(buf, (uint16_t)out);
    if (wr < 0)
    {
        return false;
    }
    return true;
}

static bool send_device_reset_command(uint8_t reason)
{
    uint8_t buf[PROTO_MAX_FRAME_SIZE];
    int out = proto_build_ctrl_device_reset(reason, buf, sizeof(buf));
    if (out <= 0)
    {
        return false;
    }

    int wr = uart_transport_send(buf, (uint16_t)out);
    if (wr < 0)
    {
        LOGW("[B] UART send DEVICE_RESET failed wr=%d out=%d", wr, out);
        return false;
    }

    LOGI("[B] DEVICE_RESET command sent reason=%u", reason);
    return true;
}
