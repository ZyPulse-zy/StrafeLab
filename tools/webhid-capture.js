// DevTools/Playwright instrumentation. Records only this page's WebHID calls.
(() => {
  if (window.StrafeLabHidCapture || !window.HIDDevice) return;
  const entries = [], devices = new Set();
  const bytes = value => value == null ? [] : Array.from(new Uint8Array(value.buffer || value, value.byteOffset || 0, value.byteLength));
  const log = (device, operation, reportId, data, extra = {}) => {
    if (entries.length >= 250000) entries.splice(0, 1000);
    entries.push({us: Math.round(performance.now() * 1000), utc: Date.now(), operation,
      vid: device.vendorId, pid: device.productId, product: device.productName,
      reportId, data: bytes(data), ...extra});
  };
  const attach = device => {
    if (devices.has(device)) return;
    devices.add(device);
    log(device, 'device', null, null, {collections: device.collections});
    device.addEventListener('inputreport', event => log(device, 'inputreport', event.reportId, event.data));
  };
  for (const name of ['open','close','sendReport','sendFeatureReport','receiveFeatureReport']) {
    const original = HIDDevice.prototype[name];
    HIDDevice.prototype[name] = async function (...args) {
      attach(this);
      log(this, name, args[0] ?? null, args[1] ?? null);
      try {
        const result = await original.apply(this, args);
        if (name === 'receiveFeatureReport') log(this, name + ':result', args[0], result);
        return result;
      } catch (error) {log(this, name + ':error', args[0] ?? null, null, {error: String(error)});throw error;}
    };
  }
  navigator.hid.getDevices().then(ds => ds.forEach(attach));
  navigator.hid.addEventListener('connect', event => attach(event.device));
  window.StrafeLabHidCapture = {entries, mark: label => entries.push({us:Math.round(performance.now()*1000),utc:Date.now(),operation:'mark',label}),
    summary: () => ({count:entries.length, devices:devices.size, operations:entries.reduce((a,x)=>(a[x.operation]=(a[x.operation]||0)+1,a),{})})};
})();
