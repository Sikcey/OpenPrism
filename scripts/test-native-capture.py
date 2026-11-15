import json, subprocess, pathlib, urllib.request, urllib.error, sys
root = pathlib.Path(__file__).resolve().parent.parent
backend = str(root / "artifacts/OpenPrism/backend/WGTun.Service.exe")
report = {"checks": []}
def invoke(action, payload=None):
    r = subprocess.run([backend, action], input=json.dumps(payload) if payload else None, capture_output=True, text=True, timeout=45)
    return json.loads(r.stdout)
def snapshot():
    r = subprocess.run(["powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", str(root / "scripts/admin-command.ps1"), "-Operation", "Snapshot"], capture_output=True, text=True, timeout=15)
    return json.loads(r.stdout)
def normalized(state, key):
    return sorted(json.dumps(x, sort_keys=True) for x in state[key])
def check(name, ok, detail=None):
    report["checks"].append({"name":name,"passed":bool(ok),"detail":detail})
    print(name, "PASS" if ok else "FAIL", flush=True)
settings = {"profile":2,"scope":1,"ruleSchemaVersion":2,"autoConnect":False,"detailedLogs":True,"dpi":{"engine":"byedpi","presetId":"byedpi-standard"},"rules":[{"type":5,"value":"curl.exe","enabled":True,"action":0,"protocol":"TCP + UDP"}]}
before = snapshot()
try:
    result = invoke("connect", settings)
    check("process-capture-start", result.get("success"), result.get("message"))
    if not result.get("success"): raise RuntimeError("capture did not start")
    during = snapshot()
    check("native-routes-unchanged-while-active", normalized(before,"routes") == normalized(during,"routes"))
    check("native-dns-unchanged-while-active", normalized(before,"dns") == normalized(during,"dns"))
    r = subprocess.run(["curl.exe","--max-time","12","-I","https://example.com"],capture_output=True,text=True,timeout=15)
    check("selected-process-https", r.returncode == 0, r.stdout.splitlines()[:1])
    for url in ["http://127.0.0.1:11470/settings", "http://[::1]:11470/settings", "https://api.strem.io/"]:
        try:
            with urllib.request.urlopen(url, timeout=8) as response: code=response.status
            check("native-probe-"+url, True, code)
        except urllib.error.HTTPError as e: check("native-probe-"+url, True, e.code)
        except Exception as e: check("native-probe-"+url, False, type(e).__name__)
except Exception as e:
    report["error"] = type(e).__name__
finally:
    check("disconnect", invoke("disconnect").get("success"))
    after=snapshot()
    check("routes-restored", normalized(before,"routes") == normalized(after,"routes"))
    check("dns-restored", normalized(before,"dns") == normalized(after,"dns"))
    target=root / "artifacts/admin-runner/capture-verification.json"
    target.write_text(json.dumps(report,indent=2),encoding="utf-8")
sys.exit(0 if all(c["passed"] for c in report["checks"]) and "error" not in report else 1)
