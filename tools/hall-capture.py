"""Read-only inventory / concurrent capture of every MCHOSE HID collection.
Windows raw descriptors are reconstructed by hidapi from HID preparsed data.
No output or feature reports are written by this script.
"""
import argparse, collections, ctypes as C, json, pathlib, sys, threading, time
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[1] / 'work' / 'hidprobe'))
import hid

class Caps(C.Structure):
    _fields_ = [('Usage', C.c_ushort), ('UsagePage', C.c_ushort), ('InputReportByteLength', C.c_ushort),
                ('OutputReportByteLength', C.c_ushort), ('FeatureReportByteLength', C.c_ushort),
                ('Reserved', C.c_ushort * 17)] + [(n, C.c_ushort) for n in
                ['NumberLinkCollectionNodes','NumberInputButtonCaps','NumberInputValueCaps','NumberInputDataIndices',
                 'NumberOutputButtonCaps','NumberOutputValueCaps','NumberOutputDataIndices',
                 'NumberFeatureButtonCaps','NumberFeatureValueCaps','NumberFeatureDataIndices']]

def get_caps(path):
    k, h = C.WinDLL('kernel32', use_last_error=True), C.WinDLL('hid', use_last_error=True)
    k.CreateFileW.restype = C.c_void_p
    k.CreateFileW.argtypes = [C.c_wchar_p,C.c_uint32,C.c_uint32,C.c_void_p,C.c_uint32,C.c_uint32,C.c_void_p]
    k.CloseHandle.argtypes = [C.c_void_p]
    h.HidD_GetPreparsedData.argtypes = [C.c_void_p,C.POINTER(C.c_void_p)]
    h.HidD_FreePreparsedData.argtypes = [C.c_void_p]
    h.HidP_GetCaps.argtypes = [C.c_void_p,C.POINTER(Caps)]
    handle = k.CreateFileW(path, 0, 3, None, 3, 0, None)
    if handle == C.c_void_p(-1).value: return {'error': C.get_last_error()}
    pp = C.c_void_p()
    try:
        if not h.HidD_GetPreparsedData(handle, C.byref(pp)): return {'error': C.get_last_error()}
        caps = Caps(); h.HidP_GetCaps(pp,C.byref(caps))
        result = {n:getattr(caps,n) for n,t in caps._fields_ if n != 'Reserved'}
        # Both HIDP_BUTTON_CAPS and HIDP_VALUE_CAPS start with UsagePage (u16),
        # ReportID (u8); Windows structures are 72 bytes on x86 and x64.
        ids = {}
        for typ,prefix in enumerate(['Input','Output','Feature']):
            found = set()
            for kind in ['Button','Value']:
                length=C.c_ushort(result['Number'+prefix+kind+'Caps'])
                if not length.value: continue
                buf=C.create_string_buffer(72*length.value)
                fn=getattr(h,'HidP_Get'+kind+'Caps'); fn.argtypes=[C.c_int,C.c_void_p,C.POINTER(C.c_ushort),C.c_void_p]
                status=fn(typ,buf,C.byref(length),pp)
                if status >= 0: found.update(buf.raw[i*72+2] for i in range(length.value))
            ids[prefix]=sorted(found)
        result['report_ids']=ids
        return result
    finally:
        if pp: h.HidD_FreePreparsedData(pp)
        k.CloseHandle(handle)

def inventory():
    entries=[]
    for device in hid.enumerate(0x41e4,0x2120):
        entry={k:(v.decode(errors='replace') if isinstance(v,bytes) else v) for k,v in device.items()}
        entry.pop('serial_number',None)
        entry['caps']=get_caps(entry['path'])
        stream=hid.device()
        try:
            stream.open_path(device['path'])
            entry['reconstructed_descriptor_hex']=bytes(stream.get_report_descriptor()).hex(' ')
            entry['read_open']=True
        except Exception as ex: entry['read_error']=str(ex)
        finally: stream.close()
        entries.append(entry)
    return entries

def main():
    p=argparse.ArgumentParser(); p.add_argument('--seconds',type=int,default=0);p.add_argument('--out',default='work/hall/passive');args=p.parse_args()
    base=pathlib.Path(args.out);base.parent.mkdir(parents=True,exist_ok=True)
    entries=inventory();base.with_suffix('.inventory.json').write_text(json.dumps(entries,indent=2),encoding='utf8')
    print(json.dumps([{'interface':d['interface_number'],'usage_page':d['usage_page'],'usage':d['usage'],'caps':d['caps'],'read_open':d.get('read_open',False),'error':d.get('read_error')} for d in entries],indent=2),flush=True)
    if args.seconds<=0: return
    deadline=time.monotonic()+args.seconds;lock=threading.Lock();counts=collections.Counter();t0=time.perf_counter_ns()
    with base.with_suffix('.jsonl').open('w',encoding='utf8',buffering=1) as file:
        def worker(i,d):
            stream=hid.device()
            try:
                stream.open_path(d['path'].encode())
                while time.monotonic()<deadline:
                    data=stream.read(max(64,d['caps'].get('InputReportByteLength',64)),100)
                    if data:
                        packet={'us':(time.perf_counter_ns()-t0)//1000,'collection':i,'interface':d['interface_number'],'data':data,'hex':bytes(data).hex(' ')}
                        with lock: file.write(json.dumps(packet)+'\n');counts[i]+=1
            except Exception as ex:
                with lock: file.write(json.dumps({'collection':i,'error':str(ex)})+'\n')
            finally: stream.close()
        threads=[threading.Thread(target=worker,args=(i,d),daemon=True) for i,d in enumerate(entries)]
        for t in threads:t.start()
        print('CAPTURE_READY',flush=True)
        for t in threads:t.join()
    print('CAPTURE_DONE '+json.dumps(dict(counts)),flush=True)

if __name__=='__main__':main()
