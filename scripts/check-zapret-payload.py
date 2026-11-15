"""Check imports of the pinned experimental winws payload without loading a driver."""
from pathlib import Path
import hashlib
import json
import os
import struct
import sys

def imports(path):
    data = path.read_bytes()
    pe = struct.unpack_from('<I', data, 0x3c)[0]
    if data[:2] != b'MZ' or data[pe:pe+4] != b'PE\0\0':
        raise ValueError(f'Invalid PE: {path}')
    count = struct.unpack_from('<H', data, pe+6)[0]
    optional_size = struct.unpack_from('<H', data, pe+20)[0]
    optional = pe+24
    magic = struct.unpack_from('<H', data, optional)[0]
    directory = optional+(112 if magic == 0x20b else 96)
    import_rva = struct.unpack_from('<I', data, directory+8)[0]
    sections = [struct.unpack_from('<8sIIII', data, optional+optional_size+40*i) for i in range(count)]
    def offset(rva):
        for _, virtual_size, virtual_address, raw_size, raw in sections:
            if virtual_address <= rva < virtual_address+max(virtual_size, raw_size):
                return raw+rva-virtual_address
        raise ValueError(f'Unmapped RVA: {rva:x}')
    result = []
    if not import_rva:
        return result
    entry = offset(import_rva)
    while any(data[entry:entry+20]):
        name_rva = struct.unpack_from('<I', data, entry+12)[0]
        name = offset(name_rva)
        result.append(data[name:data.index(0, name)].decode('ascii'))
        entry += 20
    return result

root = Path(sys.argv[1]).resolve()
system = Path(os.environ.get('WINDIR', 'C:/Windows'))/'System32'
records = []
missing = []
for path in sorted(root.iterdir()):
    if path.suffix.lower() not in ('.dll', '.exe'):
        continue
    dependencies = imports(path)
    unresolved = [name for name in dependencies if not (root/name).is_file()
                  and not (system/name).is_file() and not name.lower().startswith(('api-ms-', 'ext-ms-'))]
    missing.extend(unresolved)
    records.append({'file': path.name, 'sha256': hashlib.sha256(path.read_bytes()).hexdigest(),
                    'imports': dependencies, 'missing': unresolved})
print(json.dumps({'success': not missing, 'files': records}, indent=2))
sys.exit(bool(missing))
