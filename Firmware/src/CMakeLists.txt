add_subdirectory(common)

# ---- A_device (USB device endpoint) ----
add_executable(A_device
    A_device/main.c
    A_device/hid_proxy_dev.c
    A_device/usb_descriptors.c
    A_device/remote_storage.c
    common/uart_transport.c
    common/proto_frame.c
    common/crc16.c
)
target_link_libraries(A_device PRIVATE
    pico_stdlib
    tinyusb_device
    tinyusb_board
    hardware_uart
    bridge_common
)
target_include_directories(A_device PRIVATE
    ${CMAKE_CURRENT_LIST_DIR}/A_device
    ${CMAKE_CURRENT_LIST_DIR}/common
)
pico_enable_stdio_usb(A_device 0)
pico_enable_stdio_uart(A_device 1)

pico_add_extra_outputs(A_device)

# ---- B_host (USB host endpoint) ----
add_executable(B_host
    B_host/main.c
    B_host/hid_host.c
    B_host/hid_proxy_host.c
    B_host/control_uart.c
    B_host/descriptor_logger.c
    B_host/string_manager.c
    common/proto_frame.c
    common/uart_transport.c
    common/crc16.c
)
target_include_directories(B_host PRIVATE
    ${CMAKE_CURRENT_LIST_DIR}/B_host
    ${CMAKE_CURRENT_LIST_DIR}/common
)
target_link_libraries(B_host PRIVATE
    pico_stdlib
    hardware_uart
    tinyusb_host
    tinyusb_board
    bridge_common
)

pico_enable_stdio_usb(B_host 0)
pico_enable_stdio_uart(B_host 1)

pico_add_extra_outputs(B_host)

