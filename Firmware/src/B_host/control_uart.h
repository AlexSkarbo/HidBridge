#pragma once

// External control UART for injecting HID input reports via B_host.
// Protocol: SLIP framed commands on `PROXY_CTRL_UART_*` (see common/proxy_config.h).
//
// Frame format (payload inside SLIP):
//   cmd=0x01 (INJECT_REPORT):
//     [0]=0x01
//     [1]=itf_sel (0..CFG_TUH_HID-1, 0xFF=mouse, 0xFE=keyboard)
//     [2]=report_len (N)
//     [3..]=report bytes (N)
//
// Notes:
// - No responses/ACKs are sent (one-way control).
// - SLIP markers: END=0xC0, ESC=0xDB, ESC_END=0xDC, ESC_ESC=0xDD.

void control_uart_init(void);
void control_uart_task(void);

