#ifndef _TUSB_CONFIG_H_
#define _TUSB_CONFIG_H_

#include "pico/stdlib.h"
#include "tusb_option.h"

#ifdef __cplusplus
extern "C" {
#endif

//--------------------------------------------------------------------
// TinyUSB configuration for A_device (USB device role)
//--------------------------------------------------------------------

#define CFG_TUSB_MCU           OPT_MCU_RP2040
#define CFG_TUSB_OS            OPT_OS_PICO

// USB Device on the on-chip root hub
#define CFG_TUSB_RHPORT0_MODE  (OPT_MODE_DEVICE)
#define CFG_TUSB_RHPORT1_MODE  OPT_MODE_NONE

#ifndef BOARD_TUD_RHPORT
#define BOARD_TUD_RHPORT 0
#endif

// Device stack only
#define CFG_TUD_ENABLED        1
#define CFG_TUH_ENABLED        0

#ifndef CFG_TUSB_DEBUG
#define CFG_TUSB_DEBUG 3
#endif

#define CFG_TUSB_MEM_SECTION
#define CFG_TUSB_MEM_ALIGN     __attribute__((aligned(4)))

//--------------------------------------------------------------------
// Device stack
//--------------------------------------------------------------------

#define CFG_TUD_ENDPOINT0_SIZE 64

#define CFG_TUD_HID            4
#define CFG_TUD_CDC            0
#define CFG_TUD_MSC            0
#define CFG_TUD_MIDI           0
#define CFG_TUD_VENDOR         0

// Максимальна довжина HID report descriptor, яку TinyUSB може віддати
// (фактичні дескриптори ми тягнемо віддалено, тут виставляємо верхню межу).
#define CFG_TUD_HID_REPORT_DESC_LEN 128

#define CFG_TUD_HID_EP_BUFSIZE 64

#ifdef __cplusplus
}
#endif

#endif // _TUSB_CONFIG_H_
