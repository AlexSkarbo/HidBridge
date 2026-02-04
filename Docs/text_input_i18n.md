# Text Input & I18N (HID-Only)

HidBridge **does not install any software on the controlled device**. Text input is implemented by sending **HID keyboard key presses** (physical key usages + modifiers).

This has an important consequence:

- HID can only "press keys".
- The **operating system** on the controlled device decides which **Unicode characters** those keys produce based on:
  - active keyboard layout(s)
  - IME (for CJK)
  - application context (composition, dead keys, candidates, etc.)

Because of this, **universal "type any Unicode text" is not possible** in a portable, cross-OS way using HID alone.

## Supported Modes

### 1) ASCII typing (default)

This mode maps characters through a fixed ASCII table:

- supported: letters `A-Z`, `a-z`, digits, basic punctuation, space, tab, enter
- unsupported: anything outside ASCII (e.g. Cyrillic, CJK, Arabic, Hebrew)

If an unsupported char is encountered, the server returns:

- WS: `{"ok":false,"type":"keyboard.text","error":"unsupported_char_XXXX"}`
- REST: `{"ok":false,"error":"Unsupported char: U+XXXX"}`

### 2) Layout-dependent typing (best-effort)

For some languages, we can map a character to a **physical key** that would produce that character under a specific OS keyboard layout.

Currently implemented layouts:

- `layout = "uk"`: Ukrainian (best-effort)
- `layout = "ru"`: Russian (best-effort)

This works only if the controlled device is already configured to use that layout **and it is active** at the moment of typing.

Example: `keyboard.text` with layout

```json
{ "type": "keyboard.text", "text": "Привіт", "layout": "uk", "itfSel": 2 }
```

If the active layout on the controlled device is not Ukrainian, the output will be incorrect (typically Latin).

## How To Add More Languages (No Agent)

If you still want additional languages without installing an agent, the only feasible approach is:

1. Decide the **target OS** and **keyboard layout / IME** you will rely on.
2. Implement a mapping from Unicode characters to physical key usages for that layout.
3. Add a new `layout` id and mapping table in:
   - `Tools/Shared/HidControl.Core/HidReports.cs` (`TryMapTextToHidKey`)
4. Send the desired layout from the client:
   - WS `keyboard.text`: include `"layout":"..."` (see below)

Notes:

- This scales poorly: every OS/layout/IME combination can differ.
- CJK IME input usually needs composition + candidate selection, which is not covered by simple key mapping.

## WS Contract: `keyboard.text`

Fields:

- `type`: `"keyboard.text"`
- `text`: string
- `layout`: optional string, e.g. `"ascii" | "uk" | "ru"`
- `itfSel`: optional byte

