"""Fail before emulator boot if managed Android components were omitted from packaging."""
import pathlib, xml.etree.ElementTree as ET
root = pathlib.Path('src/Ftx1.Android/obj/Debug/net8.0-android/android/AndroidManifest.xml')
manifest = ET.parse(root).getroot()
android = '{http://schemas.android.com/apk/res/android}'
app = manifest.find('application')
activities = {x.get(android + 'name') for x in app.findall('activity')}
services = {x.get(android + 'name') for x in app.findall('service')}
assert 'ro.eugen68.ftx1remote.MainActivity' in activities, activities
assert 'ro.eugen68.ftx1remote.ControlService' in services, services
assert any(name.endswith('.QrActivity') for name in activities), activities
assembly = pathlib.Path('src/Ftx1.Android/bin/Debug/net8.0-android/Ftx1.Android.dll').read_bytes()
assert 'smokeConfig'.encode('utf-16-le') not in assembly, 'Test entry must not be compiled into distributed APK'
print('Android package: launcher, service, camera activity, no test entry - OK')
