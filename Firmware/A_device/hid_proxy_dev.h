#ifndef HID_PROXY_DEV_H
#define HID_PROXY_DEV_H

#include <stdint.h>
#include <stdbool.h>

void hid_proxy_dev_init(void);
void hid_proxy_dev_task(void);
void hid_proxy_dev_service(void);
bool hid_proxy_dev_usb_ready(void);
bool hid_proxy_dev_get_device_descriptor(uint8_t const** out_data,
                                         uint16_t *out_len);
bool hid_proxy_dev_get_config_descriptor(uint8_t const** out_data,
                                         uint16_t *out_len);
bool hid_proxy_dev_get_report_descriptor(uint8_t itf,
                                         uint8_t const** out_data,
                                         uint16_t *out_len);
bool hid_proxy_dev_get_string_descriptor(uint8_t index,
                                         uint16_t langid,
                                         uint8_t const** out_data,
                                         uint16_t *out_len);

#endif // HID_PROXY_DEV_H
