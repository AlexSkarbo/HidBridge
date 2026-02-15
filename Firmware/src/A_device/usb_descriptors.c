#include "tusb.h"
#include "hid_proxy_dev.h"
#include "logging.h"
#include "bsp/board.h"
#include "proxy_config.h"
#include "remote_storage.h"
// ---------------------------------------------------------
// Device identifiers
// ---------------------------------------------------------
#ifndef USB_VID
#define USB_VID   0xCafe
#endif

#ifndef USB_PID
#define USB_PID   0x4000
#endif

#define USB_BCD   0x0200

// ---------------------------------------------------------
// HID Report Descriptor (boot mouse)
// ---------------------------------------------------------

static uint8_t const desc_hid_report_boot_mouse[] =
{
    0x05, 0x01,       // Usage Page (Generic Desktop)
    0x09, 0x02,       // Usage (Mouse)
    0xA1, 0x01,       // Collection (Application)
    0x09, 0x01,       //   Usage (Pointer)
    0xA1, 0x00,       //   Collection (Physical)

    // --- Buttons (3 bits) ---
    0x05, 0x09,       //     Usage Page (Buttons)
    0x19, 0x01,       //     Usage Minimum (1)
    0x29, 0x03,       //     Usage Maximum (3)
    0x15, 0x00,       //     Logical Minimum (0)
    0x25, 0x01,       //     Logical Maximum (1)
    0x95, 0x03,       //     Report Count (3)
    0x75, 0x01,       //     Report Size (1)
    0x81, 0x02,       //     Input (Data,Var,Abs)

    0x95, 0x01,       //     Report Count (1)
    0x75, 0x05,       //     Report Size (5)
    0x81, 0x03,       //     Input (Const,Var,Abs) - padding

    // --- X, Y ---
    0x05, 0x01,       //     Usage Page (Generic Desktop)
    0x09, 0x30,       //     Usage (X)
    0x09, 0x31,       //     Usage (Y)
    0x15, 0x81,       //     Logical Minimum (-127)
    0x25, 0x7F,       //     Logical Maximum (127)
    0x75, 0x08,       //     Report Size (8)
    0x95, 0x02,       //     Report Count (2)
    0x81, 0x06,       //     Input (Data,Var,Rel)

    0xC0,             //   End Collection
    0xC0              // End Collection
};

uint8_t const *tud_hid_descriptor_report_cb(uint8_t instance)
{
    LOGI("[DEV] tud_hid_descriptor_report_cb itf=%u (time=%u ms)",
         instance, board_millis());
    uint8_t const* remote = NULL;
    uint16_t len = 0;

    if (hid_proxy_dev_get_report_descriptor(instance, &remote, &len))
    {
        LOGI("[DEV] tud_hid_descriptor_report_cb itf=%u remote len=%u", instance, len);
        return remote;
    }

    // Fallback: boot mouse for instance 0, stall others.
    if (instance == 0)
    {
        LOGW("[DEV] tud_hid_descriptor_report_cb itf=%u using fallback boot mouse", instance);
        return desc_hid_report_boot_mouse;
    }
    LOGW("[DEV] tud_hid_descriptor_report_cb itf=%u returning NULL (no descriptor)", instance);
    return NULL;
}

uint16_t tud_hid_descriptor_report_len_cb(uint8_t instance)
{
    LOGI("[DEV] tud_hid_descriptor_report_len_cb itf=%u (time=%u ms)",
         instance, board_millis());
    uint8_t const* remote = NULL;
    uint16_t len = 0;

    if (hid_proxy_dev_get_report_descriptor(instance, &remote, &len))
    {
        LOGI("[DEV] tud_hid_descriptor_report_len_cb itf=%u len=%u", instance, len);
        return len;
    }

    if (instance == 0)
    {
        return (uint16_t)sizeof(desc_hid_report_boot_mouse);
    }

    LOGW("[DEV] tud_hid_descriptor_report_len_cb itf=%u len=0 (no descriptor)", instance);
    return 0;
}

// ---------------------------------------------------------
// Device descriptor
// ---------------------------------------------------------

tusb_desc_device_t const desc_device =
{
    .bLength            = sizeof(tusb_desc_device_t),
    .bDescriptorType    = TUSB_DESC_DEVICE,

    .bcdUSB             = USB_BCD,
    .bDeviceClass       = TUSB_CLASS_HID,
    .bDeviceSubClass    = 0,
    .bDeviceProtocol    = 0,

    .bMaxPacketSize0    = CFG_TUD_ENDPOINT0_SIZE,

    .idVendor           = USB_VID,
    .idProduct          = USB_PID,
    .bcdDevice          = 0x0100,

    .iManufacturer      = 0x01,
    .iProduct           = 0x02,
    .iSerialNumber      = 0x03,

    .bNumConfigurations = 0x01
};

uint8_t const *tud_descriptor_device_cb(void)
{
    uint8_t const* remote = NULL;
    uint16_t len = 0;

    if (hid_proxy_dev_get_device_descriptor(&remote, &len) &&
        len >= sizeof(tusb_desc_device_t))
    {
        return remote;
    }

    return (uint8_t const *) &desc_device;
}

// ---------------------------------------------------------
// Configuration descriptor (single HID interface)
// ---------------------------------------------------------

enum
{
    ITF_NUM_HID,
    ITF_NUM_TOTAL
};

#define CONFIG_TOTAL_LEN    (TUD_CONFIG_DESC_LEN + TUD_HID_DESC_LEN)
#define EPNUM_HID           0x81

uint8_t const desc_configuration[] =
{
    TUD_CONFIG_DESCRIPTOR(
        1,                  // Configuration number
        ITF_NUM_TOTAL,      // Interfaces count
        0,                  // String index
        CONFIG_TOTAL_LEN,   // Total length
        TUSB_DESC_CONFIG_ATT_REMOTE_WAKEUP,
        100                 // 200 mA
    ),

    TUD_HID_DESCRIPTOR(
        ITF_NUM_HID,        // Interface number
        0,                  // String index
        HID_ITF_PROTOCOL_MOUSE,
        sizeof(desc_hid_report_boot_mouse),
        EPNUM_HID,
        CFG_TUD_HID_EP_BUFSIZE,
        10                  // polling interval (ms)
    )
};

uint8_t const *tud_descriptor_configuration_cb(uint8_t index)
{
    (void)index;
    uint8_t const* remote = NULL;
    uint16_t len = 0;

    if (hid_proxy_dev_get_config_descriptor(&remote, &len) &&
        len >= sizeof(tusb_desc_configuration_t))
    {
        return remote;
    }

    return desc_configuration;
}

// ---------------------------------------------------------
// String descriptors (minimal fallback: only LangID if remote not available)
// ---------------------------------------------------------

char const *string_desc_arr[] =
{
    (const char[]){ 0x09, 0x04 }, // 0: LangID = 0x0409 (English US)
};

static uint16_t _desc_str[32];
static uint16_t s_string_cb_count[256];

uint16_t const *tud_descriptor_string_cb(uint8_t index, uint16_t langid)
{
    (void) langid;

    uint16_t count = ++s_string_cb_count[index];
    if (count <= 3 || (count % 10) == 0)
    {
        LOGI("[DEV] tud_descriptor_string_cb index=%u lang=0x%04X count=%u",
             index,
             langid,
             count);
    }
    hid_proxy_dev_service();

    uint8_t const* remote = NULL;
    uint16_t remote_len = 0;
    if (hid_proxy_dev_get_string_descriptor(index, langid, &remote, &remote_len) &&
        remote && remote_len)
    {
        if (remote_len > sizeof(_desc_str))
        {
            remote_len = sizeof(_desc_str);
        }
        memcpy(_desc_str, remote, remote_len);
        return _desc_str;
    }
    // else if (index > 2)
    // {
    //     if (count <= 3 || (count % 10) == 0)
    //     {
    //         LOGI("[DEV] string idx=%u unsupported -> STALL", index);
    //     }
    //     return NULL;
    // }
    // Fallback: only provide LangID (index 0). For others without remote data, return minimal empty descriptor.
    if (index == 0)
    {
        uint8_t chr_count = 1;
        _desc_str[1] = (uint16_t)string_desc_arr[0][1] << 8 | string_desc_arr[0][0];
        _desc_str[0] = (uint16_t)((TUSB_DESC_STRING << 8) | (2 * chr_count + 2));
        return _desc_str;
    }

    if (count <= 3 || (count % 10) == 0)
    {
        LOGI("[DEV] string idx=%u unsupported -> EMPTY", index);
    }

    // Minimal empty descriptor: bLength=2, bDescriptorType=STRING, no UTF-16 chars.
    _desc_str[0] = (uint16_t)((TUSB_DESC_STRING << 8) | 2);
    return _desc_str;
}

// ---------------------------------------------------------
// Контрольні запити: перехоплюємо GET_DESCRIPTOR (REPORT) вручну,
// щоб віддати кешований дескриптор з правильною довжиною.
// ---------------------------------------------------------
bool tud_control_request_cb(uint8_t rhport, tusb_control_request_t const* request)
{
    // IN, будь-який тип/отримувач, bRequest=GET_DESCRIPTOR, HID (0x21) або Report (0x22)
    bool is_hid_desc    = (request->bmRequestType_bit.direction == TUSB_DIR_IN) &&
                          (request->bRequest == TUSB_REQ_GET_DESCRIPTOR) &&
                          ((request->wValue >> 8) == HID_DESC_TYPE_HID);
    bool is_report_desc = (request->bmRequestType_bit.direction == TUSB_DIR_IN) &&
                          (request->bRequest == TUSB_REQ_GET_DESCRIPTOR) &&
                          ((request->wValue >> 8) == HID_DESC_TYPE_REPORT);

    if (is_hid_desc || is_report_desc)
    {
        uint8_t itf = (uint8_t)request->wIndex;
        uint8_t const* rep = NULL;
        uint16_t rep_len = 0;
        bool have_rep = hid_proxy_dev_get_report_descriptor(itf, &rep, &rep_len);

        if (is_hid_desc)
        {
            uint16_t rlen = have_rep ? rep_len : s_remote_desc.hid_report_expected_len[itf];
            uint8_t hid_desc[9] = {
                9,
                HID_DESC_TYPE_HID,
                0x10, 0x01,   // bcdHID 1.10
                0x00,         // country
                0x01,         // bNumDescriptors
                HID_DESC_TYPE_REPORT,
                (uint8_t)(rlen & 0xFF),
                (uint8_t)(rlen >> 8)
            };
            uint16_t send_len = sizeof(hid_desc);
            if (send_len > request->wLength) send_len = request->wLength;
            LOGI("[DEV] ctrl GET_DESCRIPTOR(HID) type=%u rcpt=%u itf=%u req_len=%u send=%u rlen=%u",
                 request->bmRequestType_bit.type,
                 request->bmRequestType_bit.recipient,
                 itf, request->wLength, send_len, rlen);
            tud_control_xfer(rhport, request, hid_desc, send_len);
            return true;
        }

        LOGI("[DEV] ctrl GET_DESCRIPTOR(report) type=%u rcpt=%u itf=%u req_len=%u have=%u ok=%u",
             request->bmRequestType_bit.type,
             request->bmRequestType_bit.recipient,
             itf, request->wLength, rep_len, have_rep ? 1 : 0);
        if (have_rep && rep && rep_len)
        {
            static uint8_t padded[PROXY_MAX_DESC_SIZE];
            uint16_t send_len = rep_len;
            if (send_len > sizeof(padded)) send_len = sizeof(padded);
            memcpy(padded, rep, send_len);
            // Допадимо нулями, якщо хост попросив більше.
            uint16_t want = request->wLength;
            if (want > sizeof(padded)) want = sizeof(padded);
            if (want > send_len)
            {
                memset(padded + send_len, 0, want - send_len);
                send_len = want;
            }
            tud_control_xfer(rhport, request, (void*)padded, send_len);
            return true;
        }
        // Якщо немає даних — нехай TinyUSB обробляє далі (може згенерувати STALL)
    }

    // Логуємо інші класові HID-запити для діагностики.
    if (request->bmRequestType_bit.recipient == TUSB_REQ_RCPT_INTERFACE &&
        request->bmRequestType_bit.type == TUSB_REQ_TYPE_CLASS)
    {
        LOGI("[DEV] ctrl HID class req=0x%02X itf=%u dir=%u wValue=0x%04X wIndex=0x%04X wLength=%u",
             request->bRequest,
             (uint8_t)request->wIndex,
             request->bmRequestType_bit.direction,
             request->wValue,
             request->wIndex,
             request->wLength);
    }

    return false; // пропускаємо обробку за замовчуванням
}
