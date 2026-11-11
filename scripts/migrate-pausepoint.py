#!/usr/bin/env python3
"""One-time WSL migration into a NEW Windows Toolbox workspace; source stays intact."""
import argparse
from contextlib import closing
import datetime
import json
import os
from pathlib import Path
import shutil
import sqlite3
import subprocess
import tempfile


def migrate(source: Path, destination: Path):
    if destination.exists():
        raise FileExistsError(f"Destination already exists; no data was changed: {destination}")
    destination.parent.mkdir(parents=True, exist_ok=True)
    stage = Path(tempfile.mkdtemp(prefix=".pausepoint-migration-", dir=destination.parent))
    try:
        with closing(sqlite3.connect(source.resolve().as_uri() + "?mode=ro", uri=True)) as original:
            with closing(sqlite3.connect(stage / "pausepoint-original.sqlite3")) as backup:
                original.backup(backup)
        with closing(sqlite3.connect(stage / "pausepoint-original.sqlite3")) as connection:
            connection.row_factory = sqlite3.Row
            rows = lambda table: [dict(row) for row in connection.execute("select * from " + table)]
            projects, tasks, checkpoints, runs, states = map(rows, ("projects", "tasks", "checkpoints", "runs", "app_state"))
        data = {"Version": 1, "Projects": [], "Checkpoints": [], "Runs": [], "Journals": [],
                "SelectedProject": states[0]["project_id"] if states else "", "View": "projects", "Page": "现场", "Width": 1180, "Height": 820}
        for project in projects:
            path = subprocess.check_output(["wslpath", "-w", project["path"]], text=True).strip()
            data["Projects"].append({"Id": project["id"], "Name": project["name"], "Directory": path,
                "Editor": "", "Terminal": bool(project["terminal"]), "Urls": "\r\n".join(json.loads(project["urls"])),
                "Archived": bool(project["archived"]), "Done": project["done"], "Blocked": project["blocked"],
                "Next": project["next_step"], "Draft": project["command_draft"], "Mode": "Windows PowerShell" if project["command_draft"] else "ADB Shell",
                "Commands": [{"Name": task["name"] + "（旧版脚本，请检查环境）", "Text": task["command"], "Mode": "Windows PowerShell"}
                             for task in tasks if task["project_id"] == project["id"]]})
        for checkpoint in reversed(checkpoints):
            data["Checkpoints"].append({"Id": checkpoint["id"], "ProjectId": checkpoint["project_id"], "Created": checkpoint["created"],
                                       "Done": checkpoint["done"], "Blocked": checkpoint["blocked"], "Next": checkpoint["next_step"]})
        (stage / "runs").mkdir()
        for run in reversed(runs):
            if len(run["id"]) != 32 or any(c not in "0123456789abcdef" for c in run["id"]):
                raise ValueError("Invalid legacy run ID")
            log = source.parent / "logs" / (run["id"] + ".log")
            relative = "runs/" + run["id"] + ".log"
            if log.exists():
                shutil.copy2(log, stage / relative)
            else:
                (stage / relative).write_text("[旧版输出文件缺失；已保留命令元数据]\n", encoding="utf-8")
            status = {"success": "成功", "failed": "失败", "cancelled": "已停止", "stopped": "已停止", "running": "意外中断"}.get(run["status"], run["status"])
            data["Runs"].append({"Id": run["id"], "ProjectId": run["project_id"], "Started": run["started"], "Ended": run["ended"] or "",
                                 "Command": run["command"], "Directory": run["cwd"], "Target": "旧版 WSL · " + run["name"],
                                 "Status": status, "ExitCode": run["exit_code"], "Log": relative})
        (stage / "workspace.json").write_text(json.dumps(data, ensure_ascii=False, indent=2), encoding="utf-8")
        (stage / "migration.txt").write_text(
            "Migrated " + datetime.datetime.now().isoformat() + "\nSource: " + str(source) +
            "\n项目路径已转为 Windows 可访问的路径。旧版脚本保留原文，请确认 Windows PowerShell 环境后再运行；不自动运行。\n"
            "编辑器请在项目设置中重新选择 Windows EXE。完整旧数据库见 pausepoint-original.sqlite3，原数据仍保留。\n", encoding="utf-8")
        if destination.exists():
            raise FileExistsError("Destination appeared during migration; refusing to replace it")
        os.rename(stage, destination)
        return {key: len(data[key]) for key in ("Projects", "Checkpoints", "Runs")}
    except BaseException:
        shutil.rmtree(stage, ignore_errors=True)
        raise


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("source", type=Path)
    parser.add_argument("destination", type=Path)
    args = parser.parse_args()
    print(json.dumps(migrate(args.source, args.destination), ensure_ascii=False))
