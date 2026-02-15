// common/proto_frame.h
#pragma once

#include <stdint.h>
#include <stdbool.h>

#define PROTO_MAX_FRAME_SIZE   260
#define PROTO_HEADER_SIZE      4
#define PROTO_CRC_SIZE         2
#define PROTO_MAX_PAYLOAD_SIZE (PROTO_MAX_FRAME_SIZE - PROTO_HEADER_SIZE - PROTO_CRC_SIZE)

// Frame types
typedef enum
{
    PF_DESCRIPTOR = 1,   // Descriptor chunks + lifecycle markers
    PF_INPUT      = 2,   // HID input report from B_host -> A_device
    PF_CONTROL    = 3,   // Control commands from A_device -> B_host
    PF_UNMOUNT    = 4    // Notify that physical device was detached
} proto_frame_type_t;

// Descriptor commands (used when type == PF_DESCRIPTOR)
typedef enum
{
    PF_DESC_DEVICE   = 1,   // USB device descriptor
    PF_DESC_CONFIG   = 2,   // USB configuration descriptor (includes interfaces)
    PF_DESC_HID      = 3,   // HID descriptor for a specific interface
    PF_DESC_REPORT   = 4,   // Full HID report descriptor (payload starts with itf_id)
    PF_DESC_STRING   = 5,   // USB string descriptor
    PF_DESC_DONE     = 6    // Marker signalling descriptor transmission complete
} proto_desc_cmd_t;

// Control commands (inside PF_CONTROL)
typedef enum
{
    PF_CTRL_SET_PROTOCOL = 1,   // set protocol (boot/report)
    PF_CTRL_GET_REPORT   = 2,   // get report
    PF_CTRL_SET_REPORT   = 3,   // set report
    PF_CTRL_SET_IDLE     = 4,   // set idle
    PF_CTRL_READY        = 5,   // device ready for input stream
    PF_CTRL_STRING_REQ   = 6,   // request USB string descriptor
    PF_CTRL_DEVICE_RESET = 7    // force TinyUSB disconnect/re-enumeration
} proto_ctrl_cmd_t;

typedef enum
{
    PF_RESET_REASON_REENUMERATE = 1, // descriptors changed, reattach
    PF_RESET_REASON_HOST_REQUEST = 2,
    PF_RESET_REASON_REMOTE_ERROR = 3
} proto_ctrl_reset_reason_t;

typedef struct
{
    uint8_t  type;
    uint8_t  cmd;
    uint16_t len; // payload length
    uint8_t  data[PROTO_MAX_PAYLOAD_SIZE];
} proto_frame_t;

// Parse raw buffer into proto_frame_t
bool proto_parse(const uint8_t *buf, uint16_t len, proto_frame_t *out);

// Builders used on host side (B_host)
int proto_build_input(uint8_t itf_id, uint32_t host_time_ms, uint16_t seq,
                      const uint8_t *report, uint16_t len,
                      uint8_t *out_buf, uint16_t out_max);

int proto_build_descriptor(uint8_t desc_cmd, const uint8_t *desc, uint16_t len,
                           uint8_t *out_buf, uint16_t out_max);

int proto_build_unmount(uint8_t *out_buf, uint16_t out_max);
int proto_build_ctrl_device_reset(uint8_t reason,
                                  uint8_t *out_buf, uint16_t out_max);

// Builders used on device side (A_device) to send control to host
int proto_build_ctrl_set_protocol(uint8_t itf_id, uint8_t protocol,
                                  uint8_t *out_buf, uint16_t out_max);

int proto_build_ctrl_get_report(uint8_t itf_id, uint8_t rtype, uint8_t rid, uint16_t req_len,
                                uint8_t *out_buf, uint16_t out_max);

int proto_build_ctrl_set_report(uint8_t itf_id, uint8_t rtype, uint8_t rid,
                                const uint8_t *payload, uint16_t plen,
                                uint8_t *out_buf, uint16_t out_max);

int proto_build_ctrl_set_idle(uint8_t itf_id, uint8_t duration, uint8_t rid,
                              uint8_t *out_buf, uint16_t out_max);

int proto_build_ctrl_ready(uint8_t *out_buf, uint16_t out_max);
int proto_build_ctrl_string_req(uint8_t index, uint16_t langid,
                                uint8_t *out_buf, uint16_t out_max);
int proto_build_ctrl_get_report_resp(uint8_t itf_id, uint8_t rtype, uint8_t rid,
                                     uint8_t const* report, uint16_t len,
                                     uint8_t *out_buf, uint16_t out_max);
