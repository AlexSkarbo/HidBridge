// common/proto_frame.c
#include "proto_frame.h"
#include "crc16.h"
#include "logging.h"
#include <string.h>

#ifndef PROTO_LOG_VERBOSE
#define PROTO_LOG_VERBOSE 0
#endif

static void log_hexdump(const char* tag, const uint8_t* buf, uint16_t len)
{
    if (!tag || !buf || !len) return;
    uint16_t dump = (len > 16) ? 16 : len;
    char line[3 * 16 + 1];
    int idx = 0;
    memset(line, 0, sizeof(line));
    for (uint16_t i = 0; i < dump && idx < (int)sizeof(line); i++)
    {
        idx += snprintf(line + idx, sizeof(line) - (size_t)idx, " %02X", buf[i]);
    }
    LOGW("%s%s", tag, line);
}

static uint16_t le16_read(const uint8_t *p)
{
    return (uint16_t)p[0] | ((uint16_t)p[1] << 8);
}

static void le16_write(uint8_t *p, uint16_t v)
{
    p[0] = (uint8_t)(v & 0xFF);
    p[1] = (uint8_t)(v >> 8);
}

static uint32_t le32_read(const uint8_t *p)
{
    return ((uint32_t)p[0]) |
           ((uint32_t)p[1] << 8) |
           ((uint32_t)p[2] << 16) |
           ((uint32_t)p[3] << 24);
}

static void le32_write(uint8_t *p, uint32_t v)
{
    p[0] = (uint8_t)(v & 0xFF);
    p[1] = (uint8_t)((v >> 8) & 0xFF);
    p[2] = (uint8_t)((v >> 16) & 0xFF);
    p[3] = (uint8_t)((v >> 24) & 0xFF);
}

bool proto_parse(const uint8_t *buf, uint16_t len, proto_frame_t *out)
{
    if (!buf || !out) return false;
    if (len < PROTO_HEADER_SIZE + PROTO_CRC_SIZE) return false;

    uint8_t  type = buf[0];
    uint8_t  cmd  = buf[1];
    uint16_t plen = le16_read(&buf[2]);

    if (plen > PROTO_MAX_PAYLOAD_SIZE)
    {
    if (PROTO_LOG_VERBOSE)
    {
        LOGW("[PROTO] payload too large plen=%u", plen);
    }
        return false;
    }
    uint16_t frame_len = PROTO_HEADER_SIZE + plen + PROTO_CRC_SIZE;
    if (frame_len > len)
    {
        if (PROTO_LOG_VERBOSE)
        {
            LOGW("[PROTO] short frame len=%u expected=%u", len, frame_len);
        }
        return false;
    }

    uint16_t crc_expected = le16_read(&buf[PROTO_HEADER_SIZE + plen]);
    uint16_t crc_calc = crc16_ccitt(buf, PROTO_HEADER_SIZE + plen, 0xFFFF);
    if (crc_calc != crc_expected)
    {
        if (PROTO_LOG_VERBOSE)
        {
            LOGW("[PROTO] CRC mismatch calc=0x%04X exp=0x%04X len=%u", crc_calc, crc_expected, frame_len);
            log_hexdump("[PROTO] hdr/payload:", buf, (uint16_t)(PROTO_HEADER_SIZE + ((plen < 16) ? plen : 16)));

            // Dump хвіст кадру разом із CRC для діагностики перекручень на лінії.
            uint16_t tail_start = (frame_len > 12) ? (frame_len - 12) : 0;
            log_hexdump("[PROTO] tail+crc:", buf + tail_start, (uint16_t)(frame_len - tail_start));
        }
        return false;
    }

    out->type = type;
    out->cmd  = cmd;
    out->len  = plen;
    if (plen)
    {
        memcpy(out->data, &buf[PROTO_HEADER_SIZE], plen);
    }
    return true;
}

static int proto_build_common(uint8_t type, uint8_t cmd,
                              const uint8_t *payload, uint16_t plen,
                              uint8_t *out_buf, uint16_t out_max)
{
    if (!out_buf) return -1;
    if (plen > PROTO_MAX_PAYLOAD_SIZE) return -1;
    if (PROTO_HEADER_SIZE + plen + PROTO_CRC_SIZE > out_max) return -1;

    out_buf[0] = type;
    out_buf[1] = cmd;
    le16_write(&out_buf[2], plen);

    if (payload && plen)
    {
        memcpy(&out_buf[PROTO_HEADER_SIZE], payload, plen);
    }

    uint16_t crc = crc16_ccitt(out_buf, PROTO_HEADER_SIZE + plen, 0xFFFF);
    le16_write(&out_buf[PROTO_HEADER_SIZE + plen], crc);

    uint16_t frame_len = (uint16_t)(PROTO_HEADER_SIZE + plen + PROTO_CRC_SIZE);
    if (PROTO_LOG_VERBOSE)
    {
        LOGT("[PROTO] build type=0x%02X cmd=%u plen=%u crc=0x%04X len=%u",
             type, cmd, plen, crc, frame_len);
        if (frame_len)
        {
            uint16_t tail_start = (frame_len > 12) ? (uint16_t)(frame_len - 12) : 0;
            log_hexdump("[PROTO] build tail:", &out_buf[tail_start], (uint16_t)(frame_len - tail_start));
        }
    }
    return PROTO_HEADER_SIZE + plen + PROTO_CRC_SIZE;
}

int proto_build_input(uint8_t itf_id, uint32_t host_time_ms, uint16_t seq,
                      const uint8_t *report, uint16_t len,
                      uint8_t *out_buf, uint16_t out_max)
{
    if (!report || len == 0) return -1;
    // payload: itf_id (1) + ts (4) + seq (2) + report
    if ((uint32_t)len + 7 > PROTO_MAX_PAYLOAD_SIZE) return -1;

    uint8_t buf[PROTO_MAX_PAYLOAD_SIZE];
    buf[0] = itf_id;
    le32_write(&buf[1], host_time_ms);
    le16_write(&buf[5], seq);
    memcpy(&buf[7], report, len);
    return proto_build_common(PF_INPUT, 0, buf, (uint16_t)(len + 7), out_buf, out_max);
}

int proto_build_descriptor(uint8_t desc_cmd, const uint8_t *desc, uint16_t len,
                           uint8_t *out_buf, uint16_t out_max)
{
    return proto_build_common(PF_DESCRIPTOR, desc_cmd, desc, len, out_buf, out_max);
}

int proto_build_unmount(uint8_t *out_buf, uint16_t out_max)
{
    return proto_build_common(PF_UNMOUNT, 0, NULL, 0, out_buf, out_max);
}

int proto_build_ctrl_device_reset(uint8_t reason,
                                  uint8_t *out_buf, uint16_t out_max)
{
    uint8_t payload[1] = { reason };
    return proto_build_common(PF_CONTROL,
                              PF_CTRL_DEVICE_RESET,
                              payload,
                              1,
                              out_buf,
                              out_max);
}

int proto_build_ctrl_set_protocol(uint8_t itf_id, uint8_t protocol,
                                  uint8_t *out_buf, uint16_t out_max)
{
    uint8_t payload[2] = { itf_id, protocol };
    return proto_build_common(PF_CONTROL, PF_CTRL_SET_PROTOCOL,
                              payload, 2, out_buf, out_max);
}

int proto_build_ctrl_get_report(uint8_t itf_id, uint8_t rtype, uint8_t rid, uint16_t req_len,
                                uint8_t *out_buf, uint16_t out_max)
{
    uint8_t payload[5] = {
        itf_id,
        rtype,
        rid,
        (uint8_t)(req_len & 0xFF),
        (uint8_t)(req_len >> 8)
    };
    return proto_build_common(PF_CONTROL, PF_CTRL_GET_REPORT,
                              payload, 5, out_buf, out_max);
}

int proto_build_ctrl_set_report(uint8_t itf_id, uint8_t rtype, uint8_t rid,
                                const uint8_t *payload, uint16_t plen,
                                uint8_t *out_buf, uint16_t out_max)
{
    if (plen + 3 > PROTO_MAX_PAYLOAD_SIZE) return -1;

    uint8_t buf[PROTO_MAX_PAYLOAD_SIZE];
    buf[0] = itf_id;
    buf[1] = rtype;
    buf[2] = rid;
    if (plen)
    {
        memcpy(&buf[3], payload, plen);
    }
    return proto_build_common(PF_CONTROL, PF_CTRL_SET_REPORT,
                              buf, (uint16_t)(plen + 3), out_buf, out_max);
}

int proto_build_ctrl_set_idle(uint8_t itf_id, uint8_t duration, uint8_t rid,
                              uint8_t *out_buf, uint16_t out_max)
{
    uint8_t payload[3] = { itf_id, duration, rid };
    return proto_build_common(PF_CONTROL, PF_CTRL_SET_IDLE,
                              payload, 3, out_buf, out_max);
}

int proto_build_ctrl_ready(uint8_t *out_buf, uint16_t out_max)
{
    return proto_build_common(PF_CONTROL, PF_CTRL_READY,
                              NULL, 0, out_buf, out_max);
}

int proto_build_ctrl_string_req(uint8_t index, uint16_t langid,
                                uint8_t *out_buf, uint16_t out_max)
{
    uint8_t payload[3] = {
        index,
        (uint8_t)(langid & 0xFF),
        (uint8_t)(langid >> 8)
    };
    return proto_build_common(PF_CONTROL, PF_CTRL_STRING_REQ,
                              payload, 3, out_buf, out_max);
}

int proto_build_ctrl_get_report_resp(uint8_t itf_id, uint8_t rtype, uint8_t rid,
                                     uint8_t const* report, uint16_t len,
                                     uint8_t *out_buf, uint16_t out_max)
{
    if (report && (len + 3 > PROTO_MAX_PAYLOAD_SIZE))
    {
        len = PROTO_MAX_PAYLOAD_SIZE - 3;
    }

    if (!report || len == 0)
    {
        return proto_build_common(PF_CONTROL, PF_CTRL_GET_REPORT,
                                  NULL, 0, out_buf, out_max);
    }

    uint8_t buf[PROTO_MAX_PAYLOAD_SIZE];
    buf[0] = itf_id;
    buf[1] = rtype;
    buf[2] = rid;
    memcpy(&buf[3], report, len);
    return proto_build_common(PF_CONTROL, PF_CTRL_GET_REPORT,
                              buf, (uint16_t)(len + 3), out_buf, out_max);
}
