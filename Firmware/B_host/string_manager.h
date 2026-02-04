#ifndef STRING_MANAGER_H
#define STRING_MANAGER_H

#include <stdint.h>
#include <stdbool.h>

#define PROXY_STRING_DESC_MAX 128

typedef struct
{
    bool     (*send_frames)(uint8_t cmd, const uint8_t* data, uint16_t len);
    uint32_t (*time_ms)(void);
} string_manager_ops_t;

void string_manager_init(const string_manager_ops_t* ops);
void string_manager_reset(void);
void string_manager_set_default_lang(uint16_t langid);
uint16_t string_manager_get_default_lang(void);
void string_manager_cache_store(uint8_t index, uint16_t langid,
                                const uint8_t* data, uint16_t len);
void string_manager_handle_ctrl_request(const uint8_t* payload, uint16_t len);
void string_manager_task(void);

#endif // STRING_MANAGER_H
