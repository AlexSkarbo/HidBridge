// common/logging.h
#pragma once

#include <stdio.h>
#include <stdint.h>

#ifndef LOG_LEVEL
// 0 = off, 1 = errors, 2 = warn, 3 = info, 4 = trace
#define LOG_LEVEL 4
#endif

extern volatile uint8_t g_log_level;

static inline uint8_t logging_get_level(void)
{
    return g_log_level;
}

static inline void logging_set_level(uint8_t level)
{
    g_log_level = level;
}

#define LOGE(fmt, ...) do { if (logging_get_level() >= 1) printf("[E] " fmt "\n", ##__VA_ARGS__); } while(0)
#define LOGW(fmt, ...) do { if (logging_get_level() >= 2) printf("[W] " fmt "\n", ##__VA_ARGS__); } while(0)
#define LOGI(fmt, ...) do { if (logging_get_level() >= 3) printf("[I] " fmt "\n", ##__VA_ARGS__); } while(0)
#define LOGT(fmt, ...) do { if (logging_get_level() >= 4) printf("[T] " fmt "\n", ##__VA_ARGS__); } while(0)
