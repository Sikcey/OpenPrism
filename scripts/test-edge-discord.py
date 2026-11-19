"""Bounded, no-focus Edge check. Uses an isolated profile, never personal cookies."""
import argparse
import html
import json
import os
from pathlib import Path
import re
import subprocess
import time

parser = argparse.ArgumentParser()
parser.add_argument('--phase', default='baseline')
parser.add_argument('--disable-quic', action='store_true')
parser.add_argument('--socks-port', type=int)
args = parser.parse_args()
if not re.fullmatch(r'[a-zA-Z0-9_-]{1,40}', args.phase):
    raise SystemExit('Invalid phase')
root = Path(__file__).resolve().parents[1]
out = root / 'artifacts' / 'edge-discord' / args.phase
out.mkdir(parents=True, exist_ok=True)
edge = Path(os.environ.get('ProgramFiles(x86)', r'C:\Program Files (x86)')) / 'Microsoft/Edge/Application/msedge.exe'
if not edge.is_file():
    raise SystemExit('Microsoft Edge not installed')
profile = out / ('profile-' + str(time.time_ns()))
command = [str(edge), '--headless=new', '--mute-audio', '--disable-gpu', '--no-first-run',
    '--no-default-browser-check', '--disable-background-networking',
    '--disable-extensions', '--disable-component-update',
    '--user-data-dir=' + str(profile), '--window-size=1000,780',
    '--timeout=20000', '--virtual-time-budget=12000',
    '--dump-dom', 'https://discord.com/']
if args.disable_quic:
    command.insert(-1, '--disable-quic')
if args.socks_port is not None:
    if not 1024 <= args.socks_port <= 65535:
        raise SystemExit('Invalid loopback SOCKS port')
    command.insert(-1, '--proxy-server=socks5://127.0.0.1:' + str(args.socks_port))
report = {'browser': str(edge), 'url': 'https://discord.com/', 'phase': args.phase,
          'isolatedProfile': True, 'focusRequested': False, 'audioMuted': True,
          'socksPort': args.socks_port, 'quicDisabled': args.disable_quic, 'success': False}
start = time.monotonic()
try:
    result = subprocess.run(command, capture_output=True, timeout=45,
        creationflags=subprocess.CREATE_NO_WINDOW)
    content = result.stdout.decode('utf-8', errors='replace')
    errors = result.stderr.decode('utf-8', errors='replace')
    (out / 'page.html').write_text(content, encoding='utf-8')
    (out / 'browser.log').write_text(errors, encoding='utf-8')
    title = re.search(r'<title[^>]*>(.*?)</title>', content, re.I | re.S)
    report['exitCode'] = result.returncode
    report['title'] = html.unescape(title.group(1)).strip() if title else ''
    report['documentBytes'] = len(result.stdout)
    report['networkErrors'] = sorted(set(re.findall(r'ERR_[A-Z_]+', content + errors)))
    report['success'] = (result.returncode == 0 and len(content) > 5000
        and 'discord' in report['title'].lower()
        and not any(x in content for x in ['chrome-error://', 'ERR_CONNECTION_RESET',
            'ERR_NAME_NOT_RESOLVED', 'ERR_TUNNEL_CONNECTION_FAILED']))
    # A loaded landing page does not establish login, voice, UDP or rule isolation.
except subprocess.TimeoutExpired:
    report['error'] = 'Edge check exceeded 45 seconds; owned main process terminated by subprocess timeout.'
report['elapsedSeconds'] = round(time.monotonic() - start, 2)
report['limits'] = 'Landing-page navigation only; not login, voice, UDP or proof of split routing.'
(out / 'result.json').write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding='utf-8')
print(json.dumps(report, ensure_ascii=False))
