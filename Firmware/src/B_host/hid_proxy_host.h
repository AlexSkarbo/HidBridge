#ifndef HID_PROXY_HOST_H
#define HID_PROXY_HOST_H

#include <stdint.h>
#include <stdbool.h>
#include <stddef.h>
#include "hid_host.h"

void hid_proxy_host_init(void);

void hid_proxy_host_on_mount(uint8_t dev_addr, uint8_t instance,
                             const uint8_t* desc_report, uint16_t desc_len);

void hid_proxy_host_on_unmount(uint8_t dev_addr, uint8_t instance);

void hid_proxy_host_on_report(uint8_t dev_addr, uint8_t instance,
                              const uint8_t* report, uint16_t len);

void hid_proxy_host_task(void);

bool hid_proxy_host_request_device_reset(uint8_t reason);

typedef struct
{
    uint8_t dev_addr;
    uint8_t itf;
    uint8_t itf_protocol; // HID ITF protocol (keyboard/mouse/other)
    uint8_t protocol;     // HID protocol (boot/report)
    uint8_t inferred_type; // bit0=keyboard, bit1=mouse (from report descriptor)
    uint8_t active;
    uint8_t mounted;
} hid_proxy_itf_info_t;

typedef struct
{
    uint8_t itf;
    uint8_t report_id;
    uint8_t layout_kind; // 1=mouse,2=keyboard,3=mouse+keyboard
    uint8_t flags;       // bit0=hasButtons, bit1=hasWheel, bit2=hasX, bit3=hasY
    uint8_t buttons_offset_bits;
    uint8_t buttons_count;
    uint8_t buttons_size_bits;
    uint8_t x_offset_bits;
    uint8_t x_size_bits;
    uint8_t x_signed;
    uint8_t y_offset_bits;
    uint8_t y_size_bits;
    uint8_t y_signed;
    uint8_t wheel_offset_bits;
    uint8_t wheel_size_bits;
    uint8_t wheel_signed;
    uint8_t kb_report_len;
    uint8_t kb_has_report_id;
} hid_report_layout_t;

// Snapshot of active HID interfaces (B_host side).
// Returns number of entries written to `out`.
size_t hid_proxy_host_list_interfaces(hid_proxy_itf_info_t* out, size_t max_entries);

// Update inferred HID type (keyboard/mouse) from a report descriptor for interface `itf`.
void hid_proxy_host_update_inferred_type(uint8_t itf, uint8_t const* desc, uint16_t len);
void hid_proxy_host_store_report_desc(uint8_t itf, uint8_t const* desc, uint16_t len);
uint16_t hid_proxy_host_get_report_desc(uint8_t itf, uint8_t* out, uint16_t max_len, bool* truncated);
bool hid_proxy_host_get_report_layout(uint8_t itf, uint8_t report_id, hid_report_layout_t* out);

// Inject an input report into the bridge (B_host -> A_device), using the same PF_INPUT format
// as physical HID reports. `itf_sel` can be a concrete interface index (0..CFG_TUH_HID-1),
// or special values:
//   0xFF = first mounted mouse interface
//   0xFE = first mounted keyboard interface
bool hid_proxy_host_inject_report(uint8_t itf_sel, uint8_t const* report, uint16_t len);

// Utility: get dev_addr of first active HID (0 if none)
uint8_t hid_proxy_host_first_dev_addr(void);

// Ensure tracking slot exists for a given dev/itf (used when TinyUSB host
// callbacks не приходять для всіх HID інтерфейсів, але контролі вже йдуть).
void hid_proxy_host_ensure_slot(uint8_t dev_addr, uint8_t itf);

#endif // HID_PROXY_HOST_H
