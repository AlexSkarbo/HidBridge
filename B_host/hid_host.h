#ifndef HID_HOST_H_
#define HID_HOST_H_

#include <stdint.h>
#include <stdbool.h>
#include "tusb.h"
#include "logging.h"

void hid_host_init(void);
void hid_host_task(void);

#endif // HID_HOST_H_
