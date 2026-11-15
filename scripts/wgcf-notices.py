"""Collect dependency notices without executing wgcf. Requires Python 3 and Go metadata reader."""
import concurrent.futures
import hashlib
import io
import json
from pathlib import Path
import subprocess
import urllib.request
import zipfile

ROOT = Path(__file__).resolve().parents[1] / 'dependencies' / 'wgcf'
EXPECTED = 'e222fd9443d7cb3264d825a3e86967797fc9c938507c5d93e3cc21e4aed11dcc'
assert hashlib.sha256((ROOT / 'wgcf.exe').read_bytes()).hexdigest() == EXPECTED
metadata = subprocess.check_output(['go', 'version', '-m', str(ROOT / 'wgcf.exe')], text=True)
(ROOT / 'build-info.txt').write_text(metadata, encoding='utf-8')
modules = [line.split()[1:3] for line in metadata.splitlines() if line.strip().startswith('dep\t')]

def collect(module):
    name, version = module
    escaped = ''.join('!' + c.lower() if c.isupper() else c for c in name)
    url = f'https://proxy.golang.org/{escaped}/@v/{version}.zip'
    data = urllib.request.urlopen(url, timeout=60).read()
    archive = zipfile.ZipFile(io.BytesIO(data))
    prefix = f'{name}@{version}/'
    found = []
    for entry in archive.namelist():
        relative = entry[len(prefix):]
        leaf = relative.rsplit('/', 1)[-1].upper()
        if leaf.startswith(('LICENSE', 'LICENCE', 'COPYING', 'NOTICE', 'COPYRIGHT', 'PATENTS')) and not entry.endswith('/'):
            dest = ROOT / 'licenses' / name.replace('/', '_') / relative
            dest.parent.mkdir(parents=True, exist_ok=True)
            dest.write_bytes(archive.read(entry))
            found.append(relative)
    if not found:
        raise RuntimeError('Missing license for ' + name)
    return {'module': name, 'version': version, 'source': url, 'archiveSha256': hashlib.sha256(data).hexdigest(), 'notices': found}

with concurrent.futures.ThreadPoolExecutor(max_workers=6) as pool:
    manifest = list(pool.map(collect, modules))
# Go standard library license; runtime source is identified by the binary's build metadata.
go_license = urllib.request.urlopen('https://raw.githubusercontent.com/golang/go/go1.27.0/LICENSE', timeout=30).read()
(ROOT / 'GO-LICENSE').write_bytes(go_license)
(ROOT / 'dependency-notices.json').write_text(json.dumps(manifest, indent=2) + '\n', encoding='utf-8')
print(f'Collected notices for {len(manifest)} modules plus Go runtime; wgcf was not executed.')
