import threading
import time
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
from .travian_client import ManualVerificationRequired, TravianClient, VillageSnapshot, VillageStatus


class TbotUi:
    def __init__(self, root: tk.Tk) -> None:
        self.root = root
        self.root.title("Tbot Ultra")
        self.root.geometry("820x620")
        self.root.minsize(760, 560)
        self.root.configure(bg="#f4f6f8")

        self.account_var = tk.StringVar()
        self.name_var = tk.StringVar()
        self.username_var = tk.StringVar()
        self.password_var = tk.StringVar()
        self.status_var = tk.StringVar(value="Ready.")
        self.bot_running = False
        self.verification_running = False
        self.manual_step_popup_open = False
        self.logout_requested = False
        self.active_browser_ready = False
        self.action_buttons: list[ttk.Button] = []
        self.verification_button: ttk.Button | None = None
        self.summary_var = tk.StringVar(value="Run Read village status to populate the views.")
        self.resource_tree: ttk.Treeview | None = None
        self.building_tree: ttk.Treeview | None = None
        self.queue_tree: ttk.Treeview | None = None
        self.resource_map_frame: ttk.Frame | None = None
        self.building_map_frame: ttk.Frame | None = None

        self._configure_styles()
        self._build()
        self.refresh_accounts()

    def _configure_styles(self) -> None:
        style = ttk.Style()
        style.theme_use("clam")
        style.configure(".", font=("Segoe UI", 9), background="#f4f6f8", foreground="#17202a")
        style.configure("App.TFrame", background="#f4f6f8")
        style.configure("Panel.TFrame", background="#ffffff", relief="flat")
        style.configure("Header.TLabel", background="#f4f6f8", foreground="#101820", font=("Segoe UI Semibold", 16))
        style.configure("Subtle.TLabel", background="#f4f6f8", foreground="#5f6b7a")
        style.configure("PanelTitle.TLabel", background="#ffffff", foreground="#101820", font=("Segoe UI Semibold", 10))
        style.configure("Panel.TLabel", background="#ffffff", foreground="#17202a")
        style.configure("Status.TLabel", background="#ffffff", foreground="#344054", justify="left")
        style.configure("TEntry", fieldbackground="#ffffff", bordercolor="#d0d7de", lightcolor="#d0d7de", darkcolor="#d0d7de")
        style.configure("TCombobox", fieldbackground="#ffffff", bordercolor="#d0d7de", lightcolor="#d0d7de", darkcolor="#d0d7de")
        style.configure("TButton", padding=(8, 5), background="#eef2f6", foreground="#17202a", bordercolor="#d0d7de")
        style.map("TButton", background=[("active", "#e2e8f0"), ("disabled", "#edf0f3")])
        style.configure("Map.TButton", padding=(5, 5), background="#f8fafc", foreground="#17202a", bordercolor="#d0d7de")
        style.map("Map.TButton", background=[("active", "#e0f2fe")])
        style.configure("Primary.TButton", padding=(9, 6), background="#2563eb", foreground="#ffffff", bordercolor="#2563eb")
        style.map("Primary.TButton", background=[("active", "#1d4ed8"), ("disabled", "#94a3b8")], foreground=[("disabled", "#ffffff")])
        style.configure("Danger.TButton", padding=(8, 5), background="#fff1f2", foreground="#b42318", bordercolor="#fecdd3")
        style.map("Danger.TButton", background=[("active", "#ffe4e6")])
        style.configure("Treeview", rowheight=23, background="#ffffff", fieldbackground="#ffffff", foreground="#17202a")
        style.configure("Treeview.Heading", font=("Segoe UI Semibold", 9), background="#f1f5f9", foreground="#17202a")

    def _build(self) -> None:
        frame = ttk.Frame(self.root, padding=12, style="App.TFrame")
        frame.pack(fill=tk.BOTH, expand=True)
        frame.columnconfigure(0, weight=1)
        frame.rowconfigure(3, weight=1)

        header = ttk.Frame(frame, style="App.TFrame")
        header.grid(row=0, column=0, sticky="ew", pady=(0, 8))
        header.columnconfigure(0, weight=1)

        ttk.Label(header, text="Tbot Ultra", style="Header.TLabel").grid(row=0, column=0, sticky="w")
        ttk.Label(header, text="Local Travian control panel", style="Subtle.TLabel").grid(row=1, column=0, sticky="w", pady=(2, 0))
        ttk.Button(header, text="Settings", command=self.open_settings).grid(row=0, column=1, rowspan=2, sticky="e")

        account_panel = ttk.Frame(frame, padding=10, style="Panel.TFrame")
        account_panel.grid(row=1, column=0, sticky="ew")
        account_panel.columnconfigure(1, weight=1)
        account_panel.columnconfigure(2, weight=1)

        ttk.Label(account_panel, text="Account", style="PanelTitle.TLabel").grid(row=0, column=0, columnspan=4, sticky="w", pady=(0, 8))

        ttk.Label(account_panel, text="Selected", style="Panel.TLabel").grid(row=1, column=0, sticky="w", pady=3)
        self.account_box = ttk.Combobox(account_panel, textvariable=self.account_var, state="readonly")
        self.account_box.grid(row=1, column=1, columnspan=2, sticky="ew", padx=(10, 6), pady=3)
        self.account_box.bind("<<ComboboxSelected>>", lambda _: self.load_selected_account())
        ttk.Button(account_panel, text="Refresh", command=self.refresh_accounts).grid(row=1, column=3, sticky="ew", pady=3)

        ttk.Label(account_panel, text="Name", style="Panel.TLabel").grid(row=2, column=0, sticky="w", pady=3)
        ttk.Entry(account_panel, textvariable=self.name_var).grid(row=2, column=1, sticky="ew", padx=(10, 6), pady=3)

        ttk.Label(account_panel, text="Username/email", style="Panel.TLabel").grid(row=2, column=2, sticky="w", padx=(10, 6), pady=3)
        ttk.Entry(account_panel, textvariable=self.username_var).grid(row=2, column=3, sticky="ew", pady=3)

        ttk.Label(account_panel, text="Password", style="Panel.TLabel").grid(row=3, column=0, sticky="w", pady=3)
        ttk.Entry(account_panel, textvariable=self.password_var, show="*").grid(row=3, column=1, columnspan=3, sticky="ew", padx=(10, 0), pady=3)

        account_buttons = ttk.Frame(account_panel, style="Panel.TFrame")
        account_buttons.grid(row=4, column=0, columnspan=4, sticky="ew", pady=(8, 0))
        account_buttons.columnconfigure((0, 1, 2, 3, 4), weight=1)
        ttk.Button(account_buttons, text="Save", command=self.save_current_account).grid(row=0, column=0, sticky="ew", padx=(0, 5))
        ttk.Button(account_buttons, text="Login", command=self.start_login, style="Primary.TButton").grid(row=0, column=1, sticky="ew", padx=5)
        ttk.Button(account_buttons, text="Logout", command=self.start_travian_logout).grid(row=0, column=2, sticky="ew", padx=5)
        ttk.Button(account_buttons, text="Clear session", command=self.logout_session).grid(row=0, column=3, sticky="ew", padx=5)
        ttk.Button(account_buttons, text="Delete", command=self.delete_current_account, style="Danger.TButton").grid(row=0, column=4, sticky="ew", padx=(5, 0))

        actions_panel = ttk.Frame(frame, padding=10, style="Panel.TFrame")
        actions_panel.grid(row=2, column=0, sticky="ew", pady=(8, 0))
        actions_panel.columnconfigure((0, 1, 2), weight=1)

        ttk.Label(actions_panel, text="Actions", style="PanelTitle.TLabel").grid(row=0, column=0, columnspan=3, sticky="w", pady=(0, 8))
        login_button = ttk.Button(actions_panel, text="Read villages", command=self.start_read_villages, style="Primary.TButton")
        status_button = ttk.Button(actions_panel, text="Read village status", command=self.start_village_status)
        self.verification_button = ttk.Button(actions_panel, text="Open verification browser", command=self.start_verification_browser)
        login_button.grid(row=1, column=0, sticky="ew", padx=(0, 5))
        status_button.grid(row=1, column=1, sticky="ew", padx=5)
        self.verification_button.grid(row=1, column=2, sticky="ew", padx=(5, 0))
        self.action_buttons = [login_button, status_button]

        status_panel = ttk.Frame(frame, padding=10, style="Panel.TFrame")
        status_panel.grid(row=3, column=0, sticky="nsew", pady=(8, 0))
        status_panel.columnconfigure(0, weight=1)
        status_panel.rowconfigure(2, weight=1)

        ttk.Label(status_panel, text="Status", style="PanelTitle.TLabel").grid(row=0, column=0, sticky="w", pady=(0, 6))
        ttk.Label(status_panel, textvariable=self.status_var, style="Status.TLabel", wraplength=760).grid(row=1, column=0, sticky="ew", pady=(0, 6))
        self._build_result_views(status_panel)

    def _build_result_views(self, parent: ttk.Frame) -> None:
        notebook = ttk.Notebook(parent)
        notebook.grid(row=2, column=0, sticky="nsew")

        overview = ttk.Frame(notebook, padding=8, style="Panel.TFrame")
        resources = ttk.Frame(notebook, padding=8, style="Panel.TFrame")
        buildings = ttk.Frame(notebook, padding=8, style="Panel.TFrame")
        resource_map = ttk.Frame(notebook, padding=8, style="Panel.TFrame")
        building_map = ttk.Frame(notebook, padding=8, style="Panel.TFrame")
        queue = ttk.Frame(notebook, padding=8, style="Panel.TFrame")

        notebook.add(overview, text="Overview")
        notebook.add(resources, text="Resource fields")
        notebook.add(buildings, text="Buildings")
        notebook.add(resource_map, text="Resource map")
        notebook.add(building_map, text="Building map")
        notebook.add(queue, text="Build queue")

        overview.columnconfigure(0, weight=1)
        ttk.Label(overview, textvariable=self.summary_var, style="Status.TLabel", wraplength=760).grid(row=0, column=0, sticky="nw")

        self.resource_tree = self._create_tree(resources, ("slot", "type", "level"), ("Slot", "Type", "Level"))
        self.building_tree = self._create_tree(buildings, ("slot", "name", "level"), ("Slot", "Building", "Level"))
        self.queue_tree = self._create_tree(queue, ("text", "time"), ("Item", "Time left"))

        self.resource_map_frame = resource_map
        self.building_map_frame = building_map
        for column in range(6):
            resource_map.columnconfigure(column, weight=1)
        for column in range(5):
            building_map.columnconfigure(column, weight=1)
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
                self.status_var.set(f"Loaded account '{account.name}'.")
                return

    def save_current_account(self) -> bool:
        try:
            normalized_name = normalize_account_name(self.name_var.get())
            account = StoredAccount(
                name=normalized_name,
                username=self.username_var.get(),
                password=self.password_var.get(),
            )
            save_account(account)
            self.name_var.set(normalized_name)
            self.status_var.set(f"Saved account '{normalized_name}'.")
            self.refresh_accounts()
            return True
        except RuntimeError as exc:
            messagebox.showerror("Could not save account", str(exc))
            return False

    def start_login(self) -> None:
        self._start_bot_action("login")

    def start_read_villages(self) -> None:
        self._start_bot_action("villages")

    def start_village_status(self) -> None:
        self._start_bot_action("status")

    def start_travian_logout(self) -> None:
        if self.bot_running:
            if self.active_browser_ready:
                self.logout_requested = True
                self._set_status("Logout requested. The bot will log out in the open Chromium window.")
            else:
                messagebox.showinfo("Bot is busy", "Wait until the browser is ready, then press Logout again.")
            return

        account_name = self.name_var.get().strip() or self.account_var.get().strip()
        if not account_name:
            messagebox.showerror("Missing account", "Add or select an account first.")
            return

        if not self.save_current_account():
            return

        account_name = normalize_account_name(account_name)
        self.bot_running = True
        self._set_actions_enabled(False)
        self._set_status(f"Logging out account '{account_name}' from Travian...")
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
                client.logout()
                browser_session.save_state()
                self._set_status(f"Logged out account '{account_name}' from Travian. Closing Chromium...")
        except ManualVerificationRequired as exc:
            self._set_status(f"{exc}\n\nOpen verification browser if needed, then try Logout again.")
        except Exception as exc:
            self._set_status(f"Logout error: {exc}")
        finally:
            self.root.after(0, self._mark_bot_stopped)

    def _start_bot_action(self, action: str) -> None:
        if self.bot_running:
            messagebox.showinfo("Bot is already running", "Close the current browser window before starting another scan.")
            return

        account_name = self.name_var.get().strip() or self.account_var.get().strip()
        if not account_name:
            messagebox.showerror("Missing account", "Add or select an account first.")
            return

        if not self.save_current_account():
            return

        account_name = normalize_account_name(account_name)
        self.bot_running = True
        self.logout_requested = False
        self.active_browser_ready = False
        self._set_actions_enabled(False)
        self._set_status(f"Opening visible browser for account '{account_name}'...")

        thread = threading.Thread(target=self._run_bot_action, args=(action, account_name), daemon=True)
        thread.start()

    def _run_bot_action(self, action: str, account_name: str) -> None:
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
                client.login()
                self.active_browser_ready = True

                if action == "login":
                    self._set_status("Login completed.")
                    summary = "Login completed. Browser is still open. Close Chromium when you are done."
                elif action == "status":
                    self._set_status("Reading village status...")
                    result = client.read_village_status()
                    summary = self._format_village_status(result)
                    self.root.after(0, self._render_village_status, result)
                else:
                    self._set_status("Reading villages and resources...")
                    result = client.read_village_snapshot()
                    summary = self._format_village_snapshot(result)
                    self.root.after(0, self._render_village_snapshot, result)

                browser_session.save_state()
                self._set_status(f"{summary}\n\nBrowser is still open. Close Chromium when you are done.")
                self._wait_for_browser_close_or_logout(page, client, browser_session, account_name)
        except ManualVerificationRequired as exc:
            self._set_status(
                f"{exc}\n\nUse 'Open verification browser', solve the manual step, close Chromium, then run the action again."
            )
        except Exception as exc:
            self._set_status(f"Error: {exc}")
        finally:
            self.root.after(0, self._mark_bot_stopped)

    def start_verification_browser(self) -> None:
        if self.verification_running:
            messagebox.showinfo("Verification browser is already open", "Close the current Chromium window first.")
            return

        account_name = self.name_var.get().strip() or self.account_var.get().strip()
        if not account_name:
            messagebox.showerror("Missing account", "Add or select an account first.")
            return

        if not self.save_current_account():
            return

        account_name = normalize_account_name(account_name)
        self.verification_running = True
        self._set_status(f"Opening visible verification browser for account '{account_name}'...")
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
                browser_session.save_state()
                self._set_status("Verification/login completed. Session saved. Closing Chromium...")
        except Exception as exc:
            self._set_status(f"Verification browser error: {exc}")
        finally:
            self.root.after(0, self._mark_verification_stopped)

    def _mark_verification_stopped(self) -> None:
        self.verification_running = False

    def _wait_for_browser_close(self, page) -> None:
        while True:
            try:
                if page.is_closed():
                    return
            except Exception:
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
                    return
                if self.logout_requested:
                    self._set_status(f"Logging out account '{account_name}' in the open Chromium window...")
                    client.logout()
                    browser_session.save_state()
                    self._set_status(f"Logged out account '{account_name}' from Travian. Closing Chromium...")
                    return
            except Exception as exc:
                self._set_status(f"Logout error: {exc}")
                return
            time.sleep(0.5)

    def _mark_bot_stopped(self) -> None:
        self.bot_running = False
        self.logout_requested = False
        self.active_browser_ready = False
        self._set_actions_enabled(True)

    def _set_status(self, text: str) -> None:
        self.root.after(0, self.status_var.set, text)

    def _set_actions_enabled(self, enabled: bool) -> None:
        state = "normal" if enabled else "disabled"
        for button in self.action_buttons:
            button.configure(state=state)

    def _show_manual_step_popup(self, message: str) -> None:
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

    def _format_village_snapshot(self, snapshot: VillageSnapshot) -> str:
        villages = ", ".join(village.name for village in snapshot.villages) or "none found"
        resources = ", ".join(f"{name}: {value}" for name, value in snapshot.resources.items()) or "none found"
        return f"Villages: {villages}\nResources: {resources}"

    def _format_village_status(self, status: VillageStatus) -> str:
        resources = ", ".join(f"{name}: {value}" for name, value in status.resources.items()) or "none found"
        return (
            f"Active village: {status.active_village}\n"
            f"Villages found: {len(status.villages)}\n"
            f"Resources: {resources}\n"
            f"Resource fields found: {len(status.resource_fields)}\n"
            f"Buildings found: {len(status.buildings)}\n"
            f"Build queue items: {len(status.build_queue)}"
        )

    def _render_village_snapshot(self, snapshot: VillageSnapshot) -> None:
        resources = "\n".join(f"{name}: {value}" for name, value in snapshot.resources.items()) or "No resources found."
        villages = "\n".join(f"- {village.name}" for village in snapshot.villages) or "- No villages found."
        self.summary_var.set(f"Villages\n{villages}\n\nResources\n{resources}")
        self._clear_tree(self.resource_tree)
        self._clear_tree(self.building_tree)
        self._clear_tree(self.queue_tree)
        self._render_empty_maps()

    def _render_village_status(self, status: VillageStatus) -> None:
        resources = "\n".join(f"{name}: {value}" for name, value in status.resources.items()) or "No resources found."
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
                self.resource_tree.insert("", "end", values=(slot, field.field_type, level))

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

    def _clear_tree(self, tree: ttk.Treeview | None) -> None:
        if not tree:
            return
        for item in tree.get_children():
            tree.delete(item)

    def _render_empty_maps(self) -> None:
        if self.resource_map_frame:
            self._clear_children(self.resource_map_frame)
            ttk.Label(
                self.resource_map_frame,
                text="Run Read village status to draw the resource field map.",
                style="Status.TLabel",
            ).grid(row=0, column=0, sticky="nw")
        if self.building_map_frame:
            self._clear_children(self.building_map_frame)
            ttk.Label(
                self.building_map_frame,
                text="Run Read village status to draw the building map.",
                style="Status.TLabel",
            ).grid(row=0, column=0, sticky="nw")

    def _render_resource_map(self, status: VillageStatus) -> None:
        if not self.resource_map_frame:
            return

        self._clear_children(self.resource_map_frame)
        slots = {field.slot_id: field for field in status.resource_fields if field.slot_id is not None}
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

    def _render_building_map(self, status: VillageStatus) -> None:
        if not self.building_map_frame:
            return

        self._clear_children(self.building_map_frame)
        buildings = {building.slot_id: building for building in status.buildings if building.slot_id is not None}
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
        return f"Slot {slot_id}\n{field.field_type}\nLvl {level}"

    def _building_card_text(self, slot_id: int, building) -> str:
        if not building:
            return f"Slot {slot_id}\nEmpty/unknown\nLvl ?"
        level = building.level if building.level is not None else "?"
        name = building.name if len(building.name) <= 22 else f"{building.name[:19]}..."
        return f"Slot {slot_id}\n{name}\nLvl {level}"

    def _select_resource_slot(self, slot_id: int, field) -> None:
        if not field:
            self.status_var.set(f"Selected resource slot {slot_id}. No field data was found for this slot.")
            return
        level = field.level if field.level is not None else "?"
        self.status_var.set(
            f"Selected resource slot {slot_id}: {field.field_type}, level {level}.\n"
            "Upgrade action is not connected yet; next step is reading cost and queue state safely."
        )

    def _select_building_slot(self, slot_id: int, building) -> None:
        if not building:
            self.status_var.set(f"Selected building slot {slot_id}. No building data was found for this slot.")
            return
        level = building.level if building.level is not None else "?"
        self.status_var.set(
            f"Selected building slot {slot_id}: {building.name}, level {level}.\n"
            "Upgrade action is not connected yet; next step is reading cost and queue state safely."
        )

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


class SettingsDialog:
    def __init__(self, parent: tk.Tk, status_var: tk.StringVar) -> None:
        self.status_var = status_var
        self.window = tk.Toplevel(parent)
        self.window.title("Settings")
        self.window.geometry("520x360")
        self.window.minsize(480, 330)
        self.window.transient(parent)
        self.window.grab_set()

        config = load_bot_config()
        self.server_name_var = tk.StringVar(value=config.server_name)
        self.base_url_var = tk.StringVar(value=config.base_url)
        self.login_path_var = tk.StringVar(value=config.login_path)
        self.overview_path_var = tk.StringVar(value=config.village_overview_path)
        self.headless_var = tk.BooleanVar(value=config.headless)
        self.timeout_var = tk.StringVar(value=str(config.timeout_ms))
        self.manual_timeout_var = tk.StringVar(value=str(config.manual_login_timeout_seconds))

        self._build()

    def _build(self) -> None:
        frame = ttk.Frame(self.window, padding=16)
        frame.pack(fill=tk.BOTH, expand=True)
        frame.columnconfigure(1, weight=1)

        ttk.Label(frame, text="Server name").grid(row=0, column=0, sticky="w", pady=6)
        ttk.Entry(frame, textvariable=self.server_name_var).grid(row=0, column=1, sticky="ew", pady=6)

        ttk.Label(frame, text="Base URL").grid(row=1, column=0, sticky="w", pady=6)
        ttk.Entry(frame, textvariable=self.base_url_var).grid(row=1, column=1, sticky="ew", pady=6)

        ttk.Label(frame, text="Login path").grid(row=2, column=0, sticky="w", pady=6)
        ttk.Entry(frame, textvariable=self.login_path_var).grid(row=2, column=1, sticky="ew", pady=6)

        ttk.Label(frame, text="Village overview path").grid(row=3, column=0, sticky="w", pady=6)
        ttk.Entry(frame, textvariable=self.overview_path_var).grid(row=3, column=1, sticky="ew", pady=6)

        ttk.Label(frame, text="Timeout ms").grid(row=4, column=0, sticky="w", pady=6)
        ttk.Entry(frame, textvariable=self.timeout_var).grid(row=4, column=1, sticky="ew", pady=6)

        ttk.Label(frame, text="Manual login timeout sec").grid(row=5, column=0, sticky="w", pady=6)
        ttk.Entry(frame, textvariable=self.manual_timeout_var).grid(row=5, column=1, sticky="ew", pady=6)

        ttk.Checkbutton(frame, text="Headless browser", variable=self.headless_var).grid(
            row=6, column=1, sticky="w", pady=8
        )

        buttons = ttk.Frame(frame)
        buttons.grid(row=7, column=0, columnspan=2, sticky="ew", pady=(18, 0))
        buttons.columnconfigure((0, 1), weight=1)
        ttk.Button(buttons, text="Save", command=self.save).grid(row=0, column=0, sticky="ew", padx=(0, 6))
        ttk.Button(buttons, text="Close", command=self.window.destroy).grid(row=0, column=1, sticky="ew", padx=(6, 0))

    def save(self) -> None:
        try:
            timeout_ms = int(self.timeout_var.get())
            manual_timeout = int(self.manual_timeout_var.get())
            if timeout_ms <= 0 or manual_timeout <= 0:
                raise ValueError
        except ValueError:
            messagebox.showerror("Invalid settings", "Timeout values must be positive numbers.")
            return

        base_url = self.base_url_var.get().strip().rstrip("/")
        login_path = self._clean_path(self.login_path_var.get())
        overview_path = self._clean_path(self.overview_path_var.get())
        if not base_url.startswith(("http://", "https://")):
            messagebox.showerror("Invalid settings", "Base URL must start with http:// or https://.")
            return

        config = BotConfig(
            server_name=self.server_name_var.get().strip() or "Travian server",
            base_url=base_url,
            login_path=login_path,
            village_overview_path=overview_path,
            headless=self.headless_var.get(),
            timeout_ms=timeout_ms,
            manual_login_timeout_seconds=manual_timeout,
        )
        save_bot_config(config)
        self.status_var.set("Settings saved.")

    def _clean_path(self, value: str) -> str:
        cleaned = value.strip() or "/"
        if not cleaned.startswith("/"):
            cleaned = f"/{cleaned}"
        return cleaned


def start_ui() -> None:
    root = tk.Tk()
    TbotUi(root)
    root.mainloop()
