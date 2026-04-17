"""Dump PS[19] input signature from the cached patched skin shpk so we can see
which v<N> register holds UV (needed for Ripple feature)."""
import os, sys, ctypes, struct, glob
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from parse_shpk import parse_shpk


def d3d_disassemble(dxbc_bytes):
    d3d = ctypes.CDLL('D3DCompiler_47.dll')
    blob_ptr = ctypes.c_void_p()
    hr = d3d.D3DDisassemble(dxbc_bytes, len(dxbc_bytes), 0, None, ctypes.byref(blob_ptr))
    if hr != 0 or not blob_ptr.value:
        raise RuntimeError(f"D3DDisassemble failed hr=0x{hr:X}")

    class Vtbl(ctypes.Structure):
        _fields_ = [('QueryInterface', ctypes.c_void_p), ('AddRef', ctypes.c_void_p),
                    ('Release', ctypes.c_void_p), ('GetBufferPointer', ctypes.c_void_p),
                    ('GetBufferSize', ctypes.c_void_p)]
    blob = blob_ptr.value
    vtbl_addr = struct.unpack('<Q', ctypes.string_at(blob, 8))[0]
    vtbl = ctypes.cast(vtbl_addr, ctypes.POINTER(Vtbl))[0]
    GetPtr = ctypes.WINFUNCTYPE(ctypes.c_void_p, ctypes.c_void_p)
    GetSize = ctypes.WINFUNCTYPE(ctypes.c_size_t, ctypes.c_void_p)
    ptr = GetPtr(vtbl.GetBufferPointer)(blob)
    size = GetSize(vtbl.GetBufferSize)(blob)
    return ctypes.string_at(ptr, size).decode('utf-8', errors='replace')


candidates = glob.glob(os.path.expanduser("~/AppData/Roaming/XIVLauncherCN/pluginConfigs/SkinTattoo/preview/skin_ct_v*.shpk"))
candidates.sort()
if not candidates:
    print("No cached skin_ct_v*.shpk found  open the plugin once to generate one.")
    sys.exit(1)
path = candidates[-1]
print(f"Using: {path}\n")

shpk = parse_shpk(path)
ps_nodes = shpk.pixel_shaders
print(f"PS count: {len(ps_nodes)}")

ps19 = ps_nodes[19]
print(f"\nPS[19] blob len = {len(ps19.blob)} bytes\n")

dis = d3d_disassemble(ps19.blob)
lines = dis.splitlines()
# Print input signature + declarations (first ~60 lines)
for i, line in enumerate(lines[:80]):
    print(f"{i:3}: {line}")
