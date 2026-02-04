#ifndef _TUSB_CONFIG_H_
#define _TUSB_CONFIG_H_

#include "pico/stdlib.h"
#include "tusb_option.h"

#ifdef __cplusplus
extern "C" {
#endif

//--------------------------------------------------------------------
// TinyUSB configuration for B_host (USB host role)
//--------------------------------------------------------------------

#define CFG_TUSB_MCU           OPT_MCU_RP2040
#define CFG_TUSB_OS            OPT_OS_PICO

#define CFG_TUSB_RHPORT0_MODE  OPT_MODE_HOST
#define CFG_TUSB_RHPORT1_MODE  OPT_MODE_NONE

#define CFG_TUD_ENABLED        0
#define CFG_TUH_ENABLED        1

#ifndef CFG_TUSB_DEBUG
#define CFG_TUSB_DEBUG 2
#endif

#define CFG_TUSB_MEM_SECTION
#define CFG_TUSB_MEM_ALIGN     __attribute__((aligned(4)))

//--------------------------------------------------------------------
// Host stack
//--------------------------------------------------------------------

#define CFG_TUH_HUB            1
#define CFG_TUH_HID            4
#define CFG_TUH_HID_EPIN_BUFSIZE   64
#define CFG_TUH_HID_EPOUT_BUFSIZE  64

#ifdef __cplusplus
}
#endif

#endif // _TUSB_CONFIG_H_
