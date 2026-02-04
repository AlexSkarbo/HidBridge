#include "control_uart.h"

#include <stdint.h>
#include <stdbool.h>
#include <string.h>

#include "pico/stdlib.h"
#include "pico/unique_id.h"
#include "pico/unique_id.h"
#include "hardware/uart.h"
#include "hardware/gpio.h"

#include "crc16.h"
#include "logging.h"
#include "proxy_config.h"
#include "hid_proxy_host.h"
#include "sha256.h"

#define SLIP_END     0xC0
#define SLIP_ESC     0xDB
#define SLIP_ESC_END 0xDC
#define SLIP_ESC_ESC 0xDD

#define CTRL_RX_BUF_MAX 512
#define CTRL_TX_BUF_MAX 512

#define CTRL_V2_MAGIC 0xF1
#define CTRL_V2_VERSION 0x01
#define CTRL_V2_HDR_LEN 6
#define CTRL_V2_CRC_LEN 2
#define CTRL_V2_HMAC_LEN 16
#define CTRL_V2_MIN_LEN (CTRL_V2_HDR_LEN + CTRL_V2_CRC_LEN + CTRL_V2_HMAC_LEN)

#define CTRL_FLAG_RESPONSE 0x01
#define CTRL_FLAG_ERROR    0x02

#define CTRL_ERR_BAD_LEN         1
#define CTRL_ERR_INJECT_FAILED   2
#define CTRL_ERR_DESC_MISSING    3
#define CTRL_ERR_LAYOUT_MISSING  4

static uint8_t  s_ctrl_rx_buf[CTRL_RX_BUF_MAX];
static uint16_t s_ctrl_rx_len = 0;
static bool     s_ctrl_rx_esc = false;
static uint8_t  s_ctrl_hmac_derived[32];
static bool     s_ctrl_hmac_ready = false;

static void ctrl_init_hmac_key(void)
{
    const uint8_t* key = (const uint8_t*)PROXY_CTRL_HMAC_KEY;
    size_t key_len = strlen(PROXY_CTRL_HMAC_KEY);
    pico_unique_board_id_t id;
    pico_get_unique_board_id(&id);
    hmac_sha256(key, key_len, id.id, PICO_UNIQUE_BOARD_ID_SIZE_BYTES, s_ctrl_hmac_derived);
    s_ctrl_hmac_ready = true;
}

static void ctrl_get_hmac_key(uint8_t cmd, const uint8_t** key, size_t* key_len)
{
    if (!key || !key_len) return;
    if (cmd == 0x06 || !s_ctrl_hmac_ready)
    {
        *key = (const uint8_t*)PROXY_CTRL_HMAC_KEY;
        *key_len = strlen(PROXY_CTRL_HMAC_KEY);
        return;
    }
    *key = s_ctrl_hmac_derived;
    *key_len = sizeof(s_ctrl_hmac_derived);
}

static int slip_encode(const uint8_t* data, uint16_t len, uint8_t* out, uint16_t out_max)
{
    if (!data || !out) return -1;
    if (out_max < 2) return -1;

    uint16_t pos = 0;
    out[pos++] = SLIP_END;
    for (uint16_t i = 0; i < len; i++)
    {
        uint8_t b = data[i];
        if (b == SLIP_END)
        {
            if (pos + 2 > out_max) return -1;
            out[pos++] = SLIP_ESC;
            out[pos++] = SLIP_ESC_END;
        }
        else if (b == SLIP_ESC)
        {
            if (pos + 2 > out_max) return -1;
            out[pos++] = SLIP_ESC;
            out[pos++] = SLIP_ESC_ESC;
        }
        else
        {
            if (pos + 1 > out_max) return -1;
            out[pos++] = b;
        }
    }
    if (pos + 1 > out_max) return -1;
    out[pos++] = SLIP_END;
    return (int)pos;
}

static int build_v2_frame(uint8_t seq, uint8_t cmd, uint8_t flags,
                          const uint8_t* payload, uint8_t payload_len,
                          uint8_t* out, uint16_t out_max,
                          bool use_bootstrap)
{
#if !PROXY_CTRL_UART_ENABLED
    (void)seq;
    (void)cmd;
    (void)flags;
    (void)payload;
    (void)payload_len;
    (void)out;
    (void)out_max;
    return 0;
#else
    if (PROXY_CTRL_UART_ID == PROXY_UART_ID) return 0;

    if (payload_len > 240) return 0;
    uint16_t total_len = (uint16_t)(CTRL_V2_HDR_LEN + payload_len + CTRL_V2_CRC_LEN + CTRL_V2_HMAC_LEN);
    if (total_len > out_max) return 0;

    out[0] = CTRL_V2_MAGIC;
    out[1] = CTRL_V2_VERSION;
    out[2] = flags;
    out[3] = seq;
    out[4] = cmd;
    out[5] = payload_len;
    if (payload_len && payload)
    {
        memcpy(&out[6], payload, payload_len);
    }

    uint16_t crc = crc16_ccitt(out, (uint32_t)(CTRL_V2_HDR_LEN + payload_len), 0xFFFF);
    out[6 + payload_len] = (uint8_t)(crc & 0xFF);
    out[7 + payload_len] = (uint8_t)(crc >> 8);

    const uint8_t* key = NULL;
    size_t key_len = 0;
    if (use_bootstrap)
    {
        key = (const uint8_t*)PROXY_CTRL_HMAC_KEY;
        key_len = strlen(PROXY_CTRL_HMAC_KEY);
    }
    else
    {
        ctrl_get_hmac_key(cmd, &key, &key_len);
    }
    uint8_t mac[32];
    hmac_sha256(key, key_len, out, (size_t)(CTRL_V2_HDR_LEN + payload_len + CTRL_V2_CRC_LEN), mac);
    memcpy(&out[8 + payload_len], mac, CTRL_V2_HMAC_LEN);

    return (int)total_len;
#endif
}

static void ctrl_send_response(uint8_t seq, uint8_t cmd, uint8_t flags,
                               const uint8_t* payload, uint8_t payload_len,
                               bool use_bootstrap)
{
#if !PROXY_CTRL_UART_ENABLED
    (void)seq;
    (void)cmd;
    (void)flags;
    (void)payload;
    (void)payload_len;
    return;
#else
    if (PROXY_CTRL_UART_ID == PROXY_UART_ID) return;

    uint8_t frame[CTRL_TX_BUF_MAX];
    int frame_len = build_v2_frame(seq, cmd, flags, payload, payload_len, frame, sizeof(frame), use_bootstrap);
    if (frame_len <= 0) return;

    uint8_t encoded[CTRL_TX_BUF_MAX];
    int enc_len = slip_encode(frame, (uint16_t)frame_len, encoded, sizeof(encoded));
    if (enc_len <= 0) return;
    uart_write_blocking(PROXY_CTRL_UART_ID, encoded, enc_len);
#endif
}

static void send_interface_list(uint8_t seq, bool use_bootstrap)
{
    hid_proxy_itf_info_t list[CFG_TUH_HID];
    size_t count = hid_proxy_host_list_interfaces(list, CFG_TUH_HID);

    uint8_t payload[240];
    uint16_t pos = 0;
    payload[pos++] = (uint8_t)count;

    for (size_t i = 0; i < count; i++)
    {
        if (pos + 7 > sizeof(payload)) break;
        payload[pos++] = list[i].dev_addr;
        payload[pos++] = list[i].itf;
        payload[pos++] = list[i].itf_protocol;
        payload[pos++] = list[i].protocol;
        payload[pos++] = list[i].inferred_type;
        payload[pos++] = list[i].active;
        payload[pos++] = list[i].mounted;
    }

    ctrl_send_response(seq, 0x02, CTRL_FLAG_RESPONSE, payload, (uint8_t)pos, use_bootstrap);
}

static void send_report_descriptor(uint8_t seq, uint8_t itf, bool use_bootstrap)
{
    uint8_t payload[240];
    bool truncated = false;
    uint16_t max_data = (uint16_t)(sizeof(payload) - 4);

    uint16_t total_len = hid_proxy_host_get_report_desc(itf, &payload[4], max_data, &truncated);
    if (!total_len)
    {
        uint8_t err = CTRL_ERR_DESC_MISSING;
        ctrl_send_response(seq, 0x04, CTRL_FLAG_RESPONSE | CTRL_FLAG_ERROR, &err, 1, use_bootstrap);
        return;
    }

    payload[0] = itf;
    payload[1] = (uint8_t)(total_len & 0xFF);
    payload[2] = (uint8_t)(total_len >> 8);
    payload[3] = truncated ? 1 : 0;

    uint16_t send_len = (uint16_t)(4 + (total_len > max_data ? max_data : total_len));
    ctrl_send_response(seq, 0x04, CTRL_FLAG_RESPONSE, payload, (uint8_t)send_len, use_bootstrap);
}

static void send_report_layout(uint8_t seq, uint8_t itf, uint8_t report_id, bool use_bootstrap)
{
    hid_report_layout_t layout;
    if (!hid_proxy_host_get_report_layout(itf, report_id, &layout))
    {
        uint8_t err = CTRL_ERR_LAYOUT_MISSING;
        ctrl_send_response(seq, 0x05, CTRL_FLAG_RESPONSE | CTRL_FLAG_ERROR, &err, 1, use_bootstrap);
        return;
    }

    uint8_t payload[18];
    payload[0] = layout.itf;
    payload[1] = layout.report_id;
    payload[2] = layout.layout_kind;
    payload[3] = layout.flags;
    payload[4] = layout.buttons_offset_bits;
    payload[5] = layout.buttons_count;
    payload[6] = layout.buttons_size_bits;
    payload[7] = layout.x_offset_bits;
    payload[8] = layout.x_size_bits;
    payload[9] = layout.x_signed;
    payload[10] = layout.y_offset_bits;
    payload[11] = layout.y_size_bits;
    payload[12] = layout.y_signed;
    payload[13] = layout.wheel_offset_bits;
    payload[14] = layout.wheel_size_bits;
    payload[15] = layout.wheel_signed;
    payload[16] = layout.kb_report_len;
    payload[17] = layout.kb_has_report_id;
    ctrl_send_response(seq, 0x05, CTRL_FLAG_RESPONSE, payload, sizeof(payload), use_bootstrap);
}

static void send_device_id(uint8_t seq)
{
    pico_unique_board_id_t id;
    pico_get_unique_board_id(&id);

    uint8_t payload[1 + PICO_UNIQUE_BOARD_ID_SIZE_BYTES];
    payload[0] = PICO_UNIQUE_BOARD_ID_SIZE_BYTES;
    memcpy(&payload[1], id.id, PICO_UNIQUE_BOARD_ID_SIZE_BYTES);
    ctrl_send_response(seq, 0x06, CTRL_FLAG_RESPONSE, payload, sizeof(payload), true);
}

static void ctrl_rx_reset(void)
{
    s_ctrl_rx_len = 0;
    s_ctrl_rx_esc = false;
}

static bool ctrl_hmac_equal(const uint8_t* a, const uint8_t* b, uint16_t len)
{
    uint8_t diff = 0;
    for (uint16_t i = 0; i < len; i++)
    {
        diff |= (uint8_t)(a[i] ^ b[i]);
    }
    return diff == 0;
}

typedef enum
{
    HMAC_KEY_NONE = 0,
    HMAC_KEY_DERIVED = 1,
    HMAC_KEY_BOOTSTRAP = 2
} hmac_key_kind_t;

static hmac_key_kind_t ctrl_verify_hmac(uint8_t cmd, const uint8_t* data, uint16_t payload_len)
{
    const uint8_t* key = NULL;
    size_t key_len = 0;
    ctrl_get_hmac_key(cmd, &key, &key_len);
    uint8_t mac[32];
    hmac_sha256(key, key_len, data, (size_t)(CTRL_V2_HDR_LEN + payload_len + CTRL_V2_CRC_LEN), mac);
    if (ctrl_hmac_equal(mac, &data[8 + payload_len], CTRL_V2_HMAC_LEN))
    {
        return HMAC_KEY_DERIVED;
    }

    const uint8_t* boot_key = (const uint8_t*)PROXY_CTRL_HMAC_KEY;
    size_t boot_len = strlen(PROXY_CTRL_HMAC_KEY);
    hmac_sha256(boot_key, boot_len, data, (size_t)(CTRL_V2_HDR_LEN + payload_len + CTRL_V2_CRC_LEN), mac);
    if (ctrl_hmac_equal(mac, &data[8 + payload_len], CTRL_V2_HMAC_LEN))
    {
        return HMAC_KEY_BOOTSTRAP;
    }
    return HMAC_KEY_NONE;
}

static void handle_ctrl_frame(uint8_t const* data, uint16_t len)
{
    if (!data || len < CTRL_V2_MIN_LEN) return;
    if (data[0] != CTRL_V2_MAGIC || data[1] != CTRL_V2_VERSION) return;

    uint8_t payload_len = data[5];
    uint16_t total_len = (uint16_t)(CTRL_V2_HDR_LEN + payload_len + CTRL_V2_CRC_LEN + CTRL_V2_HMAC_LEN);
    if (len != total_len) return;

    uint16_t crc = crc16_ccitt(data, (uint32_t)(CTRL_V2_HDR_LEN + payload_len), 0xFFFF);
    uint16_t msg_crc = (uint16_t)data[6 + payload_len] | ((uint16_t)data[7 + payload_len] << 8);
    if (crc != msg_crc) return;

    hmac_key_kind_t key_kind = ctrl_verify_hmac(data[4], data, payload_len);
    if (key_kind == HMAC_KEY_NONE) return;
    bool use_bootstrap = (key_kind == HMAC_KEY_BOOTSTRAP);

    uint8_t seq = data[3];
    uint8_t cmd = data[4];
    uint8_t const* payload = &data[6];

    switch (cmd)
    {
        case 0x01: // INJECT_REPORT
        {
            if (payload_len < 2) { uint8_t err = CTRL_ERR_BAD_LEN; ctrl_send_response(seq, cmd, CTRL_FLAG_RESPONSE | CTRL_FLAG_ERROR, &err, 1, use_bootstrap); return; }
            uint8_t itf_sel = payload[0];
            uint8_t rlen    = payload[1];
            if ((uint16_t)rlen > (uint16_t)(payload_len - 2))
            {
                rlen = (uint8_t)(payload_len - 2);
            }
            if (rlen == 0) { uint8_t err = CTRL_ERR_BAD_LEN; ctrl_send_response(seq, cmd, CTRL_FLAG_RESPONSE | CTRL_FLAG_ERROR, &err, 1, use_bootstrap); return; }

            bool ok = hid_proxy_host_inject_report(itf_sel, &payload[2], rlen);
            if (!ok)
            {
                uint8_t err = CTRL_ERR_INJECT_FAILED;
                ctrl_send_response(seq, cmd, CTRL_FLAG_RESPONSE | CTRL_FLAG_ERROR, &err, 1, use_bootstrap);
            }
            else
            {
                ctrl_send_response(seq, cmd, CTRL_FLAG_RESPONSE, NULL, 0, use_bootstrap);
            }
            break;
        }
        case 0x02: // LIST_INTERFACES
        {
            send_interface_list(seq, use_bootstrap);
            break;
        }
        case 0x04: // GET_REPORT_DESC
        {
            if (payload_len < 1) { uint8_t err = CTRL_ERR_BAD_LEN; ctrl_send_response(seq, cmd, CTRL_FLAG_RESPONSE | CTRL_FLAG_ERROR, &err, 1, use_bootstrap); return; }
            send_report_descriptor(seq, payload[0], use_bootstrap);
            break;
        }
        case 0x05: // GET_REPORT_LAYOUT
        {
            if (payload_len < 2) { uint8_t err = CTRL_ERR_BAD_LEN; ctrl_send_response(seq, cmd, CTRL_FLAG_RESPONSE | CTRL_FLAG_ERROR, &err, 1, use_bootstrap); return; }
            send_report_layout(seq, payload[0], payload[1], use_bootstrap);
            break;
        }
        case 0x03: // SET_LOG_LEVEL
        {
            if (payload_len < 1) { uint8_t err = CTRL_ERR_BAD_LEN; ctrl_send_response(seq, cmd, CTRL_FLAG_RESPONSE | CTRL_FLAG_ERROR, &err, 1, use_bootstrap); return; }
            logging_set_level(payload[0]);
            ctrl_send_response(seq, cmd, CTRL_FLAG_RESPONSE, NULL, 0, use_bootstrap);
            break;
        }
        case 0x06: // GET_DEVICE_ID
        {
            if (payload_len != 0) { uint8_t err = CTRL_ERR_BAD_LEN; ctrl_send_response(seq, cmd, CTRL_FLAG_RESPONSE | CTRL_FLAG_ERROR, &err, 1, use_bootstrap); return; }
            send_device_id(seq);
            break;
        }

        default:
            // Unknown command: ignore.
            break;
    }
}

static void ctrl_slip_feed(uint8_t b)
{
    if (b == SLIP_END)
    {
        if (s_ctrl_rx_len)
        {
            handle_ctrl_frame(s_ctrl_rx_buf, s_ctrl_rx_len);
        }
        ctrl_rx_reset();
        return;
    }

    if (b == SLIP_ESC)
    {
        s_ctrl_rx_esc = true;
        return;
    }

    if (s_ctrl_rx_esc)
    {
        if (b == SLIP_ESC_END)      b = SLIP_END;
        else if (b == SLIP_ESC_ESC) b = SLIP_ESC;
        s_ctrl_rx_esc = false;
    }

    if (s_ctrl_rx_len < CTRL_RX_BUF_MAX)
    {
        s_ctrl_rx_buf[s_ctrl_rx_len++] = b;
    }
    else
    {
        // Overflow: drop frame and resync.
        ctrl_rx_reset();
    }
}

void control_uart_init(void)
{
#if !PROXY_CTRL_UART_ENABLED
    return;
#else
    // Don't allow sharing the same UART as the internal bridge link.
    if (PROXY_CTRL_UART_ID == PROXY_UART_ID)
    {
        LOGW("[CTRL] PROXY_CTRL_UART_ID conflicts with PROXY_UART_ID; control UART disabled");
        return;
    }

    uint32_t requested_baud = PROXY_CTRL_UART_BAUD;
    uint32_t actual_baud = uart_init(PROXY_CTRL_UART_ID, requested_baud);

    if (PROXY_CTRL_UART_USE_HW_FLOW)
    {
        uart_set_hw_flow(PROXY_CTRL_UART_ID, true, true);
    }
    uart_set_format(PROXY_CTRL_UART_ID, 8, 1, UART_PARITY_NONE);
    uart_set_fifo_enabled(PROXY_CTRL_UART_ID, true);

    gpio_set_function(PROXY_CTRL_UART_TX_PIN, GPIO_FUNC_UART);
    gpio_set_function(PROXY_CTRL_UART_RX_PIN, GPIO_FUNC_UART);
    if (PROXY_CTRL_UART_USE_HW_FLOW)
    {
        gpio_set_function(PROXY_CTRL_UART_CTS_PIN, GPIO_FUNC_UART);
        gpio_set_function(PROXY_CTRL_UART_RTS_PIN, GPIO_FUNC_UART);
    }

    if (actual_baud != requested_baud)
    {
        LOGW("[CTRL] UART baud clamped: requested=%u actual=%u", requested_baud, actual_baud);
    }
    LOGI("[CTRL] UART init on %s TX=%u RX=%u @%u baud%s",
         (PROXY_CTRL_UART_ID == uart0) ? "uart0" : "uart1",
         PROXY_CTRL_UART_TX_PIN,
         PROXY_CTRL_UART_RX_PIN,
         actual_baud,
         PROXY_CTRL_UART_USE_HW_FLOW ? " HW_FLOW=ON" : " HW_FLOW=OFF");

    ctrl_init_hmac_key();
    ctrl_rx_reset();
#endif
}

void control_uart_task(void)
{
#if !PROXY_CTRL_UART_ENABLED
    return;
#else
    if (PROXY_CTRL_UART_ID == PROXY_UART_ID)
    {
        return;
    }

    // Keep processing bounded so we don't starve USB host tasks.
    const uint32_t budget_us  = 500u;
    const uint32_t t_start_us = time_us_32();
    uint32_t bytes_processed  = 0;
    const uint32_t max_bytes  = 512u;

    while (uart_is_readable(PROXY_CTRL_UART_ID))
    {
        uint8_t b = (uint8_t)uart_getc(PROXY_CTRL_UART_ID);
        ctrl_slip_feed(b);

        if (++bytes_processed >= max_bytes) break;
        if ((time_us_32() - t_start_us) >= budget_us) break;
    }
#endif
}
