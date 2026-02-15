#include "i2c_slave.h"
#include "hardware/structs/i2c.h"

static i2c_inst_t *s_i2c = i2c0;

static inline i2c_hw_t *slv_hw(void) {
    return i2c_get_hw(s_i2c);
}

void i2c_slave_init(uint8_t addr, uint sda, uint scl, uint32_t baud) {
    s_i2c = i2c0;

    gpio_set_function(sda, GPIO_FUNC_I2C);
    gpio_set_function(scl, GPIO_FUNC_I2C);
    gpio_pull_up(sda);
    gpio_pull_up(scl);

    i2c_init(s_i2c, baud);
    i2c_set_slave_mode(s_i2c, true, addr);

    i2c_hw_t *hw = slv_hw();

    while (hw->rxflr) {
        (void)hw->data_cmd;
    }

    while (hw->txflr) {
        (void)hw->txflr;
        hw->enable = 0;
        hw->enable = 1;
        break;
    }
}

int i2c_slave_read(uint8_t *buf, uint16_t maxlen) {
    if (!buf || maxlen == 0) return 0;

    i2c_hw_t *hw = slv_hw();
    uint16_t count = 0;

    while ((count < maxlen) && hw->rxflr) {
        buf[count++] = (uint8_t)(hw->data_cmd & 0xFF);
    }

    return (int)count;
}

int i2c_slave_write(const uint8_t *buf, uint16_t len) {
    if (!buf || len == 0) return 0;

    i2c_hw_t *hw = slv_hw();
    uint16_t count = 0;

    while (count < len) {
        while (hw->txflr >= 16) {
            __asm volatile ("nop");
        }

        hw->data_cmd = buf[count++];
    }

    return (int)count;
}
