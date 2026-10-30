#!/usr/bin/env bash
# self-check.sh -- Gauntlet structural + launch net for the .NET 10 port of GravitySimulator.
#
# What it guards (task gravity-simulator-2026-10-30-latest-dotnet):
#   * the csproj is SDK-style, targets net10.0-windows, uses WinForms, and is named
#     GravitySimulator (AssemblyName + RootNamespace; the old typo "Gav..." is gone);
#   * no .NET Framework 4.6.1 targeting is left in the tree;
#   * every .cs file the OLD csproj compiled is still on disk AND still evaluated as a
#     Compile item by MSBuild (catches deletions, <Compile Remove>, and
#     EnableDefaultCompileItems=false without explicit includes);
#   * both .resx files are still EmbeddedResource items, and the built assembly embeds
#     them under the GravitySimulator.* manifest names the code looks up;
#   * `dotnet build -c Release` succeeds with 0 errors (warning count printed);
#   * the freshly built GravitySimulator.exe launches, stays alive >= 5 s and shows a
#     main window with a non-empty title, then is killed.
#
# Limits (stated as limits): this is SILVER-ceiling fallback evidence. It does NOT check
# the physics or rendering output -- "it launches and shows a window" is the only runtime
# behaviour asserted. The forbidden-string scans are grep heuristics over git-visible
# files (tracked + untracked-not-ignored), excluding this script itself.
#
# Runs in Git Bash on Windows. Needs: git, dotnet (SDK 10), powershell.exe.
# Usage: bash self-check.sh        Exit: 0 all PASS, 1 any FAIL, 2 setup error.

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$SCRIPT_DIR"; cd "$ROOT" || exit 2

PROJ_DIR="GravitySimulator"
CSPROJ="$PROJ_DIR/GravitySimulator.csproj"
OUT_DIR="$PROJ_DIR/bin/Release/net10.0-windows"
EXE="$OUT_DIR/GravitySimulator.exe"
DLL="$OUT_DIR/GravitySimulator.dll"
TYPO="Gav""itySimulator"          # split so this script never matches its own scan
OLD_TFV="TargetFramework""Version"
OLD_VER="v4\\.6\\.1"

fails=0
pass(){ printf '  [PASS] %s\n' "$1"; }
fail(){ printf '  [FAIL] %s\n' "$1"; fails=$((fails+1)); }
have(){  grep -qE "$3" "$2" 2>/dev/null && pass "$1" || fail "$1"; }   # pattern present
lacks(){ grep -qE "$3" "$2" 2>/dev/null && fail "$1" || pass "$1"; }   # pattern absent

# Every <Compile Include> of the ORIGINAL (pre-port, .NET Framework 4.6.1) csproj,
# as of main@ef7365c. Hardcoded on purpose: the old csproj will not exist after the port.
OLD_COMPILE=(
  "Engines/Engine3D.cs"
  "Engines/GameEngine.cs"
  "Engines/GraphicsEngine.cs"
  "Engines/GravityEngine.cs"
  "Form1.cs"
  "Form1.Designer.cs"
  "Models/Engine3D/mat4x4.cs"
  "Models/Engine3D/mesh.cs"
  "Models/Engine3D/triangle.cs"
  "Models/Engine3D/vec2d.cs"
  "Models/Engine3D/vec3d.cs"
  "Models/Facet.cs"
  "Models/Plane.cs"
  "Models/PointInSpace.cs"
  "Models/PointLight.cs"
  "Models/SphereModel.cs"
  "Models/Vector.cs"
  "Models/IModel.cs"
  "Program.cs"
  "Properties/AssemblyInfo.cs"
  "Properties/Resources.Designer.cs"
  "Properties/Settings.Designer.cs"
)
OLD_RESX=( "Form1.resx" "Properties/Resources.resx" )

echo "== 1. csproj shape =="
[ -f "$CSPROJ" ] && pass "csproj exists ($CSPROJ)" || fail "csproj exists ($CSPROJ)"
have  "csproj is SDK-style (Sdk=\"Microsoft.NET.Sdk\")" "$CSPROJ" '<Project[[:space:]]+Sdk="Microsoft\.NET\.Sdk"'
have  "TargetFramework is exactly net10.0-windows" "$CSPROJ" '<TargetFramework>[[:space:]]*net10\.0-windows[[:space:]]*</TargetFramework>'
lacks "no multi-targeting <TargetFrameworks>" "$CSPROJ" '<TargetFrameworks>'
have  "UseWindowsForms is true" "$CSPROJ" '<UseWindowsForms>[[:space:]]*true[[:space:]]*</UseWindowsForms>'
have  "OutputType is WinExe" "$CSPROJ" '<OutputType>[[:space:]]*WinExe[[:space:]]*</OutputType>'
have  "AssemblyName is GravitySimulator" "$CSPROJ" '<AssemblyName>[[:space:]]*GravitySimulator[[:space:]]*</AssemblyName>'
have  "RootNamespace is GravitySimulator" "$CSPROJ" '<RootNamespace>[[:space:]]*GravitySimulator[[:space:]]*</RootNamespace>'
lacks "csproj has no old ToolsVersion/msbuild-2003 xmlns" "$CSPROJ" 'schemas\.microsoft\.com/developer/msbuild/2003'

echo "== 2. leftovers in git-visible files (heuristic grep; excludes self-check.sh) =="
scan_absent(){ # name, pattern
  local hits
  hits="$(git grep -I -n --untracked -E "$2" -- . ':!self-check.sh' 2>/dev/null)"
  if [ -z "$hits" ]; then pass "$1"; else fail "$1"; printf '%s\n' "$hits" | head -5 | sed 's/^/         /'; fi
}
scan_absent "no '$TYPO' string anywhere"          "$TYPO"
scan_absent "no $OLD_TFV anywhere"                "$OLD_TFV"
scan_absent "no v4.6.1 anywhere"                  "$OLD_VER"

echo "== 3. old Compile list still present and compiled =="
missing=0
for f in "${OLD_COMPILE[@]}" "${OLD_RESX[@]}"; do
  [ -f "$PROJ_DIR/$f" ] || { missing=$((missing+1)); echo "         missing on disk: $f"; }
done
[ "$missing" -eq 0 ] && pass "all ${#OLD_COMPILE[@]} old .cs + ${#OLD_RESX[@]} .resx files exist" \
                     || fail "all ${#OLD_COMPILE[@]} old .cs + ${#OLD_RESX[@]} .resx files exist ($missing missing)"

# Ask MSBuild which items it actually evaluates -- catches Remove globs / disabled defaults.
items_json="$(dotnet msbuild "$CSPROJ" -nologo -getItem:Compile -getItem:EmbeddedResource 2>/dev/null | tr '\\' '/' | sed 's#//*#/#g')"
if [ -z "$items_json" ]; then
  fail "MSBuild item evaluation ran (dotnet msbuild -getItem)"
else
  excl=0
  for f in "${OLD_COMPILE[@]}"; do
    printf '%s' "$items_json" | grep -qF "\"Identity\": \"$f\"" || { excl=$((excl+1)); echo "         not a Compile item: $f"; }
  done
  [ "$excl" -eq 0 ] && pass "all ${#OLD_COMPILE[@]} old .cs files are evaluated Compile items" \
                    || fail "all ${#OLD_COMPILE[@]} old .cs files are evaluated Compile items ($excl excluded)"
  rexcl=0
  for f in "${OLD_RESX[@]}"; do
    printf '%s' "$items_json" | grep -qF "\"Identity\": \"$f\"" || { rexcl=$((rexcl+1)); echo "         not an item: $f"; }
  done
  [ "$rexcl" -eq 0 ] && pass "both .resx files are evaluated items" || fail "both .resx files are evaluated items"
fi

echo "== 4. dotnet build -c Release =="
marker="$(mktemp)"; sleep 1
build_log="$(dotnet build "$CSPROJ" -c Release -nologo 2>&1)"; build_rc=$?
errs="$(printf '%s\n' "$build_log" | grep -E '^[[:space:]]*[0-9]+ Error\(s\)' | tail -1 | grep -oE '[0-9]+')"
warns="$(printf '%s\n' "$build_log" | grep -E '^[[:space:]]*[0-9]+ Warning\(s\)' | tail -1 | grep -oE '[0-9]+')"
echo "         build exit=$build_rc errors=${errs:-?} warnings=${warns:-?}"
if [ "$build_rc" -eq 0 ] && [ "${errs:-1}" = "0" ]; then
  pass "dotnet build -c Release succeeds with 0 errors (warnings: ${warns:-?})"
else
  fail "dotnet build -c Release succeeds with 0 errors"
  printf '%s\n' "$build_log" | grep -E 'error [A-Z]+[0-9]+' | sort -u | head -8 | sed 's/^/         /'
fi

echo "== 5. built artefacts =="
if [ -f "$EXE" ] && [ "$EXE" -nt "$marker" ]; then
  pass "fresh $EXE produced by this build"; exe_ok=1
else
  fail "fresh $EXE produced by this build"; exe_ok=0
fi
rm -f "$marker"
if [ -f "$DLL" ]; then
  for r in "GravitySimulator.Form1.resources" "GravitySimulator.Properties.Resources.resources"; do
    grep -aqF "$r" "$DLL" && pass "assembly embeds $r" || fail "assembly embeds $r"
  done
else
  fail "assembly $DLL exists (needed for manifest-resource checks)"
fi

echo "== 6. launch =="
if [ "$exe_ok" -eq 1 ]; then
  exe_win="$(cygpath -w "$ROOT/$EXE")"
  launch_out="$(powershell.exe -NoProfile -NonInteractive -Command "
    \$p = Start-Process -FilePath '$exe_win' -PassThru
    Start-Sleep -Seconds 6
    \$p.Refresh()
    if (\$p.HasExited) { 'RESULT exited code=' + \$p.ExitCode }
    else { 'RESULT alive title=[' + \$p.MainWindowTitle + ']'; Stop-Process -Id \$p.Id -Force }
  " 2>&1 | tr -d '\r')"
  echo "         ${launch_out}"
  if printf '%s' "$launch_out" | grep -qE 'RESULT alive title=\[[^]]+\]'; then
    pass "GravitySimulator.exe alive >= 5 s with non-empty MainWindowTitle (then killed)"
  else
    fail "GravitySimulator.exe alive >= 5 s with non-empty MainWindowTitle"
  fi
else
  fail "GravitySimulator.exe alive >= 5 s with non-empty MainWindowTitle (no fresh exe to launch)"
fi

echo
if [ "$fails" -eq 0 ]; then echo "self-check: ALL PASS"; exit 0; fi
echo "self-check: $fails FAIL(s)"; exit 1
