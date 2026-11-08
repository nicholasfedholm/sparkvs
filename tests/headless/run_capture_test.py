"""Headless SparkVS server capture test. Usage: python -I run_capture_test.py <tag> <capture_seconds> [notick]
Wipes <T>\\srvdata_<tag>, starts VintagestoryServer.exe with the SparkVS zip and the test helper mod, waits for the bundle, stops the server."""
import json, os, shutil, subprocess, sys, time, glob

T = os.path.dirname(os.path.abspath(__file__))
VS = r"%USERPROFILE%\AppData\Roaming\Vintagestory"
ZIP = os.path.join(os.path.dirname(T), "out", "0.3.0", "SparkVS-0.3.0.zip")
tag = sys.argv[1]
secs = int(sys.argv[2])
notick = len(sys.argv) > 3 and sys.argv[3] == "notick"

data = os.path.join(T, "srvdata_" + tag)
if os.path.isdir(data):
    shutil.rmtree(data)
os.makedirs(os.path.join(data, "ModConfig"))
shutil.copy2(ZIP, os.path.join(T, "mods", "SparkVS-0.3.0.zip"))
if notick:
    with open(os.path.join(data, "ModConfig", "SparkVS.json"), "w") as f:
        json.dump({"ConfigSchema": 2, "EntityTickTiming": False}, f)

helper_log = os.path.join(T, "helper_" + tag + ".log")
if os.path.exists(helper_log):
    os.remove(helper_log)
env = dict(os.environ, SPARKVS_TEST_LOG=helper_log, SPARKVS_TEST_CAPTURE_SEC=str(secs))
wc = "{AdvertiseServer:false,Upnp:false,MapSizeY:256,WorldConfig:{Seed:'sparkcap1',PlayStyle:'creativebuilding',WorldType:'superflat'}}"
args = [os.path.join(VS, "VintagestoryServer.exe"), "--dataPath", data, "--port", "43399", "--ip", "127.0.0.1",
        "--addModPath", os.path.join(T, "mods"), "--withconfig", wc]
out = open(os.path.join(T, "server_stdout_" + tag + ".txt"), "w")
p = subprocess.Popen(args, cwd=VS, stdout=out, stderr=subprocess.STDOUT, stdin=subprocess.PIPE, env=env)
print("started pid", p.pid, flush=True)
t0 = time.time()
saved = False
while time.time() - t0 < 300 + secs:
    if p.poll() is not None:
        print("server exited early rc", p.returncode, flush=True)
        break
    caps = glob.glob(os.path.join(data, "ModData", "sparkvs", "captures", "*-server-*", "summary.md"))
    if caps:
        saved = True
        print("bundle summary.md seen at", int(time.time() - t0), "s:", os.path.dirname(caps[0]), flush=True)
        time.sleep(5)
        break
    time.sleep(2)
if p.poll() is None:
    try:
        p.stdin.write(b"/stop\r\n")
        p.stdin.flush()
        print("sent /stop", flush=True)
    except Exception as e:
        print("stdin write failed", e, flush=True)
    for _ in range(30):
        if p.poll() is not None:
            break
        time.sleep(1)
if p.poll() is None:
    print("killing own pid", p.pid, flush=True)
    p.kill()
else:
    print("server exited rc", p.returncode, flush=True)
print("saved", saved, "wall", int(time.time() - t0), flush=True)
