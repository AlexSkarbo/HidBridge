add_library(bridge_common STATIC
    proto_frame.c
    uart_transport.c
    logging.c
    crc16.c
    sha256.c
)

target_include_directories(bridge_common PUBLIC
    ${CMAKE_CURRENT_LIST_DIR}
)

target_link_libraries(bridge_common PUBLIC
    pico_stdlib
    hardware_uart
    hardware_gpio
)
