#pragma once
#include "tusb.h"

#ifdef __cplusplus
extern "C" {
#endif

enum {
  ITF_NUM_KBD = 0,
  ITF_NUM_MOUSE,
  ITF_NUM_CONSUMER,
  ITF_NUM_TOTAL
};

enum {
  RID_KBD      = 1,
  RID_MOUSE    = 2,
  RID_CONSUMER = 3
};

// TinyUSB device callbacks
uint8_t  const* tud_descriptor_device_cb(void);
uint8_t  const* tud_descriptor_configuration_cb(uint8_t index);
uint16_t const* tud_descriptor_string_cb(uint8_t index, uint16_t langid);

// HID specific callbacks
uint8_t  const* tud_hid_descriptor_report_cb(uint8_t instance);
uint16_t        tud_hid_descriptor_report_len_cb(uint8_t instance);
uint16_t        tud_hid_get_report_cb(uint8_t instance, uint8_t report_id,
                                      hid_report_type_t report_type,
                                      uint8_t* buffer, uint16_t reqlen);

#ifdef __cplusplus
} // extern "C"
#endif
