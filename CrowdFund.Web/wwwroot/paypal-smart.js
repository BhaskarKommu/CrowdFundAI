window.crowdFundPayPal = {
  async loadSdk(config) {
    if (window.paypal) return window.paypal;
    const params = new URLSearchParams({
      'client-id': config.clientId || 'sb',
      currency: config.currency || 'USD',
      intent: config.intent || 'capture',
      components: config.components || 'buttons,messages'
    });
    if (config.enableFunding) params.set('enable-funding', config.enableFunding);
    if (config.disableFunding) params.set('disable-funding', config.disableFunding);
    await new Promise((resolve, reject) => {
      const s = document.createElement('script');
      s.src = `https://www.paypal.com/sdk/js?${params}`;
      s.onload = resolve;
      s.onerror = reject;
      document.head.appendChild(s);
    });
    return window.paypal;
  },

  async renderButtons(elementId, opts) {
    const paypal = await this.loadSdk(opts.config);
    const el = document.getElementById(elementId);
    if (!el) return;
    el.innerHTML = '';
    return paypal.Buttons({
      style: {
        layout: opts.config.smartButtons?.layout || 'vertical',
        color: opts.config.smartButtons?.color || 'gold',
        shape: opts.config.smartButtons?.shape || 'rect',
        label: opts.config.smartButtons?.label || 'paypal'
      },
      createOrder: async () => {
        const r = await fetch(opts.createOrderUrl, {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify(opts.createOrderBody())
        });
        if (!r.ok) throw new Error(await r.text());
        const data = await r.json();
        window.__cfLastDonationId = data.donationId;
        return data.orderId;
      },
      onApprove: async (data) => {
        const r = await fetch(opts.captureUrl, {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({
            orderId: data.orderID,
            donationId: window.__cfLastDonationId
          })
        });
        const result = await r.json();
        if (opts.onCaptured) opts.onCaptured(result);
      },
      onError: (err) => {
        if (opts.onError) opts.onError(String(err));
      }
    }).render('#' + elementId);
  }
};
