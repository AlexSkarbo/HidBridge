#include "pico/stdlib.h"
#include "bsp/board.h"
#include "tusb.h"
#include "hid_proxy_dev.h"
#include "logging.h"

int main(void)
{
    stdio_init_all();
    board_init();
    hid_proxy_dev_init();     // Initialize UART transport; TinyUSB starts after descriptors are received

    LOGI("[BOOT] A_device starting...");

    while (1)
    {
        hid_proxy_dev_task(); // Process inbound UART frames

        if (hid_proxy_dev_usb_ready())
        {
            tud_task();       // TinyUSB device state machine (once started)
        }
    }

    return 0;
}
