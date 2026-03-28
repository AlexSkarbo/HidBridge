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
