#ifndef PROXY_CONFIG_H
#define PROXY_CONFIG_H

#include "pico/stdlib.h"
#include "hardware/uart.h"

#ifndef PROXY_I2C_ADDR
#  define PROXY_I2C_ADDR    0x28
#endif

#ifndef I2C_BAUD
#  define I2C_BAUD          (8000000)
#endif

#ifndef I2C_SDA_PIN
#  define I2C_SDA_PIN       20
#endif

#ifndef I2C_SCL_PIN
#  define I2C_SCL_PIN       21
#endif

#ifndef PROXY_IRQ_PIN
#  define PROXY_IRQ_PIN     22
#endif

#ifndef PROXY_UART_ID
#  define PROXY_UART_ID     uart1
#endif

#ifndef PROXY_UART_TX_PIN
#  define PROXY_UART_TX_PIN 4
#endif

#ifndef PROXY_UART_RX_PIN
#  define PROXY_UART_RX_PIN 5
#endif

#ifndef PROXY_UART_USE_HW_FLOW
#  define PROXY_UART_USE_HW_FLOW 1
#endif

#ifndef PROXY_UART_CTS_PIN
#  define PROXY_UART_CTS_PIN 6
#endif

#ifndef PROXY_UART_RTS_PIN
#  define PROXY_UART_RTS_PIN 7
#endif

#ifndef PROXY_UART_BAUD_DEFAULT
#  define PROXY_UART_BAUD_DEFAULT (12000000)
#endif

#ifndef PROXY_UART_BAUD_FAST
#  define PROXY_UART_BAUD_FAST    (16000000)
#endif

#ifndef PROXY_UART_BAUD
#  define PROXY_UART_BAUD   (PROXY_UART_BAUD_FAST) // Вищі значення будуть автоматично обмежені максимумом UART.
#endif

// ---------------------------------------------------------
// Optional external control UART (typically on B_host), used to inject mouse/keyboard
// reports from an external controller.
// ---------------------------------------------------------

#ifndef PROXY_CTRL_UART_ENABLED
#  define PROXY_CTRL_UART_ENABLED 1
#endif

#ifndef PROXY_CTRL_UART_ID
#  define PROXY_CTRL_UART_ID uart0
#endif

#ifndef PROXY_CTRL_UART_TX_PIN
#  define PROXY_CTRL_UART_TX_PIN 0
#endif

#ifndef PROXY_CTRL_UART_RX_PIN
#  define PROXY_CTRL_UART_RX_PIN 1
#endif

#ifndef PROXY_CTRL_UART_USE_HW_FLOW
#  define PROXY_CTRL_UART_USE_HW_FLOW 0
#endif

#ifndef PROXY_CTRL_UART_CTS_PIN
#  define PROXY_CTRL_UART_CTS_PIN 2
#endif

#ifndef PROXY_CTRL_UART_RTS_PIN
#  define PROXY_CTRL_UART_RTS_PIN 3
#endif

#ifndef PROXY_CTRL_UART_BAUD
// 3 Мбод часто працює з USB-UART адаптерами; за потреби можна підняти.
#  define PROXY_CTRL_UART_BAUD (3000000)
#endif

#ifndef PROXY_CTRL_HMAC_KEY
    #define PROXY_CTRL_HMAC_KEY "your-master-secret"
#endif

#ifndef INPUT_LOG_VERBOSE
#  define INPUT_LOG_VERBOSE 0
#endif

#ifndef PROTO_LOG_VERBOSE
#  define PROTO_LOG_VERBOSE 0
#endif

#ifndef LOG_SAMPLE_UART
#  define LOG_SAMPLE_UART 500   // 0 = логувати всі UART TX/RX, N>0 = перший і кожен N-й
#endif

#ifndef LOG_SAMPLE_INPUT
#  define LOG_SAMPLE_INPUT 500  // 0 = всі tuh_hid_report_received_cb, N>0 = перший і кожен N-й
#endif

#ifndef PROXY_MAX_DESC_SIZE
#  define PROXY_MAX_DESC_SIZE  512
#endif

// Bound the amount of UART RX processing per `hid_proxy_*_task()` call.
// Helps prevent starving TinyUSB (device enumeration/state machine) when the
// other side is streaming input frames early.
#ifndef PROXY_UART_RX_BUDGET_ENUM_US
#  define PROXY_UART_RX_BUDGET_ENUM_US 500u
#endif

#ifndef PROXY_UART_RX_BUDGET_RUN_US
#  define PROXY_UART_RX_BUDGET_RUN_US 5000u
#endif

#ifndef PROXY_UART_RX_MAX_FRAMES_ENUM
#  define PROXY_UART_RX_MAX_FRAMES_ENUM 16u
#endif

#ifndef PROXY_UART_RX_MAX_FRAMES_RUN
#  define PROXY_UART_RX_MAX_FRAMES_RUN 128u
#endif

#ifndef LOG_LEVEL
#define LOG_LEVEL 4
#endif

#endif // PROXY_CONFIG_H
