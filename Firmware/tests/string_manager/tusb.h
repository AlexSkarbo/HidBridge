#pragma once

#include <stdint.h>
#include <stdbool.h>

#ifndef TU_MIN
#define TU_MIN(a, b) ((a) < (b) ? (a) : (b))
#endif

#ifndef TUSB_DESC_STRING
#define TUSB_DESC_STRING 0x03
#endif

typedef enum
{
    XFER_RESULT_SUCCESS = 0,
    XFER_RESULT_FAILED  = 1
} xfer_result_t;

struct tuh_xfer_s;
typedef struct tuh_xfer_s tuh_xfer_t;
typedef void (*tuh_xfer_cb_t)(tuh_xfer_t* xfer);

struct tuh_xfer_s
{
    uint8_t   daddr;
    uint8_t   ep_addr;
    uint8_t   result;
    uint16_t  actual_len;
    uint8_t*  buffer;
    uintptr_t user_data;
};

bool tuh_descriptor_get_string(uint8_t dev_addr,
                               uint8_t index,
                               uint16_t langid,
                               uint8_t* buffer,
                               uint16_t bufsize,
                               tuh_xfer_cb_t complete_cb,
                               uintptr_t user_data);
