import threading
import time
import json
import re
import urllib.error
import urllib.parse
import urllib.request
import webbrowser
from datetime import datetime, timedelta
import tkinter as tk
from tkinter import messagebox, ttk

from .account_store import (
    StoredAccount,
    active_account_name,
    delete_account,
    list_accounts,
    normalize_account_name,
    save_account,
)
from .browser import BrowserSession
from .config import BotConfig, ROOT_DIR, load_bot_config, save_bot_config
from .config import load_account
from .server_discovery import ServerOption, fetch_ss_travi_servers
from .travian_client import AccountSnapshot, ManualVerificationRequired, TravianClient, VillageStatus
from .version import APP_VERSION


LOOP_TASKS = {
    "status": {
        "label": "Check village status",
        "action": "status",
        "implemented": True,
    },
    "scan_all_villages": {
        "label": "Scan all villages",
        "action": "scan_all_villages",
        "implemented": True,
    },
    "farmlist": {
        "label": "Farmlist",
        "action": "farmlist",
        "implemented": False,
    },
    "hero_adventures": {
        "label": "Hero adventures",
        "action": "hero_adventures",
        "implemented": False,
    },
}

DEFAULT_SETTINGS = {
    "headless": False,
    "timeout_ms": 15000,
    "manual_login_timeout_seconds": 180,
    "loop_interval_seconds": 60,
    "loop_tasks": ["status"],
    "github_releases_url": "",
    "human_like_enabled": False,
    "human_like_speed": "medium",
}

BUILDING_CATALOG = [
    {"gid": 10, "name": "Warehouse", "unique": False, "requirements": []},
    {"gid": 11, "name": "Granary / Silo", "unique": False, "requirements": []},
    {"gid": 15, "name": "Main Building", "unique": True, "requirements": []},
    {"gid": 16, "name": "Rally Point", "unique": True, "requirements": []},
    {"gid": 17, "name": "Marketplace", "unique": True, "requirements": [("Main Building", 3), ("Warehouse", 1), ("Granary", 1)]},
    {"gid": 18, "name": "Embassy", "unique": True, "requirements": [("Main Building", 1)]},
    {"gid": 19, "name": "Barracks", "unique": True, "requirements": [("Main Building", 3), ("Rally Point", 1)]},
    {"gid": 20, "name": "Stable", "unique": True, "requirements": [("Academy", 5), ("Blacksmith", 3)]},
    {"gid": 21, "name": "Workshop", "unique": True, "requirements": [("Academy", 10), ("Main Building", 5)]},
    {"gid": 22, "name": "Academy", "unique": True, "requirements": [("Barracks", 3), ("Main Building", 3)]},
    {"gid": 23, "name": "Cranny", "unique": False, "requirements": []},
    {"gid": 24, "name": "Town Hall", "unique": True, "requirements": [("Academy", 10), ("Main Building", 10)]},
    {"gid": 25, "name": "Residence", "unique": True, "requirements": [("Main Building", 5)]},
    {"gid": 26, "name": "Palace", "unique": True, "requirements": [("Embassy", 1), ("Main Building", 5)]},
    {"gid": 27, "name": "Treasury", "unique": True, "requirements": [("Main Building", 10)]},
    {"gid": 28, "name": "Trade Office", "unique": True, "requirements": [("Marketplace", 20), ("Stable", 10)]},
    {"gid": 31, "name": "Wall", "unique": True, "requirements": [("Rally Point", 1)], "special": "wall"},
    {"gid": 32, "name": "Wall", "unique": True, "requirements": [("Rally Point", 1)], "special": "wall"},
    {"gid": 33, "name": "Wall", "unique": True, "requirements": [("Rally Point", 1)], "special": "wall"},
    {"gid": 34, "name": "Stonemason", "unique": True, "requirements": [("Main Building", 5)]},
    {"gid": 37, "name": "Hero Mansion", "unique": True, "requirements": [("Main Building", 3), ("Rally Point", 1)]},
    {"gid": 38, "name": "Great Warehouse", "unique": False, "requirements": []},
    {"gid": 39, "name": "Great Granary", "unique": False, "requirements": []},
    {"gid": 41, "name": "Horse Drinking Trough", "unique": True, "requirements": [("Stable", 20)]},
    {"gid": 42, "name": "Wall", "unique": True, "requirements": [("Rally Point", 1)], "special": "wall"},
    {"gid": 43, "name": "Wall", "unique": True, "requirements": [("Rally Point", 1)], "special": "wall"},
]



class TbotUi:
    def __init__(self, root: tk.Tk) -> None:
        self.root = root
        self.root.title("Tbot Ultra")
        self.root.geometry("820x560")
        self.root.minsize(760, 520)
        self.root.configure(bg="#f4f6f8")

        self.account_var = tk.StringVar()
        self.name_var = tk.StringVar()
        self.username_var = tk.StringVar()
        self.password_var = tk.StringVar()
        self.status_var = tk.StringVar(value="Ready.")
        self.selected_village_var = tk.StringVar(value="Village: -")
        self.village_select_var = tk.StringVar(value="")
        self.tribe_var = tk.StringVar(value="Tribe: -")
        self.village_count_var = tk.StringVar(value="Villages: -")
        self.last_scan_var = tk.StringVar(value="Last scan: -")
        self.browser_state_var = tk.StringVar(value="Browser: idle")
        self.clock_var = tk.StringVar(value="Time: -")
        self.loop_state_var = tk.StringVar(value="Loop: stopped")
        self.loop_tooltip_text = tk.StringVar(value="Loop: stopped")
        self.version_var = tk.StringVar(value=f"v{APP_VERSION}")
        self.current_action_var = tk.StringVar(value="Current action: none")
        self.server_time_base: datetime | None = None
        self.server_time_monotonic: float | None = None
        self.server_time_from_page = False
        self.bot_running = False
        self.loop_running = False
        self.loop_paused = False
        self.loop_error_paused = False
        self.loop_stop_event: threading.Event | None = None
        self.loop_pause_event: threading.Event | None = None
        self.verification_running = False
        self.manual_step_popup_open = False
        self.logout_requested = False
        self.active_browser_ready = False
        self.action_queue: list[str] = []
        self.action_queue_lock = threading.Lock()
        self.action_buttons: list[ttk.Button] = []
        self.verification_button: ttk.Button | None = None
        self.summary_var = tk.StringVar(value="Run Check village status to populate the views.")
        self.latest_village_status: VillageStatus | None = None
        self.village_statuses: dict[str, VillageStatus] = {}
        self.village_box: ttk.Combobox | None = None
        self.resource_tree: ttk.Treeview | None = None
        self.building_tree: ttk.Treeview | None = None
        self.action_queue_tree: ttk.Treeview | None = None
        self.queue_tree: ttk.Treeview | None = None
        self.resource_map_frame: ttk.Frame | None = None
        self.building_map_frame: ttk.Frame | None = None
        self.loop_indicator_canvas: tk.Canvas | None = None
        self.loop_indicator_item: int | None = None
        self.alarm_button: ttk.Button | None = None
        self.activity_log: list[str] = []
        self.alarm_log: list[str] = []
        self.unacknowledged_alarms = 0
        self.log_dialog: LogDialog | None = None

        self._configure_styles()
        self._build()
        self.refresh_accounts()
        self._tick_clock()

    def _configure_styles(self) -> None:
        style = ttk.Style()
        style.theme_use("clam")
        style.configure(".", font=("Segoe UI", 9), background="#f4f6f8", foreground="#17202a")
        style.configure("App.TFrame", background="#f4f6f8")
        style.configure("Panel.TFrame", background="#ffffff", relief="flat")
        style.configure("Warning.TFrame", background="#fff7ed", relief="solid", borderwidth=1)
        style.configure("Header.TLabel", background="#f4f6f8", foreground="#101820", font=("Segoe UI Semibold", 16))
        style.configure("Subtle.TLabel", background="#f4f6f8", foreground="#5f6b7a")
        style.configure("PanelTitle.TLabel", background="#ffffff", foreground="#101820", font=("Segoe UI Semibold", 10))
        style.configure("Panel.TLabel", background="#ffffff", foreground="#17202a")
        style.configure("Status.TLabel", background="#ffffff", foreground="#344054", justify="left")
        style.configure("TEntry", fieldbackground="#ffffff", bordercolor="#d0d7de", lightcolor="#d0d7de", darkcolor="#d0d7de")
        style.configure("TCombobox", fieldbackground="#ffffff", bordercolor="#d0d7de", lightcolor="#d0d7de", darkcolor="#d0d7de")
        style.configure("TButton", padding=(6, 4), background="#eef2f6", foreground="#17202a", bordercolor="#d0d7de")
        style.map("TButton", background=[("active", "#e2e8f0"), ("disabled", "#edf0f3")])
        style.configure("Icon.TButton", padding=(6, 4), background="#eef2f6", foreground="#17202a", bordercolor="#d0d7de")
        style.configure("Map.TButton", padding=(4, 4), background="#f8fafc", foreground="#17202a", bordercolor="#d0d7de")
        style.map("Map.TButton", background=[("active", "#e0f2fe")])
        style.configure("Primary.TButton", padding=(7, 4), background="#2563eb", foreground="#ffffff", bordercolor="#2563eb")
        style.map("Primary.TButton", background=[("active", "#1d4ed8"), ("disabled", "#94a3b8")], foreground=[("disabled", "#ffffff")])
        style.configure("Danger.TButton", padding=(6, 4), background="#fff1f2", foreground="#b42318", bordercolor="#fecdd3")
        style.map("Danger.TButton", background=[("active", "#ffe4e6")])
        style.configure("AlarmOff.TButton", padding=(6, 4), background="#eef2f6", foreground="#475467", bordercolor="#d0d7de")
        style.configure("AlarmOn.TButton", padding=(6, 4), background="#fdb022", foreground="#101820", bordercolor="#dc6803")
        style.map("AlarmOn.TButton", background=[("active", "#f79009")])
        style.configure("Treeview", rowheight=23, background="#ffffff", fieldbackground="#ffffff", foreground="#17202a")
        style.configure("Treeview.Heading", font=("Segoe UI Semibold", 9), background="#f1f5f9", foreground="#17202a")

    def _build(self) -> None:
        frame = ttk.Frame(self.root, padding=10, style="App.TFrame")
        frame.pack(fill=tk.BOTH, expand=True)
        frame.columnconfigure(0, weight=1)
        frame.rowconfigure(1, weight=1)
        frame.rowconfigure(2, weight=0)

        header = ttk.Frame(frame, style="App.TFrame")
        header.grid(row=0, column=0, sticky="ew", pady=(0, 8))
        header.columnconfigure(1, weight=1)

        ttk.Label(header, text="Tbot Ultra", style="Header.TLabel").grid(row=0, column=0, sticky="w", padx=(0, 12))
        ttk.Label(header, textvariable=self.version_var, style="Subtle.TLabel").grid(row=1, column=0, sticky="w")

        session_bar = ttk.Frame(header, style="App.TFrame")
        session_bar.grid(row=0, column=1, sticky="ew")
        session_bar.columnconfigure(1, weight=1)

        ttk.Label(session_bar, text="Account", style="Subtle.TLabel").grid(row=0, column=0, sticky="w", padx=(0, 6))
        self.account_box = ttk.Combobox(session_bar, textvariable=self.account_var, state="readonly", width=18)
        self.account_box.grid(row=0, column=1, sticky="ew", padx=(0, 6))
        self.account_box.bind("<<ComboboxSelected>>", lambda _: self.load_selected_account())

        ttk.Button(session_bar, text="Accounts", command=self.open_accounts).grid(row=0, column=2, sticky="ew", padx=(0, 6))
        ttk.Button(session_bar, text="Login", command=self.start_login, style="Primary.TButton").grid(row=0, column=3, sticky="ew", padx=(0, 4))
        ttk.Button(session_bar, text="Logout", command=self.start_travian_logout).grid(row=0, column=4, sticky="ew", padx=(0, 4))
        ttk.Button(session_bar, text="Check village status", command=self.start_village_status).grid(row=0, column=5, sticky="ew", padx=(8, 4))
        start_button = ttk.Button(session_bar, text=">", width=3, command=self.start_loop, style="Icon.TButton")
        start_button.grid(row=0, column=6, sticky="ew", padx=(0, 4))
        Tooltip(start_button, tk.StringVar(value="Start loop"))
        pause_button = ttk.Button(session_bar, text="||", width=3, command=self.pause_loop, style="Icon.TButton")
        pause_button.grid(row=0, column=7, sticky="ew", padx=(0, 4))
        Tooltip(pause_button, tk.StringVar(value="Pause loop"))
        stop_button = ttk.Button(session_bar, text="[]", width=3, command=self.stop_loop, style="Icon.TButton")
        stop_button.grid(row=0, column=8, sticky="ew", padx=(0, 4))
        Tooltip(stop_button, tk.StringVar(value="Stop loop"))
        loop_state = ttk.Frame(session_bar, style="App.TFrame")
        loop_state.grid(row=0, column=9, sticky="e", padx=(4, 0))
        self.loop_indicator_canvas = tk.Canvas(loop_state, width=14, height=14, highlightthickness=0, bg="#f4f6f8")
        self.loop_indicator_canvas.grid(row=0, column=0, sticky="w")
        self.loop_indicator_item = self.loop_indicator_canvas.create_oval(2, 2, 12, 12, fill="#98a2b3", outline="#667085")
        Tooltip(self.loop_indicator_canvas, self.loop_tooltip_text)

        tools = ttk.Frame(header, style="App.TFrame")
        tools.grid(row=0, column=2, sticky="e", padx=(8, 0))
        self.alarm_button = ttk.Button(tools, text="!", width=3, command=self.open_logs, style="AlarmOff.TButton")
        self.alarm_button.grid(row=0, column=0, sticky="e", padx=(0, 4))
        contact_button = ttk.Button(tools, text="?", width=3, command=self.open_contact, style="Icon.TButton")
        contact_button.grid(row=0, column=1, sticky="e", padx=(0, 4))
        Tooltip(contact_button, tk.StringVar(value="Contact"))
        update_button = ttk.Button(tools, text="v", width=3, command=self.open_update, style="Icon.TButton")
        update_button.grid(row=0, column=2, sticky="e", padx=(0, 4))
        Tooltip(update_button, tk.StringVar(value="Updates"))
        settings_button = ttk.Button(tools, text="⚙", width=3, command=self.open_settings, style="Icon.TButton")
        settings_button.grid(row=0, column=3, sticky="e")
        Tooltip(settings_button, tk.StringVar(value="Settings"))

        state_bar = ttk.Frame(header, style="App.TFrame")
        state_bar.grid(row=1, column=1, columnspan=2, sticky="ew", pady=(4, 0))
        state_bar.columnconfigure(1, weight=2)
        state_bar.columnconfigure((2, 3, 4, 5, 6), weight=1)
        ttk.Label(state_bar, text="Village", style="Subtle.TLabel").grid(row=0, column=0, sticky="w", padx=(0, 5))
        self.village_box = ttk.Combobox(state_bar, textvariable=self.village_select_var, state="readonly", width=18)
        self.village_box.grid(row=0, column=1, sticky="ew", padx=(0, 8))
        self.village_box.bind("<<ComboboxSelected>>", lambda _: self._render_selected_village_status())
        ttk.Label(state_bar, textvariable=self.village_count_var, style="Subtle.TLabel").grid(row=0, column=2, sticky="w")
        ttk.Label(state_bar, textvariable=self.tribe_var, style="Subtle.TLabel").grid(row=0, column=3, sticky="w")
        ttk.Label(state_bar, textvariable=self.last_scan_var, style="Subtle.TLabel").grid(row=0, column=4, sticky="w")
        ttk.Label(state_bar, textvariable=self.browser_state_var, style="Subtle.TLabel").grid(row=0, column=5, sticky="w")
        ttk.Label(state_bar, textvariable=self.clock_var, style="Subtle.TLabel").grid(row=0, column=6, sticky="w")

        self._build_main_pages(frame)

        status_bar = ttk.Frame(frame, padding=(8, 6), style="Panel.TFrame")
        status_bar.grid(row=2, column=0, sticky="ew", pady=(8, 0))
        status_bar.columnconfigure(1, weight=1)
        ttk.Label(status_bar, text="Status", style="PanelTitle.TLabel").grid(row=0, column=0, sticky="w", padx=(0, 8))
        ttk.Label(status_bar, textvariable=self.status_var, style="Status.TLabel", wraplength=700).grid(row=0, column=1, sticky="ew")

    def _build_main_pages(self, parent: ttk.Frame) -> None:
        notebook = ttk.Notebook(parent)
        notebook.grid(row=1, column=0, sticky="nsew", pady=(8, 0))

        dashboard = ttk.Frame(notebook, padding=10, style="Panel.TFrame")
        resources = ttk.Frame(notebook, padding=10, style="Panel.TFrame")
        buildings = ttk.Frame(notebook, padding=10, style="Panel.TFrame")
        queue = ttk.Frame(notebook, padding=10, style="Panel.TFrame")

        notebook.add(dashboard, text="Dashboard")
        notebook.add(resources, text="Resources")
        notebook.add(buildings, text="Buildings")
        notebook.add(queue, text="Queue")

        self._build_dashboard_page(dashboard)
        self._build_resources_page(resources)
        self._build_buildings_page(buildings)
        self._build_queue_page(queue)

    def _build_dashboard_page(self, parent: ttk.Frame) -> None:
        parent.columnconfigure(0, weight=1)
        parent.rowconfigure(1, weight=1)

        actions = ttk.Frame(parent, style="Panel.TFrame")
        actions.grid(row=0, column=0, sticky="ew", pady=(0, 10))
        actions.columnconfigure((0, 1), weight=1)

        all_villages_button = ttk.Button(actions, text="Scan all villages", command=self.start_scan_all_villages, style="Primary.TButton")
        self.verification_button = ttk.Button(actions, text="Open verification browser", command=self.start_verification_browser)
        all_villages_button.grid(row=0, column=0, sticky="ew", padx=(0, 5))
        self.verification_button.grid(row=0, column=1, sticky="ew", padx=(5, 0))
        self.action_buttons = [all_villages_button]

        summary_frame = ttk.Frame(parent, padding=10, style="Panel.TFrame")
        summary_frame.grid(row=1, column=0, sticky="nsew")
        summary_frame.columnconfigure(0, weight=1)
        parent.rowconfigure(1, weight=1)
        ttk.Label(summary_frame, text="Overview", style="PanelTitle.TLabel").grid(row=0, column=0, sticky="w", pady=(0, 6))
        ttk.Label(summary_frame, textvariable=self.summary_var, style="Status.TLabel", wraplength=780).grid(row=1, column=0, sticky="nw")

    def _build_resources_page(self, parent: ttk.Frame) -> None:
        parent.columnconfigure(0, weight=2)
        parent.columnconfigure(1, weight=1)
        parent.rowconfigure(1, weight=1)

        header = ttk.Frame(parent, style="Panel.TFrame")
        header.grid(row=0, column=0, columnspan=2, sticky="ew", pady=(0, 8))
        header.columnconfigure(0, weight=1)
        ttk.Label(header, text="Resources", style="PanelTitle.TLabel").grid(row=0, column=0, sticky="w")
        resource_actions = ttk.Frame(header, style="Panel.TFrame")
        resource_actions.grid(row=0, column=1, sticky="e")
        ttk.Button(resource_actions, text="Upgrade all to level", command=self.open_upgrade_all_resources).grid(row=0, column=0)

        self.resource_map_frame = ttk.Frame(parent, style="Panel.TFrame")
        self.resource_map_frame.grid(row=1, column=0, sticky="nsew", padx=(0, 8))
        for column in range(6):
            self.resource_map_frame.columnconfigure(column, weight=1)

        table_frame = ttk.Frame(parent, style="Panel.TFrame")
        table_frame.grid(row=1, column=1, sticky="nsew")
        self.resource_tree = self._create_tree(table_frame, ("slot", "name", "type", "level"), ("Slot", "Name", "Type", "Level"))

    def _build_buildings_page(self, parent: ttk.Frame) -> None:
        parent.columnconfigure(0, weight=2)
        parent.columnconfigure(1, weight=1)
        parent.rowconfigure(1, weight=1)

        header = ttk.Frame(parent, style="Panel.TFrame")
        header.grid(row=0, column=0, columnspan=2, sticky="ew", pady=(0, 8))
        header.columnconfigure(0, weight=1)
        ttk.Label(header, text="Buildings", style="PanelTitle.TLabel").grid(row=0, column=0, sticky="w")

        self.building_map_frame = ttk.Frame(parent, style="Panel.TFrame")
        self.building_map_frame.grid(row=1, column=0, sticky="nsew", padx=(0, 8))
        for column in range(5):
            self.building_map_frame.columnconfigure(column, weight=1)

        table_frame = ttk.Frame(parent, style="Panel.TFrame")
        table_frame.grid(row=1, column=1, sticky="nsew")
        self.building_tree = self._create_tree(table_frame, ("slot", "name", "level"), ("Slot", "Building", "Level"))

    def _build_queue_page(self, parent: ttk.Frame) -> None:
        parent.columnconfigure(0, weight=1)
        parent.rowconfigure(3, weight=1)
        parent.rowconfigure(5, weight=1)

        ttk.Label(parent, text="Action queue", style="PanelTitle.TLabel").grid(row=0, column=0, sticky="w", pady=(0, 4))
        ttk.Label(parent, textvariable=self.current_action_var, style="Status.TLabel").grid(row=1, column=0, sticky="ew", pady=(0, 6))
        queue_buttons = ttk.Frame(parent, style="Panel.TFrame")
        queue_buttons.grid(row=2, column=0, sticky="ew", pady=(0, 6))
        queue_buttons.columnconfigure((0, 1, 2), weight=1)
        ttk.Button(queue_buttons, text="Remove selected", command=self.remove_selected_queued_action).grid(
            row=0, column=0, sticky="ew", padx=(0, 5)
        )
        ttk.Button(queue_buttons, text="Clear queue", command=self.clear_action_queue).grid(
            row=0, column=1, sticky="ew", padx=5
        )
        action_table = ttk.Frame(parent, style="Panel.TFrame")
        action_table.grid(row=3, column=0, sticky="nsew", pady=(0, 10))
        self.action_queue_tree = self._create_tree(action_table, ("position", "action"), ("#", "Waiting action"))

        ttk.Label(parent, text="Travian build queue", style="PanelTitle.TLabel").grid(row=4, column=0, sticky="w", pady=(0, 4))
        build_table = ttk.Frame(parent, style="Panel.TFrame")
        build_table.grid(row=5, column=0, sticky="nsew")
        self.queue_tree = self._create_tree(build_table, ("text", "time"), ("Item", "Time left"))
        self._render_empty_maps()

    def _create_tree(self, parent: ttk.Frame, columns: tuple[str, ...], headings: tuple[str, ...]) -> ttk.Treeview:
        parent.rowconfigure(0, weight=1)
        parent.columnconfigure(0, weight=1)
        tree = ttk.Treeview(parent, columns=columns, show="headings", selectmode="browse")
        for column, heading in zip(columns, headings):
            tree.heading(column, text=heading)
            width = 90 if column in {"slot", "level", "time"} else 260
            tree.column(column, width=width, anchor="w")
        tree.grid(row=0, column=0, sticky="nsew")
        scrollbar = ttk.Scrollbar(parent, orient="vertical", command=tree.yview)
        scrollbar.grid(row=0, column=1, sticky="ns")
        tree.configure(yscrollcommand=scrollbar.set)
        return tree

    def refresh_accounts(self) -> None:
        accounts = list_accounts()
        names = [account.name for account in accounts]
        self.account_box["values"] = names

        selected = active_account_name()
        if selected in names:
            self.account_var.set(selected)
        elif names:
            self.account_var.set(names[0])
        else:
            self.account_var.set("")
            self.name_var.set("main")
            self.username_var.set("")
            self.password_var.set("")
            self.status_var.set("Add your first account, then save it.")
            return

        self.load_selected_account()

    def load_selected_account(self) -> None:
        selected = self.account_var.get()
        for account in list_accounts():
            if account.name == selected:
                self.name_var.set(account.name)
                self.username_var.set(account.username)
                self.password_var.set(account.password)
                self._apply_account_server(account)
                self.status_var.set(f"Loaded account '{account.name}'.")
                return

    def save_current_account(self) -> bool:
        try:
            normalized_name = normalize_account_name(self.name_var.get())
            current_config = load_bot_config()
            account = StoredAccount(
                name=normalized_name,
                username=self.username_var.get(),
                password=self.password_var.get(),
                server_name=current_config.server_name,
                server_url=current_config.base_url,
            )
            save_account(account)
            self.name_var.set(normalized_name)
            self.status_var.set(f"Saved account '{normalized_name}'.")
            self.refresh_accounts()
            return True
        except RuntimeError as exc:
            messagebox.showerror("Could not save account", str(exc))
            return False

    def _selected_account_name(self) -> str | None:
        account_name = self.account_var.get().strip() or self.name_var.get().strip()
        if not account_name:
            messagebox.showerror("Missing account", "Select or add an account first.")
            return None
        return normalize_account_name(account_name)

    def _apply_account_server(self, account: StoredAccount) -> None:
        if not account.server_url:
            return

        current = load_bot_config()
        config = BotConfig(
            server_name=account.server_name or account.server_url,
            base_url=account.server_url,
            login_path="/login.php",
            village_overview_path="/dorf1.php",
            headless=current.headless,
            timeout_ms=current.timeout_ms,
            manual_login_timeout_seconds=current.manual_login_timeout_seconds,
            loop_interval_seconds=current.loop_interval_seconds,
            loop_tasks=current.loop_tasks,
            github_releases_url=current.github_releases_url,
            human_like_enabled=current.human_like_enabled,
            human_like_speed=current.human_like_speed,
        )
        save_bot_config(config)

    def start_login(self) -> None:
        self._start_bot_action("login")

    def start_village_status(self) -> None:
        self._start_bot_action("status")

    def start_scan_all_villages(self) -> None:
        self._start_bot_action("scan_all_villages")

    def start_loop(self) -> None:
        if self.loop_running and self.loop_paused:
            if self.loop_pause_event:
                self.loop_pause_event.clear()
            self.loop_paused = False
            self.loop_error_paused = False
            self._set_loop_state("running")
            self._set_status("Loop resumed.")
            return

        if self.loop_running:
            self._set_status("Loop is already running.")
            return

        if self.bot_running:
            messagebox.showinfo("Bot is busy", "Stop or close the current bot/browser action before starting the loop.")
            return

        account_name = self._selected_account_name()
        if not account_name:
            return

        config = load_bot_config()
        task_ids = self._configured_loop_task_ids(config)
        if not task_ids:
            self._show_not_available("Continuous loop", "No loop tasks are selected in Settings.")
            return

        unimplemented = [task_id for task_id in task_ids if not LOOP_TASKS.get(task_id, {}).get("implemented")]
        if unimplemented:
            labels = ", ".join(LOOP_TASKS.get(task_id, {}).get("label", task_id) for task_id in unimplemented)
            self._show_not_available("Continuous loop", f"These selected tasks are not implemented yet: {labels}.")
            return

        self.loop_stop_event = threading.Event()
        self.loop_pause_event = threading.Event()
        self.loop_running = True
        self.loop_paused = False
        self.loop_error_paused = False
        self.bot_running = True
        self.logout_requested = False
        self.active_browser_ready = False
        self._set_current_action(None)
        self._set_actions_enabled(False)
        self._set_loop_state("running")
        self._set_browser_state("opening")
        self._set_status(f"Starting continuous loop for account '{account_name}'...")

        thread = threading.Thread(target=self._run_loop, args=(account_name, task_ids), daemon=True)
        thread.start()

    def pause_loop(self) -> None:
        if not self.loop_running:
            self._show_not_available("Pause loop", "The continuous loop is not running.")
            return
        if self.loop_pause_event:
            self.loop_pause_event.set()
        self.loop_paused = True
        self._set_loop_state("paused")
        self._set_status("Loop paused.")

    def stop_loop(self) -> None:
        if not self.loop_running and not self.loop_error_paused:
            self._set_loop_state("stopped")
            self._set_status("Loop is already stopped.")
            return
        if self.loop_stop_event:
            self.loop_stop_event.set()
        if self.loop_pause_event:
            self.loop_pause_event.clear()
        self.loop_paused = False
        self.loop_error_paused = False
        if not self.loop_running:
            self._mark_loop_stopped()
            return
        self._set_status("Stopping loop after the current step...")

    def _configured_loop_task_ids(self, config: BotConfig) -> list[str]:
        return [task_id for task_id in config.loop_tasks if task_id in LOOP_TASKS]

    def _run_loop(self, account_name: str, task_ids: list[str]) -> None:
        try:
            config = load_bot_config()
            account = load_account(account_name)
            browser_session = BrowserSession(config, account)

            with browser_session as page:
                client = TravianClient(
                    page,
                    config,
                    account,
                    interactive=False,
                    browser_visible=not config.headless,
                    status_callback=self._set_status,
                    manual_step_callback=self._show_manual_step_popup,
                )
                self._set_status("Loop browser opened. Logging in if needed...")
                self._set_browser_state("logging in")
                client.login()
                self._sync_server_clock(client.server_time_utc)
                self._sync_visible_server_clock(client)
                account_snapshot = client.read_account_snapshot()
                self.root.after(0, self._render_account_snapshot, account_snapshot)
                self.active_browser_ready = True
                self._set_browser_state("loop running")

                while not self._loop_should_stop():
                    if self._wait_while_loop_paused():
                        break

                    for task_id in task_ids:
                        if self._loop_should_stop() or self._wait_while_loop_paused():
                            break

                        self._set_loop_state("running")
                        client.wait_between_actions()
                        summary = self._run_loop_task(task_id, client)
                        browser_session.save_state()
                        self._set_status(f"Loop completed: {summary}")

                    if not self._loop_should_stop():
                        self._loop_sleep(config.loop_interval_seconds)
        except ManualVerificationRequired as exc:
            self._add_alarm("warning", str(exc))
            self._pause_loop_after_error(str(exc))
        except Exception as exc:
            self._add_alarm("error", f"Loop error: {exc}")
            self._pause_loop_after_error(f"Loop error: {exc}")
        finally:
            if not self.loop_error_paused:
                self.root.after(0, self._mark_loop_stopped)

    def _loop_should_stop(self) -> bool:
        return bool(self.loop_stop_event and self.loop_stop_event.is_set())

    def _wait_while_loop_paused(self) -> bool:
        while self.loop_pause_event and self.loop_pause_event.is_set():
            if self._loop_should_stop():
                return True
            time.sleep(0.5)
        return self._loop_should_stop()

    def _loop_sleep(self, seconds: int) -> None:
        deadline = time.monotonic() + max(1, seconds)
        while time.monotonic() < deadline:
            if self._loop_should_stop() or (self.loop_pause_event and self.loop_pause_event.is_set()):
                return
            time.sleep(0.5)

    def _run_loop_task(self, task_id: str, client: TravianClient) -> str:
        task = LOOP_TASKS.get(task_id)
        if not task or not task.get("implemented"):
            raise RuntimeError(f"Loop task '{task_id}' is not implemented.")

        handler_name = f"_loop_task_{task_id}"
        handler = getattr(self, handler_name, None)
        if not handler:
            handler = self._loop_task_browser_action

        self._set_status(f"Loop running: {task['label']}...")
        return handler(task_id, client)

    def _loop_task_browser_action(self, task_id: str, client: TravianClient) -> str:
        task = LOOP_TASKS[task_id]
        return self._perform_browser_action(str(task["action"]), client)

    def _loop_task_status(self, task_id: str, client: TravianClient) -> str:
        return self._perform_browser_action("status", client)

    def _loop_task_scan_all_villages(self, task_id: str, client: TravianClient) -> str:
        return self._perform_browser_action("scan_all_villages", client)

    def _pause_loop_after_error(self, message: str) -> None:
        self.loop_error_paused = True
        self.loop_paused = True
        self.loop_running = False
        self.bot_running = False
        self.active_browser_ready = False
        self._set_actions_enabled(True)
        self._set_loop_state("paused")
        self._set_browser_state("paused after error")
        self._set_status(f"{message}\n\nLoop paused because of an error. Press Start to try again, or Stop to clear.")

    def _mark_loop_stopped(self) -> None:
        self.loop_running = False
        self.loop_paused = False
        self.loop_error_paused = False
        self.bot_running = False
        self.logout_requested = False
        self.active_browser_ready = False
        self.loop_stop_event = None
        self.loop_pause_event = None
        self._set_actions_enabled(True)
        self._set_loop_state("stopped")
        self._set_browser_state("idle")

    def start_travian_logout(self) -> None:
        if self.loop_running:
            self._show_not_available("Logout", "Stop the continuous loop before logging out.")
            return
        if self.bot_running:
            if self.active_browser_ready:
                self.logout_requested = True
                self._set_status("Logout requested. The bot will log out in the open Chromium window.")
            else:
                messagebox.showinfo("Bot is busy", "Wait until the browser is ready, then press Logout again.")
            return

        account_name = self._selected_account_name()
        if not account_name:
            return
        self.bot_running = True
        self._set_actions_enabled(False)
        self._set_status(f"Logging out account '{account_name}' from Travian...")
        self._set_browser_state("opening")
        thread = threading.Thread(target=self._run_travian_logout, args=(account_name,), daemon=True)
        thread.start()

    def _run_travian_logout(self, account_name: str) -> None:
        try:
            config = load_bot_config()
            account = load_account(account_name)
            browser_session = BrowserSession(config, account, headless_override=False)

            with browser_session as page:
                client = TravianClient(
                    page,
                    config,
                    account,
                    interactive=False,
                    browser_visible=True,
                    status_callback=self._set_status,
                    manual_step_callback=self._show_manual_step_popup,
                )
                self.active_browser_ready = True
                self._set_browser_state("open")
                client.logout()
                self._sync_server_clock(client.server_time_utc)
                browser_session.save_state()
                self._set_browser_state("logged out")
                self._set_status(f"Logged out account '{account_name}' from Travian. Chromium is still open.")
                self._wait_for_browser_close(page)
        except ManualVerificationRequired as exc:
            self._add_alarm("warning", str(exc))
            self._set_status(f"{exc}\n\nOpen verification browser if needed, then try Logout again.")
        except Exception as exc:
            self._add_alarm("error", f"Logout error: {exc}")
            self._set_status(f"Logout error: {exc}")
        finally:
            self.root.after(0, self._mark_bot_stopped)

    def _start_bot_action(self, action: str) -> None:
        if self.loop_running or self.loop_error_paused:
            self._show_not_available(self._action_label(action), "Stop the continuous loop before running a manual action.")
            return

        account_name = self._selected_account_name()
        if not account_name:
            return

        queued_action = self._attach_selected_village(action)
        self._enqueue_action(queued_action)

        if self.bot_running:
            self._set_status(f"Queued action: {self._action_label(queued_action)}.")
            return
        self.bot_running = True
        self.logout_requested = False
        self.active_browser_ready = False
        self._set_status(f"Queued action: {self._action_label(queued_action)}. Opening visible browser for account '{account_name}'...")
        self._set_browser_state("opening")

        thread = threading.Thread(target=self._run_bot_action, args=(account_name,), daemon=True)
        thread.start()

    def _run_bot_action(self, account_name: str) -> None:
        try:
            config = load_bot_config()
            account = load_account(account_name)
            browser_session = BrowserSession(config, account)

            with browser_session as page:
                client = TravianClient(
                    page,
                    config,
                    account,
                    interactive=False,
                    browser_visible=not config.headless,
                    status_callback=self._set_status,
                    manual_step_callback=self._show_manual_step_popup,
                )
                self._set_status("Browser opened. Logging in if needed...")
                self._set_browser_state("logging in")
                client.login()
                self._sync_server_clock(client.server_time_utc)
                self._sync_visible_server_clock(client)
                account_snapshot = client.read_account_snapshot()
                self.root.after(0, self._render_account_snapshot, account_snapshot)
                self.active_browser_ready = True
                self._set_browser_state("open")
                self._set_actions_enabled(True)

                self._process_action_queue(client, browser_session)
                browser_session.save_state()
                self._set_status("Action queue is empty.\n\nBrowser is still open. Close Chromium when you are done.")
                self._wait_for_browser_close_or_logout(page, client, browser_session, account_name)
        except ManualVerificationRequired as exc:
            self._add_alarm("warning", str(exc))
            self._set_status(
                f"{exc}\n\nUse 'Open verification browser', solve the manual step, close Chromium, then run the action again."
            )
        except Exception as exc:
            self._add_alarm("error", f"Bot action error: {exc}")
            self._set_status(f"Error: {exc}")
        finally:
            self.root.after(0, self._mark_bot_stopped)

    def _process_action_queue(self, client: TravianClient, browser_session: BrowserSession) -> None:
        while True:
            action = self._dequeue_action()
            if not action:
                self._set_current_action(None)
                return

            label = self._action_label(action)
            self._set_current_action(label)
            self._set_status(f"Running queued action: {label}...")
            self._set_browser_state("running")
            summary = self._perform_browser_action(action, client)
            browser_session.save_state()
            self._set_status(f"Completed queued action: {summary}")

    def _perform_browser_action(self, action: str, client: TravianClient) -> str:
        village_name, village_url, action = self._split_village_action(action)
        if village_name or village_url:
            client.switch_to_village(village_name, village_url)

        if action == "login":
            return "Login completed."

        if action.startswith("upgrade_resource:"):
            _, slot_text, target = action.split(":", 2)
            slot_id = int(slot_text)
            if target == "max":
                summary = client.upgrade_resource_to_max(slot_id)
            else:
                summary = client.upgrade_resource_to_level(slot_id, int(target))
            status = client.read_village_status()
            self._sync_visible_server_clock(client)
            self.root.after(0, self._render_village_status, status)
            return summary

        if action.startswith("upgrade_building:"):
            _, slot_text, target = action.split(":", 2)
            slot_id = int(slot_text)
            if target == "max":
                summary = client.upgrade_building_to_max(slot_id)
            else:
                summary = client.upgrade_building_to_level(slot_id, int(target))
            status = client.read_village_status()
            self._sync_visible_server_clock(client)
            self.root.after(0, self._render_village_status, status)
            return summary

        if action.startswith("upgrade_all_resources:"):
            _, target = action.split(":", 1)
            summary = client.upgrade_all_resources_to_level(int(target))
            status = client.read_village_status()
            self._sync_visible_server_clock(client)
            self.root.after(0, self._render_village_status, status)
            return summary

        if action.startswith("construct_building:"):
            _, slot_text, gid_text = action.split(":", 2)
            slot_id = int(slot_text)
            gid = int(gid_text)
            building = self._building_catalog_by_gid(gid)
            name = str(building["name"]) if building else f"Building gid {gid}"
            summary = client.construct_building(slot_id, gid, name)
            status = client.read_village_status()
            self._sync_visible_server_clock(client)
            self.root.after(0, self._render_village_status, status)
            return summary

        if action == "status":
            self._set_status("Reading village status...")
            result = client.read_village_status()
            self._sync_visible_server_clock(client)
            self.root.after(0, self._render_village_status, result)
            return self._format_village_status(result)

        if action == "scan_all_villages":
            self._set_status("Scanning all villages...")
            results = client.read_all_village_statuses()
            self._sync_visible_server_clock(client)
            self.root.after(0, self._render_all_village_statuses, results)
            return f"Scanned {len(results)} villages."

        raise RuntimeError(f"Action '{action}' is not implemented yet.")

    def _action_label(self, action: str) -> str:
        village_name, _village_url, base_action = self._split_village_action(action)
        labels = {
            "login": "Login",
            "status": "Check village status",
            "scan_all_villages": "Scan all villages",
        }
        action = base_action
        if action.startswith("upgrade_resource:"):
            _, slot_id, target = action.split(":", 2)
            label = f"Upgrade resource slot {slot_id} to {'max' if target == 'max' else f'level {target}'}"
            return self._with_village_label(label, village_name)
        if action.startswith("upgrade_building:"):
            _, slot_id, target = action.split(":", 2)
            label = f"Upgrade building slot {slot_id} to {'max' if target == 'max' else f'level {target}'}"
            return self._with_village_label(label, village_name)
        if action.startswith("upgrade_all_resources:"):
            _, target = action.split(":", 1)
            return self._with_village_label(f"Upgrade all resources to level {target}", village_name)
        if action.startswith("construct_building:"):
            _, slot_id, gid = action.split(":", 2)
            building = self._building_catalog_by_gid(int(gid))
            name = str(building["name"]) if building else f"gid {gid}"
            return self._with_village_label(f"Build {name} in slot {slot_id}", village_name)
        return self._with_village_label(labels.get(action, action), village_name)

    def _with_village_label(self, label: str, village_name: str) -> str:
        return f"{label} ({village_name})" if village_name else label

    def _action_needs_village(self, action: str) -> bool:
        return action.startswith(
            (
                "upgrade_resource:",
                "upgrade_building:",
                "upgrade_all_resources:",
                "construct_building:",
            )
        )

    def _attach_selected_village(self, action: str) -> str:
        if not self._action_needs_village(action):
            return action

        village_name = self.village_select_var.get()
        village_url = self._selected_village_url(village_name)
        if not village_name and not village_url:
            return action

        encoded_name = urllib.parse.quote(village_name or "", safe="")
        encoded_url = urllib.parse.quote(village_url or "", safe="")
        return f"village:{encoded_name}:{encoded_url}:{action}"

    def _split_village_action(self, action: str) -> tuple[str, str | None, str]:
        if not action.startswith("village:"):
            return "", None, action
        parts = action.split(":", 3)
        if len(parts) != 4:
            return "", None, action
        village_name = urllib.parse.unquote(parts[1])
        village_url = urllib.parse.unquote(parts[2]) or None
        return village_name, village_url, parts[3]

    def _selected_village_url(self, village_name: str) -> str | None:
        status = self.village_statuses.get(village_name) if village_name else self.latest_village_status
        if not status:
            return None
        for village in status.villages:
            if village.name == village_name and village.url:
                return village.url
        return None

    def _enqueue_action(self, action: str) -> None:
        with self.action_queue_lock:
            self.action_queue.append(action)
        self._refresh_action_queue()

    def _remove_queued_action(self, action: str) -> None:
        with self.action_queue_lock:
            if action in self.action_queue:
                self.action_queue.remove(action)
        self._refresh_action_queue()

    def remove_selected_queued_action(self) -> None:
        if not self.action_queue_tree:
            return
        selection = self.action_queue_tree.selection()
        if not selection:
            messagebox.showinfo("Queue", "Select a queued action first.")
            return
        try:
            index = int(selection[0])
        except ValueError:
            return
        with self.action_queue_lock:
            if 0 <= index < len(self.action_queue):
                removed = self.action_queue.pop(index)
            else:
                return
        self._set_status(f"Removed queued action: {self._action_label(removed)}.")
        self._refresh_action_queue()

    def clear_action_queue(self) -> None:
        with self.action_queue_lock:
            count = len(self.action_queue)
            self.action_queue.clear()
        self._refresh_action_queue()
        self._set_status(f"Cleared {count} queued actions.")

    def _dequeue_action(self) -> str | None:
        with self.action_queue_lock:
            action = self.action_queue.pop(0) if self.action_queue else None
        self._refresh_action_queue()
        return action

    def _has_queued_actions(self) -> bool:
        with self.action_queue_lock:
            return bool(self.action_queue)

    def _action_queue_snapshot(self) -> list[str]:
        with self.action_queue_lock:
            return list(self.action_queue)

    def _refresh_action_queue(self) -> None:
        self.root.after(0, self._refresh_action_queue_on_ui_thread)

    def _refresh_action_queue_on_ui_thread(self) -> None:
        if not self.action_queue_tree:
            return
        self._clear_tree(self.action_queue_tree)
        for index, action in enumerate(self._action_queue_snapshot()):
            self.action_queue_tree.insert("", "end", iid=str(index), values=(index + 1, self._action_label(action)))

    def _set_current_action(self, label: str | None) -> None:
        text = f"Current action: {label}" if label else "Current action: none"
        self.root.after(0, self.current_action_var.set, text)

    def start_verification_browser(self) -> None:
        if self.verification_running:
            messagebox.showinfo("Verification browser is already open", "Close the current Chromium window first.")
            return

        account_name = self._selected_account_name()
        if not account_name:
            return
        self.verification_running = True
        self._set_status(f"Opening visible verification browser for account '{account_name}'...")
        self._set_browser_state("verification")
        thread = threading.Thread(target=self._run_verification_browser, args=(account_name,), daemon=True)
        thread.start()

    def _run_verification_browser(self, account_name: str) -> None:
        try:
            config = load_bot_config()
            account = load_account(account_name)
            browser_session = BrowserSession(config, account, headless_override=False)

            with browser_session as page:
                client = TravianClient(
                    page,
                    config,
                    account,
                    interactive=False,
                    browser_visible=True,
                    status_callback=self._set_status,
                    manual_step_callback=self._show_manual_step_popup,
                )
                self._set_status("Verification browser opened. Solve any manual step in Chromium.")
                client.login()
                self._sync_server_clock(client.server_time_utc)
                self._sync_visible_server_clock(client)
                browser_session.save_state()
                self._set_browser_state("verified")
                self._set_status("Verification/login completed. Session saved. Closing Chromium...")
        except Exception as exc:
            self._add_alarm("error", f"Verification browser error: {exc}")
            self._set_status(f"Verification browser error: {exc}")
        finally:
            self.root.after(0, self._mark_verification_stopped)

    def _mark_verification_stopped(self) -> None:
        self.verification_running = False
        if not self.bot_running:
            self._set_browser_state("idle")

    def _wait_for_browser_close(self, page) -> None:
        while True:
            try:
                if page.is_closed():
                    self._set_browser_state("closed")
                    return
            except Exception:
                self._set_browser_state("closed")
                return
            time.sleep(0.5)

    def _wait_for_browser_close_or_logout(
        self,
        page,
        client: TravianClient,
        browser_session: BrowserSession,
        account_name: str,
    ) -> None:
        while True:
            try:
                if page.is_closed():
                    self._set_browser_state("closed")
                    return
                if self.logout_requested:
                    self._set_status(f"Logging out account '{account_name}' in the open Chromium window...")
                    self._set_actions_enabled(False)
                    self._set_browser_state("logging out")
                    client.logout()
                    self._sync_server_clock(client.server_time_utc)
                    browser_session.save_state()
                    self.logout_requested = False
                    self._set_actions_enabled(True)
                    self._set_browser_state("logged out")
                    self._set_status(f"Logged out account '{account_name}' from Travian. Chromium is still open.")

                if self._has_queued_actions():
                    self._set_status("Running queued actions in the open Chromium window...")
                    client.login()
                    self._sync_server_clock(client.server_time_utc)
                    self._sync_visible_server_clock(client)
                    account_snapshot = client.read_account_snapshot()
                    self.root.after(0, self._render_account_snapshot, account_snapshot)
                    self._process_action_queue(client, browser_session)
                    self._set_browser_state("open")
                    self._set_status("Action queue is empty.\n\nBrowser is still open. Close Chromium when you are done.")
            except Exception as exc:
                self._add_alarm("error", f"Browser action error: {exc}")
                self._set_actions_enabled(True)
                self.logout_requested = False
                self._set_current_action(None)
                self._set_status(f"Browser action error: {exc}")
            time.sleep(0.5)

    def _mark_bot_stopped(self) -> None:
        self.bot_running = False
        self.logout_requested = False
        self.active_browser_ready = False
        self._set_current_action(None)
        self._set_actions_enabled(True)

    def _set_status(self, text: str) -> None:
        self.root.after(0, self._set_status_on_ui_thread, text)

    def _set_status_on_ui_thread(self, text: str) -> None:
        self.status_var.set(text)
        self._add_activity_log(text)

    def _add_activity_log(self, text: str) -> None:
        timestamp = datetime.now().strftime("%H:%M:%S")
        self.activity_log.append(f"[{timestamp}] {text}")
        if len(self.activity_log) > 500:
            self.activity_log = self.activity_log[-500:]
        if self.log_dialog:
            self.log_dialog.refresh()

    def _add_alarm(self, severity: str, text: str) -> None:
        self.root.after(0, self._add_alarm_on_ui_thread, severity.upper(), text)

    def _add_alarm_on_ui_thread(self, severity: str, text: str) -> None:
        timestamp = datetime.now().strftime("%H:%M:%S")
        self.alarm_log.append(f"[{timestamp}] {severity}: {text}")
        if len(self.alarm_log) > 500:
            self.alarm_log = self.alarm_log[-500:]
        self.unacknowledged_alarms += 1
        self._refresh_alarm_button()
        if self.log_dialog:
            self.log_dialog.refresh()

    def acknowledge_alarms(self) -> None:
        self.unacknowledged_alarms = 0
        self._refresh_alarm_button()
        if self.log_dialog:
            self.log_dialog.refresh()

    def clear_logs(self) -> None:
        self.activity_log.clear()
        self.alarm_log.clear()
        self.unacknowledged_alarms = 0
        self._refresh_alarm_button()
        if self.log_dialog:
            self.log_dialog.refresh()

    def _refresh_alarm_button(self) -> None:
        if not self.alarm_button:
            return
        if self.unacknowledged_alarms:
            self.alarm_button.configure(style="AlarmOn.TButton", text=f"! {self.unacknowledged_alarms}")
        else:
            self.alarm_button.configure(style="AlarmOff.TButton", text="!")

    def _show_not_available(self, title: str, reason: str) -> None:
        self._add_alarm("warning", f"{title}: {reason}")
        messagebox.showinfo(title, f"{title} is not available.\n\n{reason}")
        self.status_var.set(f"{title} is not available. {reason}")

    def _set_loop_state(self, state: str) -> None:
        colors = {
            "running": ("#12b76a", "#039855", "Loop: running"),
            "paused": ("#fdb022", "#dc6803", "Loop: paused"),
            "stopped": ("#98a2b3", "#667085", "Loop: stopped"),
        }
        fill, outline, label = colors.get(state, colors["stopped"])
        self.root.after(0, self._set_loop_state_on_ui_thread, fill, outline, label)

    def _set_loop_state_on_ui_thread(self, fill: str, outline: str, label: str) -> None:
        self.loop_state_var.set(label)
        self.loop_tooltip_text.set(label)
        if self.loop_indicator_canvas and self.loop_indicator_item:
            self.loop_indicator_canvas.itemconfigure(self.loop_indicator_item, fill=fill, outline=outline)

    def _set_browser_state(self, state: str) -> None:
        self.root.after(0, self.browser_state_var.set, f"Browser: {state}")

    def _set_selected_village(self, village: str) -> None:
        self.root.after(0, self.selected_village_var.set, f"Village: {village or '-'}")

    def _update_village_selector(self, names: list[str], selected: str) -> None:
        clean_names = [name for name in names if name]
        selected_name = selected if selected in clean_names else (clean_names[0] if clean_names else "")
        if self.village_box:
            self.village_box.configure(values=clean_names)
        self.village_select_var.set(selected_name)
        self._set_selected_village(selected_name)

    def _render_selected_village_status(self) -> None:
        name = self.village_select_var.get()
        status = self.village_statuses.get(name)
        if not status:
            self._set_status(f"No cached status found for village '{name}'. Run Scan all villages or Check village status.")
            return
        self._render_village_status(status, update_selector=False)
        self._set_status(f"Showing cached status for village '{name}'.")

    def _set_last_scan_now(self) -> None:
        timestamp = datetime.now().strftime("%H:%M:%S")
        self.root.after(0, self.last_scan_var.set, f"Last scan: {timestamp}")

    def _sync_server_clock(self, server_time_utc: datetime | None) -> None:
        if not server_time_utc or self.server_time_from_page:
            return

        server_local = server_time_utc.astimezone().replace(tzinfo=None, microsecond=0)
        self.server_time_base = server_local
        self.server_time_monotonic = time.monotonic()

    def _sync_visible_server_clock(self, client: TravianClient) -> None:
        visible_time = client.read_visible_server_datetime()
        if not visible_time:
            return

        visible_server_time = visible_time.replace(tzinfo=None, microsecond=0)
        self.server_time_base = visible_server_time
        self.server_time_monotonic = time.monotonic()
        self.server_time_from_page = True

    def _tick_clock(self) -> None:
        if self.server_time_base is None or self.server_time_monotonic is None:
            current = datetime.now()
            label = f"Time: {current.strftime('%Y-%m-%d %H:%M:%S')} local"
        else:
            elapsed = time.monotonic() - self.server_time_monotonic
            synced_server = self.server_time_base + timedelta(seconds=elapsed)
            label = f"Time: {synced_server.strftime('%Y-%m-%d %H:%M:%S')} server"

        self.clock_var.set(label)
        self.root.after(1000, self._tick_clock)

    def _set_actions_enabled(self, enabled: bool) -> None:
        self.root.after(0, self._set_actions_enabled_on_ui_thread, enabled)

    def _set_actions_enabled_on_ui_thread(self, enabled: bool) -> None:
        state = "normal" if enabled else "disabled"
        for button in self.action_buttons:
            button.configure(state=state)

    def _show_manual_step_popup(self, message: str) -> None:
        self._add_alarm("warning", message)
        self.root.after(0, self._show_manual_step_popup_on_ui_thread, message)

    def _show_manual_step_popup_on_ui_thread(self, message: str) -> None:
        if self.manual_step_popup_open:
            return

        self.manual_step_popup_open = True
        try:
            messagebox.showwarning(
                "Manual verification needed",
                f"{message}\n\nIf Chromium is visible, solve it there and the bot will continue. If the bot is running headless, open the verification browser from the UI.",
            )
        finally:
            self.manual_step_popup_open = False

    def _format_village_status(self, status: VillageStatus) -> str:
        resources = ", ".join(f"{name}: {value}" for name, value in status.resources.items()) or "none found"
        return (
            f"Checked dorf1 and dorf2.\n"
            f"Active village: {status.active_village}\n"
            f"Villages found: {len(status.villages)}\n"
            f"Resources: {resources}\n"
            f"Resource fields found: {len(status.resource_fields)}\n"
            f"Buildings found: {len(status.buildings)}\n"
            f"Build queue items: {len(status.build_queue)}"
        )

    def _render_account_snapshot(self, snapshot: AccountSnapshot) -> None:
        self._sync_server_clock(snapshot.server_time_utc)
        self._set_selected_village(snapshot.active_village)
        self.tribe_var.set(f"Tribe: {snapshot.tribe}")
        self.village_count_var.set(f"Villages: {snapshot.village_count}")
        self._update_village_selector([village.name for village in snapshot.villages], snapshot.active_village)

        villages = "\n".join(f"- {village.name}" for village in snapshot.villages) or "- No villages found."
        self.summary_var.set(
            f"Active village: {snapshot.active_village}\n"
            f"Tribe: {snapshot.tribe}\n"
            f"Villages: {snapshot.village_count}\n\n"
            f"Village list\n{villages}"
        )

    def _render_all_village_statuses(self, statuses: list[VillageStatus]) -> None:
        self.village_statuses.clear()
        for status in statuses:
            name = status.active_village or f"Village {len(self.village_statuses) + 1}"
            key = name
            duplicate = 2
            while key in self.village_statuses:
                key = f"{name} ({duplicate})"
                duplicate += 1
            self.village_statuses[key] = status

        names = list(self.village_statuses)
        current_selection = self.village_select_var.get()
        selected = current_selection if current_selection in self.village_statuses else (names[0] if names else "")
        self._update_village_selector(names, selected)
        self.village_count_var.set(f"Villages: {len(names)}")
        self._set_last_scan_now()
        if selected:
            self._render_village_status(self.village_statuses[selected], update_selector=False)
        else:
            self.summary_var.set("No villages found.")

    def _render_village_status(self, status: VillageStatus, update_selector: bool = True) -> None:
        self.latest_village_status = status
        if update_selector:
            name = status.active_village or f"Village {len(self.village_statuses) + 1}"
            self.village_statuses[name] = status
            self._update_village_selector(list(self.village_statuses), name)
        resources = "\n".join(f"{name}: {value}" for name, value in status.resources.items()) or "No resources found."
        self._sync_server_clock(status.server_time_utc)
        self._set_selected_village(status.active_village)
        self.village_count_var.set(f"Villages: {len(status.villages)}")
        self._set_last_scan_now()
        self.summary_var.set(
            f"Active village: {status.active_village}\n\n"
            f"Villages: {len(status.villages)}\n"
            f"Resource fields: {len(status.resource_fields)}\n"
            f"Buildings: {len(status.buildings)}\n"
            f"Build queue items: {len(status.build_queue)}\n\n"
            f"Resources\n{resources}"
        )

        self._clear_tree(self.resource_tree)
        if self.resource_tree:
            for field in status.resource_fields:
                slot = field.slot_id if field.slot_id is not None else "?"
                level = field.level if field.level is not None else "?"
                self.resource_tree.insert("", "end", values=(slot, field.name, field.field_type, level))

        self._clear_tree(self.building_tree)
        if self.building_tree:
            for building in status.buildings:
                slot = building.slot_id if building.slot_id is not None else "?"
                level = building.level if building.level is not None else "?"
                self.building_tree.insert("", "end", values=(slot, building.name, level))

        self._clear_tree(self.queue_tree)
        if self.queue_tree:
            for item in status.build_queue:
                self.queue_tree.insert("", "end", values=(item.text, item.time_left or ""))

        self._render_resource_map(status)
        self._render_building_map(status)
        self._refresh_action_queue()

    def _clear_tree(self, tree: ttk.Treeview | None) -> None:
        if not tree:
            return
        for item in tree.get_children():
            tree.delete(item)

    def _render_empty_maps(self) -> None:
        if self.resource_map_frame:
            self._render_resource_map(self.latest_village_status)
        if self.building_map_frame:
            self._render_building_map(self.latest_village_status)

    def _render_resource_map(self, status: VillageStatus | None) -> None:
        if not self.resource_map_frame:
            return

        self._clear_children(self.resource_map_frame)
        slots = {field.slot_id: field for field in status.resource_fields if field.slot_id is not None} if status else {}
        layout = [
            [1, 2, 3, 4, 5, 6],
            [7, 8, 9, 10, 11, 12],
            [13, 14, 15, 16, 17, 18],
        ]

        for row, row_slots in enumerate(layout):
            for column, slot_id in enumerate(row_slots):
                field = slots.get(slot_id)
                text = self._resource_card_text(slot_id, field)
                command = lambda selected_slot=slot_id: self._select_resource_slot(selected_slot, slots.get(selected_slot))
                ttk.Button(self.resource_map_frame, text=text, command=command, style="Map.TButton").grid(
                    row=row, column=column, sticky="nsew", padx=4, pady=4
                )

    def _render_building_map(self, status: VillageStatus | None) -> None:
        if not self.building_map_frame:
            return

        self._clear_children(self.building_map_frame)
        buildings = {building.slot_id: building for building in status.buildings if building.slot_id is not None} if status else {}
        slot_ids = list(range(19, 41))

        for index, slot_id in enumerate(slot_ids):
            building = buildings.get(slot_id)
            text = self._building_card_text(slot_id, building)
            command = lambda selected_slot=slot_id: self._select_building_slot(selected_slot, buildings.get(selected_slot))
            ttk.Button(self.building_map_frame, text=text, command=command, style="Map.TButton").grid(
                row=index // 5, column=index % 5, sticky="nsew", padx=4, pady=4
            )

    def _resource_card_text(self, slot_id: int, field) -> str:
        if not field:
            return f"Slot {slot_id}\nUnknown\nLvl ?"
        level = field.level if field.level is not None else "?"
        name = field.name if len(field.name) <= 18 else f"{field.name[:15]}..."
        field_type = field.field_type if field.field_type else "unknown"
        return f"Slot {slot_id}\n{name}\n{field_type} - Lvl {level}"

    def _building_card_text(self, slot_id: int, building) -> str:
        if not building:
            return f"Slot {slot_id}\nEmpty/unknown\nLvl ?"
        level = building.level if building.level is not None else "?"
        name = building.name if len(building.name) <= 22 else f"{building.name[:19]}..."
        return f"Slot {slot_id}\n{name}\nLvl {level}"

    def _select_resource_slot(self, slot_id: int, field) -> None:
        UpgradeChoiceDialog(
            self.root,
            title="Upgrade resource",
            description=self._resource_card_text(slot_id, field).replace("\n", " - "),
            on_target=lambda target: self._start_bot_action(f"upgrade_resource:{slot_id}:{target}"),
            on_max=lambda: self._start_bot_action(f"upgrade_resource:{slot_id}:max"),
        )

    def _select_building_slot(self, slot_id: int, building) -> None:
        if not building:
            BuildChoiceDialog(
                self.root,
                slot_id=slot_id,
                buildings=self._available_buildings_for_slot(slot_id),
                on_build=lambda gid: self._start_bot_action(f"construct_building:{slot_id}:{gid}"),
            )
            return

        UpgradeChoiceDialog(
            self.root,
            title="Upgrade building",
            description=self._building_card_text(slot_id, building).replace("\n", " - "),
            on_target=lambda target: self._start_bot_action(f"upgrade_building:{slot_id}:{target}"),
            on_max=lambda: self._start_bot_action(f"upgrade_building:{slot_id}:max"),
        )

    def _building_catalog_by_gid(self, gid: int) -> dict | None:
        return next((building for building in BUILDING_CATALOG if int(building["gid"]) == gid), None)

    def _available_buildings_for_slot(self, slot_id: int) -> list[dict]:
        existing = self.latest_village_status.buildings if self.latest_village_status else []
        available = []
        for building in BUILDING_CATALOG:
            allowed, reason = self._building_allowed(building, existing)
            item = dict(building)
            item["allowed"] = allowed
            item["reason"] = reason
            available.append(item)
        return available

    def _building_allowed(self, building: dict, existing: list) -> tuple[bool, str]:
        if not self.latest_village_status:
            return False, "Run Check village status first so requirements can be verified."

        name = str(building["name"])
        special = building.get("special")
        existing_names = [item.name.lower() for item in existing]
        existing_gids = [item.gid for item in existing if item.gid is not None]

        if special == "wall" and any("wall" in item for item in existing_names):
            return False, "A wall already exists."

        if building.get("unique") and int(building["gid"]) in existing_gids:
            return False, f"{name} already exists."

        if name in {"Residence", "Palace"} and any(item in existing_names for item in ["residence", "palace"]):
            return False, "Residence and Palace are mutually exclusive."

        missing = []
        for required_name, required_level in building.get("requirements", []):
            current = self._building_level(existing, required_name)
            if current < required_level:
                missing.append(f"{required_name} level {required_level}")
        if missing:
            return False, "Missing: " + ", ".join(missing)

        return True, "Local rules OK. Server is checked before build."

    def _building_level(self, buildings: list, name: str) -> int:
        lowered = name.lower()
        aliases = {
            "granary": ["granary", "silo"],
            "wall": ["wall", "palisade"],
        }.get(lowered, [lowered])
        levels = [
            building.level or 0
            for building in buildings
            if any(alias in building.name.lower() for alias in aliases)
        ]
        return max(levels) if levels else 0

    def open_upgrade_all_resources(self) -> None:
        UpgradeChoiceDialog(
            self.root,
            title="Upgrade all resources",
            description="Upgrade all resource fields to the selected level. Lowest level is prioritized first.",
            on_target=self._confirm_upgrade_all_resources,
            on_max=None,
        )

    def _confirm_upgrade_all_resources(self, target: int) -> None:
        warning = (
            f"Queue upgrade for all resource fields in '{self.village_select_var.get() or 'selected village'}' to level {target}?\n\n"
            "This can start many upgrades. Lowest levels are prioritized first."
        )
        if target >= 10:
            warning += "\n\nLevel 10 or higher can be expensive. Confirm before continuing."
        if not messagebox.askyesno("Confirm mass upgrade", warning):
            return
        self._start_bot_action(f"upgrade_all_resources:{target}")

    def _clear_children(self, parent: ttk.Frame) -> None:
        for child in parent.winfo_children():
            child.destroy()

    def logout_session(self) -> None:
        account_name = self.account_var.get() or self.name_var.get().strip()
        if not account_name:
            messagebox.showerror("Missing account", "Select an account first.")
            return

        state_path = ROOT_DIR / "playwright" / ".auth" / f"{account_name}.json"
        if state_path.exists():
            state_path.unlink()
            self.status_var.set(f"Removed saved browser session for '{account_name}'.")
        else:
            self.status_var.set(f"No saved browser session found for '{account_name}'.")

    def delete_current_account(self) -> None:
        account_name = self.account_var.get() or self.name_var.get().strip()
        if not account_name:
            messagebox.showerror("Missing account", "Select an account first.")
            return

        account_name = normalize_account_name(account_name)
        confirmed = messagebox.askyesno(
            "Delete account",
            f"Delete account '{account_name}' from .env?\n\nSaved browser session will also be removed if it exists.",
        )
        if not confirmed:
            return

        try:
            delete_account(account_name)
        except RuntimeError as exc:
            messagebox.showerror("Could not delete account", str(exc))
            return

        state_path = ROOT_DIR / "playwright" / ".auth" / f"{account_name}.json"
        if state_path.exists():
            state_path.unlink()

        self.name_var.set("")
        self.username_var.set("")
        self.password_var.set("")
        self.status_var.set(f"Deleted account '{account_name}'.")
        self.refresh_accounts()

    def open_settings(self) -> None:
        SettingsDialog(self.root, self.status_var)

    def open_logs(self) -> None:
        if self.log_dialog:
            self.log_dialog.focus()
            return
        self.log_dialog = LogDialog(self)

    def open_contact(self) -> None:
        ContactDialog(self.root, self.status_var)

    def open_update(self) -> None:
        UpdateDialog(self.root, self.status_var)

    def open_accounts(self) -> None:
        AccountDialog(self)


class LogDialog:
    def __init__(self, ui: TbotUi) -> None:
        self.ui = ui
        self.window = tk.Toplevel(ui.root)
        self.window.title("Logs")
        self.window.geometry("720x420")
        self.window.minsize(560, 320)
        self.window.transient(ui.root)
        self.window.protocol("WM_DELETE_WINDOW", self.close)

        frame = ttk.Frame(self.window, padding=10)
        frame.pack(fill=tk.BOTH, expand=True)
        frame.rowconfigure(0, weight=1)
        frame.columnconfigure(0, weight=1)

        self.notebook = ttk.Notebook(frame)
        self.notebook.grid(row=0, column=0, sticky="nsew")

        self.activity_text = self._build_log_tab("Activity")
        self.alarm_text = self._build_log_tab("Alarms")

        buttons = ttk.Frame(frame)
        buttons.grid(row=1, column=0, sticky="ew", pady=(8, 0))
        buttons.columnconfigure((0, 1, 2), weight=1)
        ttk.Button(buttons, text="Acknowledge alarms", command=self.ui.acknowledge_alarms).grid(row=0, column=0, sticky="ew", padx=(0, 5))
        ttk.Button(buttons, text="Clear logs", command=self.ui.clear_logs).grid(row=0, column=1, sticky="ew", padx=5)
        ttk.Button(buttons, text="Close", command=self.close).grid(row=0, column=2, sticky="ew", padx=(5, 0))

        self.refresh()

    def _build_log_tab(self, title: str) -> tk.Text:
        tab = ttk.Frame(self.notebook, padding=6)
        tab.rowconfigure(0, weight=1)
        tab.columnconfigure(0, weight=1)
        self.notebook.add(tab, text=title)

        text = tk.Text(
            tab,
            wrap="word",
            height=12,
            font=("Consolas", 9),
            bg="#101828",
            fg="#d0d5dd",
            insertbackground="#d0d5dd",
            relief="flat",
        )
        text.grid(row=0, column=0, sticky="nsew")
        scrollbar = ttk.Scrollbar(tab, orient="vertical", command=text.yview)
        scrollbar.grid(row=0, column=1, sticky="ns")
        text.configure(yscrollcommand=scrollbar.set, state="disabled")
        return text

    def refresh(self) -> None:
        self._write_log(self.activity_text, self.ui.activity_log)
        alarm_lines = list(self.ui.alarm_log)
        if self.ui.unacknowledged_alarms:
            alarm_lines.append("")
            alarm_lines.append(f"Unacknowledged alarms: {self.ui.unacknowledged_alarms}")
        self._write_log(self.alarm_text, alarm_lines)

    def _write_log(self, text_widget: tk.Text, lines: list[str]) -> None:
        text_widget.configure(state="normal")
        text_widget.delete("1.0", tk.END)
        text_widget.insert(tk.END, "\n".join(lines) if lines else "No entries.")
        text_widget.see(tk.END)
        text_widget.configure(state="disabled")

    def focus(self) -> None:
        self.window.deiconify()
        self.window.lift()
        self.window.focus_force()

    def close(self) -> None:
        self.ui.log_dialog = None
        self.window.destroy()


class ContactDialog:
    DISCORD_URL = "https://discord.gg/qrge94p7TH"

    def __init__(self, parent: tk.Tk, status_var: tk.StringVar) -> None:
        self.status_var = status_var
        self.window = tk.Toplevel(parent)
        self.window.title("Contact")
        self.window.geometry("340x170")
        self.window.minsize(320, 160)
        self.window.transient(parent)
        self.window.grab_set()

        frame = ttk.Frame(self.window, padding=14)
        frame.pack(fill=tk.BOTH, expand=True)
        frame.columnconfigure(0, weight=1)

        ttk.Label(frame, text="Contact", style="PanelTitle.TLabel").grid(row=0, column=0, sticky="w", pady=(0, 8))
        ttk.Label(
            frame,
            text="Need help, want to report a bug, or discuss the next bot features? Open Discord from here.",
            wraplength=300,
            justify="left",
        ).grid(row=1, column=0, sticky="ew")

        buttons = ttk.Frame(frame)
        buttons.grid(row=2, column=0, sticky="ew", pady=(16, 0))
        buttons.columnconfigure((0, 1), weight=1)
        ttk.Button(buttons, text="Discord", command=self.open_discord, style="Primary.TButton").grid(
            row=0, column=0, sticky="ew", padx=(0, 6)
        )
        ttk.Button(buttons, text="Close", command=self.window.destroy).grid(row=0, column=1, sticky="ew", padx=(6, 0))

    def open_discord(self) -> None:
        webbrowser.open(self.DISCORD_URL)
        self.status_var.set("Opened Discord contact link.")


class Tooltip:
    def __init__(self, widget, text_var: tk.StringVar) -> None:
        self.widget = widget
        self.text_var = text_var
        self.window: tk.Toplevel | None = None
        self.after_id: str | None = None
        widget.bind("<Enter>", self._schedule)
        widget.bind("<Leave>", self._hide)
        widget.bind("<ButtonPress>", self._hide)

    def _schedule(self, _event=None) -> None:
        self._cancel()
        self.after_id = self.widget.after(350, self._show)

    def _cancel(self) -> None:
        if self.after_id:
            self.widget.after_cancel(self.after_id)
            self.after_id = None

    def _show(self) -> None:
        self.after_id = None
        text = self.text_var.get()
        if not text or self.window:
            return

        x = self.widget.winfo_rootx() + 12
        y = self.widget.winfo_rooty() + self.widget.winfo_height() + 8
        self.window = tk.Toplevel(self.widget)
        self.window.wm_overrideredirect(True)
        self.window.wm_geometry(f"+{x}+{y}")
        label = tk.Label(
            self.window,
            text=text,
            bg="#101828",
            fg="#ffffff",
            padx=7,
            pady=4,
            font=("Segoe UI", 8),
            relief="solid",
            bd=1,
        )
        label.pack()

    def _hide(self, _event=None) -> None:
        self._cancel()
        if self.window:
            self.window.destroy()
            self.window = None


class UpdateDialog:
    def __init__(self, parent: tk.Tk, status_var: tk.StringVar) -> None:
        self.status_var = status_var
        self.config = load_bot_config()
        self.window = tk.Toplevel(parent)
        self.window.title("Updates")
        self.window.geometry("420x220")
        self.window.minsize(380, 200)
        self.window.transient(parent)
        self.window.grab_set()

        self.latest_var = tk.StringVar(value="Checking GitHub...")
        self.update_status_var = tk.StringVar(value="Version strategy: bump APP_VERSION in version.py before each release.")

        frame = ttk.Frame(self.window, padding=14)
        frame.pack(fill=tk.BOTH, expand=True)
        frame.columnconfigure(1, weight=1)

        ttk.Label(frame, text="Updates", style="PanelTitle.TLabel").grid(row=0, column=0, columnspan=2, sticky="w", pady=(0, 10))
        ttk.Label(frame, text="Current version").grid(row=1, column=0, sticky="w", pady=5)
        ttk.Label(frame, text=f"v{APP_VERSION}", style="Panel.TLabel").grid(row=1, column=1, sticky="w", pady=5)
        ttk.Label(frame, text="Latest version").grid(row=2, column=0, sticky="w", pady=5)
        ttk.Label(frame, textvariable=self.latest_var, style="Panel.TLabel").grid(row=2, column=1, sticky="w", pady=5)
        ttk.Label(frame, textvariable=self.update_status_var, wraplength=360, justify="left").grid(
            row=3, column=0, columnspan=2, sticky="ew", pady=(6, 0)
        )

        buttons = ttk.Frame(frame)
        buttons.grid(row=4, column=0, columnspan=2, sticky="ew", pady=(18, 0))
        buttons.columnconfigure((0, 1), weight=1)
        state = "normal" if self.config.github_releases_url else "disabled"
        ttk.Button(buttons, text="GitHub Releases", command=self.open_releases, state=state).grid(
            row=0, column=0, sticky="ew", padx=(0, 6)
        )
        ttk.Button(buttons, text="Close", command=self.window.destroy).grid(row=0, column=1, sticky="ew", padx=(6, 0))

        thread = threading.Thread(target=self._fetch_latest_version, daemon=True)
        thread.start()

    def _fetch_latest_version(self) -> None:
        try:
            latest = self._read_latest_release()
        except Exception as exc:
            latest = f"Could not check ({exc})"
        if self.window.winfo_exists():
            self.window.after(0, self._set_latest_version, latest)

    def _set_latest_version(self, latest: str) -> None:
        self.latest_var.set(latest)
        local = self._normalize_version(APP_VERSION)
        remote = self._normalize_version(latest)
        if remote and local and remote != local:
            self.update_status_var.set("New version available. Open GitHub Releases to download/update.")
        elif remote and local:
            self.update_status_var.set("You are on the latest release.")
        else:
            self.update_status_var.set("Version strategy: bump APP_VERSION in version.py before each release.")

    def _normalize_version(self, value: str) -> str:
        match = re.search(r"\d+(?:\.\d+){1,3}", value)
        return match.group(0) if match else ""

    def _read_latest_release(self) -> str:
        releases_url = self.config.github_releases_url.strip()
        if not releases_url:
            return "GitHub Releases URL not configured"

        api_url = self._to_latest_release_api_url(releases_url)
        request = urllib.request.Request(api_url, headers={"User-Agent": "Tbot Ultra"})
        try:
            with urllib.request.urlopen(request, timeout=12) as response:
                payload = json.loads(response.read().decode("utf-8"))
        except urllib.error.HTTPError as exc:
            if exc.code in {401, 403, 404}:
                return "Not accessible. Check GitHub URL or private repo access."
            raise RuntimeError(f"GitHub returned HTTP {exc.code}") from exc
        except urllib.error.URLError as exc:
            raise RuntimeError(exc.reason) from exc

        tag = str(payload.get("tag_name") or payload.get("name") or "").strip()
        return tag or "No release tag found"

    def _to_latest_release_api_url(self, releases_url: str) -> str:
        cleaned = releases_url.rstrip("/")
        if "api.github.com/repos/" in cleaned:
            return cleaned if cleaned.endswith("/latest") else f"{cleaned}/latest"

        marker = "github.com/"
        if marker not in cleaned:
            raise RuntimeError("GitHub Releases URL must be a github.com URL")

        path = cleaned.split(marker, 1)[1].strip("/")
        parts = path.split("/")
        if len(parts) < 2:
            raise RuntimeError("GitHub Releases URL must include owner and repo")

        owner, repo = parts[0], parts[1]
        return f"https://api.github.com/repos/{owner}/{repo}/releases/latest"

    def open_releases(self) -> None:
        releases_url = self.config.github_releases_url.strip()
        if not releases_url:
            messagebox.showinfo("GitHub Releases", "GitHub Releases URL is not configured in Settings.")
            return
        webbrowser.open(releases_url)
        self.status_var.set("Opened GitHub Releases.")


class UpgradeChoiceDialog:
    def __init__(
        self,
        parent: tk.Tk,
        title: str,
        description: str,
        on_target,
        on_max,
    ) -> None:
        self.on_target = on_target
        self.on_max = on_max
        self.window = tk.Toplevel(parent)
        self.window.title(title)
        self.window.geometry("360x210")
        self.window.minsize(330, 190)
        self.window.transient(parent)
        self.window.grab_set()
        self.target_var = tk.StringVar(value="10")

        frame = ttk.Frame(self.window, padding=14)
        frame.pack(fill=tk.BOTH, expand=True)
        frame.columnconfigure(1, weight=1)

        ttk.Label(frame, text=title, style="PanelTitle.TLabel").grid(row=0, column=0, columnspan=2, sticky="w", pady=(0, 8))
        ttk.Label(frame, text=description, wraplength=320, justify="left").grid(row=1, column=0, columnspan=2, sticky="ew")

        ttk.Label(frame, text="Target level").grid(row=2, column=0, sticky="w", pady=(14, 4))
        ttk.Spinbox(frame, from_=0, to=100, textvariable=self.target_var, width=8).grid(row=2, column=1, sticky="w", pady=(14, 4))

        buttons = ttk.Frame(frame)
        buttons.grid(row=3, column=0, columnspan=2, sticky="ew", pady=(12, 0))
        buttons.columnconfigure((0, 1, 2), weight=1)
        ttk.Button(buttons, text="Upgrade to level", command=self._upgrade_target, style="Primary.TButton").grid(
            row=0, column=0, sticky="ew", padx=(0, 5)
        )
        max_state = "normal" if on_max else "disabled"
        ttk.Button(buttons, text="Upgrade to max", command=self._upgrade_max, state=max_state).grid(
            row=0, column=1, sticky="ew", padx=5
        )
        ttk.Button(buttons, text="Close", command=self.window.destroy).grid(row=0, column=2, sticky="ew", padx=(5, 0))

    def _upgrade_target(self) -> None:
        try:
            target = int(self.target_var.get())
            if target < 0:
                raise ValueError
        except ValueError:
            messagebox.showerror("Invalid level", "Target level must be 0 or higher.")
            return

        self.window.destroy()
        self.on_target(target)

    def _upgrade_max(self) -> None:
        if not self.on_max:
            return
        self.window.destroy()
        self.on_max()


class BuildChoiceDialog:
    def __init__(self, parent: tk.Tk, slot_id: int, buildings: list[dict], on_build) -> None:
        self.slot_id = slot_id
        self.buildings = buildings
        self.on_build = on_build
        self.window = tk.Toplevel(parent)
        self.window.title(f"Build in slot {slot_id}")
        self.window.geometry("620x360")
        self.window.minsize(520, 300)
        self.window.transient(parent)
        self.window.grab_set()

        frame = ttk.Frame(self.window, padding=12)
        frame.pack(fill=tk.BOTH, expand=True)
        frame.rowconfigure(1, weight=1)
        frame.columnconfigure(0, weight=1)

        ttk.Label(frame, text=f"Choose building for slot {slot_id}", style="PanelTitle.TLabel").grid(
            row=0, column=0, sticky="w", pady=(0, 8)
        )

        table_frame = ttk.Frame(frame)
        table_frame.grid(row=1, column=0, sticky="nsew")
        table_frame.rowconfigure(0, weight=1)
        table_frame.columnconfigure(0, weight=1)

        self.tree = ttk.Treeview(table_frame, columns=("name", "status"), show="headings", selectmode="browse")
        self.tree.heading("name", text="Building")
        self.tree.heading("status", text="Status")
        self.tree.column("name", width=210, anchor="w")
        self.tree.column("status", width=330, anchor="w")
        self.tree.grid(row=0, column=0, sticky="nsew")
        scrollbar = ttk.Scrollbar(table_frame, orient="vertical", command=self.tree.yview)
        scrollbar.grid(row=0, column=1, sticky="ns")
        self.tree.configure(yscrollcommand=scrollbar.set)

        for building in buildings:
            status = building.get("reason", "")
            self.tree.insert("", "end", iid=str(building["gid"]), values=(building["name"], status))

        buttons = ttk.Frame(frame)
        buttons.grid(row=2, column=0, sticky="ew", pady=(10, 0))
        buttons.columnconfigure((0, 1), weight=1)
        ttk.Button(buttons, text="Build selected", command=self.build_selected, style="Primary.TButton").grid(
            row=0, column=0, sticky="ew", padx=(0, 6)
        )
        ttk.Button(buttons, text="Close", command=self.window.destroy).grid(row=0, column=1, sticky="ew", padx=(6, 0))

    def build_selected(self) -> None:
        selection = self.tree.selection()
        if not selection:
            messagebox.showinfo("Choose building", "Select a building first.")
            return

        gid = int(selection[0])
        building = next((item for item in self.buildings if int(item["gid"]) == gid), None)
        if not building:
            messagebox.showerror("Choose building", "Selected building could not be found.")
            return

        if not building.get("allowed"):
            messagebox.showinfo("Requirements not met", str(building.get("reason", "This building is not available.")))
            return

        self.window.destroy()
        self.on_build(gid)


class SettingsDialog:
    def __init__(self, parent: tk.Tk, status_var: tk.StringVar) -> None:
        self.status_var = status_var
        self.window = tk.Toplevel(parent)
        self.window.title("Settings")
        self.window.geometry("540x500")
        self.window.minsize(500, 460)
        self.window.transient(parent)
        self.window.grab_set()
        self.window.bind("<FocusOut>", self._focus_back)
        self.window.protocol("WM_DELETE_WINDOW", self.window.destroy)

        config = load_bot_config()
        self.config = config
        self.headless_var = tk.BooleanVar(value=config.headless)
        self.timeout_var = tk.StringVar(value=str(config.timeout_ms))
        self.manual_timeout_var = tk.StringVar(value=str(config.manual_login_timeout_seconds))
        self.loop_interval_var = tk.StringVar(value=str(config.loop_interval_seconds))
        self.github_releases_url_var = tk.StringVar(value=config.github_releases_url)
        self.human_like_enabled_var = tk.BooleanVar(value=config.human_like_enabled)
        self.human_like_speed_var = tk.StringVar(value=config.human_like_speed)
        self.loop_task_vars = {
            task_id: tk.BooleanVar(value=task_id in config.loop_tasks)
            for task_id in LOOP_TASKS
        }

        self._build()

    def _build(self) -> None:
        frame = ttk.Frame(self.window, padding=14)
        self.frame = frame
        frame.pack(fill=tk.BOTH, expand=True)
        frame.columnconfigure(1, weight=1)

        ttk.Label(frame, text="Timeout ms").grid(row=0, column=0, sticky="w", pady=5)
        ttk.Entry(frame, textvariable=self.timeout_var).grid(row=0, column=1, sticky="ew", pady=5, padx=(8, 0))

        ttk.Label(frame, text="Manual timeout sec").grid(row=1, column=0, sticky="w", pady=5)
        ttk.Entry(frame, textvariable=self.manual_timeout_var).grid(row=1, column=1, sticky="ew", pady=5, padx=(8, 0))

        self._checkbox(frame, "Headless browser", self.headless_var).grid(
            row=2, column=1, sticky="w", pady=6, padx=(8, 0)
        )

        ttk.Label(frame, text="Loop interval sec").grid(row=3, column=0, sticky="w", pady=5)
        ttk.Entry(frame, textvariable=self.loop_interval_var).grid(row=3, column=1, sticky="ew", pady=5, padx=(8, 0))

        ttk.Label(frame, text="GitHub releases URL").grid(row=4, column=0, sticky="w", pady=5)
        ttk.Entry(frame, textvariable=self.github_releases_url_var).grid(row=4, column=1, sticky="ew", pady=5, padx=(8, 0))

        self._checkbox(frame, "Human-like behavior", self.human_like_enabled_var).grid(
            row=5, column=1, sticky="w", pady=6, padx=(8, 0)
        )

        ttk.Label(frame, text="Behavior speed").grid(row=6, column=0, sticky="w", pady=5)
        ttk.Combobox(
            frame,
            textvariable=self.human_like_speed_var,
            values=("slow", "medium", "fast"),
            state="readonly",
        ).grid(row=6, column=1, sticky="ew", pady=5, padx=(8, 0))

        tasks_frame = ttk.LabelFrame(frame, text="Loop tasks", padding=8)
        tasks_frame.grid(row=7, column=0, columnspan=2, sticky="ew", pady=(10, 0))
        tasks_frame.columnconfigure(0, weight=1)
        for row, (task_id, task) in enumerate(LOOP_TASKS.items()):
            label = str(task["label"])
            if not task["implemented"]:
                label = f"{label} (not implemented)"
            self._checkbox(
                tasks_frame,
                label,
                self.loop_task_vars[task_id],
                lambda selected_task=task_id: self._task_toggled(selected_task),
            ).grid(row=row, column=0, sticky="w", pady=2)

        buttons = ttk.Frame(frame)
        buttons.grid(row=8, column=0, columnspan=2, sticky="ew", pady=(14, 0))
        buttons.columnconfigure((0, 1, 2), weight=1)
        ttk.Button(buttons, text="Save", command=self.save).grid(row=0, column=0, sticky="ew", padx=(0, 6))
        ttk.Button(buttons, text="Restore defaults", command=self.restore_defaults).grid(row=0, column=1, sticky="ew", padx=6)
        ttk.Button(buttons, text="Close", command=self.window.destroy).grid(row=0, column=2, sticky="ew", padx=(6, 0))

    def _checkbox(self, parent, text: str, variable: tk.BooleanVar, command=None) -> tk.Checkbutton:
        return tk.Checkbutton(
            parent,
            text=text,
            variable=variable,
            command=command,
            indicatoron=True,
            anchor="w",
            bg="#f4f6f8",
            activebackground="#f4f6f8",
            fg="#17202a",
            activeforeground="#17202a",
            selectcolor="#ffffff",
            highlightthickness=0,
            bd=0,
            padx=0,
            pady=0,
        )

    def _focus_back(self, _event=None) -> None:
        self.window.after(50, self._ensure_focus)

    def _ensure_focus(self) -> None:
        if not self.window.winfo_exists():
            return
        focused = self.window.focus_get()
        if focused is None:
            return
        if not str(focused).startswith(str(self.window)):
            self._flash()
            self.window.lift()
            self.window.focus_force()

    def _flash(self) -> None:
        original = self.frame.cget("style") if hasattr(self, "frame") else ""
        self.window.bell()
        self.frame.configure(style="Warning.TFrame")
        self.window.after(160, lambda: self._restore_flash(original))

    def _restore_flash(self, original: str) -> None:
        if self.window.winfo_exists() and self.frame.winfo_exists():
            self.frame.configure(style=original or "TFrame")

    def restore_defaults(self) -> None:
        self.headless_var.set(bool(DEFAULT_SETTINGS["headless"]))
        self.timeout_var.set(str(DEFAULT_SETTINGS["timeout_ms"]))
        self.manual_timeout_var.set(str(DEFAULT_SETTINGS["manual_login_timeout_seconds"]))
        self.loop_interval_var.set(str(DEFAULT_SETTINGS["loop_interval_seconds"]))
        self.github_releases_url_var.set(str(DEFAULT_SETTINGS["github_releases_url"]))
        self.human_like_enabled_var.set(bool(DEFAULT_SETTINGS["human_like_enabled"]))
        self.human_like_speed_var.set(str(DEFAULT_SETTINGS["human_like_speed"]))
        default_tasks = set(DEFAULT_SETTINGS["loop_tasks"])
        for task_id, variable in self.loop_task_vars.items():
            variable.set(task_id in default_tasks)

    def _task_toggled(self, task_id: str) -> None:
        task = LOOP_TASKS[task_id]
        if task["implemented"]:
            return
        self.loop_task_vars[task_id].set(False)
        messagebox.showinfo(
            "Task not implemented",
            f"{task['label']} is not implemented yet.\n\nIt can be added to the loop when the feature exists.",
        )

    def save(self) -> None:
        try:
            timeout_ms = int(self.timeout_var.get())
            manual_timeout = int(self.manual_timeout_var.get())
            loop_interval = int(self.loop_interval_var.get())
            if timeout_ms <= 0 or manual_timeout <= 0 or loop_interval <= 0:
                raise ValueError
        except ValueError:
            messagebox.showerror("Invalid settings", "Timeout and interval values must be positive numbers.")
            return

        loop_tasks = [
            task_id
            for task_id, selected in self.loop_task_vars.items()
            if selected.get() and LOOP_TASKS[task_id]["implemented"]
        ]
        human_like_speed = self.human_like_speed_var.get()
        if human_like_speed not in {"slow", "medium", "fast"}:
            messagebox.showerror("Invalid settings", "Behavior speed must be slow, medium, or fast.")
            return

        config = BotConfig(
            server_name=self.config.server_name,
            base_url=self.config.base_url,
            login_path="/login.php",
            village_overview_path="/dorf1.php",
            headless=self.headless_var.get(),
            timeout_ms=timeout_ms,
            manual_login_timeout_seconds=manual_timeout,
            loop_interval_seconds=loop_interval,
            loop_tasks=loop_tasks,
            github_releases_url=self.github_releases_url_var.get().strip(),
            human_like_enabled=self.human_like_enabled_var.get(),
            human_like_speed=human_like_speed,
        )
        save_bot_config(config)
        self.config = config
        self.status_var.set("Settings saved.")
        self.window.destroy()


class AccountDialog:
    def __init__(self, ui: TbotUi) -> None:
        self.ui = ui
        self.window = tk.Toplevel(ui.root)
        self.window.title("Accounts")
        self.window.geometry("520x360")
        self.window.minsize(480, 330)
        self.window.transient(ui.root)
        self.window.grab_set()

        self.account_var = tk.StringVar()
        self.name_var = tk.StringVar()
        self.username_var = tk.StringVar()
        self.password_var = tk.StringVar()
        current_config = load_bot_config()
        self.server_var = tk.StringVar(value=current_config.server_name)
        self.server_options: dict[str, ServerOption] = {
            current_config.server_name: ServerOption(current_config.server_name, current_config.base_url)
        }
        self.is_new_account = False

        self._build()
        self.refresh_servers()
        self.refresh()

    def _build(self) -> None:
        frame = ttk.Frame(self.window, padding=12)
        frame.pack(fill=tk.BOTH, expand=True)
        frame.columnconfigure(1, weight=1)

        ttk.Label(frame, text="Selected").grid(row=0, column=0, sticky="w", pady=4)
        self.account_box = ttk.Combobox(frame, textvariable=self.account_var, state="readonly")
        self.account_box.grid(row=0, column=1, sticky="ew", pady=4, padx=(8, 0))
        self.account_box.bind("<<ComboboxSelected>>", lambda _: self.load_selected())

        ttk.Label(frame, text="Name").grid(row=1, column=0, sticky="w", pady=4)
        ttk.Entry(frame, textvariable=self.name_var).grid(row=1, column=1, sticky="ew", pady=4, padx=(8, 0))

        ttk.Label(frame, text="Username/email").grid(row=2, column=0, sticky="w", pady=4)
        ttk.Entry(frame, textvariable=self.username_var).grid(row=2, column=1, sticky="ew", pady=4, padx=(8, 0))

        ttk.Label(frame, text="Password").grid(row=3, column=0, sticky="w", pady=4)
        ttk.Entry(frame, textvariable=self.password_var, show="*").grid(row=3, column=1, sticky="ew", pady=4, padx=(8, 0))

        ttk.Label(frame, text="Server").grid(row=4, column=0, sticky="w", pady=4)
        server_row = ttk.Frame(frame)
        server_row.grid(row=4, column=1, sticky="ew", pady=4, padx=(8, 0))
        server_row.columnconfigure(0, weight=1)
        self.server_box = ttk.Combobox(server_row, textvariable=self.server_var, state="readonly")
        self.server_box.grid(row=0, column=0, sticky="ew", padx=(0, 6))
        self.server_box["values"] = list(self.server_options.keys())
        ttk.Button(server_row, text="Refresh", command=self.refresh_servers).grid(row=0, column=1, sticky="e")

        buttons = ttk.Frame(frame)
        buttons.grid(row=5, column=0, columnspan=2, sticky="ew", pady=(14, 0))
        buttons.columnconfigure((0, 1, 2, 3), weight=1)

        ttk.Button(buttons, text="Save", command=self.save, style="Primary.TButton").grid(row=0, column=0, sticky="ew", padx=(0, 5))
        ttk.Button(buttons, text="Add account", command=self.new_account).grid(row=0, column=1, sticky="ew", padx=5)
        ttk.Button(buttons, text="Delete", command=self.delete, style="Danger.TButton").grid(row=0, column=2, sticky="ew", padx=5)
        ttk.Button(buttons, text="Close", command=self.window.destroy).grid(row=0, column=3, sticky="ew", padx=(5, 0))

        tools = ttk.Frame(frame)
        tools.grid(row=6, column=0, columnspan=2, sticky="ew", pady=(10, 0))
        tools.columnconfigure((0, 1), weight=1)
        ttk.Button(tools, text="Clear session", command=self.clear_session).grid(row=0, column=0, sticky="ew", padx=(0, 5))
        ttk.Button(tools, text="Open verification browser", command=self.open_verification_browser).grid(
            row=0, column=1, sticky="ew", padx=(5, 0)
        )

    def refresh(self) -> None:
        accounts = list_accounts()
        names = [account.name for account in accounts]
        self.account_box["values"] = names

        selected = self.ui.account_var.get() or active_account_name()
        if selected in names:
            self.account_var.set(selected)
            self.load_selected()
        elif names:
            self.account_var.set(names[0])
            self.load_selected()
        else:
            self.new_account()

    def load_selected(self) -> None:
        selected = self.account_var.get()
        for account in list_accounts():
            if account.name == selected:
                self.name_var.set(account.name)
                self.username_var.set(account.username)
                self.password_var.set(account.password)
                self._set_account_server(account)
                self.is_new_account = False
                return

    def new_account(self) -> None:
        self.account_var.set("")
        self.name_var.set("")
        self.username_var.set("")
        self.password_var.set("")
        self.is_new_account = True
        current_config = load_bot_config()
        self.server_options.setdefault(
            current_config.server_name,
            ServerOption(current_config.server_name, current_config.base_url),
        )
        self.server_box["values"] = list(self.server_options.keys())
        self.server_var.set(current_config.server_name)

    def _set_account_server(self, account: StoredAccount) -> None:
        current_config = load_bot_config()
        server_name = account.server_name or current_config.server_name
        server_url = account.server_url or current_config.base_url
        self.server_options.setdefault(server_name, ServerOption(server_name, server_url))
        self.server_box["values"] = list(self.server_options.keys())
        self.server_var.set(server_name)

    def refresh_servers(self) -> None:
        self.ui.status_var.set("Fetching SS-Travi server list...")
        thread = threading.Thread(target=self._refresh_servers_worker, daemon=True)
        thread.start()

    def _refresh_servers_worker(self) -> None:
        try:
            servers = fetch_ss_travi_servers()
        except Exception as exc:
            self.window.after(0, self._server_fetch_failed, str(exc))
            return

        self.window.after(0, self._server_fetch_done, servers)

    def _server_fetch_done(self, servers: list[ServerOption]) -> None:
        if not servers:
            self.ui.status_var.set("No SS-Travi servers found.")
            return

        selected = self.server_var.get()
        self.server_options.update({server.name: server for server in servers})
        self.server_box["values"] = list(self.server_options.keys())
        if selected in self.server_options:
            self.server_var.set(selected)
        else:
            self.server_var.set(servers[0].name)
        self.ui.status_var.set(f"Fetched {len(servers)} SS-Travi servers.")

    def _server_fetch_failed(self, error: str) -> None:
        self.server_box["values"] = list(self.server_options.keys())
        self.ui.status_var.set(f"Could not fetch server list: {error}")

    def save(self) -> None:
        try:
            normalized_name = normalize_account_name(self.name_var.get())
            selected_server = self.server_options.get(self.server_var.get())
            if not selected_server:
                messagebox.showerror("Could not save account", "Select a server first.")
                return
            if self.is_new_account and normalized_name in [account.name for account in list_accounts()]:
                messagebox.showerror("Could not save account", f"Account '{normalized_name}' already exists. Select it to update it.")
                return
            account = StoredAccount(
                name=normalized_name,
                username=self.username_var.get(),
                password=self.password_var.get(),
                server_name=selected_server.name,
                server_url=selected_server.base_url,
            )
            save_account(account)
        except RuntimeError as exc:
            messagebox.showerror("Could not save account", str(exc))
            return

        self.ui.refresh_accounts()
        self.ui.account_var.set(normalized_name)
        self.ui.load_selected_account()
        self.account_var.set(normalized_name)
        self.is_new_account = False
        self.refresh()
        self.ui.status_var.set(f"Saved account '{normalized_name}'.")

    def delete(self) -> None:
        account_name = self.account_var.get() or self.name_var.get().strip()
        if not account_name:
            messagebox.showerror("Missing account", "Select an account first.")
            return

        account_name = normalize_account_name(account_name)
        if not messagebox.askyesno("Delete account", f"Delete account '{account_name}' from .env?"):
            return

        try:
            delete_account(account_name)
        except RuntimeError as exc:
            messagebox.showerror("Could not delete account", str(exc))
            return

        state_path = ROOT_DIR / "playwright" / ".auth" / f"{account_name}.json"
        if state_path.exists():
            state_path.unlink()

        self.ui.refresh_accounts()
        self.refresh()
        self.ui.status_var.set(f"Deleted account '{account_name}'.")

    def clear_session(self) -> None:
        account_name = self.account_var.get() or self.name_var.get().strip()
        if not account_name:
            messagebox.showerror("Missing account", "Select an account first.")
            return

        self.ui.account_var.set(normalize_account_name(account_name))
        self.ui.logout_session()

    def open_verification_browser(self) -> None:
        account_name = self.account_var.get() or self.name_var.get().strip()
        if not account_name:
            messagebox.showerror("Missing account", "Select an account first.")
            return

        self.ui.account_var.set(normalize_account_name(account_name))
        self.ui.load_selected_account()
        self.ui.start_verification_browser()


def start_ui() -> None:
    root = tk.Tk()
    TbotUi(root)
    root.mainloop()
