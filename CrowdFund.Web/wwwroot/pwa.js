(function () {
  if (!("serviceWorker" in navigator)) return;
  window.addEventListener("load", function () {
    navigator.serviceWorker.register("/sw.js").catch(function (err) {
      console.warn("SW register failed", err);
    });
  });
  window.crowdfundSubscribePush = async function () {
    if (!("Notification" in window) || !("serviceWorker" in navigator)) return { ok: false, error: "unsupported" };
    const perm = await Notification.requestPermission();
    if (perm !== "granted") return { ok: false, error: "denied" };
    const reg = await navigator.serviceWorker.ready;
    // Production: use PushManager.subscribe with VAPID public key
    return { ok: true, message: "Permission granted. Store subscription via POST /api/push/subscribe when VAPID keys are configured." };
  };
})();
