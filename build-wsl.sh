#!/usr/bin/env bash
set -euo pipefail

project_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
case "${1:-build}" in
  build) script="build.cmd" ;;
  test) script="tests/run.cmd" ;;
  *) echo "Usage: $0 [build|test]" >&2; exit 2 ;;
esac

cmd_exe="$(command -v cmd.exe || true)"
if [[ -z "$cmd_exe" ]]; then
  cmd_exe="/mnt/c/Windows/System32/cmd.exe"
fi
if [[ ! -x "$cmd_exe" ]]; then
  echo "Windows interop is required to build this WinForms application." >&2
  exit 1
fi

"$cmd_exe" /c "$(wslpath -w "$project_dir/$script")"
