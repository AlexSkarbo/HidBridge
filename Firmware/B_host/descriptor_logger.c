#include "descriptor_logger.h"

#include "hid_proxy_host.h"
#include "logging.h"
#include "proto_frame.h"
#include "string_manager.h"
#include "tusb.h"
#include "pico/stdlib.h"

#include <string.h>

#ifndef TUSB_DESC_HID
#define TUSB_DESC_HID 0x21
#endif
#ifndef HID_DESC_TYPE_REPORT
#define HID_DESC_TYPE_REPORT 0x22
#endif

#define DESC_LOG_MAX_CONFIG_LEN 512
#define DESC_LOG_HEX_CHUNK      16

#define DESC_FWD_DEVICE  TU_BIT(0)
#define DESC_FWD_CONFIG  TU_BIT(1)
#define DESC_FWD_STRINGS TU_BIT(2)

typedef enum
{
    DESC_STR_STAGE_LANG = 0,
    DESC_STR_STAGE_MANUF,
    DESC_STR_STAGE_PRODUCT,
    DESC_STR_STAGE_SERIAL,
    DESC_STR_STAGE_DONE
} desc_str_stage_t;

typedef struct
{
    uint8_t  dev_addr;
    bool     active;
    uint16_t langid;
    uint16_t cfg_len;
    tusb_desc_device_t device;
    uint8_t  cfg_buf[DESC_LOG_MAX_CONFIG_LEN];
    uint8_t  string_buf[PROXY_STRING_DESC_MAX];
    uint8_t  string_indices[3];
    uint8_t  forward_pending;
    uint8_t  hid_report_expected_mask;
    uint8_t  hid_report_forwarded_mask;
    uint8_t  hid_fetch_pending;
    uint16_t hid_report_len[CFG_TUH_HID];
    bool     hid_config_seen;
    bool     done_sent;
} descriptor_log_ctx_t;

static descriptor_logger_ops_t s_ops;
static descriptor_log_ctx_t s_desc_log;

static void descriptor_log_reset(void);
static void descriptor_log_start_internal(uint8_t dev_addr,
                                          const uint8_t* report_desc,
                                          uint16_t report_len);
static void descriptor_log_dump_hex(const char* label,
                                    const uint8_t* data,
                                    uint16_t len);
static void descriptor_log_print_interfaces(const uint8_t* desc,
                                            uint16_t len);
static void descriptor_log_report_cb(tuh_xfer_t* xfer);
static void descriptor_forward_reset(void);
static void descriptor_forward_set_pending(uint8_t mask);
static void descriptor_forward_clear_pending(uint8_t mask);
static void descriptor_forward_try_complete(void);
static const char* descriptor_log_stage_label(desc_str_stage_t stage);
static void descriptor_log_schedule_strings(desc_str_stage_t stage);
static void descriptor_log_device_cb(tuh_xfer_t* xfer);
static void descriptor_log_config_cb(tuh_xfer_t* xfer);
static void descriptor_log_string_cb(tuh_xfer_t* xfer);
static void descriptor_log_request_config(void);
static void descriptor_log_fetch_missing_reports(void)
{
    if (!s_desc_log.active)
    {
        return;
    }

    // Не відправляємо кілька control-запитів одночасно: дочекаємось завершення попереднього.
    if (s_desc_log.hid_fetch_pending)
    {
        return;
    }

    uint8_t missing = s_desc_log.hid_report_expected_mask &
                      (uint8_t)~s_desc_log.hid_report_forwarded_mask;

    for (uint8_t itf = 0; itf < CFG_TUH_HID; itf++)
    {
        if (!(missing & TU_BIT(itf)))
        {
            continue;
        }

        uint16_t rep_len = s_desc_log.hid_report_len[itf];
        if (rep_len == 0 || rep_len > sizeof(s_desc_log.cfg_buf))
        {
            rep_len = sizeof(s_desc_log.cfg_buf);
        }

        bool queued = tuh_descriptor_get_hid_report(s_desc_log.dev_addr,
                                                    itf,
                                                    HID_DESC_TYPE_REPORT,
                                                    0,
                                                    s_desc_log.cfg_buf,
                                                    rep_len,
                                                    descriptor_log_report_cb,
                                                    itf);
        if (queued)
        {
            LOGI("[B] requesting HID report descriptor itf=%u len=%u",
                 itf, rep_len);
            s_desc_log.hid_fetch_pending = TU_BIT(itf);
            break; // чекаємо завершення, потім візьмемо наступний
        }
        else
        {
            LOGW("[B] failed to request HID report descriptor itf=%u (will retry)", itf);
            // Спробуємо інший інтерфейс у цьому проході, але без паралельних запитів.
        }
    }

    // In case all reports are already satisfied (e.g., via stubs), check completion.
    descriptor_forward_try_complete();
}

void descriptor_logger_init(const descriptor_logger_ops_t* ops)
{
    if (ops)
    {
        s_ops = *ops;
    }
    descriptor_log_reset();
}

void descriptor_logger_reset(void)
{
    descriptor_log_reset();
}

void descriptor_logger_start(uint8_t dev_addr,
                             const uint8_t* report_desc,
                             uint16_t report_len)
{
    descriptor_log_start_internal(dev_addr, report_desc, report_len);
}

void descriptor_logger_mark_report_forwarded(uint8_t itf)
{
    if (itf < CFG_TUH_HID)
    {
        s_desc_log.hid_report_forwarded_mask |= TU_BIT(itf);
    }
    descriptor_forward_try_complete();
}

static void descriptor_log_reset(void)
{
    memset(&s_desc_log, 0, sizeof(s_desc_log));
    descriptor_forward_reset();
    s_desc_log.hid_report_expected_mask = 0;
    s_desc_log.hid_report_forwarded_mask = 0;
    s_desc_log.hid_fetch_pending = 0;
    s_desc_log.hid_config_seen     = false;
}

static void descriptor_log_dump_hex(const char* label,
                                    const uint8_t* data,
                                    uint16_t len)
{
    if (!label || !data || !len)
    {
        return;
    }

    for (uint16_t offset = 0; offset < len; offset += DESC_LOG_HEX_CHUNK)
    {
        uint16_t chunk = len - offset;
        if (chunk > DESC_LOG_HEX_CHUNK)
        {
            chunk = DESC_LOG_HEX_CHUNK;
        }

        char line[(DESC_LOG_HEX_CHUNK * 3) + 1];
        int  idx = 0;
        memset(line, 0, sizeof(line));
        for (uint16_t i = 0; i < chunk && idx < (int)sizeof(line); ++i)
        {
            idx += snprintf(line + idx,
                            sizeof(line) - (size_t)idx,
                            " %02X",
                            data[offset + i]);
        }

        LOGI("[B] %s +%03u:%s", label, offset, line);
    }
}

static void descriptor_log_start_internal(uint8_t dev_addr,
                                          const uint8_t* report_desc,
                                          uint16_t report_len)
{
    if (!(s_desc_log.active && s_desc_log.dev_addr == dev_addr))
    {
        descriptor_log_reset();

        s_desc_log.dev_addr = dev_addr;
        s_desc_log.active   = true;
        s_desc_log.forward_pending = DESC_FWD_DEVICE;
        descriptor_forward_set_pending(DESC_FWD_STRINGS);

        if (!tuh_descriptor_get_device(dev_addr,
                                       &s_desc_log.device,
                                       sizeof(s_desc_log.device),
                                       descriptor_log_device_cb,
                                       0))
        {
            LOGW("[B] failed to request device descriptor dev=%u", dev_addr);
            descriptor_forward_clear_pending(DESC_FWD_DEVICE);
            s_desc_log.active = false;
        }
    }

    if (report_desc && report_len)
    {
        LOGI("[B] HID report descriptor dump len=%u", report_len);
        descriptor_log_dump_hex("HID report", report_desc, report_len);
    }
}

static void descriptor_log_finish(void)
{
    descriptor_forward_try_complete();

    if (s_desc_log.done_sent)
    {
        if (s_desc_log.active)
        {
            LOGI("[B] descriptor logging complete for dev=%u", s_desc_log.dev_addr);
        }
        descriptor_log_reset();
    }
}

static void descriptor_log_print_interfaces(const uint8_t* desc,
                                            uint16_t len)
{
    if (!desc || len < 2)
    {
        return;
    }

    uint8_t const* p = desc;
    uint8_t const* end = desc + len;
    while (p < end)
    {
        uint8_t const bLength = tu_min16(p[0], (uint16_t)(end - p));
        uint8_t const bDescriptorType = p[1];
        if (!bLength)
        {
            break;
        }

        if (bDescriptorType == TUSB_DESC_INTERFACE)
        {
            tusb_desc_interface_t const* itf = (tusb_desc_interface_t const*)p;
            LOGI("[B] interface #%u class=0x%02X subclass=0x%02X proto=0x%02X eps=%u",
                 itf->bInterfaceNumber,
                 itf->bInterfaceClass,
                 itf->bInterfaceSubClass,
                 itf->bInterfaceProtocol,
                 itf->bNumEndpoints);
        }

        p += bLength;
    }
}

static const char* descriptor_log_stage_label(desc_str_stage_t stage)
{
    switch (stage)
    {
        case DESC_STR_STAGE_LANG: return "LangID";
        case DESC_STR_STAGE_MANUF: return "Manufacturer";
        case DESC_STR_STAGE_PRODUCT: return "Product";
        case DESC_STR_STAGE_SERIAL: return "Serial";
        default: return "String";
    }
}

static void descriptor_log_schedule_strings(desc_str_stage_t stage)
{
    if (!s_desc_log.active)
    {
        return;
    }

    if (stage >= DESC_STR_STAGE_DONE)
    {
        descriptor_forward_clear_pending(DESC_FWD_STRINGS);
        descriptor_log_finish();
        return;
    }

    if (stage == DESC_STR_STAGE_LANG)
    {
        if (!tuh_descriptor_get_string(s_desc_log.dev_addr,
                                       0,
                                       0,
                                       s_desc_log.string_buf,
                                       sizeof(s_desc_log.string_buf),
                                       descriptor_log_string_cb,
                                       (uintptr_t)stage))
        {
            LOGW("[B] failed to request LangID descriptor dev=%u", s_desc_log.dev_addr);
            descriptor_log_schedule_strings((desc_str_stage_t)(stage + 1));
        }
        return;
    }

    uint8_t index = s_desc_log.string_indices[stage - DESC_STR_STAGE_MANUF];
    if (index == 0)
    {
        LOGI("[B] %s string missing", descriptor_log_stage_label(stage));
        descriptor_log_schedule_strings((desc_str_stage_t)(stage + 1));
        return;
    }

    uint16_t lang = s_desc_log.langid ? s_desc_log.langid : 0x0409;
    LOGI("[B] requesting string idx=%u lang=0x%04X stage=%u",
         index, lang, stage);
    if (!tuh_descriptor_get_string(s_desc_log.dev_addr,
                                   index,
                                   lang,
                                   s_desc_log.string_buf,
                                   sizeof(s_desc_log.string_buf),
                                   descriptor_log_string_cb,
                                   (uintptr_t)stage))
    {
        LOGW("[B] failed to request %s string idx=%u",
             descriptor_log_stage_label(stage),
             index);
        descriptor_log_schedule_strings((desc_str_stage_t)(stage + 1));
    }
}

static void descriptor_log_device_cb(tuh_xfer_t* xfer)
{
    if (!s_desc_log.active || xfer->daddr != s_desc_log.dev_addr)
    {
        return;
    }

    if (xfer->result != XFER_RESULT_SUCCESS)
    {
        LOGW("[B] device descriptor transfer failed dev=%u result=%d",
             xfer->daddr,
             xfer->result);
        descriptor_forward_clear_pending(DESC_FWD_DEVICE);
        descriptor_log_finish();
        return;
    }

    tusb_desc_device_t const* desc = &s_desc_log.device;
    uint16_t vid = tu_le16toh(desc->idVendor);
    uint16_t pid = tu_le16toh(desc->idProduct);

    LOGI("[B] device descriptor: VID=0x%04X PID=0x%04X class=0x%02X subclass=0x%02X proto=0x%02X",
         vid,
         pid,
         desc->bDeviceClass,
         desc->bDeviceSubClass,
         desc->bDeviceProtocol);
    LOGI("[B] device descriptor: bcdUSB=0x%04X bMaxPacketSize0=%u iMan=%u iProd=%u iSer=%u",
         tu_le16toh(desc->bcdUSB),
         desc->bMaxPacketSize0,
         desc->iManufacturer,
         desc->iProduct,
         desc->iSerialNumber);

    uint16_t dump_len = (uint16_t)TU_MIN(sizeof(tusb_desc_device_t),
                                         (size_t)xfer->actual_len);
    descriptor_log_dump_hex("device desc",
                            (uint8_t const*)desc,
                            dump_len);

    if (dump_len && s_ops.send_descriptor_frames)
    {
        if (s_ops.send_descriptor_frames(PF_DESC_DEVICE,
                                         (uint8_t const*)desc,
                                         dump_len))
        {
            LOGI("[B] device descriptor forwarded len=%u", dump_len);
        }
        else
        {
            LOGW("[B] failed to forward device descriptor len=%u", dump_len);
        }
    }

    s_desc_log.string_indices[0] = desc->iManufacturer;
    s_desc_log.string_indices[1] = desc->iProduct;
    s_desc_log.string_indices[2] = desc->iSerialNumber;

    descriptor_log_request_config();
    descriptor_forward_clear_pending(DESC_FWD_DEVICE);
}

static void descriptor_log_request_config(void)
{
    if (!s_desc_log.active)
    {
        return;
    }

    descriptor_forward_set_pending(DESC_FWD_CONFIG);

    if (!tuh_descriptor_get_configuration(s_desc_log.dev_addr,
                                          0,
                                          s_desc_log.cfg_buf,
                                          sizeof(s_desc_log.cfg_buf),
                                          descriptor_log_config_cb,
                                          0))
    {
        LOGW("[B] failed to request config descriptor dev=%u",
             s_desc_log.dev_addr);
        descriptor_forward_clear_pending(DESC_FWD_CONFIG);
        descriptor_log_schedule_strings(DESC_STR_STAGE_LANG);
    }
}

static void descriptor_log_config_cb(tuh_xfer_t* xfer)
{
    if (!s_desc_log.active || xfer->daddr != s_desc_log.dev_addr)
    {
        return;
    }

    if (xfer->result != XFER_RESULT_SUCCESS)
    {
        LOGW("[B] config descriptor transfer failed dev=%u result=%d",
             xfer->daddr,
             xfer->result);
        descriptor_log_schedule_strings(DESC_STR_STAGE_LANG);
        descriptor_forward_clear_pending(DESC_FWD_CONFIG);
        return;
    }

    uint16_t len = (uint16_t)TU_MIN((size_t)xfer->actual_len,
                                    sizeof(s_desc_log.cfg_buf));
    s_desc_log.cfg_len = len;

    if (len < sizeof(tusb_desc_configuration_t))
    {
        LOGW("[B] config descriptor too short len=%u", len);
        descriptor_log_schedule_strings(DESC_STR_STAGE_LANG);
        descriptor_forward_clear_pending(DESC_FWD_CONFIG);
        return;
    }

    tusb_desc_configuration_t const* cfg =
        (tusb_desc_configuration_t const*)s_desc_log.cfg_buf;

    LOGI("[B] config descriptor: bNumInterfaces=%u wTotalLength=%u attr=0x%02X",
         cfg->bNumInterfaces,
         tu_le16toh(cfg->wTotalLength),
         cfg->bmAttributes);
    LOGI("[B] config descriptor: bConfigurationValue=%u maxPower=%umA",
         cfg->bConfigurationValue,
         cfg->bMaxPower * 2);

    descriptor_log_dump_hex("config desc", s_desc_log.cfg_buf, len);
    descriptor_log_print_interfaces(s_desc_log.cfg_buf, len);

    // Collect HID interfaces and expected report lengths.
    uint8_t hid_mask = 0;
    memset(s_desc_log.hid_report_len, 0, sizeof(s_desc_log.hid_report_len));
    uint8_t const* p = s_desc_log.cfg_buf;
    uint8_t const* end = p + len;
    uint8_t last_itf = 0xFF;
    while (p < end && (p + 1) < end)
    {
        uint8_t blen = tu_min16(p[0], (uint16_t)(end - p));
        uint8_t dtype = p[1];
        if (blen == 0) break;
        if (dtype == TUSB_DESC_INTERFACE && blen >= sizeof(tusb_desc_interface_t))
        {
            tusb_desc_interface_t const* itf = (tusb_desc_interface_t const*)p;
            last_itf = itf->bInterfaceNumber;
            if (itf->bInterfaceClass == TUSB_CLASS_HID && last_itf < CFG_TUH_HID)
            {
                hid_mask |= TU_BIT(last_itf);
            }
            else if (itf->bInterfaceClass == TUSB_CLASS_HID)
            {
                LOGW("[B] HID interface #%u exceeds CFG_TUH_HID=%u; skipping",
                     last_itf,
                     CFG_TUH_HID);
            }
        }
        else if (dtype == TUSB_DESC_HID && blen >= 9 && last_itf < CFG_TUH_HID)
        {
            uint16_t rep_len = (uint16_t)p[7] | ((uint16_t)p[8] << 8);
            s_desc_log.hid_report_len[last_itf] = rep_len;
        }
        p += blen;
    }
    if (hid_mask == 0) hid_mask = TU_BIT(0); // at least one
    s_desc_log.hid_report_expected_mask = hid_mask;
    s_desc_log.hid_config_seen     = true;
    for (uint8_t itf = 0; itf < CFG_TUH_HID; itf++)
    {
        if (hid_mask & TU_BIT(itf))
        {
            hid_proxy_host_ensure_slot(s_desc_log.dev_addr, itf);
        }
    }
    LOGI("[B] HID report descriptors expected mask=0x%02X", hid_mask);
    descriptor_log_fetch_missing_reports();

    if (s_ops.send_descriptor_frames)
    {
        if (s_ops.send_descriptor_frames(PF_DESC_CONFIG, s_desc_log.cfg_buf, len))
        {
            LOGI("[B] config descriptor forwarded len=%u", len);
        }
        else
        {
            LOGW("[B] failed to forward config descriptor len=%u", len);
        }
    }

    descriptor_log_schedule_strings(DESC_STR_STAGE_LANG);
    descriptor_forward_clear_pending(DESC_FWD_CONFIG);

    // Try to fetch missing HID reports that didn't arrive via mount.
    descriptor_log_fetch_missing_reports();
}

static void descriptor_log_string_cb(tuh_xfer_t* xfer)
{
    desc_str_stage_t stage = (desc_str_stage_t)(uintptr_t)xfer->user_data;
    if (!s_desc_log.active || xfer->daddr != s_desc_log.dev_addr)
    {
        return;
    }

    uint16_t len = (uint16_t)TU_MIN((size_t)xfer->actual_len,
                                    sizeof(s_desc_log.string_buf));
    memcpy(s_desc_log.string_buf, xfer->buffer, len);

    if (stage == DESC_STR_STAGE_LANG)
    {
        if (len < 4)
        {
            LOGW("[B] LangID descriptor too short");
        }
        else
        {
            uint8_t  count = (uint8_t)((len - 2) / 2);
            uint16_t lang  = (uint16_t)s_desc_log.string_buf[2]
                           | ((uint16_t)s_desc_log.string_buf[3] << 8);
            s_desc_log.langid = lang;
            string_manager_set_default_lang(lang);
            LOGI("[B] LangID descriptor: count=%u first=0x%04X",
                 count,
                 lang);
        }
    }
    else
    {
        char ascii[(PROXY_STRING_DESC_MAX / 2) + 1];
        memset(ascii, 0, sizeof(ascii));

        uint16_t char_count = (uint16_t)((len >= 2) ? ((len - 2) / 2) : 0);
        uint16_t max_chars  = (uint16_t)((sizeof(ascii) - 1));
        if (char_count > max_chars)
        {
            char_count = max_chars;
        }

        for (uint16_t i = 0; i < char_count; ++i)
        {
            ascii[i] = (char)s_desc_log.string_buf[2 + (i * 2)];
        }
        ascii[char_count] = '\0';

        uint16_t lang = s_desc_log.langid ? s_desc_log.langid : 0x0409;
        uint8_t  idx  = s_desc_log.string_indices[stage - DESC_STR_STAGE_MANUF];

        LOGI("[B] %s string idx=%u lang=0x%04X: %s",
             descriptor_log_stage_label(stage),
             idx,
             lang,
             ascii);
    }

    uint8_t payload_index = (stage == DESC_STR_STAGE_LANG) ? 0
                           : s_desc_log.string_indices[stage - DESC_STR_STAGE_MANUF];
    uint16_t cache_lang = (stage == DESC_STR_STAGE_LANG)
                          ? 0
                          : (s_desc_log.langid ? s_desc_log.langid : 0x0409);

    if (len)
    {
        string_manager_cache_store(payload_index, cache_lang, s_desc_log.string_buf, len);
    }

    descriptor_log_schedule_strings((desc_str_stage_t)(stage + 1));
}

static void descriptor_log_report_cb(tuh_xfer_t* xfer)
{
    if (!xfer || !s_desc_log.active || xfer->daddr != s_desc_log.dev_addr)
    {
        return;
    }

    uint8_t itf = (uint8_t)xfer->user_data;

    // Звільнити слоти очікування, аби можна було запросити наступний HID report.
    s_desc_log.hid_fetch_pending &= (uint8_t)~TU_BIT(itf);

    if (xfer->result != XFER_RESULT_SUCCESS)
    {
        LOGW("[B] HID report descriptor fetch failed itf=%u result=%d", itf, xfer->result);
        descriptor_log_fetch_missing_reports();
        return;
    }

    uint16_t len = (uint16_t)xfer->actual_len;
    if (len > PROTO_MAX_PAYLOAD_SIZE - 1)
    {
        len = PROTO_MAX_PAYLOAD_SIZE - 1;
    }

    // Update inferred type using the full report descriptor.
    hid_proxy_host_update_inferred_type(itf, xfer->buffer, (uint16_t)xfer->actual_len);
    hid_proxy_host_store_report_desc(itf, xfer->buffer, (uint16_t)xfer->actual_len);

    // If we already sent a stub for this interface, do not resend.
    if (s_desc_log.hid_report_forwarded_mask & TU_BIT(itf))
    {
        descriptor_forward_try_complete();
        return;
    }

    uint8_t tmp[PROTO_MAX_PAYLOAD_SIZE];
    tmp[0] = itf;
    memcpy(&tmp[1], xfer->buffer, len);
    if (s_ops.send_descriptor_frames &&
        s_ops.send_descriptor_frames(PF_DESC_REPORT, tmp, (uint16_t)(len + 1)))
    {
        LOGI("[B] HID report descriptor fetched itf=%u len=%u", itf, len);
        descriptor_logger_mark_report_forwarded(itf);
    }
    else
    {
        LOGW("[B] failed to forward fetched HID report descriptor itf=%u", itf);
    }

    descriptor_forward_try_complete();
    descriptor_log_fetch_missing_reports();
}

static void descriptor_forward_reset(void)
{
    s_desc_log.forward_pending = 0;
    s_desc_log.done_sent = false;
    s_desc_log.hid_report_forwarded_mask = 0;
    s_desc_log.hid_fetch_pending = 0;
}

static void descriptor_forward_set_pending(uint8_t mask)
{
    s_desc_log.forward_pending |= mask;
}

static void descriptor_forward_clear_pending(uint8_t mask)
{
    s_desc_log.forward_pending &= (uint8_t)~mask;
    descriptor_forward_try_complete();
}

static void descriptor_forward_try_complete(void)
{
    if (s_desc_log.done_sent) return;

    if (!s_desc_log.hid_config_seen || s_desc_log.forward_pending != 0)
    {
        return;
    }

    bool have_all = (s_desc_log.hid_report_expected_mask != 0) &&
                    ((s_desc_log.hid_report_forwarded_mask &
                      s_desc_log.hid_report_expected_mask) ==
                     s_desc_log.hid_report_expected_mask);

    if (!have_all)
    {
        LOGW("[B] HID reports incomplete, defer DONE (have=0x%02X expect=0x%02X)",
             s_desc_log.hid_report_forwarded_mask,
             s_desc_log.hid_report_expected_mask);
        return;
    }

    // Re-send critical descriptors right before DONE to tolerate UART loss.
    if (s_ops.send_descriptor_frames)
    {
        if (s_desc_log.device.bLength)
        {
            s_ops.send_descriptor_frames(PF_DESC_DEVICE,
                                         (uint8_t const*)&s_desc_log.device,
                                         sizeof(s_desc_log.device));
            sleep_ms(2);
        }
        if (s_desc_log.cfg_len)
        {
            s_ops.send_descriptor_frames(PF_DESC_CONFIG,
                                         s_desc_log.cfg_buf,
                                         s_desc_log.cfg_len);
            sleep_ms(2);
        }
    }

    LOGI("[B] descriptor completion check: expect=0x%02X got=0x%02X",
         s_desc_log.hid_report_expected_mask,
         s_desc_log.hid_report_forwarded_mask);
    if (s_ops.send_descriptor_done && s_ops.send_descriptor_done())
    {
        s_desc_log.done_sent = true;
        LOGI("[B] Descriptor transmission complete");
    }
    else
    {
        LOGW("[B] send_descriptor_done failed");
    }
}
