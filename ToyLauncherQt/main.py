#!/usr/bin/env python3
"""Toy Bridge Launcher (Qt) - cross-platform GUI wrapper around ButtplugBridge, GamePatcher, and
ApkPatcher, plus a manager for ModRoom-style external mod folders (custom_wives, custom_futas,
custom_bedrooms, dialogue_packs, texture_packs).

This intentionally does NOT reimplement any of the Buttplug protocol, the game-patching logic, or
the APK-patching logic in Python - it launches the same tested, self-contained ButtplugBridge /
GamePatcher / ApkPatcher executables (built separately, one per platform) as subprocesses and
manages them. Replaces the earlier WinForms-only ToyLauncher.exe with something that runs on
Windows, Linux, and macOS from one codebase.

Settings file (launcher_settings.json, same schema as the old WinForms version so existing
settings carry over) and profiles.json (owned by ButtplugBridge, not this app) both live next
to this program's own executable.

Threading note: the Intiface reachability check, running GamePatcher, and running ApkPatcher are
all short-to-moderate (under several seconds to maybe a minute for a big data file) one-off
operations, so they run synchronously on the GUI thread - an occasional freeze is a fair trade
for not having to reason about Qt's cross-thread signal rules for them. The one thing that's
genuinely long-running is ButtplugBridge itself once started (minutes, for the whole play
session), so ONLY its output reader runs on a background QThread - see BridgeOutputReader below,
and note it's connected with an explicit QueuedConnection, which is required for correctness:
connecting a cross-thread signal to anything other than a bound method of a QObject (e.g. a
lambda) is not reliably queued onto the receiving thread by Qt's auto-detection, and touching
widgets from the wrong thread is undefined behavior.
"""
from __future__ import annotations

import json
import os
import re
import shutil
import signal
import socket
import subprocess
import sys
import tempfile
import zipfile
from dataclasses import dataclass, asdict
from datetime import datetime
from pathlib import Path

from PySide6.QtCore import Qt, QObject, QThread, Signal
from PySide6.QtGui import QGuiApplication
from PySide6.QtWidgets import (
    QApplication, QMainWindow, QWidget, QLabel, QComboBox, QLineEdit, QPushButton,
    QCheckBox, QSpinBox, QTextEdit, QVBoxLayout, QHBoxLayout, QGroupBox, QFileDialog,
    QMessageBox, QSizePolicy, QTabWidget, QTreeWidget, QTreeWidgetItem, QInputDialog, QDialog,
)

PROFILE_CHOICES: list[tuple[str, str]] = [
    ("Default (0-100%, full range)", "default"),
    ("Easy (0-30%)", "easy"),
    ("Mid (30-60%)", "mid"),
    ("Hard (60-100%)", "hard"),
    ("Mid-Easy (0-50%)", "mideasy"),
    ("Mid-Hard (50-100%)", "midhard"),
    ("Custom", "custom"),
]

INTIFACE_HOST = "127.0.0.1"
INTIFACE_PORT = 12345

# HMV in-game speaker click -> ping (see GamePatcher.cs's SPEAKER PATCH section for the game
# side). PC/Windows-only feature - the game only ever pings its own machine's loopback address,
# so this doesn't need to be configurable the way Intiface's host/port might.
HMV_PING_HOST = "127.0.0.1"
HMV_PING_PORT = 45737
HMV_LIVE_HOST = "127.0.0.1"
HMV_LIVE_PORT = 45736

AUDIO_EXTENSIONS = (".mp3", ".wav", ".ogg", ".flac", ".m4a", ".aac", ".wma")

# Folder names ModRoom (and compatible mods) actually use, and how they're labeled in the UI.
CATEGORY_FOLDERS: dict[str, str] = {
    "custom_wives": "Custom Wives",
    "custom_futas": "Custom Futas",
    "custom_bedrooms": "Custom Bedrooms",
    "dialogue_packs": "Dialogue Packs",
    "texture_packs": "Texture Packs",
}

# --------------------------------------------------------- mod compatibility checker/converter --
# Two genuinely different custom-character mod systems exist across builds of this game (confirmed
# by directly decompiling both - see GamePatcher.cs's CheckCustomModSystem for the game-side half of
# this): vanilla Wife's Bedroom uses one flat "custom/" folder with type auto-detected per-subfolder
# by which data file extension is present; ModRoom uses separate "custom_futas/"/"custom_wives/"/
# "custom_bedrooms/" folders instead, with type declared by which folder a character sits in. The
# actual per-character FILES turn out to be compatible between the two (same INI fields, same simple
# sprite filenames) - so converting a pack between the two is mostly a folder-restructuring exercise,
# NOT a content rewrite, for packs that use the "simple" filename convention below. Packs using the
# OLDER numbered convention (custom_futa_mating_press_0.png, etc.) genuinely can't move between the
# two systems without being redrawn - vanilla's own loader was confirmed (by reading its real GML)
# to never reference any numbered-suffix filename at all.

# Maps a data-file NAME actually found on disk to its character "kind". Both wife extensions are
# recognized here during classification - real community packs on this machine use EITHER one -
# but which one a given TARGET actually requires is a SEPARATE lookup below, because vanilla and
# ModRoom disagree with each other: confirmed directly by decompiling both games' real GML that
# vanilla's func_load_custom only recognizes "custom_data.spouse", while ModRoom's own
# check_custom_wife only recognizes "custom_data.wife" (real packs on disk here - Meru, Momo
# Yaoyorozu, custom_wife_template - all actually use .wife). Converting between the two systems
# needs this one file RENAMED, not just relocated - futa/bedroom extensions are identical in both.
CUSTOM_DATA_FILE_KIND: dict[str, str] = {
    "custom_data.futa": "futa",
    "custom_data.wife": "wife",
    "custom_data.spouse": "wife",
    "custom_data.bedroom": "bedroom",
}
KIND_TO_MODROOM_FOLDER: dict[str, str] = {"futa": "custom_futas", "wife": "custom_wives", "bedroom": "custom_bedrooms"}

# The exact data-file name each target system expects for a given kind.
EXPECTED_DATA_FILENAME: dict[tuple[str, str], str] = {
    ("futa", "Vanilla"): "custom_data.futa",
    ("futa", "ModRoomStyle"): "custom_data.futa",
    ("wife", "Vanilla"): "custom_data.spouse",
    ("wife", "ModRoomStyle"): "custom_data.wife",
    ("bedroom", "Vanilla"): "custom_data.bedroom",
    ("bedroom", "ModRoomStyle"): "custom_data.bedroom",
}

# Telltale filename shapes for the OLDER numbered convention this can't safely convert - a plain
# regex is enough since these are genuinely distinctive shapes (a numeric suffix right before the
# extension) that the simple convention never produces (the one numbered-LOOKING exception, the
# "_alt" suffix vanilla's own loader supports, is NOT numeric and is explicitly excluded below).
_OLD_CONVENTION_PATTERN = re.compile(r"^custom_(futa|wife)_\w+_\d+\.png$", re.IGNORECASE)
_OLD_CONVENTION_DATA_PATTERN = re.compile(r"^custom_data_\d+\.(futa|spouse)$", re.IGNORECASE)


@dataclass
class ModPackClassification:
    folder: Path
    name: str
    kind: str  # "futa" / "wife" / "bedroom" / "unknown"
    data_filename: str | None  # the actual data file name found, e.g. "custom_data.wife"
    portable: bool
    reason: str


def classify_custom_pack(folder: Path) -> ModPackClassification:
    """Classifies one character/bedroom folder as portable (simple convention - can just be
    copied into the other mod system's expected layout) or not (uses the older numbered
    convention, which needs real redrawn art, not just a file move)."""
    try:
        entries = [e.name for e in folder.iterdir()]
    except OSError as ex:
        return ModPackClassification(folder, folder.name, "unknown", None, False, f"Couldn't read folder: {ex}")

    data_file = next((e for e in entries if e.lower() in CUSTOM_DATA_FILE_KIND), None)
    if data_file is None:
        # Also recognize the WB-ModRoom-style numbered alt data files (custom_data_1.futa, etc.) -
        # not portable, but at least correctly identified rather than reported as "unrecognized."
        if any(_OLD_CONVENTION_DATA_PATTERN.match(e) for e in entries):
            return ModPackClassification(folder, folder.name, "unknown", None, False,
                                          "Uses a numbered-alt data file (custom_data_1.futa style) - a different mod scheme entirely, not portable.")
        return ModPackClassification(folder, folder.name, "unknown", None, False,
                                      "No recognized custom_data.futa/.wife/.spouse/.bedroom file found.")

    kind = CUSTOM_DATA_FILE_KIND[data_file.lower()]
    old_style_files = [e for e in entries if _OLD_CONVENTION_PATTERN.match(e)]
    if old_style_files:
        return ModPackClassification(folder, folder.name, kind, data_file, False,
                                      f"Uses the older numbered sprite convention ({old_style_files[0]}, etc.) - "
                                      "needs to be redrawn for the other system, not just relocated.")
    return ModPackClassification(folder, folder.name, kind, data_file, True, "Simple convention - portable as-is.")


def scan_source_mods_folder(mods_root: Path) -> list[ModPackClassification]:
    """Scans every character/bedroom subfolder under a mods root, regardless of which mod-system
    layout it's actually using (ModRoom-style custom_futas/custom_wives/custom_bedrooms, OR a
    vanilla-style flat custom/) - the classification itself is layout-agnostic since it looks at
    the files INSIDE each character folder, not which parent folder it's under."""
    results: list[ModPackClassification] = []
    candidate_parents = [mods_root / "custom_futas", mods_root / "custom_wives",
                          mods_root / "custom_bedrooms", mods_root / "custom"]
    for parent in candidate_parents:
        if not parent.is_dir():
            continue
        for child in sorted(parent.iterdir(), key=lambda p: p.name.lower()):
            if child.is_dir():
                results.append(classify_custom_pack(child))
    return results


def base_dir() -> Path:
    """Directory this program's own executable/script lives in (where sibling
    ButtplugBridge/GamePatcher/ApkPatcher/settings/profiles.json are expected to be)."""
    if getattr(sys, "frozen", False):
        return Path(sys.executable).resolve().parent
    return Path(__file__).resolve().parent


def exe_name(base: str) -> str:
    return base + ".exe" if sys.platform == "win32" else base


def own_exe_name() -> str:
    """This program's own current filename, however it was actually named/renamed at build
    time - resolved dynamically rather than hardcoded so a rename (like ToyLauncherQt.exe ->
    ToyLauncher.exe) can never silently break self-exclusion from the game-exe auto-detect scan
    below the way a hardcoded name list would."""
    if getattr(sys, "frozen", False):
        return Path(sys.executable).name
    return Path(sys.argv[0]).name


OWN_TOOL_NAMES = {exe_name("ButtplugBridge"), exe_name("GamePatcher"), exe_name("ApkPatcher"),
                  exe_name("HmvLive"), own_exe_name()}


def check_intiface_reachable() -> bool:
    try:
        with socket.create_connection((INTIFACE_HOST, INTIFACE_PORT), timeout=0.8):
            return True
    except OSError:
        return False


@dataclass
class Settings:
    Profile: str = "default"
    CustomMin: float = 0.0
    CustomMax: float = 100.0
    LaunchGame: bool = True
    IntifacePath: str | None = None
    GamePath: str | None = None
    ModsPath: str | None = None
    AndroidApkPath: str | None = None
    AndroidReplaceDataEnabled: bool = False
    AndroidReplaceDataPath: str | None = None
    AndroidModsEnabled: bool = False
    AndroidTouchControls: bool = True

    @classmethod
    def load(cls, path: Path) -> "Settings":
        try:
            data = json.loads(path.read_text(encoding="utf-8"))
            known = {f for f in cls.__dataclass_fields__}
            return cls(**{k: v for k, v in data.items() if k in known})
        except Exception:
            return cls()

    def save(self, path: Path) -> None:
        try:
            path.write_text(json.dumps(asdict(self), indent=2), encoding="utf-8")
        except Exception:
            pass


class BridgeOutputReader(QObject):
    """Reads a subprocess's stdout line-by-line on a background thread and emits each line as
    a Qt signal. Must be connected with Qt.ConnectionType.QueuedConnection to a bound QObject
    method (not a lambda) so the GUI update actually happens on the main thread."""
    line_received = Signal(str)
    process_ended = Signal()

    def __init__(self, process: subprocess.Popen):
        super().__init__()
        self._process = process

    def run(self) -> None:
        assert self._process.stdout is not None
        for raw_line in self._process.stdout:
            self.line_received.emit(raw_line.rstrip("\n"))
        self.process_ended.emit()


class HmvPingListener(QObject):
    """Listens on 127.0.0.1:45737 for the game's speaker-click ping (GamePatcher.cs's SPEAKER
    PATCH sends one small UDP packet there on click - see that file's comment for why the actual
    picker UI lives here instead of in-game). Runs on a background QThread since recvfrom blocks;
    connect `ping_received` with Qt.ConnectionType.QueuedConnection to a bound method, same
    reasoning as BridgeOutputReader above (a lambda would not be reliably marshaled onto the GUI
    thread). Uses a short socket timeout + a stop flag (rather than relying on the socket being
    closed from another thread to unblock a bare recvfrom) so it can be told to stop cleanly."""
    ping_received = Signal()

    def __init__(self):
        super().__init__()
        self._stop_requested = False

    def run(self) -> None:
        try:
            sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
            sock.bind((HMV_PING_HOST, HMV_PING_PORT))
        except OSError:
            return  # port already in use (e.g. two copies of this launcher running) - just stay quiet
        sock.settimeout(0.5)
        while not self._stop_requested:
            try:
                sock.recvfrom(64)
            except socket.timeout:
                continue
            except OSError:
                break
            self.ping_received.emit()
        sock.close()

    def stop(self) -> None:
        self._stop_requested = True


class HmvSongPickerDialog(QDialog):
    """Pops up when the game's HMV speaker is clicked. Offers three ways to pick a song - drag &
    drop, Browse, or paste a path - all trivial here in Qt, which is the whole reason this lives
    in the companion app rather than in the GameMaker game itself (GML has no built-in for real OS
    file drag-and-drop, and a native extension DLL is a much bigger, riskier, Windows-only lift for
    comparatively little gain - see GamePatcher.cs's SPEAKER PATCH comment).

    Once a song is chosen, launches HmvLive.exe (the new real-time dual-band envelope-follower
    engine - see HmvMode/live.py) as a subprocess, same pattern as ButtplugBridge/GamePatcher/
    ApkPatcher elsewhere in this app: this dialog doesn't reimplement any audio/DSP logic, just
    starts and stops the already-verified tool."""

    def __init__(self, parent: QWidget, hmv_live_exe: Path) -> None:
        super().__init__(parent)
        self.setWindowTitle("HMV Mode - pick a song")
        self.setAcceptDrops(True)
        self.resize(440, 260)

        self._hmv_live_exe = hmv_live_exe
        self._live_process: subprocess.Popen | None = None

        root = QVBoxLayout(self)

        self._drop_label = QLabel("Drag && drop a song here\n\n...or use the buttons below")
        self._drop_label.setAlignment(Qt.AlignmentFlag.AlignCenter)
        self._drop_label.setStyleSheet("QLabel { border: 2px dashed gray; padding: 24px; }")
        root.addWidget(self._drop_label, 1)

        button_row = QHBoxLayout()
        browse_btn = QPushButton("Browse...")
        browse_btn.clicked.connect(self._browse)
        button_row.addWidget(browse_btn)
        paste_btn = QPushButton("Paste path")
        paste_btn.clicked.connect(self._paste)
        button_row.addWidget(paste_btn)
        root.addLayout(button_row)

        self._status_label = QLabel("Waiting for a song...")
        self._status_label.setWordWrap(True)
        root.addWidget(self._status_label)

        stop_row = QHBoxLayout()
        self._stop_btn = QPushButton("Stop HMV")
        self._stop_btn.setEnabled(False)
        self._stop_btn.clicked.connect(self._stop_live)
        stop_row.addWidget(self._stop_btn)
        stop_row.addStretch(1)
        root.addLayout(stop_row)

    # ------------------------------------------------------------------- drag & drop ----
    def dragEnterEvent(self, event) -> None:  # noqa: N802 (Qt override)
        if event.mimeData().hasUrls():
            event.acceptProposedAction()

    def dropEvent(self, event) -> None:  # noqa: N802 (Qt override)
        urls = event.mimeData().urls()
        if urls:
            self._start_song(Path(urls[0].toLocalFile()))

    # ------------------------------------------------------------------------ actions ----
    def _browse(self) -> None:
        path, _ = QFileDialog.getOpenFileName(
            self, "Pick a song for HMV mode", "",
            "Audio files (*.mp3 *.wav *.ogg *.flac *.m4a *.aac *.wma)")
        if path:
            self._start_song(Path(path))

    def _paste(self) -> None:
        text = QGuiApplication.clipboard().text().strip().strip('"')
        if not text:
            self._status_label.setText("Clipboard is empty - copy a file path first.")
            return
        self._start_song(Path(text))

    def _start_song(self, path: Path) -> None:
        if not path.is_file():
            self._status_label.setText(f"Not a file: {path}")
            return
        if path.suffix.lower() not in AUDIO_EXTENSIONS:
            self._status_label.setText(f"Doesn't look like an audio file ({path.suffix}) - trying anyway: {path.name}")
        self._stop_live()
        if not self._hmv_live_exe.exists():
            self._status_label.setText(
                f"Can't find HmvLive next to this launcher:\n{self._hmv_live_exe}\n\n"
                "(Falling back to \"python live.py\" isn't done automatically - see HMVMODE.txt.)")
            return
        try:
            kwargs: dict = {}
            if sys.platform == "win32":
                kwargs["creationflags"] = subprocess.CREATE_NEW_PROCESS_GROUP
            self._live_process = subprocess.Popen(
                [str(self._hmv_live_exe), str(path), "--host", HMV_LIVE_HOST, "--port", str(HMV_LIVE_PORT)],
                cwd=str(self._hmv_live_exe.parent),
                **kwargs,
            )
            self._status_label.setText(f"Playing: {path.name}")
            self._stop_btn.setEnabled(True)
        except Exception as ex:
            self._status_label.setText(f"Couldn't start HMV: {ex}")

    def _stop_live(self) -> None:
        if self._live_process is not None and self._live_process.poll() is None:
            try:
                if sys.platform == "win32":
                    self._live_process.send_signal(signal.CTRL_BREAK_EVENT)  # type: ignore[attr-defined]
                else:
                    self._live_process.send_signal(signal.SIGINT)
                self._live_process.wait(timeout=3)
            except subprocess.TimeoutExpired:
                self._live_process.kill()
            except Exception:
                try:
                    self._live_process.kill()
                except Exception:
                    pass
        self._live_process = None
        self._stop_btn.setEnabled(False)

    def closeEvent(self, event) -> None:  # noqa: N802 (Qt override)
        self._stop_live()
        super().closeEvent(event)


class ModCompatibilityDialog(QDialog):
    """Checks a source mods folder's character/bedroom packs against a TARGET game's custom-mod
    system (vanilla's single custom/ folder vs ModRoom's split custom_futas/custom_wives/
    custom_bedrooms folders - see GamePatcher.cs's CheckCustomModSystem and the module-level
    classify_custom_pack()/scan_source_mods_folder() above for why this is mostly a folder-
    restructuring problem, not a content-rewrite one). Always shows a dry-run report before
    copying anything, matching this app's existing confirm-before-mutate pattern (Patch Game/
    Patch APK) - originals are never modified or moved, only copied."""

    def __init__(self, parent: QWidget, gamepatcher_exe: Path, source_mods_root: Path) -> None:
        super().__init__(parent)
        self.setWindowTitle("Mod Compatibility Checker")
        self.resize(560, 420)

        self._gamepatcher_exe = gamepatcher_exe
        self._source_mods_root = source_mods_root
        self._target_dir: Path | None = None
        self._target_system: str | None = None
        self._classifications: list[ModPackClassification] = []

        root = QVBoxLayout(self)
        root.addWidget(QLabel(
            f"Source mods folder: {source_mods_root}\n"
            "Pick a target game below to check which of these packs would work there, and "
            "optionally copy the portable ones into that game's expected folder layout."
        ))

        target_row = QHBoxLayout()
        self._target_edit = QLineEdit()
        self._target_edit.setReadOnly(True)
        self._target_edit.setPlaceholderText("(no target game selected)")
        target_row.addWidget(self._target_edit, 1)
        browse_btn = QPushButton("Browse for target game...")
        browse_btn.clicked.connect(self._browse_target)
        target_row.addWidget(browse_btn)
        root.addLayout(target_row)

        self._status_box = QTextEdit()
        self._status_box.setReadOnly(True)
        self._status_box.setFontFamily("Consolas" if sys.platform == "win32" else "Monospace")
        root.addWidget(self._status_box, 1)

        button_row = QHBoxLayout()
        self._convert_btn = QPushButton("Copy Portable Packs Into Target...")
        self._convert_btn.setEnabled(False)
        self._convert_btn.clicked.connect(self._convert_clicked)
        button_row.addWidget(self._convert_btn)
        button_row.addStretch(1)
        close_btn = QPushButton("Close")
        close_btn.clicked.connect(self.reject)
        button_row.addWidget(close_btn)
        root.addLayout(button_row)

        self._log("Scanning source mods folder...")
        self._classifications = scan_source_mods_folder(source_mods_root)
        if not self._classifications:
            self._log(f"No character/bedroom packs found under {source_mods_root}.")
        else:
            self._log(f"Found {len(self._classifications)} pack(s) in the source folder:")
            for c in self._classifications:
                mark = "OK" if c.portable else "--"
                self._log(f"  [{mark}] {c.name} ({c.kind}): {c.reason}")
        self._log("\nPick a target game above to check compatibility against it.")

    def _log(self, message: str) -> None:
        self._status_box.append(message)

    def _browse_target(self) -> None:
        path, _ = QFileDialog.getOpenFileName(self, "Select the target game's executable", "", "Executables (*.exe);;All files (*)")
        if not path:
            return
        self._target_edit.setText(path)
        self._target_dir = Path(path).resolve().parent
        self._check_target_compatibility(path)

    def _check_target_compatibility(self, game_exe_path: str) -> None:
        if not self._gamepatcher_exe.exists():
            self._log(f"\nCan't find GamePatcher next to this launcher: {self._gamepatcher_exe}")
            return
        try:
            result = subprocess.run(
                [str(self._gamepatcher_exe), game_exe_path, "--check-mod-system"],
                capture_output=True, text=True, timeout=60,
            )
        except Exception as ex:
            self._log(f"\nFailed to run GamePatcher: {ex}")
            return

        output = (result.stdout or "") + (result.stderr or "")
        self._log(f"\n--- Target check ---")
        for line in output.splitlines():
            if line.strip():
                self._log(line)

        if "System: Vanilla" in output:
            self._target_system = "Vanilla"
        elif "System: ModRoomStyle" in output:
            self._target_system = "ModRoomStyle"
        else:
            self._target_system = None
            self._log("\nCouldn't determine the target's mod system - this may be a different "
                       "fork with its own scheme. Not safe to assume compatibility, so conversion is disabled.")

        portable_count = sum(1 for c in self._classifications if c.portable)
        if self._target_system is not None and portable_count > 0:
            self._log(f"\n{portable_count} of {len(self._classifications)} pack(s) use the simple, portable "
                       f"convention and can be copied into this target's {self._target_system} layout.")
            self._convert_btn.setEnabled(True)
        else:
            self._convert_btn.setEnabled(False)

    def _convert_clicked(self) -> None:
        if self._target_dir is None or self._target_system is None:
            return
        portable = [c for c in self._classifications if c.portable]
        if not portable:
            QMessageBox.information(self, "Mod Compatibility Checker", "No portable packs to copy.")
            return

        confirm = QMessageBox.question(
            self, "Copy portable packs",
            f"This will copy {len(portable)} pack(s) into:\n{self._target_dir}\n\n"
            "Original files are never modified or moved - only copied. Continue?",
        )
        if confirm != QMessageBox.StandardButton.Yes:
            return

        copied, failed = 0, []
        for c in portable:
            try:
                if self._target_system == "Vanilla":
                    dest_root = self._target_dir / "custom"
                else:
                    dest_root = self._target_dir / KIND_TO_MODROOM_FOLDER[c.kind]
                dest = dest_root / c.name
                if dest.exists():
                    self._log(f"Skipped {c.name}: already exists at destination.")
                    continue
                dest.parent.mkdir(parents=True, exist_ok=True)
                shutil.copytree(c.folder, dest)

                # futa/bedroom data filenames are identical between the two systems, but the
                # wife-type extension genuinely differs (ModRoom wants .wife, vanilla wants
                # .spouse - confirmed by reading both games' real GML) - rename if needed so the
                # TARGET's own loader actually recognizes the copied pack.
                expected_name = EXPECTED_DATA_FILENAME.get((c.kind, self._target_system))
                rename_note = ""
                if expected_name and c.data_filename and expected_name.lower() != c.data_filename.lower():
                    (dest / c.data_filename).rename(dest / expected_name)
                    rename_note = f" (renamed {c.data_filename} -> {expected_name})"

                self._log(f"Copied {c.name} -> {dest}{rename_note}")
                copied += 1
            except Exception as ex:
                failed.append(c.name)
                self._log(f"Failed to copy {c.name}: {ex}")

        summary = f"Copied {copied} pack(s)."
        if failed:
            summary += f" Failed: {', '.join(failed)}."
        self._log(f"\n{summary}")
        QMessageBox.information(self, "Mod Compatibility Checker", summary)


# --------------------------------------------------------------------- mod-folder management --

def _detect_mod_category(names: list[str]) -> str | None:
    """Guesses a mod's category from the filenames inside its zip. Always presented to the user
    as a pre-selected suggestion they can override, never applied silently - the extensions below
    are consistent within this project's own mods but a stranger's zip could be laid out oddly."""
    lower = [n.lower() for n in names]
    if any(n.endswith(".wife") for n in lower):
        return "custom_wives"
    if any(n.endswith(".futa") for n in lower):
        return "custom_futas"
    if any(n.endswith(".bedroom") for n in lower):
        return "custom_bedrooms"
    if any(n.endswith(".json") for n in lower):
        return "dialogue_packs"
    if any(n.endswith(".png") for n in lower):
        return "texture_packs"
    return None


def _safe_extract(zf: zipfile.ZipFile, dest: Path) -> None:
    for member in zf.infolist():
        member_path = Path(member.filename)
        if member_path.is_absolute() or ".." in member_path.parts:
            raise ValueError(f"Unsafe path in zip: {member.filename}")
    zf.extractall(dest)


def install_mod_zip(zip_path: Path, mods_root: Path, category: str) -> tuple[bool, str]:
    """Extracts a mod zip into mods_root/<category>/<mod name>/. The mod name is the zip's single
    wrapping top-level folder if it has one (the common case for character/bedroom mods), or
    otherwise the zip's own filename (dialogue packs and texture packs are often flat)."""
    with tempfile.TemporaryDirectory() as tmp:
        tmp_path = Path(tmp)
        try:
            with zipfile.ZipFile(zip_path) as zf:
                _safe_extract(zf, tmp_path)
        except Exception as ex:
            return False, f"Couldn't extract {zip_path.name}: {ex}"

        top_entries = [e for e in tmp_path.iterdir() if e.name not in ("__MACOSX",)]
        if len(top_entries) == 1 and top_entries[0].is_dir():
            source_dir = top_entries[0]
            mod_name = top_entries[0].name
        else:
            source_dir = tmp_path
            mod_name = zip_path.stem

        dest = mods_root / category / mod_name
        if dest.exists():
            return False, (f"\"{mod_name}\" already exists in {CATEGORY_FOLDERS[category]} - "
                            "remove it first, or rename the zip, if you meant to replace it.")
        try:
            dest.parent.mkdir(parents=True, exist_ok=True)
            shutil.copytree(source_dir, dest)
        except Exception as ex:
            return False, f"Couldn't install {zip_path.name}: {ex}"

        return True, f"Added \"{mod_name}\" to {CATEGORY_FOLDERS[category]}."


class MainWindow(QMainWindow):
    def __init__(self) -> None:
        super().__init__()
        self.setWindowTitle("Toy Bridge Launcher")
        self.resize(700, 620)
        self.setMinimumSize(540, 480)

        self._base_dir = base_dir()
        self._settings_path = self._base_dir / "launcher_settings.json"
        self._profiles_path = self._base_dir / "profiles.json"
        self._bridge_exe = self._base_dir / exe_name("ButtplugBridge")
        self._patcher_exe = self._base_dir / exe_name("GamePatcher")
        self._apkpatcher_exe = self._base_dir / exe_name("ApkPatcher")

        self._settings = Settings.load(self._settings_path)
        self._bridge_process: subprocess.Popen | None = None
        self._bridge_thread: QThread | None = None
        self._bridge_reader: BridgeOutputReader | None = None

        self._hmv_live_exe = self._base_dir / exe_name("HmvLive")
        self._hmv_dialog: HmvSongPickerDialog | None = None
        self._hmv_ping_thread: QThread | None = None
        self._hmv_ping_listener: HmvPingListener | None = None

        self._build_ui()
        self._apply_settings_to_ui()
        self._start_hmv_ping_listener()

        self.log("Checking for Intiface Central...")
        up = check_intiface_reachable()
        self.log("Intiface Central is reachable at ws://127.0.0.1:12345." if up else
                  "Intiface Central not detected yet - that's fine, you can start it before pressing Start.")

    # ---------------------------------------------------------------- UI ----

    def _build_ui(self) -> None:
        central = QWidget()
        self.setCentralWidget(central)
        root = QVBoxLayout(central)
        root.setContentsMargins(0, 0, 0, 0)

        self.tabs = QTabWidget()
        self.tabs.addTab(self._build_play_tab(), "Play")
        self.tabs.addTab(self._build_mods_tab(), "Mods")
        self.tabs.addTab(self._build_android_tab(), "Android")
        root.addWidget(self.tabs)

        self._update_custom_visibility()

    def _build_play_tab(self) -> QWidget:
        tab = QWidget()
        root = QVBoxLayout(tab)
        root.setContentsMargins(14, 14, 14, 14)
        root.setSpacing(4)

        # --- Game row ---
        root.addWidget(QLabel("Game:"))
        game_row = QHBoxLayout()
        self.game_path_edit = QLineEdit()
        self.game_path_edit.setReadOnly(True)
        self.game_path_edit.setPlaceholderText("(no game selected)")
        game_row.addWidget(self.game_path_edit, 1)
        browse_btn = QPushButton("Browse...")
        browse_btn.clicked.connect(self._browse_for_game)
        game_row.addWidget(browse_btn)
        self.patch_btn = QPushButton("Patch Game...")
        self.patch_btn.clicked.connect(self._patch_game_clicked)
        game_row.addWidget(self.patch_btn)
        root.addLayout(game_row)
        root.addSpacing(10)

        # --- Profile ---
        root.addWidget(QLabel("Intensity profile:"))
        self.profile_combo = QComboBox()
        for display, _key in PROFILE_CHOICES:
            self.profile_combo.addItem(display)
        self.profile_combo.currentIndexChanged.connect(self._update_custom_visibility)
        root.addWidget(self.profile_combo)

        self.custom_group = QGroupBox()
        custom_row = QHBoxLayout(self.custom_group)
        custom_row.addWidget(QLabel("Min %:"))
        self.custom_min_spin = QSpinBox()
        self.custom_min_spin.setRange(0, 100)
        custom_row.addWidget(self.custom_min_spin)
        custom_row.addSpacing(20)
        custom_row.addWidget(QLabel("Max %:"))
        self.custom_max_spin = QSpinBox()
        self.custom_max_spin.setRange(0, 100)
        self.custom_max_spin.setValue(100)
        custom_row.addWidget(self.custom_max_spin)
        custom_row.addStretch(1)
        root.addWidget(self.custom_group)
        root.addSpacing(10)

        # --- Launch game checkbox ---
        self.launch_game_check = QCheckBox("Also launch the game")
        self.launch_game_check.setChecked(True)
        root.addWidget(self.launch_game_check)
        root.addSpacing(10)

        # --- Buttons ---
        button_row = QHBoxLayout()
        self.start_btn = QPushButton("Start")
        self.start_btn.clicked.connect(self._start_clicked)
        button_row.addWidget(self.start_btn)
        self.stop_btn = QPushButton("Stop")
        self.stop_btn.setEnabled(False)
        self.stop_btn.clicked.connect(self._stop_bridge)
        button_row.addWidget(self.stop_btn)
        open_intiface_btn = QPushButton("Open Intiface Central")
        open_intiface_btn.clicked.connect(self._open_intiface_central)
        button_row.addWidget(open_intiface_btn)
        button_row.addStretch(1)
        root.addLayout(button_row)
        root.addSpacing(12)

        # --- Status log ---
        root.addWidget(QLabel("Status:"))
        self.status_box = QTextEdit()
        self.status_box.setReadOnly(True)
        self.status_box.setFontFamily("Consolas" if sys.platform == "win32" else "Monospace")
        self.status_box.setSizePolicy(QSizePolicy.Policy.Expanding, QSizePolicy.Policy.Expanding)
        root.addWidget(self.status_box, 1)

        return tab

    def _update_custom_visibility(self) -> None:
        index = self.profile_combo.currentIndex()
        is_custom = 0 <= index < len(PROFILE_CHOICES) and PROFILE_CHOICES[index][1] == "custom"
        self.custom_group.setVisible(is_custom)

    def log(self, message: str) -> None:
        self.status_box.append(f"{datetime.now():%H:%M:%S}  {message}")

    # ---------------------------------------------------------- settings ----

    def _apply_settings_to_ui(self) -> None:
        keys = [k for _, k in PROFILE_CHOICES]
        index = keys.index(self._settings.Profile) if self._settings.Profile in keys else 0
        self.profile_combo.setCurrentIndex(index)
        self.custom_min_spin.setValue(int(max(0, min(100, self._settings.CustomMin))))
        self.custom_max_spin.setValue(int(max(0, min(100, self._settings.CustomMax))))
        self.launch_game_check.setChecked(self._settings.LaunchGame)
        self._update_custom_visibility()

        self._resolve_game_path(prompt=False)
        self._update_game_path_label()

        self._update_mods_path_label()
        self._refresh_mods_tree()
        resolved_mods_path = self._resolve_mods_path(prompt=False)
        if resolved_mods_path is not None:
            self.android_mods_edit.setText(str(resolved_mods_path))

        if self._settings.AndroidApkPath and Path(self._settings.AndroidApkPath).exists():
            self.android_apk_edit.setText(self._settings.AndroidApkPath)
        if self._settings.AndroidReplaceDataPath and Path(self._settings.AndroidReplaceDataPath).exists():
            self.android_replace_edit.setText(self._settings.AndroidReplaceDataPath)
        # Signals blocked while restoring: android_replace_check/android_mods_check/
        # android_touch_check are all wired to _update_android_enabled_state, which ALSO persists
        # all three checkboxes' CURRENT state as a side effect - firing it after only the first of
        # the three has been restored would read the other two's still-default values and clobber
        # their real saved settings before they get their own turn. Restore all three first, THEN
        # sync/persist once at the end via a single explicit call.
        for checkbox in (self.android_replace_check, self.android_mods_check, self.android_touch_check):
            checkbox.blockSignals(True)
        self.android_replace_check.setChecked(self._settings.AndroidReplaceDataEnabled)
        self.android_mods_check.setChecked(self._settings.AndroidModsEnabled)
        self.android_touch_check.setChecked(self._settings.AndroidTouchControls)
        for checkbox in (self.android_replace_check, self.android_mods_check, self.android_touch_check):
            checkbox.blockSignals(False)
        self._update_android_enabled_state()

    def _save_settings_from_ui(self, profile_key: str) -> None:
        self._settings.Profile = profile_key
        self._settings.CustomMin = self.custom_min_spin.value()
        self._settings.CustomMax = self.custom_max_spin.value()
        self._settings.LaunchGame = self.launch_game_check.isChecked()
        self._settings.save(self._settings_path)

    # --------------------------------------------------------- game path ----

    def _update_game_path_label(self) -> None:
        if self._settings.GamePath and Path(self._settings.GamePath).exists():
            self.game_path_edit.setText(self._settings.GamePath)
        else:
            self.game_path_edit.setText("")
            self.game_path_edit.setPlaceholderText("(no game selected - click Browse)")

    def _resolve_game_path(self, prompt: bool) -> str | None:
        if self._settings.GamePath and Path(self._settings.GamePath).exists():
            return self._settings.GamePath

        # Auto-detect only makes sense on Windows, where the game exe conventionally sits
        # next to this launcher and has a distinguishing .exe extension. On Linux/macOS,
        # ordinary executables have no extension to filter on, so we just ask.
        if sys.platform == "win32":
            candidates = [
                p for p in self._base_dir.glob("*.exe")
                if p.name not in OWN_TOOL_NAMES
            ]
            if len(candidates) == 1:
                self._settings.GamePath = str(candidates[0])
                self._settings.save(self._settings_path)
                return self._settings.GamePath

        if not prompt:
            return None

        self._browse_for_game()
        if self._settings.GamePath and Path(self._settings.GamePath).exists():
            return self._settings.GamePath
        return None

    def _browse_for_game(self) -> None:
        start_dir = str(Path(self._settings.GamePath).parent) if self._settings.GamePath else str(self._base_dir)
        path, _ = QFileDialog.getOpenFileName(self, "Locate the game's executable", start_dir)
        if path:
            self._settings.GamePath = path
            self._settings.save(self._settings_path)
            self.log(f"Game set to: {path}")
        self._update_game_path_label()
        self._update_mods_path_label()
        self._refresh_mods_tree()

    # ------------------------------------------------------------- patch ----

    def _patch_game_clicked(self) -> None:
        game_path = self._resolve_game_path(prompt=True)
        if not game_path:
            return

        if not self._patcher_exe.exists():
            QMessageBox.critical(self, "Missing file", f"Can't find GamePatcher next to this launcher:\n{self._patcher_exe}")
            return

        confirm = QMessageBox.question(
            self, "Patch game for toy support",
            f"This will back up and patch:\n{game_path}\n\n"
            "A backup is made automatically first if one doesn't already exist. This only adds a "
            "small amount of code to broadcast toy telemetry - nothing else about the game is "
            "changed. Continue?",
        )
        if confirm != QMessageBox.StandardButton.Yes:
            return

        self.log(f"Patching {game_path}...")
        self.patch_btn.setEnabled(False)
        self.setCursor(Qt.CursorShape.WaitCursor)
        QApplication.processEvents()
        try:
            result = subprocess.run(
                [str(self._patcher_exe), game_path, "--yes"],
                capture_output=True, text=True, timeout=120,
            )
            output = (result.stdout or "") + (result.stderr or "")
            for line in output.splitlines():
                if line.strip():
                    self.log(f"[patch] {line}")
            if result.returncode == 0:
                QMessageBox.information(self, "Patch game for toy support", "Done - see the status log for details.")
            else:
                QMessageBox.critical(self, "Patch game for toy support", "Patching failed - see the status log for details.")
        except Exception as ex:
            self.log(f"Failed to run GamePatcher: {ex}")
            QMessageBox.critical(self, "Patch game for toy support", f"Failed to run GamePatcher:\n{ex}")
        finally:
            self.patch_btn.setEnabled(True)
            self.unsetCursor()

    # -------------------------------------------------------- custom.json --

    def _update_custom_profile_json(self, min_pct: float, max_pct: float) -> tuple[bool, str]:
        try:
            if not self._profiles_path.exists():
                return False, f"{self._profiles_path} doesn't exist yet - run ButtplugBridge once first."
            text = self._profiles_path.read_text(encoding="utf-8")
            pattern = r'"custom"\s*:\s*\{[^}]*\}'
            replacement = f'"custom":   {{ "min": {min_pct:g}, "max": {max_pct:g} }}'
            if not re.search(pattern, text):
                return False, "Couldn't find a \"custom\" entry in profiles.json to update."
            text = re.sub(pattern, replacement, text)
            self._profiles_path.write_text(text, encoding="utf-8")
            return True, ""
        except Exception as ex:
            return False, str(ex)

    # ----------------------------------------------------------- start/stop

    def _start_clicked(self) -> None:
        if not self._bridge_exe.exists():
            QMessageBox.critical(self, "Missing file", f"Can't find ButtplugBridge next to this launcher:\n{self._bridge_exe}")
            return

        index = self.profile_combo.currentIndex()
        if index < 0:
            index = 0
        profile_key = PROFILE_CHOICES[index][1]

        if profile_key == "custom":
            min_v, max_v = self.custom_min_spin.value(), self.custom_max_spin.value()
            if min_v >= max_v:
                QMessageBox.warning(self, "Check the custom range", "Custom Min % must be less than Max %.")
                return
            ok, error = self._update_custom_profile_json(min_v, max_v)
            if not ok:
                self.log(f"Couldn't update the custom profile in profiles.json: {error}")
                QMessageBox.critical(self, "Custom profile", f"Couldn't update profiles.json:\n{error}")
                return
            self.log(f"Custom profile set to {min_v}% - {max_v}%.")

        self._save_settings_from_ui(profile_key)

        self.log("Checking for Intiface Central...")
        QApplication.processEvents()
        intiface_up = check_intiface_reachable()

        if not intiface_up:
            choice = QMessageBox.question(
                self, "Intiface Central not detected",
                "Intiface Central doesn't seem to be reachable at ws://127.0.0.1:12345.\n\n"
                "Make sure Intiface Central is running AND its server is started (there's a "
                "\"Start Server\" toggle inside it).\n\n"
                "Click Yes to try launching Intiface Central now, or No to continue anyway.",
            )
            if choice == QMessageBox.StandardButton.Yes:
                self._open_intiface_central()
        else:
            self.log("Intiface Central is reachable.")

        try:
            kwargs: dict = {}
            if sys.platform == "win32":
                kwargs["creationflags"] = subprocess.CREATE_NEW_PROCESS_GROUP
            self._bridge_process = subprocess.Popen(
                [str(self._bridge_exe), "--profile", profile_key],
                cwd=str(self._base_dir),
                stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True, bufsize=1,
                **kwargs,
            )
            self.log(f"Started ButtplugBridge --profile {profile_key}")
        except Exception as ex:
            self.log(f"Failed to start ButtplugBridge: {ex}")
            QMessageBox.critical(self, "Error", f"Failed to start ButtplugBridge:\n{ex}")
            return

        self._bridge_thread = QThread()
        self._bridge_reader = BridgeOutputReader(self._bridge_process)
        self._bridge_reader.moveToThread(self._bridge_thread)
        self._bridge_thread.started.connect(self._bridge_reader.run)
        self._bridge_reader.line_received.connect(self._on_bridge_line, Qt.ConnectionType.QueuedConnection)
        self._bridge_reader.process_ended.connect(self._bridge_thread.quit, Qt.ConnectionType.QueuedConnection)
        self._bridge_thread.start()

        if self.launch_game_check.isChecked():
            game_path = self._resolve_game_path(prompt=False)
            if game_path:
                try:
                    subprocess.Popen([game_path], cwd=str(Path(game_path).parent))
                    self.log(f"Launched the game ({game_path}).")
                except Exception as ex:
                    self.log(f"Couldn't launch the game: {ex}")
            else:
                self.log("No game is set yet - click Browse next to \"Game:\" to pick one. Skipped launching the game.")

        self.start_btn.setEnabled(False)
        self.stop_btn.setEnabled(True)

    def _on_bridge_line(self, line: str) -> None:
        self.log(f"[bridge] {line}")

    def _stop_bridge(self) -> None:
        if self._bridge_process is not None and self._bridge_process.poll() is None:
            try:
                if sys.platform == "win32":
                    self._bridge_process.send_signal(signal.CTRL_BREAK_EVENT)  # type: ignore[attr-defined]
                else:
                    self._bridge_process.send_signal(signal.SIGINT)
                self._bridge_process.wait(timeout=3)
            except subprocess.TimeoutExpired:
                self._bridge_process.kill()
            except Exception as ex:
                self.log(f"Couldn't stop ButtplugBridge cleanly: {ex}")
                try:
                    self._bridge_process.kill()
                except Exception:
                    pass
            else:
                self.log("Stopped ButtplugBridge.")

        # The process is dead (or was never running) by this point, so its stdout pipe is closed
        # (or will be immediately) - the reader thread's blocking readline loop will unblock and
        # exit on its own very shortly. Wait briefly for it so the QThread object never gets torn
        # down while its run() is still active, which is a fatal error (at least on Linux).
        if self._bridge_thread is not None:
            if not self._bridge_thread.wait(4000):
                # Last resort: force-killing the process (in case the graceful stop above left
                # it - or some descendant still holding the pipe open - alive) and then forcibly
                # terminating the thread is ugly, but a guaranteed-safe stop beats letting Qt
                # destroy a still-running QThread, which is a guaranteed fatal crash.
                self.log("Bridge output reader thread didn't stop in time - forcing it to stop.")
                try:
                    if self._bridge_process is not None:
                        self._bridge_process.kill()
                except Exception:
                    pass
                self._bridge_thread.terminate()
                self._bridge_thread.wait(2000)
            self._bridge_thread = None
        self._bridge_reader = None

        self._bridge_process = None
        self.stop_btn.setEnabled(False)
        self.start_btn.setEnabled(True)

    # ------------------------------------------------------------- HMV speakers ----

    def _start_hmv_ping_listener(self) -> None:
        """Starts listening for the game's speaker-click ping right away and keeps listening for
        as long as this window is open, regardless of which tab is active - the game can send that
        ping at any time during play, not just while the Play tab happens to be in front."""
        self._hmv_ping_thread = QThread()
        self._hmv_ping_listener = HmvPingListener()
        self._hmv_ping_listener.moveToThread(self._hmv_ping_thread)
        self._hmv_ping_thread.started.connect(self._hmv_ping_listener.run)
        self._hmv_ping_listener.ping_received.connect(self._on_hmv_ping, Qt.ConnectionType.QueuedConnection)
        self._hmv_ping_thread.start()

    def _stop_hmv_ping_listener(self) -> None:
        if self._hmv_ping_listener is not None:
            self._hmv_ping_listener.stop()
        if self._hmv_ping_thread is not None:
            # Same reasoning as _stop_bridge's thread join: destroying a QThread while its run()
            # is still active is a fatal error, so always wait for it to actually exit first.
            if not self._hmv_ping_thread.wait(2000):
                self._hmv_ping_thread.terminate()
                self._hmv_ping_thread.wait(1000)
            self._hmv_ping_thread = None
        self._hmv_ping_listener = None

    def _on_hmv_ping(self) -> None:
        if self._hmv_dialog is None:
            self._hmv_dialog = HmvSongPickerDialog(self, self._hmv_live_exe)
        self._hmv_dialog.show()
        self._hmv_dialog.raise_()
        self._hmv_dialog.activateWindow()

    # -------------------------------------------------------- Intiface UI --

    def _open_intiface_central(self) -> None:
        path = self._settings.IntifacePath
        if not path or not Path(path).exists():
            path = self._guess_intiface_path()

        if not path or not Path(path).exists():
            selected, _ = QFileDialog.getOpenFileName(self, "Locate Intiface Central")
            if not selected:
                self.log("Intiface Central location not set - skipped.")
                return
            path = selected

        try:
            if sys.platform == "win32":
                os.startfile(path)  # type: ignore[attr-defined]
            elif sys.platform == "darwin":
                subprocess.Popen(["open", path])
            else:
                subprocess.Popen([path])
            self._settings.IntifacePath = path
            self._settings.save(self._settings_path)
            self.log(f"Launched Intiface Central ({path}). Remember to click \"Start Server\" inside it.")
        except Exception as ex:
            self.log(f"Couldn't launch Intiface Central: {ex}")

    @staticmethod
    def _guess_intiface_path() -> str | None:
        candidates: list[str] = []
        if sys.platform == "win32":
            candidates = [
                r"D:\NSFW Porn Programs\IntifaceCentral\intiface_central.exe",
                str(Path(os.environ.get("LOCALAPPDATA", "")) / "Programs" / "IntifaceCentral" / "intiface_central.exe"),
                r"C:\Program Files\IntifaceCentral\intiface_central.exe",
            ]
        elif sys.platform == "darwin":
            candidates = ["/Applications/Intiface Central.app"]
        else:
            candidates = [str(Path.home() / ".local/share/intiface-central/intiface-central")]
        for c in candidates:
            if Path(c).exists():
                return c
        return None

    # ------------------------------------------------------------ Mods tab --

    def _build_mods_tab(self) -> QWidget:
        tab = QWidget()
        root = QVBoxLayout(tab)
        root.setContentsMargins(14, 14, 14, 14)
        root.setSpacing(6)

        root.addWidget(QLabel(
            "Manages ModRoom-style mods (custom characters, bedrooms, dialogue, texture packs) - "
            "the same folders you'd otherwise drag-and-drop mods into by hand next to the game."
        ))

        root.addWidget(QLabel("Mods folder:"))
        mods_row = QHBoxLayout()
        self.mods_path_edit = QLineEdit()
        self.mods_path_edit.setReadOnly(True)
        self.mods_path_edit.setPlaceholderText("(defaults to the game's own folder)")
        mods_row.addWidget(self.mods_path_edit, 1)
        change_mods_btn = QPushButton("Change...")
        change_mods_btn.clicked.connect(self._change_mods_folder)
        mods_row.addWidget(change_mods_btn)
        open_mods_btn = QPushButton("Open Folder")
        open_mods_btn.clicked.connect(self._open_mods_folder)
        mods_row.addWidget(open_mods_btn)
        root.addLayout(mods_row)
        root.addSpacing(6)

        self.mods_tree = QTreeWidget()
        self.mods_tree.setHeaderHidden(True)
        root.addWidget(self.mods_tree, 1)

        button_row = QHBoxLayout()
        add_mod_btn = QPushButton("Add Mod (.zip)...")
        add_mod_btn.clicked.connect(self._add_mod_clicked)
        button_row.addWidget(add_mod_btn)
        remove_mod_btn = QPushButton("Remove Selected")
        remove_mod_btn.clicked.connect(self._remove_selected_mod)
        button_row.addWidget(remove_mod_btn)
        refresh_btn = QPushButton("Refresh")
        refresh_btn.clicked.connect(self._refresh_mods_tree)
        button_row.addWidget(refresh_btn)
        compat_btn = QPushButton("Check Compatibility with Another Game...")
        compat_btn.setToolTip(
            "Some custom character packs only work on ONE of vanilla Wife's Bedroom / ModRoom - "
            "they use different mod systems. This checks which of your packs would work on a "
            "different game install, and can copy the compatible ones into its expected layout."
        )
        compat_btn.clicked.connect(self._check_mod_compatibility_clicked)
        button_row.addWidget(compat_btn)
        button_row.addStretch(1)
        root.addLayout(button_row)

        self.mods_status_label = QLabel("")
        self.mods_status_label.setWordWrap(True)
        root.addWidget(self.mods_status_label)

        return tab

    def _update_mods_path_label(self) -> None:
        if self._settings.ModsPath:
            self.mods_path_edit.setText(self._settings.ModsPath)
        elif self._settings.GamePath:
            self.mods_path_edit.setText(str(Path(self._settings.GamePath).parent) + "  (from Game:)")
        else:
            self.mods_path_edit.setText("")

    def _resolve_mods_path(self, prompt: bool = False) -> Path | None:
        if self._settings.ModsPath and Path(self._settings.ModsPath).is_dir():
            return Path(self._settings.ModsPath)
        if self._settings.GamePath:
            candidate = Path(self._settings.GamePath).parent
            if candidate.is_dir():
                return candidate
        if not prompt:
            return None
        selected = QFileDialog.getExistingDirectory(
            self, "Select the folder containing custom_wives/custom_futas/etc. (usually the game's own folder)")
        if not selected:
            return None
        self._settings.ModsPath = selected
        self._settings.save(self._settings_path)
        self._update_mods_path_label()
        return Path(selected)

    def _change_mods_folder(self) -> None:
        start = self._settings.ModsPath or (str(Path(self._settings.GamePath).parent) if self._settings.GamePath else str(self._base_dir))
        selected = QFileDialog.getExistingDirectory(self, "Select the folder containing custom_wives/custom_futas/etc.", start)
        if selected:
            self._settings.ModsPath = selected
            self._settings.save(self._settings_path)
            self._update_mods_path_label()
            self._refresh_mods_tree()
            self.android_mods_edit.setText(selected)

    def _open_mods_folder(self) -> None:
        mods_root = self._resolve_mods_path(prompt=True)
        if mods_root is None:
            return
        try:
            if sys.platform == "win32":
                os.startfile(str(mods_root))  # type: ignore[attr-defined]
            elif sys.platform == "darwin":
                subprocess.Popen(["open", str(mods_root)])
            else:
                subprocess.Popen(["xdg-open", str(mods_root)])
        except Exception as ex:
            self.mods_status_label.setText(f"Couldn't open folder: {ex}")

    def _refresh_mods_tree(self) -> None:
        self.mods_tree.clear()
        mods_root = self._resolve_mods_path(prompt=False)
        if mods_root is None:
            return
        for folder_name, label in CATEGORY_FOLDERS.items():
            category_dir = mods_root / folder_name
            entries = []
            if category_dir.is_dir():
                entries = sorted(
                    (e for e in category_dir.iterdir() if not e.name.startswith(".")),
                    key=lambda p: p.name.lower(),
                )
            top_item = QTreeWidgetItem([f"{label} ({len(entries)})"])
            top_item.setData(0, Qt.ItemDataRole.UserRole, None)
            self.mods_tree.addTopLevelItem(top_item)
            for entry in entries:
                child = QTreeWidgetItem([entry.name])
                child.setData(0, Qt.ItemDataRole.UserRole, str(entry))
                top_item.addChild(child)
        self.mods_tree.expandAll()

    def _detect_or_ask_category(self, zip_path: Path) -> str | None:
        try:
            with zipfile.ZipFile(zip_path) as zf:
                names = zf.namelist()
        except Exception as ex:
            QMessageBox.critical(self, "Add mod", f"Couldn't read {zip_path.name}:\n{ex}")
            return None

        guessed = _detect_mod_category(names)
        keys = list(CATEGORY_FOLDERS.keys())
        labels = list(CATEGORY_FOLDERS.values())
        default_index = keys.index(guessed) if guessed else 0
        guess_text = CATEGORY_FOLDERS[guessed] if guessed else "couldn't guess"
        label, ok = QInputDialog.getItem(
            self, "What kind of mod is this?",
            f"{zip_path.name}\nAuto-detected: {guess_text}. Pick the right category (or change it):",
            labels, default_index, editable=False,
        )
        if not ok:
            return None
        return keys[labels.index(label)]

    def _add_mod_clicked(self) -> None:
        mods_root = self._resolve_mods_path(prompt=True)
        if mods_root is None:
            return

        paths, _ = QFileDialog.getOpenFileNames(
            self, "Select mod .zip file(s)", str(self._base_dir), "Zip files (*.zip)")
        if not paths:
            return

        messages: list[str] = []
        for p in paths:
            zip_path = Path(p)
            category = self._detect_or_ask_category(zip_path)
            if category is None:
                continue
            ok, message = install_mod_zip(zip_path, mods_root, category)
            messages.append(message)

        if messages:
            self.mods_status_label.setText("\n".join(messages))
        self._refresh_mods_tree()

    def _remove_selected_mod(self) -> None:
        item = self.mods_tree.currentItem()
        if item is None:
            return
        path_str = item.data(0, Qt.ItemDataRole.UserRole)
        if path_str is None:
            QMessageBox.information(self, "Remove mod", "Select a specific mod (not a category) to remove.")
            return
        path = Path(path_str)
        confirm = QMessageBox.question(
            self, "Remove mod", f"Delete:\n{path}\n\nThis cannot be undone. Continue?")
        if confirm != QMessageBox.StandardButton.Yes:
            return
        try:
            if path.is_dir():
                shutil.rmtree(path)
            else:
                path.unlink()
            self.mods_status_label.setText(f"Removed \"{path.name}\".")
        except Exception as ex:
            QMessageBox.critical(self, "Remove mod", f"Couldn't remove:\n{ex}")
        self._refresh_mods_tree()

    def _check_mod_compatibility_clicked(self) -> None:
        mods_root = self._resolve_mods_path(prompt=True)
        if mods_root is None:
            return
        dialog = ModCompatibilityDialog(self, self._patcher_exe, mods_root)
        dialog.exec()

    # --------------------------------------------------------- Android tab --

    def _build_android_tab(self) -> QWidget:
        tab = QWidget()
        root = QVBoxLayout(tab)
        root.setContentsMargins(14, 14, 14, 14)
        root.setSpacing(6)

        root.addWidget(QLabel(
            "Prepares an Android .apk with toy support (and optionally a PC mod's content and its "
            "mods) - run once on this PC, then install the result on your phone. Your original APK "
            "is never modified; this produces a new file next to it."
        ))

        root.addWidget(QLabel("Source APK (your own copy of the game):"))
        apk_row = QHBoxLayout()
        self.android_apk_edit = QLineEdit()
        self.android_apk_edit.setReadOnly(True)
        self.android_apk_edit.setPlaceholderText("(no APK selected)")
        apk_row.addWidget(self.android_apk_edit, 1)
        apk_browse_btn = QPushButton("Browse...")
        apk_browse_btn.clicked.connect(self._browse_android_apk)
        apk_row.addWidget(apk_browse_btn)
        root.addLayout(apk_row)
        root.addSpacing(6)

        self.android_replace_check = QCheckBox("Replace game content with a different data file (e.g. a PC mod's data.win)")
        self.android_replace_check.toggled.connect(self._update_android_enabled_state)
        root.addWidget(self.android_replace_check)
        replace_row = QHBoxLayout()
        self.android_replace_edit = QLineEdit()
        self.android_replace_edit.setReadOnly(True)
        replace_row.addWidget(self.android_replace_edit, 1)
        self.android_replace_browse = QPushButton("Browse...")
        self.android_replace_browse.clicked.connect(self._browse_android_replace_data)
        replace_row.addWidget(self.android_replace_browse)
        root.addLayout(replace_row)
        root.addSpacing(6)

        self.android_mods_check = QCheckBox("Bundle mod folders (custom_wives/custom_futas/etc.) from:")
        self.android_mods_check.toggled.connect(self._update_android_enabled_state)
        root.addWidget(self.android_mods_check)
        mods_row = QHBoxLayout()
        self.android_mods_edit = QLineEdit()
        self.android_mods_edit.setReadOnly(True)
        mods_row.addWidget(self.android_mods_edit, 1)
        self.android_mods_browse = QPushButton("Browse...")
        self.android_mods_browse.clicked.connect(self._browse_android_mods_folder)
        mods_row.addWidget(self.android_mods_browse)
        root.addLayout(mods_row)
        root.addSpacing(6)

        self.android_touch_check = QCheckBox(
            "Fix touch controls and custom character selection (recommended for ModRoom on Android)"
        )
        self.android_touch_check.setToolTip(
            "ModRoom relies on right-click and mouse-wheel scrolling that have no touchscreen "
            "equivalent, and its custom wife/futa picker doesn't work at all on Android without "
            "this. Adds long-press (= right-click) and drag-to-scroll, and fixes custom character "
            "discovery. Safe to leave on for the official game too - it only changes behavior for "
            "code paths ModRoom-style mods actually use."
        )
        self.android_touch_check.toggled.connect(self._update_android_enabled_state)
        root.addWidget(self.android_touch_check)
        root.addSpacing(10)

        patch_row = QHBoxLayout()
        self.android_patch_btn = QPushButton("Patch APK")
        self.android_patch_btn.clicked.connect(self._android_patch_clicked)
        patch_row.addWidget(self.android_patch_btn)
        patch_row.addStretch(1)
        root.addLayout(patch_row)
        root.addSpacing(6)

        root.addWidget(QLabel("Status:"))
        self.android_status_box = QTextEdit()
        self.android_status_box.setReadOnly(True)
        self.android_status_box.setFontFamily("Consolas" if sys.platform == "win32" else "Monospace")
        self.android_status_box.setSizePolicy(QSizePolicy.Policy.Expanding, QSizePolicy.Policy.Expanding)
        root.addWidget(self.android_status_box, 1)

        # NOTE: deliberately NOT calling _update_android_enabled_state() here - it also persists
        # the checkboxes' CURRENT (still-default, unchecked) state, which would run before
        # _apply_settings_to_ui() has restored them from disk and silently overwrite saved True
        # values back to False on every launch. _apply_settings_to_ui() calls it itself, after
        # restoring, once construction finishes - this widget is never shown before then anyway.
        return tab

    def _update_android_enabled_state(self) -> None:
        self.android_replace_edit.setEnabled(self.android_replace_check.isChecked())
        self.android_replace_browse.setEnabled(self.android_replace_check.isChecked())
        self.android_mods_edit.setEnabled(self.android_mods_check.isChecked())
        self.android_mods_browse.setEnabled(self.android_mods_check.isChecked())
        # Persisted here (rather than a separate handler per checkbox) since every checkbox that
        # affects enabled-state also needs its checked-state remembered - one hook point for both.
        self._settings.AndroidReplaceDataEnabled = self.android_replace_check.isChecked()
        self._settings.AndroidModsEnabled = self.android_mods_check.isChecked()
        self._settings.AndroidTouchControls = self.android_touch_check.isChecked()
        self._settings.save(self._settings_path)

    def android_log(self, message: str) -> None:
        self.android_status_box.append(f"{datetime.now():%H:%M:%S}  {message}")

    def _browse_android_apk(self) -> None:
        start = self._settings.AndroidApkPath or str(self._base_dir)
        path, _ = QFileDialog.getOpenFileName(self, "Select your Android APK", start, "Android packages (*.apk)")
        if path:
            self.android_apk_edit.setText(path)
            self._settings.AndroidApkPath = path
            self._settings.save(self._settings_path)

    def _browse_android_replace_data(self) -> None:
        start = self._settings.AndroidReplaceDataPath or (
            str(Path(self._settings.GamePath).parent) if self._settings.GamePath else str(self._base_dir))
        path, _ = QFileDialog.getOpenFileName(self, "Select the replacement data file (e.g. ModRoom's data.win)", start)
        if path:
            self.android_replace_edit.setText(path)
            self._settings.AndroidReplaceDataPath = path
            self._settings.save(self._settings_path)

    def _browse_android_mods_folder(self) -> None:
        start = self._settings.ModsPath or (str(Path(self._settings.GamePath).parent) if self._settings.GamePath else str(self._base_dir))
        selected = QFileDialog.getExistingDirectory(self, "Select the folder containing custom_wives/custom_futas/etc.", start)
        if selected:
            self.android_mods_edit.setText(selected)
            self._settings.ModsPath = selected
            self._settings.save(self._settings_path)

    def _run_apkpatcher(self, args: list[str]) -> bool:
        """Runs one ApkPatcher invocation, logging its output. Returns True on success (exit 0)."""
        try:
            result = subprocess.run(args, capture_output=True, text=True, timeout=600)
            output = (result.stdout or "") + (result.stderr or "")
            for line in output.splitlines():
                if line.strip():
                    self.android_log(line)
            return result.returncode == 0
        except subprocess.TimeoutExpired:
            self.android_log("Timed out.")
            return False
        except Exception as ex:
            self.android_log(f"Failed to run ApkPatcher: {ex}")
            return False

    def _android_patch_clicked(self) -> None:
        if not self._apkpatcher_exe.exists():
            QMessageBox.critical(self, "Missing file", f"Can't find ApkPatcher next to this launcher:\n{self._apkpatcher_exe}")
            return

        apk_path = self.android_apk_edit.text().strip()
        if not apk_path:
            QMessageBox.warning(self, "Patch APK", "Pick a source APK first.")
            return

        replace_path = None
        if self.android_replace_check.isChecked():
            replace_path = self.android_replace_edit.text().strip()
            if not replace_path:
                QMessageBox.warning(self, "Patch APK", "Pick a replacement data file, or uncheck that option.")
                return

        mods_path = None
        if self.android_mods_check.isChecked():
            mods_path = self.android_mods_edit.text().strip()
            if not mods_path:
                QMessageBox.warning(self, "Patch APK", "Pick a mods folder, or uncheck that option.")
                return

        want_touch_controls = self.android_touch_check.isChecked()

        apk_dir = Path(apk_path).resolve().parent
        apk_stem = Path(apk_path).stem
        final_out = str(apk_dir / f"{apk_stem}-toybridge.apk")
        # --touch-controls can't be combined with the base patch in one ApkPatcher run (they're
        # mutually exclusive per-invocation) - same reason the manual workflow needed two separate
        # commands. When both are wanted, run once into a throwaway intermediate file, then feed
        # that into a second run for --touch-controls, same composition already verified to work
        # for --hmv. The intermediate is deleted afterwards so it doesn't look like a second result.
        step1_out = str(apk_dir / f"{apk_stem}-toybridge-intermediate.apk") if want_touch_controls else final_out

        step1_args = [str(self._apkpatcher_exe), apk_path]
        if replace_path:
            step1_args += ["--replace-data", replace_path]
        if mods_path:
            step1_args += ["--include-mods", mods_path]
        step1_args += ["--out", step1_out, "--yes"]

        self.android_log("Patching - this can take a little while (decompiling/recompiling the game data)...")
        self.android_patch_btn.setEnabled(False)
        self.setCursor(Qt.CursorShape.WaitCursor)
        QApplication.processEvents()
        try:
            if not self._run_apkpatcher(step1_args):
                QMessageBox.critical(self, "Patch APK", "Patching failed - see the status log for details.")
                return

            if want_touch_controls:
                self.android_log("Applying touch controls / custom character fix (second pass)...")
                QApplication.processEvents()
                step2_args = [str(self._apkpatcher_exe), step1_out, "--touch-controls", "--out", final_out, "--yes"]
                if not self._run_apkpatcher(step2_args):
                    QMessageBox.critical(
                        self, "Patch APK",
                        f"The base patch succeeded, but the touch-controls pass failed - see the "
                        f"status log for details. The intermediate file is still at:\n{step1_out}",
                    )
                    return
                try:
                    os.remove(step1_out)
                except OSError:
                    pass

            QMessageBox.information(self, "Patch APK", f"Done - saved to:\n{final_out}")
        finally:
            self.android_patch_btn.setEnabled(True)
            self.unsetCursor()
            self.unsetCursor()

    # ------------------------------------------------------------- close ----

    def closeEvent(self, event) -> None:  # noqa: N802 (Qt override)
        # Unlike the old WinForms version (which could safely leave the bridge running when the
        # launcher closed, because Windows gave the bridge its own separate console window to
        # keep living in), this version pipes the bridge's output into a QThread that reads into
        # OUR window's log - so if this window disappears without stopping that thread first,
        # there's no way to see the bridge anymore anyway, AND destroying a QThread while its
        # run() is still blocked reading is a fatal error on at least Linux ("QThread: Destroyed
        # while thread is still running"). So: always stop the bridge (and properly join its
        # reader thread) before actually closing.
        self._stop_bridge()
        self._stop_hmv_ping_listener()
        if self._hmv_dialog is not None:
            self._hmv_dialog._stop_live()
        super().closeEvent(event)


def _excepthook(exc_type, exc_value, exc_tb) -> None:
    # PySide6 slots normally swallow exceptions silently (they cross a C++/Python boundary) -
    # without this override, a bug in a signal handler just does nothing instead of showing an
    # error, which is confusing to debug. Print loudly instead.
    import traceback
    traceback.print_exception(exc_type, exc_value, exc_tb)


def main() -> int:
    sys.excepthook = _excepthook
    app = QApplication(sys.argv)
    window = MainWindow()
    # Backstop for any quit path that doesn't go through the window's closeEvent (Ctrl+Q, an
    # OS shutdown/logout signal, app.quit() called directly, etc.) - without this, the same
    # "QThread: Destroyed while thread is still running" fatal error that closeEvent's own
    # cleanup guards against can still happen via those other exit paths.
    app.aboutToQuit.connect(window._stop_bridge)
    app.aboutToQuit.connect(window._stop_hmv_ping_listener)
    window.show()
    return app.exec()


if __name__ == "__main__":
    sys.exit(main())
