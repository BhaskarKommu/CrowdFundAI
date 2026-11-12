const CACHE = "crowdfund-ai-v1";
const ASSETS = ["/", "/manifest.webmanifest", "/favicon.png", "/app.css"];

self.addEventListener("install", (e) => {
  e.waitUntil(caches.open(CACHE).then((c) => c.addAll(ASSETS)).then(() => self.skipWaiting()));
});

self.addEventListener("activate", (e) => {
  e.waitUntil(self.clients.claim());
});

self.addEventListener("fetch", (e) => {
  if (e.request.method !== "GET") return;
  e.respondWith(
    caches.match(e.request).then((cached) => cached || fetch(e.request).catch(() => caches.match("/")))
  );
});

self.addEventListener("push", (e) => {
  let data = { title: "CrowdFund AI", body: "You have an update" };
  try { data = e.data ? e.data.json() : data; } catch {}
  e.waitUntil(self.registration.showNotification(data.title || "CrowdFund AI", {
    body: data.body,
    icon: "/favicon.png",
    data: data
  }));
});
