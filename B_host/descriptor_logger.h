#ifndef DESCRIPTOR_LOGGER_H
#define DESCRIPTOR_LOGGER_H

#include <stdint.h>
#include <stdbool.h>

typedef struct
{
    bool (*send_descriptor_frames)(uint8_t cmd, const uint8_t* data, uint16_t len);
    bool (*send_descriptor_done)(void);
} descriptor_logger_ops_t;

void descriptor_logger_init(const descriptor_logger_ops_t* ops);
void descriptor_logger_reset(void);
void descriptor_logger_start(uint8_t dev_addr,
                             const uint8_t* report_desc,
                             uint16_t report_len);
void descriptor_logger_mark_report_forwarded(uint8_t itf);

#endif // DESCRIPTOR_LOGGER_H
