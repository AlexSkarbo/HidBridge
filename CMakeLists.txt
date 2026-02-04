# == DO NOT EDIT THE FOLLOWING LINES for the Raspberry Pi Pico VS Code Extension to work ==
if(WIN32)
    set(USERHOME $ENV{USERPROFILE})
else()
    set(USERHOME $ENV{HOME})
endif()
set(sdkVersion 2.2.0)
set(toolchainVersion 14_2_Rel1)
set(picotoolVersion 2.2.0-a4)
set(picoVscode ${USERHOME}/.pico-sdk/cmake/pico-vscode.cmake)
if (EXISTS ${picoVscode})
    include(${picoVscode})
endif()
# ====================================================================================
set(PICO_BOARD waveshare_rp2040_zero CACHE STRING "Board type")

cmake_minimum_required(VERSION 3.24)

include($ENV{PICO_SDK_PATH}/external/pico_sdk_import.cmake)

project(pico_hid_bridge C CXX ASM)
set(CMAKE_C_STANDARD 11)
set(CMAKE_CXX_STANDARD 17)

pico_sdk_init()

add_subdirectory(Firmware/common)

# ---- A_device (USB device endpoint) ----
add_executable(A_device
    Firmware/A_device/main.c
    Firmware/A_device/hid_proxy_dev.c
    Firmware/A_device/usb_descriptors.c
    Firmware/A_device/remote_storage.c
    Firmware/common/uart_transport.c
    Firmware/common/proto_frame.c
    Firmware/common/crc16.c 
)
target_link_libraries(A_device PRIVATE
    pico_stdlib
    tinyusb_device
    tinyusb_board
    hardware_uart
    bridge_common
)
target_include_directories(A_device PRIVATE
    ${CMAKE_CURRENT_LIST_DIR}/Firmware/A_device
    ${CMAKE_CURRENT_LIST_DIR}/Firmware/common
)
pico_enable_stdio_usb(A_device 0)
pico_enable_stdio_uart(A_device 1)

pico_add_extra_outputs(A_device)

# ---- B_host (USB host endpoint) ----
add_executable(B_host
    Firmware/B_host/main.c
    Firmware/B_host/hid_host.c
    Firmware/B_host/hid_proxy_host.c
    Firmware/B_host/control_uart.c
    Firmware/B_host/descriptor_logger.c
    Firmware/B_host/string_manager.c
    Firmware/common/proto_frame.c
    Firmware/common/uart_transport.c
    Firmware/common/crc16.c
)
target_include_directories(B_host PRIVATE
    ${CMAKE_CURRENT_LIST_DIR}/Firmware/B_host
    ${CMAKE_CURRENT_LIST_DIR}/Firmware/common
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
