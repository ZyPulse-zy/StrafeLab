// Run in the official M HUB frame after WebHID permission has been granted.
// Changes only the documented debug bit; never starts hardware calibration.
(async () => {
  if (window.StrafeLabDebug) return {alreadyPrepared:true};
  const device = (await navigator.hid.getDevices()).find(d => d.vendorId === 0x41e4 && d.productId === 0x2120 && d.collections.some(c => c.usagePage === 1 && c.usage === 0));
  if (!device) throw Error('Expected Ace68 Air-II collection is not authorized');
  if (!device.opened) await device.open();
  async function command(cmd, offset, data, size) {
    const p = new Uint8Array(64);p[0]=0x55;p[1]=cmd;p[4]=size;p[5]=offset&255;p[6]=offset>>8;
    if(data) p.set(data,8);p[3]=p.slice(4).reduce((a,b)=>a+b,0)&255;
    return await new Promise((resolve,reject)=>{
      const timer=setTimeout(()=>{device.removeEventListener('inputreport',on);reject(Error('HID reply timeout'));},2000);
      function on(e) {
        const r=new Uint8Array(e.data.buffer,e.data.byteOffset,e.data.byteLength);
        if(e.reportId!==0||r[0]!==0xaa||r[1]!==cmd||r[5]!==p[5]||r[6]!==p[6])return;
        clearTimeout(timer);device.removeEventListener('inputreport',on);
        if(r[4]>56)reject(Error('Invalid response length'));else resolve(Array.from(r.slice(8,8+r[4])));
      }
      device.addEventListener('inputreport',on);device.sendReport(0,p).catch(e=>{clearTimeout(timer);device.removeEventListener('inputreport',on);reject(e);});
    });
  }
  const base=await command(4,0,null,56),profile=base[0];
  if(profile>2)throw Error('Unexpected active profile');
  const read=async()=>[...await command(5,64*profile,null,56),...await command(5,64*profile+56,null,8)];
  const write=async a=>{await command(6,64*profile,a.slice(0,56),56);await command(6,64*profile+56,a.slice(56),8);};
  const original=await read();if(original.length!==64)throw Error('Short config');
  let timer;
  const state=window.StrafeLabDebug={profile,original,restored:false,enabled:false,
    async restore(){
      clearTimeout(timer);
      const current=await read();current[7]=(current[7]&~8)|(original[7]&8);await write(current);
      const verified=await read();state.restored=verified.every((b,i)=>b===current[i]);state.enabled=false;
      state.restoreVerification={verified:state.restored,allOriginal:verified.every((b,i)=>b===original[i])};
      return state.restoreVerification;
    },
    async start(){
      timer=setTimeout(()=>state.restore().catch(e=>state.restoreError=String(e)),180000);
      const current=await read();if(current.some((b,i)=>b!==original[i]))throw Error('Config changed since backup');
      current[7]|=8;await write(current);
      const verified=await read();state.enabled=verified.every((b,i)=>b===current[i]);
      if(!state.enabled)throw Error('Debug config verification failed');
      window.StrafeLabHidCapture?.mark('debug-enabled');return {enabled:true,profile};
    }
  };
  return {profile,original,debugWasEnabled:!!(original[7]&8)};
})();
