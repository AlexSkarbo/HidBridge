#include "uart_transport.h"
#include "pico/stdlib.h"
#include "hardware/uart.h"
#include "hardware/gpio.h"
#include "hardware/structs/uart.h"
#include "hardware/irq.h"
#include "hardware/sync.h"
#include "pico/time.h"
#include "proxy_config.h"
#include "logging.h"
#include "proto_frame.h"
#include <string.h>

static transport_role_t s_role = TRANSPORT_ROLE_NONE;
static uart_inst_t*     s_uart = NULL;
static uint8_t          s_rx_buf[PROTO_MAX_FRAME_SIZE];
static uint16_t         s_rx_len = 0;
static bool             s_rx_esc = false;
static uint32_t         s_uart_log_tx_host = 0;
static uint32_t         s_uart_log_tx_dev  = 0;
static uint32_t         s_uart_log_rx      = 0;

#define UART_RX_RING_SIZE 16384
static uint8_t  s_rx_ring[UART_RX_RING_SIZE];
static uint32_t s_rx_head = 0;
static uint32_t s_rx_tail = 0;
static uint32_t s_rx_overflow = 0;

#define SLIP_END     0xC0
#define SLIP_ESC     0xDB
#define SLIP_ESC_END 0xDC
#define SLIP_ESC_ESC 0xDD

static inline void rx_ring_clear(void)
{
    uint32_t irq = save_and_disable_interrupts();
    s_rx_head = s_rx_tail = 0;
    s_rx_overflow = 0;
    restore_interrupts(irq);
}

static inline bool rx_ring_empty(void)
{
    return s_rx_head == s_rx_tail;
}

static inline void rx_ring_push(uint8_t b)
{
    uint32_t next = (s_rx_head + 1u) % UART_RX_RING_SIZE;
    if (next == s_rx_tail)
    {
        // Overflow: drop oldest byte, count once per overflow event.
        s_rx_tail = (s_rx_tail + 1u) % UART_RX_RING_SIZE;
        if ((s_rx_overflow++ % 128u) == 0)
        {
            LOGW("[UART] RX ring overflow (%u)", s_rx_overflow);
        }
    }
    s_rx_ring[s_rx_head] = b;
    s_rx_head = next;
}

static inline bool rx_ring_pop(uint8_t* out)
{
    if (s_rx_head == s_rx_tail) return false;
    *out = s_rx_ring[s_rx_tail];
    s_rx_tail = (s_rx_tail + 1u) % UART_RX_RING_SIZE;
    return true;
}

static void uart_transport_flush(void)
{
    if (!s_uart) return;
    while (uart_is_readable(s_uart)) (void)uart_getc(s_uart);
    rx_ring_clear();
    s_rx_len = 0;
    s_rx_esc = false;
}

// Public helper: flush RX FIFO for resync after protocol errors.
void uart_transport_flush_rx(void)
{
    uart_transport_flush();
}

// ----------------------------------------------------------------------------- 
// HOST (B_host)  - UART master
// -----------------------------------------------------------------------------
static void __isr uart_irq_handler(void)
{
    if (!s_uart) return;
    while (uart_is_readable(s_uart))
    {
        rx_ring_push((uint8_t)uart_getc(s_uart));
    }
}

static void setup_irq_handler(void)
{
    int irq = (s_uart == uart0) ? UART0_IRQ : UART1_IRQ;
    irq_set_exclusive_handler(irq, uart_irq_handler);
    irq_set_enabled(irq, true);
    uart_set_irq_enables(s_uart, true, false);
}

void uart_transport_init_host() 
{
    s_role = TRANSPORT_ROLE_HOST;
    s_uart = PROXY_UART_ID;
    uint32_t requested_baud = PROXY_UART_BAUD;
    uint32_t actual_baud = uart_init(s_uart, requested_baud);
    if (PROXY_UART_USE_HW_FLOW)
    {
        uart_set_hw_flow(s_uart, true, true);
    }
    uart_set_format(s_uart, 8, 1, UART_PARITY_NONE);
    uart_set_fifo_enabled(s_uart, true);

    gpio_set_function(PROXY_UART_TX_PIN, GPIO_FUNC_UART);
    gpio_set_function(PROXY_UART_RX_PIN, GPIO_FUNC_UART);
    if (PROXY_UART_USE_HW_FLOW)
    {
        gpio_set_function(PROXY_UART_CTS_PIN, GPIO_FUNC_UART);
        gpio_set_function(PROXY_UART_RTS_PIN, GPIO_FUNC_UART);
    }

    setup_irq_handler();
    uart_transport_flush();

    if (actual_baud != requested_baud)
    {
        LOGW("[UART] HOST baud clamped: requested=%u actual=%u", requested_baud, actual_baud);
    }
    LOGI("[UART] HOST init on %s TX=%u RX=%u @%u baud%s",
         (s_uart == uart0) ? "uart0" : "uart1",
         PROXY_UART_TX_PIN,
         PROXY_UART_RX_PIN,
         actual_baud,
         PROXY_UART_USE_HW_FLOW ? " HW_FLOW=ON" : " HW_FLOW=OFF");
    if (PROXY_UART_USE_HW_FLOW)
    {
        LOGI("[UART] HOST flow pins CTS=%u RTS=%u", PROXY_UART_CTS_PIN, PROXY_UART_RTS_PIN);
    }
}

// -----------------------------------------------------------------------------
// DEVICE (A_device) - UART device
// -----------------------------------------------------------------------------
void uart_transport_init_device()
{
    s_role = TRANSPORT_ROLE_DEVICE;
    s_uart = PROXY_UART_ID;
    uint32_t requested_baud = PROXY_UART_BAUD;
    uint32_t actual_baud = uart_init(s_uart, requested_baud);
    if (PROXY_UART_USE_HW_FLOW)
    {
        uart_set_hw_flow(s_uart, true, true);
    }
    uart_set_format(s_uart, 8, 1, UART_PARITY_NONE);
    uart_set_fifo_enabled(s_uart, true);

    gpio_set_function(PROXY_UART_TX_PIN, GPIO_FUNC_UART);
    gpio_set_function(PROXY_UART_RX_PIN, GPIO_FUNC_UART);
    if (PROXY_UART_USE_HW_FLOW)
    {
        gpio_set_function(PROXY_UART_CTS_PIN, GPIO_FUNC_UART);
        gpio_set_function(PROXY_UART_RTS_PIN, GPIO_FUNC_UART);
    }

    setup_irq_handler();
    uart_transport_flush();

    if (actual_baud != requested_baud)
    {
        LOGW("[UART] DEVICE baud clamped: requested=%u actual=%u", requested_baud, actual_baud);
    }
    LOGI("[UART] DEVICE init TX=%u RX=%u @%u baud%s",
         PROXY_UART_TX_PIN,
         PROXY_UART_RX_PIN,
         actual_baud,
         PROXY_UART_USE_HW_FLOW ? " HW_FLOW=ON" : " HW_FLOW=OFF");
    if (PROXY_UART_USE_HW_FLOW)
    {
        LOGI("[UART] DEVICE flow pins CTS=%u RTS=%u", PROXY_UART_CTS_PIN, PROXY_UART_RTS_PIN);
    }
}

// -----------------------------------------------------------------------------
// MASTER -> SLAVE (B_host sends to A_device)
// -----------------------------------------------------------------------------
static int slip_encode(const uint8_t* data, uint16_t len,
                       uint8_t* out, uint16_t out_max)
{
    if (!data || !out) return -1;

    uint16_t pos = 0;
    if (out_max < 2) return -1;
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

int uart_transport_send(const uint8_t* data, uint16_t len)
{
    if (s_role != TRANSPORT_ROLE_HOST || !s_uart) return -1;
    if (!data || !len) return 0;

    uint8_t encoded[PROTO_MAX_FRAME_SIZE * 2 + 4];
    int enc_len = slip_encode(data, len, encoded, sizeof(encoded));
    if (enc_len <= 0) return -1;

    uint32_t t0 = time_us_32();
    uart_write_blocking(s_uart, encoded, enc_len);
    uint32_t send_us = time_us_32() - t0;
    if (send_us > 2000)
    {
        LOGW("[UART] HOST send slow: %u us (raw=%u enc=%u)", (unsigned)send_us, len, enc_len);
    }
    bool do_log = false;
    if (LOG_SAMPLE_UART == 0)
    {
        do_log = true;
    }
    else
    {
        uint32_t idx = ++s_uart_log_tx_host;
        do_log = (idx == 1) || ((idx % LOG_SAMPLE_UART) == 1);
    }
    if (do_log)
    {
        LOGT("[UART] HOST send len=%u (raw=%u)", enc_len, len);
    }
    return (int)len;
}

// -----------------------------------------------------------------------------
// SLAVE reads data pushed by master (A_device consumes reports from B_host)
// -----------------------------------------------------------------------------
int uart_transport_device_send(const uint8_t* data, uint16_t len)
{
    if (s_role != TRANSPORT_ROLE_DEVICE || !s_uart) return -1;
    if (!data || !len) return 0;

    uint8_t encoded[PROTO_MAX_FRAME_SIZE * 2 + 4];
    int enc_len = slip_encode(data, len, encoded, sizeof(encoded));
    if (enc_len <= 0) return -1;

    uint32_t t0 = time_us_32();
    uart_write_blocking(s_uart, encoded, enc_len);
    uint32_t send_us = time_us_32() - t0;
    if (send_us > 2000)
    {
        LOGW("[UART] DEV send slow: %u us (raw=%u enc=%u)", (unsigned)send_us, len, enc_len);
    }
    bool do_log = false;
    if (LOG_SAMPLE_UART == 0)
    {
        do_log = true;
    }
    else
    {
        uint32_t idx = ++s_uart_log_tx_dev;
        do_log = (idx == 1) || ((idx % LOG_SAMPLE_UART) == 1);
    }
    if (do_log)
    {
        LOGT("[UART] DEV send len=%u (raw=%u)", enc_len, len);
    }
    return (int)len;
}

int uart_transport_recv_frame(uint8_t* data, uint16_t maxlen)
{
    if (!s_uart || !data || !maxlen) return -1;

    uint8_t b;
    while (rx_ring_pop(&b))
    {
        if (b == SLIP_END)
        {
            if (s_rx_len == 0)
            {
                // Просто роздільник між кадрами.
                continue;
            }

            uint16_t frame_len = s_rx_len;
            if (frame_len > maxlen)
            {
                LOGW("[UART] RX frame truncated len=%u max=%u", frame_len, maxlen);
                frame_len = maxlen;
            }
            memcpy(data, s_rx_buf, frame_len);
            s_rx_len = 0;
            s_rx_esc = false;
            bool do_log = false;
            if (LOG_SAMPLE_UART == 0)
            {
                do_log = true;
            }
            else
            {
                uint32_t idx = ++s_uart_log_rx;
                do_log = (idx == 1) || ((idx % LOG_SAMPLE_UART) == 1);
            }
            if (do_log)
            {
                LOGT("[UART] RX frame len=%u", frame_len);
            }
            return (int)frame_len;
        }
        else if (b == SLIP_ESC)
        {
            s_rx_esc = true;
        }
        else
        {
            if (s_rx_esc)
            {
                if (b == SLIP_ESC_END)      b = SLIP_END;
                else if (b == SLIP_ESC_ESC) b = SLIP_ESC;
                s_rx_esc = false;
            }

            if (s_rx_len < PROTO_MAX_FRAME_SIZE)
            {
                s_rx_buf[s_rx_len++] = b;
            }
            else
            {
                LOGW("[UART] RX buffer overflow, flushing");
                s_rx_len = 0;
                s_rx_esc = false;
            }
        }
    }

    return 0;
}
