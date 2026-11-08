"""Verify a SparkVS server bundle against the helper log. Usage: python -I verify_bundle.py <tag>"""
import glob, json, os, re, sys

T = os.path.dirname(os.path.abspath(__file__))
tag = sys.argv[1]
data = os.path.join(T, "srvdata_" + tag)
dirs = sorted(glob.glob(os.path.join(data, "ModData", "sparkvs", "captures", "*-server-*")))
rows = []
def chk(name, ok, detail=""):
    rows.append((name, "PASS" if ok else "FAIL", detail))

if not dirs:
    print("no bundle"); sys.exit(1)
d = dirs[-1]
print("bundle:", d)
def load(n):
    p = os.path.join(d, n)
    return json.load(open(p, encoding="utf-8")) if os.path.exists(p) else None
for n in ["census-start.json", "census-end.json", "events.jsonl", "context.json", "entity-ticks.json", "capture.meta.json", "summary.md", "tick.jsonl", "heap.jsonl"]:
    chk("file " + n, os.path.exists(os.path.join(d, n)), "%d bytes" % os.path.getsize(os.path.join(d, n)) if os.path.exists(os.path.join(d, n)) else "")

hl = open(os.path.join(T, "helper_%s.log" % tag), encoding="utf-8").read() if os.path.exists(os.path.join(T, "helper_%s.log" % tag)) else ""
def last(label):
    m = [x for x in re.findall(label + r".*?hens=(\d+) pigs=(\d+)", hl)]
    return tuple(map(int, m[-1])) if m else None
before = last("counts before capture")
preend = last("counts 2 s before capture end")
after = last("counts after capture end")
print("helper: before capture", before, "2s before end", preend, "after end", after)

HEN, PIG = "game:chicken-hen", "game:pig-eurasian-adult-female"
cs, ce = load("census-start.json"), load("census-end.json")
if cs and ce:
    s_h, s_p = cs["by_code"].get(HEN, 0), cs["by_code"].get(PIG, 0)
    e_h, e_p = ce["by_code"].get(HEN, 0), ce["by_code"].get(PIG, 0)
    chk("census start hens/pigs == 40/25", (s_h, s_p) == (40, 25), "got %d/%d" % (s_h, s_p))
    exp_end = preend if preend else (55, 25)
    chk("census end hens/pigs == helper log (55/25 unless deaths)", (e_h, e_p) == exp_end or (e_h, e_p) == after, "got %d/%d, helper %s" % (e_h, e_p, exp_end))
    print("census durations ms: start %.2f end %.2f; totals %d -> %d" % (cs["duration_ms"], ce["duration_ms"], cs["total"], ce["total"]))
ev = [json.loads(l) for l in open(os.path.join(d, "events.jsonl"), encoding="utf-8") if l.strip()] if os.path.exists(os.path.join(d, "events.jsonl")) else []
ch = [r for r in ev if r.get("type") == "chunks"]
lo, un = sum(r.get("loaded", 0) for r in ch), sum(r.get("unloaded", 0) for r in ch)
chk("events chunks loaded>0 and unloaded>0", lo > 0 and un > 0, "loaded=%d unloaded=%d rows=%d" % (lo, un, len(ch)))
tools = [r.get("type") for r in ev if r.get("source") == "tool"]
need = ["capture_start", "capture_stop", "census_start", "census_end", "harmony_patch", "harmony_unpatch"]
chk("events tool rows " + ",".join(need), all(n in tools for n in need), "tool rows: " + ",".join(tools))
ctx = load("context.json") or {}
miss = [k for k in ["gc", "runtime", "game_version", "mods", "sparkvs_config", "perf_files"] if k not in ctx]
chk("context.json keys", not miss, "missing: %s" % miss if miss else "all present; mods.count=%s" % (ctx.get("mods") or {}).get("count"))
et = load("entity-ticks.json") or {}
chk("entity-ticks enabled", et.get("enabled") is True, str(et.get("reason", "")))
secs = 90.0
import re
meta = load("capture.meta.json") or {}
dur = meta.get("duration_sec", secs)
if et.get("enabled"):
    byc = {r["code"]: r for r in et["rows"]}
    for code, n0, n1 in [(HEN, 40, 55), (PIG, 25, 25)]:
        r = byc.get(code)
        if not r:
            chk("entity-ticks row " + code, False, "absent"); continue
        # The server loop runs about 21.6 Hz here (tick.jsonl interval 46 ms), not 50 Hz: expected calls = server ticks x count.
        tk = [json.loads(l) for l in open(os.path.join(d, "tick.jsonl"), encoding="utf-8")][1:]
        tk = [x for x in tk if "mspt_ms" in x]
        n_ticks = len(tk)
        mid_frac = 0.0
        m1 = re.search(r"(\d\d:\d\d:\d\d\.\d+) command", hl); m2 = re.search(r"(\d\d:\d\d:\d\d\.\d+) MID spawned", hl)
        if m1 and m2:
            tt = lambda x: sum(float(v) * k for v, k in zip(x.split(":"), (3600, 60, 1)))
            mid_frac = (tt(m2.group(1)) - tt(m1.group(1))) / dur
        lo_ = n_ticks * n0 + n_ticks * (n1 - n0) * (1 - mid_frac)
        r_ = r["calls"]
        hz = n_ticks / dur
        chk("entity-ticks %s calls == ticks x count (ticks=%d, %.1f Hz)" % (code, n_ticks, hz), abs(r_ / lo_ - 1) < 0.05, "calls=%d expected~%d ratio=%.3f mean_us=%s max_us=%s share=%s" % (r_, lo_, r_ / lo_, r["mean_us"], r["max_us"], r["share_pct"]))
    print("entity-ticks: wrapped_calls_total=%s wrapper_overhead_estimate_ns=%s window=%s distinct_codes=%s" % (et["wrapped_calls_total"], et["wrapper_overhead_estimate_ns"], et["window_seconds"], et["distinct_codes"]))
    print("top rows:", [(r["code"], r["calls"], r["total_ms"]) for r in et["rows"][:4]])
print()
print("%-62s %-5s %s" % ("check", "res", "detail"))
for n, r, dt in rows:
    print("%-62s %-5s %s" % (n, r, dt))
print("PASS %d FAIL %d" % (sum(r == "PASS" for _, r, _ in rows), sum(r == "FAIL" for _, r, _ in rows)))
o = meta.get("sparkvs_overhead") or {}
print("overhead:", json.dumps(o))
# server tick time (tick.jsonl)
tp = os.path.join(d, "tick.jsonl")
if os.path.exists(tp):
    import statistics
    vals = []
    for l in open(tp, encoding="utf-8"):
        try:
            j = json.loads(l)
        except Exception:
            continue
        vals.append(j)
    print("tick.jsonl rows:", len(vals), "first:", json.dumps(vals[1])[:300] if len(vals) > 1 else "")
