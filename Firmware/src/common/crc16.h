#pragma once
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

uint16_t crc16_ccitt(const uint8_t* data, uint32_t len, uint16_t seed);

#ifdef __cplusplus
} // extern "C"
#endif
