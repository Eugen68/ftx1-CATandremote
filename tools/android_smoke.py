"""Runs a separately compiled Android test APK against a simulator station. No real radio is accessed."""
import base64, os, pathlib, re, subprocess, time
root = pathlib.Path('artifacts/android-fixture').resolve()
root.mkdir(parents=True, exist_ok=True)
log = open(root / 'station.log', 'w')
station = subprocess.Popen(['dotnet', 'run', '--project', 'tools/Ftx1.AndroidFixture', '-c', 'Release', '--', str(root)], stdout=log, stderr=subprocess.STDOUT)
app = 'ro.eugen68.ftx1remote'
def adb(*args):
    return subprocess.run(['adb', *args], check=True, capture_output=True).stdout.decode(errors='replace')
try:
    deadline = time.monotonic() + 90
    while not (root / 'pairing.json').exists():
        if station.poll() is not None or time.monotonic() > deadline:
            raise RuntimeError('Station fixture did not start: ' + (root / 'station.log').read_text())
        time.sleep(.25)
    for permission in ['RECORD_AUDIO', 'CAMERA', 'POST_NOTIFICATIONS', 'BLUETOOTH_CONNECT']:
        adb('shell', 'pm', 'grant', app, 'android.permission.' + permission)
    # The cold-boot emulator launcher can ANR and cover the app with a system dialog.
    # Stop only that unrelated launcher; never suppress ANRs or failures of our app.
    adb('shell', 'am', 'force-stop', 'com.google.android.apps.nexuslauncher')
    adb('shell', 'am', 'force-stop', 'com.android.launcher3')
    adb('logcat', '-c')
    config = base64.b64encode((root / 'pairing.json').read_bytes()).decode()
    launch = adb('shell', 'am', 'start', '-W', '-n', app + '/.MainActivity', '--es', 'smokeConfig', config)
    print(launch)
    if "Error:" in launch: raise RuntimeError(launch)
    deadline = time.monotonic() + 120
    asleep = False
    touch = None
    while time.monotonic() < deadline:
        full = adb('logcat', '-d')
        text = '\n'.join(line for line in full.splitlines() if any(word.lower() in line.lower() for word in ['FTX1Smoke', 'FTX1Ptt', 'AndroidRuntime', 'monodroid', 'libc', 'Exception', 'Fatal', 'DOTNET', 'ftx1remote', 'Avalonia']))
        pathlib.Path('artifacts/android-emulator.log').write_text(text)
        if 'FAIL:' in text or 'FATAL EXCEPTION' in text:
            raise RuntimeError(text)
        match = re.search(r'READY_TOUCH (\d+) (\d+)', text)
        if match and touch is None:
            x,y=match.groups()
            adb('shell', 'am', 'force-stop', 'com.google.android.apps.nexuslauncher')
            adb('shell', 'am', 'force-stop', 'com.android.launcher3')
            print('Injecting held touch at',x,y,flush=True)
            pathlib.Path('artifacts/android-touch.png').write_bytes(subprocess.check_output(['adb','exec-out','screencap','-p']))
            touch=subprocess.Popen(['adb','shell','input','swipe',x,y,x,y,'1800'],stdout=subprocess.PIPE,stderr=subprocess.PIPE)
        if touch is not None and touch.poll() is not None and touch.returncode != 0:
            raise RuntimeError('Touch injection failed: '+str(touch.communicate()))
        if 'READY_SLEEP' in text and not asleep:
            adb('shell', 'input', 'keyevent', '223')
            asleep = True
        if 'PASS_SCREEN_OFF' in text:
            adb('shell', 'input', 'keyevent', '224')
        if 'PASS:' in text:
            print(text)
            break
        time.sleep(1)
    else:
        raise RuntimeError('Android smoke test timed out: ' + text)
finally:
    station.terminate()
    try: station.wait(timeout=10)
    except subprocess.TimeoutExpired: station.kill()
    log.close()
