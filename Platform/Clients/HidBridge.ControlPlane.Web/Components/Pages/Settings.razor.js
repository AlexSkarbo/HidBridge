function safeParse(json) {
  if (!json) {
    return null;
  }

  try {
    return JSON.parse(json);
  } catch {
    return null;
  }
}

export function loadAgentProfile(storageKey) {
  try {
    const key = (storageKey || "").trim();
    if (!key) {
      return null;
    }

    const raw = window.localStorage.getItem(key);
    return safeParse(raw);
  } catch {
    return null;
  }
}

export function saveAgentProfile(storageKey, profile) {
  try {
    const key = (storageKey || "").trim();
    if (!key) {
      return false;
    }

    const payload = JSON.stringify(profile || {});
    window.localStorage.setItem(key, payload);
    return true;
  } catch {
    return false;
  }
}

export async function copyTextToClipboard(value) {
  const text = (value || "").toString();
  if (!text) {
    return false;
  }

  try {
    if (navigator.clipboard && typeof navigator.clipboard.writeText === "function") {
      await navigator.clipboard.writeText(text);
      return true;
    }
  } catch {
    // fallback below
  }

  try {
    const node = document.createElement("textarea");
    node.value = text;
    node.style.position = "fixed";
    node.style.left = "-9999px";
    document.body.appendChild(node);
    node.focus();
    node.select();
    const copied = document.execCommand("copy");
    document.body.removeChild(node);
    return !!copied;
  } catch {
    return false;
  }
}
