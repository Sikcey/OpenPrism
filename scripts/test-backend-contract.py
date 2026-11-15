"""Read-only unprivileged contract checks; never install or connect."""
import json
from pathlib import Path
import subprocess
import sys

root = Path(__file__).resolve().parents[1]
exe = Path(sys.argv[1]).resolve() if len(sys.argv) > 1 else root / 'artifacts/WGTun/backend/WGTun.Service.exe'
def run(command, value=None):
    p = subprocess.run([str(exe), command], input=json.dumps(value).encode() if value is not None else b'',
        capture_output=True, timeout=20, creationflags=subprocess.CREATE_NO_WINDOW)
    return json.loads(p.stdout)

scan = run('scan')
assert isinstance(scan.get('applications'), list) and isinstance(scan.get('serviceInstalled'), bool)
assert isinstance(scan.get('serviceRunning'), bool) and isinstance(scan.get('serviceReady'), bool)
assert not scan['serviceReady'] or (scan['serviceInstalled'] and scan['serviceRunning'] and scan['dependencyReady'])
assert {component['name'] for component in scan['components']} == {'WGTun', 'sing-box', 'ByeDPI'}
assert all(isinstance(component['known'], bool) and isinstance(component['running'], bool) for component in scan['components'])
settings = run('load-settings')
assert isinstance(settings.get('rules'), list) and isinstance(settings.get('profile'), int)
assert settings.get('ruleSchemaVersion', 0) >= 1
assert settings.get('language') in ('tr', 'en')
assert all(app.get('folderPath') and isinstance(app.get('running'), bool) for app in scan['applications'])
assert not any(Path(app.get('path', '')).name.lower() == 'curl.exe' for app in scan['applications'])
assert isinstance(run('status').get('connected'), bool)
assert run('validate-rule', {'type': 1, 'value': '192.0.2.1'})['success']
assert not run('validate-rule', {'type': 1, 'value': 'invalid'})['success']
assert not run('unknown')['success']
print(json.dumps({'passed': 7, 'serviceInstalled': scan['serviceInstalled'],
    'dependencyReady': scan['dependencyReady'], 'detectedApplications': len(scan['applications']),
    'liveTunnelTested': False, 'elevationRequested': False}))
