window.hidBridgeTheme = (function () {
    const storageKey = "hidbridge-theme";
    const cookieKey = "hidbridge_theme";

    function getCookieValue(name) {
        try {
            const prefix = name + "=";
            const match = document.cookie
                .split(";")
                .map(function (part) { return part.trim(); })
                .find(function (part) { return part.startsWith(prefix); });
            return match ? decodeURIComponent(match.substring(prefix.length)) : null;
        }
        catch {
            return null;
        }
    }

    function safeGet() {
        try {
            const cookiePreference = getCookieValue(cookieKey);
            if (cookiePreference === "light" || cookiePreference === "dark") {
                return cookiePreference;
            }

            return window.localStorage.getItem(storageKey) || "auto";
        }
        catch {
            return "auto";
        }
    }

    function apply(theme) {
        const normalized = theme === "light" || theme === "dark" ? theme : "auto";
        document.documentElement.dataset.themePreference = normalized;

        if (normalized === "light" || normalized === "dark") {
            document.documentElement.dataset.theme = normalized;
            return normalized;
        }

        document.documentElement.removeAttribute("data-theme");
        return "auto";
    }

    function set(theme) {
        const normalized = theme === "light" || theme === "dark" ? theme : "auto";

        try {
            if (normalized === "auto") {
                window.localStorage.removeItem(storageKey);
            }
            else {
                window.localStorage.setItem(storageKey, normalized);
            }
        }
        catch {
            // Ignore storage failures and still apply the preference for the current document.
        }

        try {
            if (normalized === "auto") {
                document.cookie = cookieKey + "=; path=/; expires=Thu, 01 Jan 1970 00:00:00 GMT; SameSite=Lax";
            }
            else {
                document.cookie = cookieKey + "=" + encodeURIComponent(normalized) + "; path=/; max-age=31536000; SameSite=Lax";
            }
        }
        catch {
            // Ignore cookie failures and still apply the preference for the current document.
        }

        return apply(normalized);
    }

    const media = window.matchMedia ? window.matchMedia("(prefers-color-scheme: dark)") : null;
    if (media && typeof media.addEventListener === "function") {
        media.addEventListener("change", function () {
            if (safeGet() === "auto") {
                apply("auto");
            }
        });
    }

    return {
        getPreference: safeGet,
        apply: apply,
        set: set
    };
})();
