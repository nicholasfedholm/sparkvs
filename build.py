"""Build SparkVS, run tests, pack out/<version>/SparkVS-<version>.zip (version from modinfo.json), testzip. Prints a summary only."""
import json, os, re, subprocess, sys, zipfile

ROOT = os.path.dirname(os.path.abspath(__file__))
SRC = os.path.join(ROOT, "src")
OUT = os.path.join(ROOT, "out")
# Managed parts of the trace reader. TraceEvent's native ETW helpers (amd64/ etc.) are not needed for nettrace files.
DLLS = ["SparkVS.dll", "Microsoft.Diagnostics.NETCore.Client.dll", "Microsoft.Diagnostics.Tracing.TraceEvent.dll",
        "Microsoft.Diagnostics.FastSerialization.dll", "Microsoft.Extensions.Logging.Abstractions.dll",
        "Microsoft.Extensions.DependencyInjection.Abstractions.dll"]


def run(args, cwd, timeout=170):
    p = subprocess.run(args, cwd=cwd, capture_output=True, text=True, timeout=timeout)
    return p.returncode, p.stdout + p.stderr


def counts(text):
    w = re.search(r"(\d+) Warning\(s\)", text)
    e = re.search(r"(\d+) Error\(s\)", text)
    return int(w.group(1)) if w else -1, int(e.group(1)) if e else -1


def version():
    """modinfo.json is the source of truth; the csproj Version must agree or the build fails."""
    with open(os.path.join(SRC, "Mod", "modinfo.json"), encoding="utf-8") as f:
        v = json.load(f)["version"]
    with open(os.path.join(SRC, "Mod", "SparkVS.csproj"), encoding="utf-8") as f:
        m = re.search(r"<Version>([^<]+)</Version>", f.read())
    if not m or m.group(1) != v:
        raise SystemExit(f"version mismatch: modinfo.json={v} csproj={m.group(1) if m else None}")
    return v


def main():
    ver = version()
    rc, text = run(["dotnet", "run", "-c", "Release", "--nologo", "--project", "Tests"], SRC)
    print("tests:", text.strip().splitlines()[-1] if text.strip() else "no output")
    if rc:
        print(text[-1500:])
        return 1

    moddir = os.path.join(SRC, "Mod")
    rc, text = run(["dotnet", "build", "-c", "Release", "--nologo", "--no-incremental", "SparkVS.csproj"], moddir)
    w, e = counts(text)
    print(f"mod build: rc={rc} warnings={w} errors={e}")
    if rc or w or e:
        print("\n".join(l[:200] for l in text.splitlines() if "error" in l or "warning" in l)[:1800])
        return 1

    binary = os.path.join(moddir, "bin", "Release")
    outdir = os.path.join(OUT, ver)
    os.makedirs(outdir, exist_ok=True)
    path = os.path.join(outdir, f"SparkVS-{ver}.zip")
    with zipfile.ZipFile(path, "w", zipfile.ZIP_DEFLATED, compresslevel=9) as z:
        z.write(os.path.join(moddir, "modinfo.json"), "modinfo.json")
        for d in DLLS:
            # Only SparkVS.dll goes in the root; see src/Mod/DepsLoader.cs.
            z.write(os.path.join(binary, d), d if d == "SparkVS.dll" else "deps/" + d + ".bin")
    with zipfile.ZipFile(path) as z:
        bad = z.testzip()
        names = z.namelist()
    print(f"zip: {path} bytes={os.path.getsize(path)} entries={len(names)} testzip={'clean' if bad is None else bad}")
    print("subfolder entries:", sum("/" in n for n in names))
    return 0 if bad is None else 1


if __name__ == "__main__":
    sys.exit(main())
