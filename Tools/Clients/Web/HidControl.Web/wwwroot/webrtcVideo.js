(function () {
  "use strict";

  window.hidbridge = window.hidbridge || {};

  function createClient(opts) {
    if (!window.hidbridge.webrtcControl || typeof window.hidbridge.webrtcControl.createClient !== "function") {
      throw new Error("webrtc_control_module_missing");
    }

    const cfg = Object.assign({}, opts || {});
    if (!cfg.room || !String(cfg.room).trim()) cfg.room = "video";
    return window.hidbridge.webrtcControl.createClient(cfg);
  }

  window.hidbridge.webrtcVideo = {
    createClient
  };
})();
