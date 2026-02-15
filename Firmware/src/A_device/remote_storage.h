#ifndef REMOTE_STORAGE_H
#define REMOTE_STORAGE_H

#include <stdint.h>
#include <stdbool.h>
#include "tusb.h"
#include "proxy_config.h"

typedef struct
{
    uint8_t  data[PROXY_MAX_DESC_SIZE];
    uint16_t len;
    bool     valid;
} remote_desc_buffer_t;

typedef struct
{
    uint8_t  data[64];
    uint16_t len;
    bool     valid;
    bool     pending;
    bool     allow_fetch;
    uint16_t langid;
} remote_string_desc_t;

typedef struct
{
    remote_desc_buffer_t reports[CFG_TUD_HID];
    remote_desc_buffer_t device;
    remote_desc_buffer_t config;
    tusb_speed_t         usb_speed;
    bool                 report_has_id[CFG_TUD_HID];
    bool                 hid_itf_present[CFG_TUD_HID];
    uint16_t             hid_report_expected_len[CFG_TUD_HID];
    remote_string_desc_t lang;
    remote_string_desc_t strings[256];
    bool                 descriptors_complete;
    bool                 usb_attached;
    bool                 tusb_initialized;
    bool                 ready_sent;
} remote_desc_state_t;

extern remote_desc_state_t s_remote_desc;

void remote_storage_init_defaults(void);
void remote_desc_append(remote_desc_buffer_t* buf,
                        uint8_t const* data,
                        uint16_t len);
remote_string_desc_t* remote_desc_get_string_entry(uint8_t index);
void remote_desc_store_string(uint8_t index,
                              uint16_t langid,
                              uint8_t const* data,
                              uint16_t len);
void remote_storage_update_string_allowlist(void);
void remote_storage_analyze_report_descriptors(void);
bool remote_storage_report_has_id(uint8_t itf);
bool remote_storage_reports_ready(void);
bool remote_storage_get_report_descriptor(uint8_t itf,
                                          uint8_t const** out_data,
                                          uint16_t *out_len);

#endif // REMOTE_STORAGE_H
