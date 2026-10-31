"""Run a bounded Windows Vulkan/Mobile comparison of the actual production Phong guard.

Requires a local Godot editor and a fresh output directory. No private assets, ADB, guest,
network, source-tree import or shared build outputs are used. The renderer needs a real GPU;
headless dummy rendering is deliberately excluded. This developer probe selects the Windows
display driver; it is not an Android device or cross-platform shader runner.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import time

GUARDED = """            // Fragment leaves v_specular exactly zero unless one of these uniform paths
            // supplies it. Avoid a per-light Phong pow without changing authored ORM/PBR.
            if (!fable_zero_specular_work || has_specular || (has_orm && is_pbr)) {
                SPECULAR_LIGHT += fable_specular(
                    NORMAL, LIGHT, VIEW, atten, LIGHT_COLOR, v_specular, v_specular_power);
            }"""
PREVIOUS = """            SPECULAR_LIGHT += fable_specular(
                NORMAL, LIGHT, VIEW, atten, LIGHT_COLOR, v_specular, v_specular_power);"""


def main():
    """Stage copied shaders, run native GPU draws and require their positive controls."""
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--godot", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    args.output = args.output.resolve()
    repo = Path(__file__).resolve().parents[2]
    source = repo / "Godot/src/Engine/material/standard_material_body.gdshaderinc"
    body = source.read_text()
    if body.count(GUARDED) != 1:
        parser.error("The production guard must occur exactly once; update this fixture deliberately after shader changes.")
    if "if (has_orm && (has_specular || is_pbr))" not in body:
        parser.error("The production ORM reconstruction condition changed; review the guard before testing.")
    args.output.mkdir(parents=True, exist_ok=False)
    project = args.output / "project"
    shutil.copytree(Path(__file__).with_name("shader-fixture"), project)
    copied = []
    for relative in ["src/Engine/material/fable_lighting.gdshaderinc", "src/Engine/level/environment/fable_atmosphere.gdshaderinc"]:
        original = repo / "Godot" / relative
        target = project / relative
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(original, target)
        copied.append({"source": str(original), "sha256": hashlib.sha256(original.read_bytes()).hexdigest()})
    for name, text in [("baseline", body.replace(GUARDED, PREVIOUS)), ("candidate", body)]:
        (project / (name + ".gdshaderinc")).write_text(text)
        (project / (name + ".gdshader")).write_text('shader_type spatial;\nrender_mode blend_mix, depth_draw_opaque, cull_disabled;\n#include "res://' + name + '.gdshaderinc"\n')
    environment = os.environ.copy()
    environment.update(APPDATA=str(args.output / "appdata"), REFII_SHADER_PROBE_OUTPUT=str(args.output.resolve()))
    Path(environment["APPDATA"]).mkdir(parents=True)
    # Isolate this process from optional desktop overlay manifests, including stale RenderDoc
    # registry entries. These loader search overrides do not change machine/user settings.
    empty_layers = args.output / "empty-vulkan-layers"
    empty_layers.mkdir()
    environment.update(VK_LAYER_PATH=str(empty_layers.resolve()), VK_IMPLICIT_LAYER_PATH=str(empty_layers.resolve()))
    command = [str(args.godot.resolve()), "--path", str(project.resolve()), "--display-driver", "windows",
               "--rendering-driver", "vulkan", "--rendering-method", "mobile", "--audio-driver", "Dummy",
               "--windowed", "--resolution", "64x64", "--position", "-32000,-32000", "--verbose"]
    options = {}
    if os.name == "nt":
        startup = subprocess.STARTUPINFO()
        startup.dwFlags |= subprocess.STARTF_USESHOWWINDOW
        startup.wShowWindow = 0
        options.update(startupinfo=startup, creationflags=subprocess.CREATE_NO_WINDOW)
    started = time.monotonic()
    try:
        result = subprocess.run(command, env=environment, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                                text=True, timeout=90, **options)
        code, log = result.returncode, result.stdout
    except subprocess.TimeoutExpired as error:
        code = "timeout"
        log = error.stdout or ""
        if isinstance(log, bytes):
            log = log.decode(errors="replace")
    (args.output / "godot.log").write_text(log, encoding="utf-8")
    (args.output / "process.json").write_text(json.dumps({"command": command, "exit_code": code,
        "seconds": time.monotonic() - started, "production_shader_sha256": hashlib.sha256(source.read_bytes()).hexdigest(),
        "engine_sha256": hashlib.sha256(args.godot.read_bytes()).hexdigest(), "copied_sources": copied,
        "device_actions": 0}, indent=2) + "\n")
    report_path = args.output / "report.json"
    if code != 0 or not report_path.exists():
        print(log[-12000:])
        raise SystemExit("GPU fixture failed or did not complete")
    report = json.loads(report_path.read_text())
    errors = [line for line in log.splitlines() if "ERROR:" in line or "SHADER ERROR:" in line]
    if report["status"] != "PASS" or report["renderer"] != "mobile" or report["driver"] != "vulkan" or errors:
        raise SystemExit("GPU fixture failed, used the wrong renderer or logged native errors: " + str(errors[:5]))
    print(json.dumps({"status": report["status"], "checks": len(report["checks"]), "cases": len(report["cases"]),
                      "gpu": report["gpu"], "seconds": time.monotonic() - started}, indent=2))


if __name__ == "__main__":
    main()
