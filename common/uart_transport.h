#ifndef UART_TRANSPORT_H_
#define UART_TRANSPORT_H_

#include <stdint.h>
#include "pico/types.h"

#ifdef __cplusplus
extern "C" {
#endif

typedef enum
{
    TRANSPORT_ROLE_NONE   = 0,
    TRANSPORT_ROLE_HOST   = 1,
    TRANSPORT_ROLE_DEVICE = 2
} transport_role_t;

void uart_transport_init_host();

void uart_transport_init_device();

// Надіслати кадр (автоматичне SLIP-кадрування всередині).
int  uart_transport_send(const uint8_t* data, uint16_t len);
int  uart_transport_device_send(const uint8_t* data, uint16_t len);

// Прочитати один декодований SLIP-кадр; 0 якщо поки нема повного кадру.
int  uart_transport_recv_frame(uint8_t* data, uint16_t maxlen);

// Drop any unread bytes from RX FIFO (used to resync after protocol errors).
void uart_transport_flush_rx(void);

#ifdef __cplusplus
}
#endif

#endif // UART_TRANSPORT_H_
