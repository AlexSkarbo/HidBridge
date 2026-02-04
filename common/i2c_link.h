// common/i2c_link.h
#pragma once

#include <stdint.h>
#include <stdbool.h>
#include "pico/stdlib.h"
#include "hardware/i2c.h"

#ifdef __cplusplus
extern "C" {
#endif

void i2c_link_init_master(i2c_inst_t *i2c, uint sda, uint scl, uint32_t baud);

int i2c_link_write(uint8_t addr, const uint8_t *data, uint16_t len, bool nostop);
int i2c_link_read (uint8_t addr, uint8_t *data, uint16_t len, bool nostop);

int i2c_link_probe(uint8_t addr);

#ifdef __cplusplus
}
#endif
