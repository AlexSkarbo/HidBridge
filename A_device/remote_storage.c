#include "remote_storage.h"

#include <string.h>

#include "hid_proxy_dev.h"
#include "tusb.h"
#include "logging.h"

#ifndef TUSB_DESC_HID
#define TUSB_DESC_HID 0x21
#endif

remote_desc_state_t s_remote_desc;

void remote_storage_init_defaults(void)
{
    memset(&s_remote_desc, 0, sizeof(s_remote_desc));
    s_remote_desc.usb_speed = TUSB_SPEED_FULL;
    for (uint8_t i = 0; i < CFG_TUD_HID; i++)
    {
        s_remote_desc.report_has_id[i] = false;
        s_remote_desc.hid_report_expected_len[i] = 0;
        s_remote_desc.hid_itf_present[i] = false;
    }
    s_remote_desc.lang.allow_fetch = true;
}

void remote_desc_append(remote_desc_buffer_t* buf,
                        uint8_t const* data,
                        uint16_t len)
{
    if (!buf || !data || !len)
    {
        return;
    }

    if (buf->len + len > PROXY_MAX_DESC_SIZE)
    {
        LOGW("[DEV] descriptor buffer overflow (len=%u)", buf->len + len);
        len = PROXY_MAX_DESC_SIZE - buf->len;
        if (len == 0)
        {
            return;
        }
    }

    memcpy(&buf->data[buf->len], data, len);
    buf->len   += len;
    buf->valid = true;
}

remote_string_desc_t* remote_desc_get_string_entry(uint8_t index)
{
    if (index == 0)
    {
        return &s_remote_desc.lang;
    }
    return &s_remote_desc.strings[index];
}

void remote_desc_store_string(uint8_t index,
                              uint16_t langid,
                              uint8_t const* data,
                              uint16_t len)
{
    remote_string_desc_t* entry = remote_desc_get_string_entry(index);
    if (!entry || !data)
    {
        return;
    }

    if (len > sizeof(entry->data))
    {
        len = sizeof(entry->data);
    }

    memcpy(entry->data, data, len);
    entry->len        = len;
    entry->valid      = true;
    entry->pending    = false;
    entry->allow_fetch = false;

    uint16_t resolved_lang = langid;
    if (index == 0)
    {
        resolved_lang = 0;
        if (len >= 4)
        {
            uint16_t first_lang = (uint16_t)data[2] | ((uint16_t)data[3] << 8);
            s_remote_desc.lang.langid = first_lang;
        }
    }
    else
    {
        if (!resolved_lang)
        {
            if (s_remote_desc.lang.langid)
            {
                resolved_lang = s_remote_desc.lang.langid;
            }
            else
            {
                resolved_lang = 0x0409;
            }
        }
    }
    entry->langid = resolved_lang;
}

static void mark_string_index(uint8_t idx)
{
    if (idx == 0)
    {
        if (!s_remote_desc.lang.allow_fetch)
        {
            s_remote_desc.lang.allow_fetch = true;
            LOGI("[DEV] string allow idx=0");
        }
    }
    else if (idx < TU_ARRAY_SIZE(s_remote_desc.strings))
    {
        if (!s_remote_desc.strings[idx].allow_fetch)
        {
            s_remote_desc.strings[idx].allow_fetch = true;
            LOGI("[DEV] string allow idx=%u", idx);
        }
    }
}

static void parse_config_for_strings(void)
{
    for (uint8_t i = 0; i < CFG_TUD_HID; i++)
    {
        s_remote_desc.hid_itf_present[i] = false;
        s_remote_desc.hid_report_expected_len[i] = 0;
    }

    if (!s_remote_desc.config.valid || s_remote_desc.config.len < 2)
    {
        return;
    }

    uint16_t offset = 0;
    uint8_t const* desc = s_remote_desc.config.data;
    uint16_t len = s_remote_desc.config.len;
    uint8_t current_itf = 0xFF;

    while (offset + 1 < len)
    {
        uint8_t const blen = desc[offset];
        uint8_t const dtype = desc[offset + 1];
        if (blen < 2) break;

        switch (dtype)
        {
            case TUSB_DESC_CONFIGURATION:
                if (offset + 6 < len)
                {
                    mark_string_index(desc[offset + 6]);
                }
                break;
            case TUSB_DESC_INTERFACE:
                if (offset + 8 < len)
                {
                    mark_string_index(desc[offset + 8]);
                    current_itf = desc[offset + 2];
                    if (current_itf < CFG_TUD_HID)
                    {
                        s_remote_desc.hid_itf_present[current_itf] = true;
                    }
                }
                break;
            case TUSB_DESC_HID:
                if (current_itf < CFG_TUD_HID && offset + 8 < len)
                {
                    uint16_t rep_len = (uint16_t)desc[offset + 7] |
                                       ((uint16_t)desc[offset + 8] << 8);
                    s_remote_desc.hid_report_expected_len[current_itf] = rep_len;
                    LOGI("[DEV] HID itf=%u report_len=%u", current_itf, rep_len);
                }
                break;
            default:
                break;
        }

        offset = (uint16_t)(offset + blen);
    }
}

void remote_storage_update_string_allowlist(void)
{
    if (s_remote_desc.device.valid &&
        s_remote_desc.device.len >= sizeof(tusb_desc_device_t))
    {
        tusb_desc_device_t const* dev =
            (tusb_desc_device_t const*)s_remote_desc.device.data;
        mark_string_index(dev->iManufacturer);
        mark_string_index(dev->iProduct);
        mark_string_index(dev->iSerialNumber);
    }

    parse_config_for_strings();
}

static void analyze_single_report(uint8_t itf, remote_desc_buffer_t const* rep)
{
    if (!rep || !rep->valid || rep->len == 0 || itf >= CFG_TUD_HID)
    {
        return;
    }

    s_remote_desc.report_has_id[itf] = false;

    const uint8_t* data = rep->data;
    uint16_t len = rep->len;

    for (uint16_t i = 0; i < len; )
    {
        uint8_t byte = data[i];

        if (byte == 0x85 && (i + 1) < len)
        {
            s_remote_desc.report_has_id[itf] = true;
            LOGI("[DEV] itf=%u report descriptor includes Report ID items", itf);
            return;
        }

        if (byte == 0xFE)
        {
            if ((i + 1) >= len) break;
            uint8_t long_len = data[i + 1];
            uint8_t skip = (uint8_t)(3 + long_len); // prefix + size + payload
            if ((uint32_t)i + skip > len) break;
            i = (uint16_t)(i + skip);
            continue;
        }

        uint8_t size_code = byte & 0x03;
        uint8_t data_len = (size_code == 3) ? 4 : size_code;
        i = (uint16_t)(i + 1 + data_len);
    }
}

void remote_storage_analyze_report_descriptors(void)
{
    // Analyze present report buffers
    for (uint8_t i = 0; i < CFG_TUD_HID; i++)
    {
        analyze_single_report(i, &s_remote_desc.reports[i]);
    }
}

bool hid_proxy_dev_get_device_descriptor(uint8_t const** out_data,
                                         uint16_t *out_len)
{
    static tusb_desc_device_t patched_desc;

    if (!s_remote_desc.device.valid ||
        s_remote_desc.device.len < sizeof(tusb_desc_device_t))
    {
        return false;
    }

    memcpy(&patched_desc,
           s_remote_desc.device.data,
           sizeof(tusb_desc_device_t));

    patched_desc.bLength         = sizeof(tusb_desc_device_t);
    patched_desc.bDescriptorType = TUSB_DESC_DEVICE;
    uint8_t max0 = patched_desc.bMaxPacketSize0;
    tusb_speed_t speed = s_remote_desc.usb_speed ? s_remote_desc.usb_speed
                                                 : TUSB_SPEED_FULL;
    if (speed == TUSB_SPEED_FULL)
    {
        // Force 64-byte control EP for FS enumeration to keep host happy.
        max0 = CFG_TUD_ENDPOINT0_SIZE;
    }
    else
    {
        if (!max0) max0 = 8;
        if (max0 < 8) max0 = 8;
        if (max0 > CFG_TUD_ENDPOINT0_SIZE) max0 = CFG_TUD_ENDPOINT0_SIZE;
    }
    patched_desc.bMaxPacketSize0 = max0;

    if (out_data) *out_data = (uint8_t const*)&patched_desc;
    if (out_len)  *out_len  = sizeof(tusb_desc_device_t);
    return true;
}

bool hid_proxy_dev_get_config_descriptor(uint8_t const** out_data,
                                         uint16_t *out_len)
{
    static uint8_t patched_cfg[PROXY_MAX_DESC_SIZE];

    if (!s_remote_desc.config.valid ||
        s_remote_desc.config.len < sizeof(tusb_desc_configuration_t))
    {
        return false;
    }

    uint16_t len = s_remote_desc.config.len;
    if (len > sizeof(patched_cfg))
    {
        len = sizeof(patched_cfg);
    }

    memcpy(patched_cfg, s_remote_desc.config.data, len);
    tusb_desc_configuration_t* cfg = (tusb_desc_configuration_t*)patched_cfg;
    cfg->wTotalLength = tu_htole16(len);

    if (out_data) *out_data = patched_cfg;
    if (out_len)  *out_len  = len;
    return true;
}

// bool hid_proxy_dev_get_config_descriptor(uint8_t const** out_data,
//                                          uint16_t *out_len)
// {
//     static uint8_t patched_cfg[PROXY_MAX_DESC_SIZE];

//     if (!s_remote_desc.config.valid ||
//         s_remote_desc.config.len < sizeof(tusb_desc_configuration_t))
//     {
//         return false;
//     }

//     uint8_t const *src     = s_remote_desc.config.data;
//     uint16_t       src_len = s_remote_desc.config.len;

//     // 1) Копіюємо заголовок конфіг-дескриптора (перші 9 байт)
//     if (src_len < sizeof(tusb_desc_configuration_t))
//     {
//         return false;
//     }

//     memcpy(patched_cfg, src, sizeof(tusb_desc_configuration_t));
//     tusb_desc_configuration_t *cfg = (tusb_desc_configuration_t *)patched_cfg;

//     uint16_t out_len_local = sizeof(tusb_desc_configuration_t);

//     // 2) Проходимо по решті дескрипторів і залишаємо тільки ті,
//     //    що належать інтерфейсам з номером < CFG_TUD_HID (тобто itf=0).
//     uint16_t offset      = sizeof(tusb_desc_configuration_t);
//     uint8_t  current_itf = 0xFF;

//     while (offset + 2u <= src_len)
//     {
//         uint8_t blen  = src[offset];
//         uint8_t dtype = src[offset + 1];

//         if (blen < 2u)
//         {
//             break;
//         }
//         if (offset + blen > src_len)
//         {
//             break;
//         }

//         if (dtype == TUSB_DESC_INTERFACE)
//         {
//             // bInterfaceNumber знаходиться в третьому байті
//             current_itf = src[offset + 2];
//         }

//         bool keep = false;

//         // Конфіг-дескриптор ми вже скопіювали вище, тут його пропускаємо
//         if (dtype == TUSB_DESC_INTERFACE)
//         {
//             // Залишаємо тільки інтерфейси з номером < CFG_TUD_HID (тобто #0)
//             if (current_itf < CFG_TUD_HID)
//             {
//                 keep = true;
//             }
//         }
//         else
//         {
//             // HID / endpoint та інші класові дескриптори відносимо до останнього
//             // зустрічного інтерфейсу
//             if (current_itf < CFG_TUD_HID)
//             {
//                 keep = true;
//             }
//         }

//         if (keep)
//         {
//             if (out_len_local + blen > sizeof(patched_cfg))
//             {
//                 // Не влазить — обриваємо, щоб не поламати буфер
//                 break;
//             }

//             memcpy(patched_cfg + out_len_local, src + offset, blen);
//             out_len_local += blen;
//         }

//         offset += blen;
//     }

//     // 3) Оновлюємо заголовок конфіг-дескриптора під нову довжину і кількість інтерфейсів
//     cfg->bNumInterfaces = CFG_TUD_HID;             // зараз 1
//     cfg->wTotalLength   = tu_htole16(out_len_local);

//     if (out_data) *out_data = patched_cfg;
//     if (out_len)  *out_len  = out_len_local;

//     return true;
// }


bool hid_proxy_dev_get_report_descriptor(uint8_t itf,
                                         uint8_t const** out_data,
                                         uint16_t *out_len)
{
    return remote_storage_get_report_descriptor(itf, out_data, out_len);
}

bool remote_storage_get_report_descriptor(uint8_t itf,
                                          uint8_t const** out_data,
                                          uint16_t *out_len)
{
    static uint8_t dummy[PROXY_MAX_DESC_SIZE];

    if (itf >= CFG_TUD_HID)
    {
        return false;
    }

    remote_desc_buffer_t const* rep = &s_remote_desc.reports[itf];
    if (rep->valid && rep->len > 0)
    {
        LOGI("[DEV] get_report_descriptor itf=%u len=%u (cached)", itf, rep->len);
        if (out_data) *out_data = rep->data;
        if (out_len)  *out_len  = rep->len;
        return true;
    }

    // Synthesise a dummy descriptor if we know expected length to keep host enumeration alive.
    uint16_t expect = s_remote_desc.hid_report_expected_len[itf];
    if (expect == 0 || expect > sizeof(dummy))
    {
        LOGW("[DEV] get_report_descriptor itf=%u missing (len=0, expect=%u)", itf, expect);
        return false;
    }

    LOGW("[DEV] get_report_descriptor itf=%u missing, synthesizing stub len=%u", itf, expect);
    // Minimal vendor-defined input, padded with 0xC0 if needed.
    static const uint8_t stub[] = {
        0x06, 0x00, 0xFF,       // Usage Page (Vendor Defined)
        0x09, 0x01,             // Usage (Vendor Usage 1)
        0xA1, 0x01,             // Collection (Application)
        0x15, 0x00,             //   Logical Minimum (0)
        0x26, 0xFF, 0x00,       //   Logical Maximum (255)
        0x75, 0x08,             //   Report Size (8)
        0x95, 0x01,             //   Report Count (1)
        0x81, 0x02,             //   Input (Data,Var,Abs)
        0xC0                    // End Collection
    };
    uint16_t stub_len = (uint16_t)sizeof(stub);
    if (stub_len > expect) stub_len = expect;
    memcpy(dummy, stub, stub_len);
    if (stub_len < expect)
    {
        memset(dummy + stub_len, 0xC0, expect - stub_len);
    }
    if (out_data) *out_data = dummy;
    if (out_len)  *out_len  = expect;
    return true;
}

bool remote_storage_report_has_id(uint8_t itf)
{
    if (itf >= CFG_TUD_HID)
    {
        return false;
    }
    return s_remote_desc.report_has_id[itf];
}

bool remote_storage_reports_ready(void)
{
    bool ready = true;
    bool logged = false;
    bool any_hid = false;
    for (uint8_t i = 0; i < CFG_TUD_HID; i++)
    {
        if (s_remote_desc.hid_itf_present[i])
        {
            any_hid = true;
            uint16_t expect = s_remote_desc.hid_report_expected_len[i];
            uint16_t have   = s_remote_desc.reports[i].len;
            bool     valid  = s_remote_desc.reports[i].valid;

            if (!valid || (expect != 0 && have < expect))
            {
                if (!logged)
                {
                    LOGT("[DEV] HID reports not ready yet:");
                    logged = true;
                }
                LOGT("      itf=%u present=1 valid=%u len=%u expect=%u",
                     i, valid ? 1 : 0, have, expect);
                ready = false;
            }
        }
    }

    // If config didn't declare HID interfaces, fall back to legacy single report.
    if (!any_hid)
    {
        ready = s_remote_desc.reports[0].valid;
    }
    // Не спамується зайвий лог при кожному виклику.
    return ready;
}
