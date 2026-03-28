let dotNetRef = null;
let liveControlEnabled = false;
let pointerLockLocked = false;
let pending = [];
let flushTimer = null;
const flushDelayMs = 40;
let captureElement = null;
const targetElement = () => captureElement || document.getElementById("live-input-capture-zone") || document.body;

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
  push("keyboard.press", {
    usage: event.keyCode & 0xff,
    modifiers: (event.ctrlKey ? 1 : 0) + (event.shiftKey ? 2 : 0) + (event.altKey ? 4 : 0) + (event.metaKey ? 8 : 0),
  });
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
