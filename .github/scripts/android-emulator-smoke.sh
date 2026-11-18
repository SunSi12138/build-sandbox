#!/usr/bin/env bash
set -euo pipefail
mkdir -p evidence

APK="$(find android/bin/Release -type f -name '*-Signed.apk' | head -n 1)"
if [ -z "$APK" ]; then
  APK="$(find android/bin/Release -type f -name '*.apk' | head -n 1)"
fi
test -n "$APK"
echo "Installing: $APK"
adb install -r "$APK"
adb logcat -c
adb shell monkey -p com.streetecotool.mobile -c android.intent.category.LAUNCHER 1
sleep 25
adb logcat -d -v time > evidence/logcat.txt

if ! grep -E 'StreetEcoOffline.*READY' evidence/logcat.txt; then
  echo "::error::Android app did not report READY; inspect logcat artifact"
  grep -Ei 'StreetEcoOffline|FATAL EXCEPTION|AndroidRuntime|SQLiteException|Unhandled Exception' evidence/logcat.txt | tail -n 70 || true
  adb exec-out screencap -p > evidence/android-emulator.png || true
  exit 1
fi

PORT="$(sed -nE 's/.*StreetEcoOffline.*READY http:\/\/127\.0\.0\.1:([0-9]+)\/.*/\1/p' evidence/logcat.txt | tail -n 1)"
test -n "$PORT"
adb forward tcp:18518 "tcp:$PORT"
curl --fail --max-time 15 --silent --show-error http://127.0.0.1:18518/api/health | tee evidence/health.json
curl --fail --max-time 15 --silent --show-error http://127.0.0.1:18518/api/bootstrap | tee evidence/bootstrap.json
curl --fail --max-time 15 --silent --show-error http://127.0.0.1:18518/api/map/layers | tee evidence/layers.json
curl --fail --max-time 15 --silent --show-error http://127.0.0.1:18518/ | grep -q 'DOCTYPE html'
adb exec-out screencap -p > evidence/android-emulator.png
test -s evidence/android-emulator.png
echo 'PASS: Android native CoreCLR, SQLite, /api/bootstrap, map layers and Nuxt all respond locally'
