#include <string.h>
#include "tusb.h"
#include "hid_host.h"
#include "hid_proxy_host.h"
#include "logging.h"
#include "proxy_config.h"

static uint32_t s_input_log_counter = 0;

void hid_host_init(void)
{
    LOGI("[B] hid_host_init");
}

void hid_host_task(void)
{
    tuh_task();
}

void tuh_hid_mount_cb(uint8_t dev_addr, uint8_t instance,
                      uint8_t const* desc_report, uint16_t desc_len)
{
    LOGI("[B] tuh_hid_mount_cb dev=%u itf=%u desc_len=%u",
         dev_addr, instance, desc_len);

    hid_proxy_host_on_mount(dev_addr, instance, desc_report, desc_len);
}

void tuh_hid_umount_cb(uint8_t dev_addr, uint8_t instance)
{
    LOGI("[B] tuh_hid_umount_cb dev=%u itf=%u", dev_addr, instance);
    hid_proxy_host_on_unmount(dev_addr, instance);
}

void tuh_hid_report_received_cb(uint8_t dev_addr, uint8_t instance,
                                uint8_t const* report, uint16_t len)
{
    bool do_log = false;
    if (LOG_SAMPLE_INPUT == 0)
    {
        do_log = true;
    }
    else
    {
        uint32_t idx = ++s_input_log_counter;
        do_log = (idx == 1) || ((idx % LOG_SAMPLE_INPUT) == 1);
    }

    if (do_log)
    {
        LOGI("[B] tuh_hid_report_received_cb dev=%u itf=%u len=%u",
             dev_addr,
             instance,
             len);
    }
    hid_proxy_host_on_report(dev_addr, instance, report, len);
}
