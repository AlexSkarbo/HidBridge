#include "pico/stdlib.h"
#include "bsp/board.h"
#include "tusb.h"
#include "hid_host.h"
#include "hid_proxy_host.h"
#include "logging.h"
#include "proxy_config.h"
#include "uart_transport.h"
#include "control_uart.h"

int main(void)
{
    stdio_init_all();
    board_init();

    LOGI("[BOOT] B_host: starting...");

    uart_transport_init_host();//0, I2C_SDA_PIN, I2C_SCL_PIN, PROXY_I2C_ADDR, I2C_BAUD);
    control_uart_init();
    hid_host_init();
    hid_proxy_host_init();

    tusb_init();

    while (1)
    {
        control_uart_task();
        tuh_task();
        hid_proxy_host_task();
    }

    return 0;
}
