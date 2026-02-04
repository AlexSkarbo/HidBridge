#ifndef I2C_SLAVE_H
#define I2C_SLAVE_H

#include <stdint.h>
#include <stdbool.h>
#include "pico/stdlib.h"
#include "hardware/i2c.h"
#include "hardware/gpio.h"

void i2c_slave_init(uint8_t addr, uint sda, uint scl, uint32_t baud);

int i2c_slave_read(uint8_t *buf, uint16_t maxlen);

int i2c_slave_write(const uint8_t *buf, uint16_t len);

#endif // I2C_SLAVE_H
