let dotNetRef = null;
let liveControlEnabled = false;
let pointerLockLocked = false;
let pending = [];
let flushTimer = null;
const flushDelayMs = 16;
let captureElement = null;
const targetElement = () => captureElement || document.getElementById("live-input-capture-zone") || document.body;
const keyCodeToUsage = new Map([
  ["KeyA", 0x04], ["KeyB", 0x05], ["KeyC", 0x06], ["KeyD", 0x07], ["KeyE", 0x08], ["KeyF", 0x09], ["KeyG", 0x0A], ["KeyH", 0x0B],
  ["KeyI", 0x0C], ["KeyJ", 0x0D], ["KeyK", 0x0E], ["KeyL", 0x0F], ["KeyM", 0x10], ["KeyN", 0x11], ["KeyO", 0x12], ["KeyP", 0x13],
  ["KeyQ", 0x14], ["KeyR", 0x15], ["KeyS", 0x16], ["KeyT", 0x17], ["KeyU", 0x18], ["KeyV", 0x19], ["KeyW", 0x1A], ["KeyX", 0x1B],
  ["KeyY", 0x1C], ["KeyZ", 0x1D],
  ["Digit1", 0x1E], ["Digit2", 0x1F], ["Digit3", 0x20], ["Digit4", 0x21], ["Digit5", 0x22], ["Digit6", 0x23], ["Digit7", 0x24], ["Digit8", 0x25], ["Digit9", 0x26], ["Digit0", 0x27],
  ["Enter", 0x28], ["Escape", 0x29], ["Backspace", 0x2A], ["Tab", 0x2B], ["Space", 0x2C],
  ["Minus", 0x2D], ["Equal", 0x2E], ["BracketLeft", 0x2F], ["BracketRight", 0x30], ["Backslash", 0x31],
  ["Semicolon", 0x33], ["Quote", 0x34], ["Backquote", 0x35], ["Comma", 0x36], ["Period", 0x37], ["Slash", 0x38],
  ["ArrowRight", 0x4F], ["ArrowLeft", 0x50], ["ArrowDown", 0x51], ["ArrowUp", 0x52],
  ["Delete", 0x4C], ["Home", 0x4A], ["End", 0x4D], ["PageUp", 0x4B], ["PageDown", 0x4E],
  ["F1", 0x3A], ["F2", 0x3B], ["F3", 0x3C], ["F4", 0x3D], ["F5", 0x3E], ["F6", 0x3F], ["F7", 0x40], ["F8", 0x41], ["F9", 0x42], ["F10", 0x43], ["F11", 0x44], ["F12", 0x45],
]);

function hasCaptureFocus() {
  const element = targetElement();
  if (!element) {
    return false;
  }
  const active = document.activeElement;
  return !!active && (active === element || element.contains(active));
}

function isEventWithinCapture(event) {
  const element = targetElement();
  if (!element || !event || !event.target) {
    return false;
  }
  return event.target === element || element.contains(event.target);
}

function push(action, args) {
  if (!liveControlEnabled || !dotNetRef) {
    return;
  }
  pending.push({ action, args });
  if (flushTimer) {
    return;
  }
  flushTimer = window.setTimeout(flush, flushDelayMs);
}

async function flush() {
  if (flushTimer) {
    clearTimeout(flushTimer);
    flushTimer = null;
  }
  if (!dotNetRef || pending.length === 0) {
    pending = [];
    return;
  }
  const batch = pending;
  pending = [];
  try {
    await dotNetRef.invokeMethodAsync("OnLiveInputBatch", batch);
  } catch {
    // Keep UI responsive even if dotnet callback fails.
  }
}

function parseButton(button) {
  switch (button) {
    case 0: return "left";
    case 1: return "middle";
    case 2: return "right";
    default: return "left";
  }
}

function getModifierMask(event) {
  return (event.ctrlKey ? 1 : 0)
    + (event.shiftKey ? 2 : 0)
    + (event.altKey ? 4 : 0)
    + (event.metaKey ? 8 : 0);
}

function resolveUsage(event) {
  if (!event || typeof event.code !== "string") {
    return 0;
  }
  return keyCodeToUsage.get(event.code) || 0;
}

function isPlainTextKey(event) {
  if (!event || typeof event.key !== "string") {
    return false;
  }
  if (event.ctrlKey || event.altKey || event.metaKey) {
    return false;
  }
  return event.key.length === 1;
}

function onKeyDown(event) {
  if (!liveControlEnabled) {
    return;
  }
  if (!pointerLockLocked && !hasCaptureFocus()) {
    return;
  }
  if (event.repeat) {
    return;
  }
  event.preventDefault();
  const usage = resolveUsage(event);
  if (usage > 0) {
    push("keyboard.press", {
      usage,
      modifiers: getModifierMask(event),
    });
    return;
  }
  if (isPlainTextKey(event)) {
    push("keyboard.text", { text: event.key });
  }
}

function onKeyUp(event) {
  if (!liveControlEnabled) {
    return;
  }
  if (!pointerLockLocked && !hasCaptureFocus()) {
    return;
  }
  event.preventDefault();
  push("keyboard.reset", {});
}

function onMouseMove(event) {
  if (!liveControlEnabled) {
    return;
  }
  if (!pointerLockLocked && !isEventWithinCapture(event)) {
    return;
  }
  const dx = Number(event.movementX || 0);
  const dy = Number(event.movementY || 0);
  if (dx === 0 && dy === 0) {
    return;
  }
  push("mouse.move", { dx, dy });
}

function onMouseDown(event) {
  if (!liveControlEnabled) {
    return;
  }
  if (!pointerLockLocked && !isEventWithinCapture(event)) {
    return;
  }
  const element = targetElement();
  if (element && typeof element.focus === "function") {
    try {
      element.focus({ preventScroll: true });
    } catch {
      element.focus();
    }
  }
  event.preventDefault();
  push("mouse.button", { button: parseButton(event.button), down: true });
}

function onMouseUp(event) {
  if (!liveControlEnabled) {
    return;
  }
  if (!pointerLockLocked && !isEventWithinCapture(event)) {
    return;
  }
  event.preventDefault();
  push("mouse.button", { button: parseButton(event.button), down: false });
}

function onWheel(event) {
  if (!liveControlEnabled) {
    return;
  }
  if (!pointerLockLocked && !isEventWithinCapture(event)) {
    return;
  }
  event.preventDefault();
  const delta = event.deltaY > 0 ? 1 : (event.deltaY < 0 ? -1 : 0);
  if (delta !== 0) {
    push("mouse.wheel", { delta });
  }
}

function onContextMenu(event) {
  if (liveControlEnabled && (pointerLockLocked || isEventWithinCapture(event))) {
    event.preventDefault();
  }
}

function onBlurOrHidden() {
  if (!liveControlEnabled) {
    return;
  }
  push("keyboard.reset", {});
  push("mouse.button", { button: "left", down: false });
  push("mouse.button", { button: "right", down: false });
  push("mouse.button", { button: "middle", down: false });
  flush();
}

async function onPointerLockChange() {
  const locked = document.pointerLockElement === targetElement();
  pointerLockLocked = locked;
  if (dotNetRef) {
    try {
      await dotNetRef.invokeMethodAsync("OnPointerLockChanged", locked);
    } catch {
      // ignore
    }
  }
}

export function initializeLiveInput(dotNetObjectRef) {
  dotNetRef = dotNetObjectRef;
  captureElement = document.getElementById("live-input-capture-zone") || document.body;
  if (captureElement && !captureElement.hasAttribute("tabindex")) {
    captureElement.setAttribute("tabindex", "0");
  }
  window.addEventListener("keydown", onKeyDown, { passive: false });
  window.addEventListener("keyup", onKeyUp, { passive: false });
  window.addEventListener("mousemove", onMouseMove, { passive: true });
  window.addEventListener("mousedown", onMouseDown, { passive: false });
  window.addEventListener("mouseup", onMouseUp, { passive: false });
  window.addEventListener("wheel", onWheel, { passive: false });
  window.addEventListener("contextmenu", onContextMenu, { passive: false });
  window.addEventListener("blur", onBlurOrHidden, { passive: true });
  document.addEventListener("visibilitychange", onBlurOrHidden, { passive: true });
  document.addEventListener("pointerlockchange", onPointerLockChange, { passive: true });
}

export function setLiveControlEnabled(enabled) {
  liveControlEnabled = !!enabled;
  if (liveControlEnabled) {
    focusCaptureZone();
  } else {
    onBlurOrHidden();
  }
}

export function focusCaptureZone() {
  const element = targetElement();
  if (!element || typeof element.focus !== "function") {
    return false;
  }
  try {
    element.focus({ preventScroll: true });
  } catch {
    element.focus();
  }
  return hasCaptureFocus();
}

export async function requestPointerLock() {
  if (!liveControlEnabled) {
    return false;
  }
  const element = targetElement();
  if (!element || !element.requestPointerLock) {
    return false;
  }
  await element.requestPointerLock();
  return true;
}

export function exitPointerLock() {
  if (document.exitPointerLock) {
    document.exitPointerLock();
  }
}

export function disposeLiveInput() {
  liveControlEnabled = false;
  dotNetRef = null;
  captureElement = null;
  pointerLockLocked = false;
  pending = [];
  if (flushTimer) {
    clearTimeout(flushTimer);
    flushTimer = null;
  }
  window.removeEventListener("keydown", onKeyDown);
  window.removeEventListener("keyup", onKeyUp);
  window.removeEventListener("mousemove", onMouseMove);
  window.removeEventListener("mousedown", onMouseDown);
  window.removeEventListener("mouseup", onMouseUp);
  window.removeEventListener("wheel", onWheel);
  window.removeEventListener("contextmenu", onContextMenu);
  window.removeEventListener("blur", onBlurOrHidden);
  document.removeEventListener("visibilitychange", onBlurOrHidden);
  document.removeEventListener("pointerlockchange", onPointerLockChange);
}
