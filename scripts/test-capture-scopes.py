"""Live TCP-path checks using an owned probe and explicit exclusions for existing applications."""
import ctypes
import json
import os
from pathlib import Path
import shutil
import struct
import subprocess
import time

root = Path(__file__).resolve().parent.parent
backend = str(root / "artifacts/OpenPrism/backend/WGTun.Service.exe")
probe_dir = root / "artifacts/network-probe"
probe_dir.mkdir(exist_ok=True)
probe = probe_dir / "OpenPrismProbe.exe"
shutil.copyfile(Path(os.environ["SystemRoot"]) / "System32/curl.exe", probe)

def invoke(action, payload=None):
    output = subprocess.run([backend, action], input=json.dumps(payload) if payload else None, capture_output=True, text=True, timeout=30)
    return json.loads(output.stdout)

def proxy_connections():
    size = ctypes.c_ulong()
    query = ctypes.WinDLL("iphlpapi").GetExtendedTcpTable
    query(None, ctypes.byref(size), False, 2, 5, 0)
    buffer = ctypes.create_string_buffer(size.value)
    if query(buffer, ctypes.byref(size), False, 2, 5, 0):
        raise RuntimeError("TCP table unavailable")
    processes = subprocess.run(["powershell", "-NoProfile", "-Command", "Get-Process ciadpi -ErrorAction SilentlyContinue | ForEach-Object { $_.Id }"], capture_output=True, text=True)
    owners = {int(line) for line in processes.stdout.splitlines() if line.strip().isdigit()}
    count = struct.unpack_from("<I", buffer)[0]
    return sum(1 for i in range(count) if struct.unpack_from("<6I", buffer, 4 + i * 24)[5] in owners
               and struct.unpack_from("<6I", buffer, 4 + i * 24)[0] == 5
               and int.from_bytes(struct.pack("<I", struct.unpack_from("<6I", buffer, 4 + i * 24)[4])[:2], "big") == 443)

paths = subprocess.run(["powershell", "-NoProfile", "-Command", "@(Get-CimInstance Win32_Process | Where-Object ExecutablePath | Select-Object -ExpandProperty ExecutablePath -Unique) | ConvertTo-Json -Compress"], capture_output=True, text=True)
folders = {str(Path(path).parent) for path in json.loads(paths.stdout)}
folders.add(os.environ["SystemRoot"])
folders = sorted(path for path in folders if Path(path).is_dir() and not str(probe).lower().startswith(path.lower().rstrip("\\") + "\\"))
attributes = ctypes.WinDLL("kernel32").GetFileAttributesW
attributes.argtypes = [ctypes.c_wchar_p]
attributes.restype = ctypes.c_ulong
folders = [path for path in folders if attributes(path) != 0xFFFFFFFF and not attributes(path) & 0x400]
base = {"profile": 2, "ruleSchemaVersion": 2, "autoConnect": False, "dpi": {"engine": "byedpi", "presetId": "byedpi-standard"}}
active = {"type": 5, "value": probe.stem, "enabled": True, "protocol": "TCP + UDP", "action": 0}
exclude_probe = {"type": 7, "value": str(probe_dir), "enabled": True, "protocol": "TCP + UDP", "action": 1}
cases = [("include", 1, [active], True),
         ("exclude", 2, [{"type": 7, "value": path, "enabled": True, "protocol": "TCP + UDP", "action": 0} for path in folders], True),
         ("all-with-exclusions", 0, [{"type": 7, "value": path, "enabled": True, "protocol": "TCP + UDP", "action": 1} for path in folders], True),
         ("direct-priority", 1, [active, exclude_probe], False)]
report = []
if invoke("status").get("connected"):
    raise RuntimeError("Disconnect before checking capture scopes")
for name, scope, rules, proxied in cases:
    child = None
    try:
        result = invoke("connect", dict(base, scope=scope, rules=rules))
        if not result.get("success"):
            raise RuntimeError(result.get("message"))
        child = subprocess.Popen([str(probe), "--max-time", "10", "--limit-rate", "1", "-4", "-sS", "https://example.com"], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, creationflags=subprocess.CREATE_NO_WINDOW)
        time.sleep(1.2)
        observed = proxy_connections()
        ok = (observed > 0) == proxied
        report.append({"scope": name, "passed": ok, "transportConnections": observed})
        print(name, "PASS" if ok else "FAIL", observed, flush=True)
    finally:
        if child is not None and child.poll() is None:
            child.terminate()
            child.wait(timeout=5)
        invoke("disconnect")
(root / "artifacts/admin-runner/scope-verification.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
if not all(case["passed"] for case in report):
    raise SystemExit(1)
