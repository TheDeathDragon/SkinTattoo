"""Read vanilla skin.shpk PS[19] disassembly from the reference txt (UTF-16LE)."""
import os
paths = [
    r"C:\Users\Shiro\Desktop\FF14Plugins\SkinTatoo\ShaderPatcher\reference\ps_019_EMISSIVE_disasm.txt",
    r"C:\Users\Shiro\Desktop\FF14Plugins\SkinTatoo\ShaderPatcher\reference\ps_019_PATCHED_disasm.txt",
]
for p in paths:
    if not os.path.exists(p):
        print(f"MISSING: {p}"); continue
    with open(p, 'rb') as f:
        raw = f.read()
    # Try various encodings
    for enc in ('utf-16-le', 'utf-16', 'utf-8', 'latin-1'):
        try:
            txt = raw.decode(enc)
            if 'Input signature' in txt or 'dcl_' in txt:
                print(f"=== {os.path.basename(p)} (encoding: {enc}) ===")
                # Print input signature block + first 40 lines of disassembly
                for i, line in enumerate(txt.splitlines()[:80]):
                    print(line)
                print()
                break
        except Exception:
            continue
    else:
        print(f"Could not decode {p}")
