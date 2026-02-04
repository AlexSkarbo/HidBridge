// common/i2c_link.c
#include "i2c_link.h"
#include "logging.h"

static i2c_inst_t *s_i2c = NULL;

void i2c_link_init_master(i2c_inst_t *i2c, uint sda, uint scl, uint32_t baud)
{
    s_i2c = i2c;
    i2c_init(i2c, baud);
    gpio_set_function(sda, GPIO_FUNC_I2C);
    gpio_set_function(scl, GPIO_FUNC_I2C);
    gpio_pull_up(sda);
    gpio_pull_up(scl);
    LOGI("[I2C] MASTER init on i2c%d SDA=%u SCL=%u @%uHz",
         (i2c == i2c0 ? 0 : 1), sda, scl, (unsigned)baud);
}

int i2c_link_write(uint8_t addr, const uint8_t *data, uint16_t len, bool nostop)
{
    if (!s_i2c) return -1;
    int ret = i2c_write_blocking(s_i2c, addr, data, len, nostop);
    LOGT("[I2C] M->S write addr=0x%02X len=%u ret=%d", addr, len, ret);
    return ret;
}

int i2c_link_read(uint8_t addr, uint8_t *data, uint16_t len, bool nostop)
{
    if (!s_i2c) return -1;
    int ret = i2c_read_blocking(s_i2c, addr, data, len, nostop);
    LOGT("[I2C] M<-S read addr=0x%02X len=%u ret=%d", addr, len, ret);
    return ret;
}

int i2c_link_probe(uint8_t addr)
{
    uint8_t dummy = 0;
    if (!s_i2c) return -1;
    int ret = i2c_write_blocking(s_i2c, addr, &dummy, 0, false);
    LOGI("[I2C] probe 0x%02X -> %d", addr, ret);
    return ret;
}
