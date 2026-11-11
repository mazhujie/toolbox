"""Exercise migration with temporary legacy data; never read a real workspace."""
from contextlib import closing
import importlib.util
import json
from pathlib import Path
import sqlite3
import tempfile
import unittest
from unittest.mock import patch


SCRIPT = Path(__file__).resolve().parents[2] / "scripts" / "migrate-pausepoint.py"
SPEC = importlib.util.spec_from_file_location("migrate_pausepoint", SCRIPT)
migration = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(migration)


class MigrationTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory(prefix="toolbox-migration-test-")
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name)
        self.source = self.root / "pausepoint.sqlite3"
        self.destination = self.root / "workspace"
        self.run_id = "a" * 32
        with closing(sqlite3.connect(self.source)) as database, database:
            database.executescript("""
                CREATE TABLE projects (
                    id, name, path, terminal, urls, archived, done, blocked,
                    next_step, command_draft
                );
                CREATE TABLE tasks (project_id, name, command);
                CREATE TABLE checkpoints (
                    id, project_id, created, done, blocked, next_step
                );
                CREATE TABLE runs (
                    id, project_id, started, ended, command, cwd, name,
                    status, exit_code
                );
                CREATE TABLE app_state (project_id);
            """)
            database.execute(
                "INSERT INTO projects VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?)",
                ("project-1", "板卡调试", "/home/test/项目", 1,
                 '["https://example.com/docs"]', 0, "已完成", "当前卡点",
                 "继续排查", "printf '中文'"),
            )
            database.execute("INSERT INTO tasks VALUES (?, ?, ?)",
                             ("project-1", "读取内核日志", "dmesg"))
            database.execute("INSERT INTO checkpoints VALUES (?, ?, ?, ?, ?, ?)",
                             ("checkpoint-1", "project-1", "2026-01-01 12:00:00",
                              "原已完成", "原卡点", "原下一步"))
            database.execute("INSERT INTO app_state VALUES (?)", ("project-1",))
            database.executemany("INSERT INTO runs VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?)", [
                (self.run_id, "project-1", "2026-01-01 12:00:00", "2026-01-01 12:01:00",
                 "uname -a", "/tmp", "调试", "success", 0),
                ("b" * 32, "project-1", "2026-01-01 12:02:00", None,
                 "sleep 60", "/tmp", "中断测试", "running", None),
            ])
        logs = self.root / "logs"
        logs.mkdir()
        (logs / (self.run_id + ".log")).write_text("真实格式的中文输出\n", encoding="utf-8")
        converter = patch.object(migration.subprocess, "check_output",
                                 return_value="\\\\wsl.localhost\\Test-Distro\\home\\test\\项目\n")
        self.convert_path = converter.start()
        self.addCleanup(converter.stop)

    def test_preserves_records_logs_and_source(self):
        original = self.source.read_bytes()
        counts = migration.migrate(self.source, self.destination)
        data = json.loads((self.destination / "workspace.json").read_text(encoding="utf-8"))
        self.assertEqual(counts, {"Projects": 1, "Checkpoints": 1, "Runs": 2})
        self.assertEqual(data["SelectedProject"], "project-1")
        project = data["Projects"][0]
        self.assertEqual(project["Name"], "板卡调试")
        self.assertEqual(project["Next"], "继续排查")
        self.assertEqual(project["Directory"], "\\\\wsl.localhost\\Test-Distro\\home\\test\\项目")
        self.assertEqual(project["Commands"][0]["Text"], "dmesg")
        self.assertEqual(data["Checkpoints"][0]["Next"], "原下一步")
        runs = {run["Id"]: run for run in data["Runs"]}
        self.assertEqual(runs[self.run_id]["ExitCode"], 0)
        self.assertEqual((self.destination / runs[self.run_id]["Log"]).read_bytes(),
                         (self.root / "logs" / (self.run_id + ".log")).read_bytes())
        self.assertEqual(runs["b" * 32]["Status"], "意外中断")
        self.assertIn("旧版输出文件缺失",
                      (self.destination / runs["b" * 32]["Log"]).read_text(encoding="utf-8"))
        with closing(sqlite3.connect(self.destination / "pausepoint-original.sqlite3")) as backup:
            self.assertEqual(backup.execute("SELECT COUNT(*) FROM runs").fetchone()[0], 2)
        self.assertEqual(self.source.read_bytes(), original)

    def test_refuses_existing_workspace(self):
        self.destination.mkdir()
        existing = self.destination / "workspace.json"
        existing.write_text("existing user data", encoding="utf-8")
        with self.assertRaises(FileExistsError):
            migration.migrate(self.source, self.destination)
        self.assertEqual(existing.read_text(encoding="utf-8"), "existing user data")
        self.convert_path.assert_not_called()

    def test_invalid_run_id_leaves_no_partial_workspace(self):
        with closing(sqlite3.connect(self.source)) as database, database:
            database.execute("UPDATE runs SET id = ? WHERE id = ?", ("../outside", self.run_id))
        original = self.source.read_bytes()
        with self.assertRaisesRegex(ValueError, "Invalid legacy run ID"):
            migration.migrate(self.source, self.destination)
        self.assertFalse(self.destination.exists())
        self.assertEqual(list(self.root.glob(".pausepoint-migration-*")), [])
        self.assertEqual(self.source.read_bytes(), original)


if __name__ == "__main__":
    unittest.main()
