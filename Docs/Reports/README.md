# HID Proxy Documentation

## Control Flow Diagram

A detailed sequence of descriptor capture, TinyUSB attach, READY/STRING_REQ handling, and DEVICE_RESET is available in Docs/Reports/diagrams/control_flow.mmd (mermaid format). Render it via any Mermaid viewer or GitHub preview.

Key highlights:
- B_host captures all descriptors, then explicitly sends PF_CTRL_DEVICE_RESET.
- A_device starts TinyUSB only after the reset command and signals PF_CTRL_READY.
- STRING_REQ requests are serialized via UART; idx>2 is deliberately stalled to avoid Windows probing.

## Test Harness
- Build: gcc Firmware/tests/string_manager/string_manager_harness.c Firmware/B_host/string_manager.c -IFirmware/tests/string_manager -IFirmware/B_host -IFirmware/common -o string_manager_harness (or clang).
- Run: ./string_manager_harness – harness prints PASS/FAIL for cached flow, extra fetch, idx>2 fallback, timeout, cache-eviction.

For state-machine notes see control_flow.md. For roadmap/details check TODO_4.4.1.md.
