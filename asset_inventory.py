"""Static asset inventory of installed Vintage Story mods. Run: python -I asset_inventory.py [mods_dir] [out_dir]

Offline and read-only. Nothing in the Mods folder is modified. Zips are opened read-only; PNGs are never decoded
(only the 24-byte header is read for width/height). Shape JSON is parsed to count elements recursively.
Outputs (under sparkvs\\asset-inventory):
  <yyyyMMdd-HHmmss>.json   per-mod inventory, schema_version 1, labelled "static" (computed offline, not a runtime measurement)
  mod-sizes-rank.csv       one row per mod, ranked by texture pixels and by zip bytes
"""
import csv
import json
import os
import struct
import sys
import time
import zipfile
from datetime import datetime

try:
    import pyjson5 as _json5
except Exception:  # pragma: no cover
    _json5 = None

SCHEMA_VERSION = 1
DEFAULT_MODS = r"C:\Users\<you>\AppData\Roaming\VintagestoryData\Mods"
DEFAULT_OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "asset-inventory")
PNG_SIG = b"\x89PNG\r\n\x1a\n"
SOUND_EXT = (".ogg", ".wav")


def parse_json(data: bytes):
    text = data.decode("utf-8-sig", errors="replace")
    if _json5 is not None:
        try:
            return _json5.decode(text)
        except Exception:
            pass
    return json.loads(text)


def png_dims(head: bytes):
    """Return (w, h) from the first 24 bytes of a PNG, or raise ValueError."""
    if len(head) < 24 or head[:8] != PNG_SIG or head[12:16] != b"IHDR":
        raise ValueError("not a PNG / bad IHDR")
    w, h = struct.unpack(">II", head[16:24])
    if w == 0 or h == 0:
        raise ValueError("zero dimension")
    return w, h


def count_elements(elems) -> int:
    n = 0
    stack = [elems]
    while stack:
        cur = stack.pop()
        if isinstance(cur, list):
            for e in cur:
                if isinstance(e, dict):
                    n += 1
                    ch = e.get("children")
                    if ch:
                        stack.append(ch)
    return n


class Source:
    """Uniform read-only access to a zip or an unpacked mod directory."""

    def __init__(self, path):
        self.path = path
        self.is_zip = os.path.isfile(path)
        if self.is_zip:
            self.zf = zipfile.ZipFile(path, "r")
            self.names = [i.filename for i in self.zf.infolist() if not i.filename.endswith("/")]
            self.size_bytes = os.path.getsize(path)
        else:
            self.zf = None
            self.names = []
            total = 0
            for root, _d, files in os.walk(path):
                for f in files:
                    full = os.path.join(root, f)
                    self.names.append(os.path.relpath(full, path).replace("\\", "/"))
                    try:
                        total += os.path.getsize(full)
                    except OSError:
                        pass
            self.size_bytes = total

    def read(self, name, limit=None):
        if self.is_zip:
            with self.zf.open(name) as fh:
                return fh.read(limit) if limit else fh.read()
        with open(os.path.join(self.path, name), "rb") as fh:
            return fh.read(limit) if limit else fh.read()

    def close(self):
        if self.zf:
            self.zf.close()


def inventory_one(path):
    base = os.path.basename(path)
    rec = {
        "source_name": base,
        "source_type": "zip" if os.path.isfile(path) else "dir",
        "modid": None, "version": None, "zip_bytes": None, "size_bytes": None,
        "texture_count": 0, "texture_pixels_total": 0,
        "shape_count": 0, "shape_elements_total": 0,
        "sound_count": 0,
        "unparseable_count": 0, "unparseable": [],
    }

    def flag(item, why):
        rec["unparseable_count"] += 1
        if len(rec["unparseable"]) < 25:
            rec["unparseable"].append({"file": item, "error": str(why)[:160]})

    try:
        src = Source(path)
    except Exception as e:
        rec["modid"] = os.path.splitext(base)[0].lower()
        flag(base, f"cannot open: {e}")
        return rec
    try:
        rec["size_bytes"] = src.size_bytes
        if src.is_zip:
            rec["zip_bytes"] = src.size_bytes
        # modinfo may be at root (normal) or nested one level (rare)
        info_name = next((n for n in src.names if n.lower() == "modinfo.json"), None)
        if info_name is None:
            info_name = next((n for n in src.names if n.lower().endswith("/modinfo.json") and n.count("/") == 1), None)
        if info_name:
            try:
                info = parse_json(src.read(info_name))
                rec["modid"] = str(info.get("modid") or info.get("name") or "").lower() or None
                rec["version"] = str(info.get("version")) if info.get("version") is not None else None
            except Exception as e:
                flag(info_name, e)
        else:
            flag("modinfo.json", "missing")
        if not rec["modid"]:
            rec["modid"] = os.path.splitext(base)[0].lower()
        for n in src.names:
            parts = n.split("/")
            # assets/<domain>/<category>/...
            if len(parts) < 4 or parts[0].lower() != "assets":
                continue
            cat = parts[2].lower()
            low = n.lower()
            if cat == "textures" and low.endswith(".png"):
                try:
                    w, h = png_dims(src.read(n, 24))
                    rec["texture_count"] += 1
                    rec["texture_pixels_total"] += w * h
                except Exception as e:
                    rec["texture_count"] += 1  # present but dimensions unknown
                    flag(n, e)
            elif cat == "shapes" and low.endswith(".json"):
                try:
                    data = parse_json(src.read(n))
                    rec["shape_count"] += 1
                    rec["shape_elements_total"] += count_elements(data.get("elements") if isinstance(data, dict) else None)
                except Exception as e:
                    rec["shape_count"] += 1
                    flag(n, e)
            elif cat == "sounds" and low.endswith(SOUND_EXT):
                rec["sound_count"] += 1
    finally:
        src.close()
    return rec


def main():
    t0 = time.perf_counter()
    mods_dir = sys.argv[1] if len(sys.argv) > 1 else DEFAULT_MODS
    out_dir = sys.argv[2] if len(sys.argv) > 2 else DEFAULT_OUT
    os.makedirs(out_dir, exist_ok=True)

    mods, skipped = [], []
    for name in sorted(os.listdir(mods_dir), key=str.lower):
        p = os.path.join(mods_dir, name)
        if os.path.isdir(p) or (os.path.isfile(p) and name.lower().endswith(".zip")):
            mods.append(inventory_one(p))
        else:
            skipped.append(name)

    now = datetime.now()
    stamp = now.strftime("%Y%m%d-%H%M%S")
    totals = {k: sum(m[k] for m in mods) for k in (
        "zip_bytes", "size_bytes", "texture_count", "texture_pixels_total", "shape_count",
        "shape_elements_total", "sound_count", "unparseable_count") if k != "zip_bytes"}
    totals["zip_bytes"] = sum((m["zip_bytes"] or 0) for m in mods)
    runtime_s = round(time.perf_counter() - t0, 2)
    doc = {
        "schema_version": SCHEMA_VERSION,
        "label": "static: computed offline from mod files, not a runtime measurement",
        "generated_local": now.astimezone().isoformat(timespec="seconds"),
        "mods_dir": mods_dir,
        "mod_count": len(mods),
        "skipped_non_mod_entries": skipped,
        "runtime_seconds": runtime_s,
        "totals": totals,
        "mods": mods,
    }
    json_path = os.path.join(out_dir, f"{stamp}.json")
    with open(json_path, "w", encoding="utf-8") as fh:
        json.dump(doc, fh, indent=1)

    # rank csv
    by_px = sorted(mods, key=lambda m: m["texture_pixels_total"], reverse=True)
    by_zip = sorted(mods, key=lambda m: m["size_bytes"] or 0, reverse=True)
    rank_zip = {id(m): i + 1 for i, m in enumerate(by_zip)}
    csv_path = os.path.join(out_dir, "mod-sizes-rank.csv")
    with open(csv_path, "w", encoding="utf-8", newline="") as fh:
        w = csv.writer(fh)
        w.writerow(["rank_by_texture_pixels", "rank_by_size_bytes", "modid", "version", "source_type", "size_bytes",
                    "texture_count", "texture_pixels_total", "shape_count", "shape_elements_total", "sound_count",
                    "unparseable_count"])
        for i, m in enumerate(by_px, 1):
            w.writerow([i, rank_zip[id(m)], m["modid"], m["version"], m["source_type"], m["size_bytes"],
                        m["texture_count"], m["texture_pixels_total"], m["shape_count"], m["shape_elements_total"],
                        m["sound_count"], m["unparseable_count"]])

    # summary table only
    print(f"mods={len(mods)} skipped_non_mod={len(skipped)} runtime={runtime_s}s")
    print(f"{'#':>2} {'modid':<34} {'tex':>6} {'tex_Mpx':>9} {'shapes':>6} {'elems':>8} {'snd':>5} {'MB':>7} {'err':>4}")
    for i, m in enumerate(by_px[:15], 1):
        print(f"{i:>2} {str(m['modid'])[:34]:<34} {m['texture_count']:>6} {m['texture_pixels_total']/1e6:>9.2f} "
              f"{m['shape_count']:>6} {m['shape_elements_total']:>8} {m['sound_count']:>5} "
              f"{(m['size_bytes'] or 0)/1048576:>7.1f} {m['unparseable_count']:>4}")
    print(f"TOTAL textures={totals['texture_count']} Mpx={totals['texture_pixels_total']/1e6:.1f} shapes={totals['shape_count']} "
          f"elements={totals['shape_elements_total']} sounds={totals['sound_count']} zip_MB={totals['zip_bytes']/1048576:.0f} "
          f"unparseable={totals['unparseable_count']} (mods with errors: {sum(1 for m in mods if m['unparseable_count'])})")
    print(f"json={json_path}")
    print(f"csv={csv_path}")


if __name__ == "__main__":
    main()
