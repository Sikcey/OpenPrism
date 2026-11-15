"""Exercise the real installer/uninstaller against temporary files, without services or registry writes."""
import hashlib
import json
import os
from pathlib import Path
import subprocess
import uuid

repo = Path(__file__).resolve().parent.parent
root = Path(os.environ["TEMP"]) / ("OpenPrism-Fixture-" + uuid.uuid4().hex)
package = root / "package"
installed = root / "installed"
package.mkdir(parents=True)
for name in ["WGTun.exe", "backend/WGTun.Service.exe", "backend/WGTun.Core.dll", "README.md"]:
    target = package / name
    target.parent.mkdir(parents=True, exist_ok=True)
    target.write_text("fixture " + name, encoding="utf-8")
(package / "uninstall-openprism.ps1").write_bytes((repo / "scripts/uninstall-openprism.ps1").read_bytes())

def inventory():
    return {"product": "OpenPrism", "version": "fixture", "files": [
        {"path": str(p.relative_to(package)).replace("\\", "/"), "sha256": hashlib.sha256(p.read_bytes()).hexdigest()}
        for p in package.rglob("*") if p.is_file() and p.name != "package-manifest.json"]}

manifest = inventory()
(package / "package-manifest.json").write_text(json.dumps(manifest), encoding="utf-8")

def run(script, *args, success=True):
    result = subprocess.run(["powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", str(script), *map(str, args)], capture_output=True, text=True)
    if (result.returncode == 0) != success:
        raise AssertionError(result.stdout + result.stderr)

installer = repo / "scripts/install-openprism.ps1"
common = ["-PackageDirectory", package, "-InstallDirectory", installed]
run(installer, *common, "-ValidateOnly", success=False)
print("PASS unsigned release rejected by default")
run(installer, *common, "-DevelopmentPackage", "-ValidateOnly")
assert not installed.exists()
print("PASS validation performs no installation")
run(installer, *common, "-DevelopmentPackage", "-Fixture")
assert (installed / "backend/WGTun.Service.exe").exists()
(installed / "foreign.txt").write_text("keep me", encoding="utf-8")
(installed / "README.md").write_text("modified by user", encoding="utf-8")
run(installed / "uninstall-openprism.ps1", "-InstallDirectory", installed, "-Fixture")
assert (installed / "foreign.txt").read_text() == "keep me"
assert (installed / "README.md").read_text() == "modified by user"
assert not (installed / "backend/WGTun.Service.exe").exists()
print("PASS uninstall removes owned payload and preserves foreign/modified files")
(package / "WGTun.exe").write_text("tampered", encoding="utf-8")
run(installer, *common, "-DevelopmentPackage", "-ValidateOnly", success=False)
print("PASS tampered package rejected")
manifest["files"][0]["path"] = "../escape.exe"
(package / "package-manifest.json").write_text(json.dumps(manifest), encoding="utf-8")
run(installer, *common, "-DevelopmentPackage", "-ValidateOnly", success=False)
assert not (root / "escape.exe").exists()
print("PASS path traversal rejected before writes")
print("Temporary fixture retained:", root)
