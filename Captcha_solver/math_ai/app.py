import os
import queue
import subprocess
import threading
import tkinter as tk
from tkinter import messagebox
from tkinter import ttk
from tkinter.scrolledtext import ScrolledText

from curate_dataset import LABEL_TOKENS, UNKNOWN_REVIEW_DIR, ensure_review_structure, is_image_file, move_file


BASE_DIR = os.path.dirname(os.path.abspath(__file__))
PYTHON_EXE = os.path.join(BASE_DIR, ".venv", "Scripts", "python.exe")


class ToolTip:
    def __init__(self, widget: tk.Widget, text: str) -> None:
        self.widget = widget
        self.text = text
        self.popup: tk.Toplevel | None = None

        self.widget.bind("<Enter>", self.show)
        self.widget.bind("<Leave>", self.hide)
        self.widget.bind("<ButtonPress>", self.hide)

    def show(self, _event: tk.Event | None = None) -> None:
        if self.popup or not self.text:
            return

        x = self.widget.winfo_rootx() + 10
        y = self.widget.winfo_rooty() + self.widget.winfo_height() + 6

        self.popup = tk.Toplevel(self.widget)
        self.popup.wm_overrideredirect(True)
        self.popup.wm_geometry(f"+{x}+{y}")

        label = tk.Label(
            self.popup,
            text=self.text,
            justify="left",
            bg="#fff7cc",
            fg="black",
            relief="solid",
            bd=1,
            padx=8,
            pady=4,
            wraplength=220,
        )
        label.pack()

    def hide(self, _event: tk.Event | None = None) -> None:
        if self.popup is not None:
            self.popup.destroy()
            self.popup = None


class CaptchaApp:
    def __init__(self, root: tk.Tk) -> None:
        self.root = root
        self.root.title("Captcha Trainer")
        self.root.geometry("560x700")
        self.root.minsize(520, 620)

        self.output_queue: queue.Queue[str] = queue.Queue()
        self.process: subprocess.Popen[str] | None = None
        self.worker_thread: threading.Thread | None = None

        self.status_var = tk.StringVar(value="Idle")
        self.dataset_count_var = tk.StringVar(value="Count: 0")
        self.review_count_var = tk.StringVar(value="Count: 0")
        self.debug_count_var = tk.StringVar(value="Count: 0")
        self.quarantine_count_var = tk.StringVar(value="Count: 0")

        ensure_review_structure(os.path.join(BASE_DIR, "review_dataset"))

        self._build_ui()
        self.refresh_counts()
        self.root.after(100, self._drain_output)

    def _build_ui(self) -> None:
        container = ttk.Frame(self.root, padding=12)
        container.pack(fill="both", expand=True)

        actions = ttk.LabelFrame(container, text="Actions", padding=10)
        actions.pack(fill="x")

        actions_left = ttk.Frame(actions)
        actions_left.grid(row=0, column=0, sticky="nw")

        actions_right = ttk.Frame(actions)
        actions_right.grid(row=0, column=1, sticky="ne")

        self.run_button = ttk.Button(actions_left, text="Run test_images", command=self.run_tests, width=18)
        self.run_button.grid(row=0, column=0, padx=(0, 8), pady=4, sticky="ew")

        self.open_test_button = ttk.Button(
            actions_left,
            text="Open test_image",
            command=lambda: self.open_folder("test_images"),
            width=15,
        )
        self.open_test_button.grid(row=0, column=1, pady=4, sticky="ew")

        self.curate_button = ttk.Button(actions_left, text="Check dataset", command=self.run_curate, width=18)
        self.curate_button.grid(row=1, column=0, padx=(0, 8), pady=4, sticky="ew")

        self.generate_button = ttk.Button(
            actions_left,
            text="Generate variations",
            command=self.generate_variations,
            width=18,
        )
        self.generate_button.grid(row=2, column=0, padx=(0, 8), pady=4, sticky="ew")

        self.train_button = ttk.Button(actions_left, text="Train", command=self.run_train, width=18)
        self.train_button.grid(row=3, column=0, padx=(0, 8), pady=4, sticky="ew")

        self.refresh_button = ttk.Button(actions_right, text="Refresh", command=self.refresh_counts, width=14)
        self.refresh_button.grid(row=0, column=0, sticky="ew")

        self.stop_button = ttk.Button(actions_right, text="Stop", command=self.stop_process, state="disabled", width=14)
        self.stop_button.grid(row=1, column=0, pady=(18, 0), ipady=18, sticky="nsew")

        ToolTip(self.run_button, "Run the solver on images in test_images.")
        ToolTip(self.train_button, "Train the model using the current dataset.")
        ToolTip(self.curate_button, "Check dataset images and move uncertain ones to review.")
        ToolTip(self.generate_button, "Create extra image variations in review_dataset.")
        ToolTip(self.open_test_button, "Open the test_images folder.")
        ToolTip(self.refresh_button, "Refresh counts and folder status.")
        ToolTip(self.stop_button, "Stop the current running task.")

        actions.columnconfigure(0, weight=1)
        actions.columnconfigure(1, weight=0)
        actions_left.columnconfigure(0, weight=1)
        actions_left.columnconfigure(1, weight=0)
        actions_right.columnconfigure(0, weight=1)

        folders = ttk.LabelFrame(container, text="Folders", padding=10)
        folders.pack(fill="x", pady=(12, 0))

        self.open_dataset_button = ttk.Button(folders, text="Open dataset", command=lambda: self.open_folder("dataset"))
        self.open_dataset_button.grid(row=0, column=0, padx=(0, 8), pady=4, sticky="ew")

        self.dataset_count_label = ttk.Label(folders, textvariable=self.dataset_count_var, width=12)
        self.dataset_count_label.grid(row=0, column=1, padx=(0, 20), pady=4, sticky="w")

        self.open_debug_button = ttk.Button(
            folders,
            text="Open debug_pictures",
            command=lambda: self.open_folder("debug_output"),
        )
        self.open_debug_button.grid(row=0, column=2, padx=(12, 8), pady=4, sticky="ew")
        self.debug_count_label = ttk.Label(folders, textvariable=self.debug_count_var, width=12)
        self.debug_count_label.grid(row=0, column=3, pady=4, sticky="w")

        self.open_review_button = ttk.Button(
            folders,
            text="Open review_dataset",
            command=lambda: self.open_folder("review_dataset"),
        )
        self.open_review_button.grid(row=1, column=0, padx=(0, 8), pady=4, sticky="ew")

        self.review_count_label = ttk.Label(folders, textvariable=self.review_count_var, width=12)
        self.review_count_label.grid(row=1, column=1, padx=(0, 20), pady=4, sticky="w")

        self.move_debug_button = ttk.Button(
            folders,
            text="Move debug to review",
            command=self.move_debug_to_review,
        )
        self.move_debug_button.grid(row=1, column=2, padx=(12, 8), pady=4, sticky="ew")

        self.clear_debug_button = ttk.Button(
            folders,
            text="Clear debug",
            command=self.clear_debug_output,
        )
        self.clear_debug_button.grid(row=2, column=2, padx=(12, 8), pady=4, sticky="ew")

        self.import_button = ttk.Button(
            folders,
            text="Import reviewed",
            command=self.import_reviewed,
        )
        self.import_button.grid(row=2, column=0, padx=(0, 8), pady=4, sticky="ew")

        self.open_quarantine_button = ttk.Button(
            folders,
            text="Open quarantine",
            command=lambda: self.open_folder("dataset_quarantine"),
        )
        self.open_quarantine_button.grid(row=3, column=2, padx=(12, 8), pady=4, sticky="ew")
        self.quarantine_count_label = ttk.Label(folders, textvariable=self.quarantine_count_var, width=12)
        self.quarantine_count_label.grid(row=3, column=3, pady=4, sticky="w")

        self.clear_reviewed_button = ttk.Button(
            folders,
            text="Clear reviewed",
            command=self.clear_reviewed,
        )
        self.clear_reviewed_button.grid(row=3, column=0, padx=(0, 8), pady=4, sticky="ew")

        ToolTip(self.import_button, "Import reviewed images back into the dataset.")
        ToolTip(self.clear_reviewed_button, "Delete reviewed images after they are handled.")
        ToolTip(self.move_debug_button, "Move character debug images into _needs_review.")
        ToolTip(self.clear_debug_button, "Delete image files from debug_output.")

        for index in range(4):
            folders.columnconfigure(index, weight=1)

        status_frame = ttk.Frame(container)
        status_frame.pack(fill="x", pady=(12, 8))
        ttk.Label(status_frame, text="Status:").pack(side="left")
        ttk.Label(status_frame, textvariable=self.status_var).pack(side="left", padx=(6, 0))

        log_frame = ttk.LabelFrame(container, text="Output", padding=10)
        log_frame.pack(fill="both", expand=True)

        self.log = ScrolledText(log_frame, wrap="word", font=("Consolas", 10))
        self.log.pack(fill="both", expand=True)
        self.log.configure(state="disabled")

        bottom = ttk.Frame(container)
        bottom.pack(fill="x", pady=(8, 0))
        ttk.Button(bottom, text="Clear output", command=self.clear_output).pack(side="left")

    def append_output(self, text: str) -> None:
        self.log.configure(state="normal")
        self.log.insert("end", text)
        self.log.see("end")
        self.log.configure(state="disabled")

    def clear_output(self) -> None:
        self.log.configure(state="normal")
        self.log.delete("1.0", "end")
        self.log.configure(state="disabled")

    def open_folder(self, relative_path: str) -> None:
        path = os.path.join(BASE_DIR, relative_path)
        os.makedirs(path, exist_ok=True)
        os.startfile(path)

        if relative_path in {"dataset", "review_dataset"}:
            self.show_folder_summary(relative_path, path)

    def show_folder_summary(self, relative_path: str, path: str) -> None:
        entries: list[tuple[str, int]] = []

        for name in sorted(os.listdir(path)):
            sub_path = os.path.join(path, name)
            if not os.path.isdir(sub_path):
                continue

            count = sum(
                1 for filename in os.listdir(sub_path)
                if os.path.isfile(os.path.join(sub_path, filename))
            )
            entries.append((name, count))

        title = "Dataset overview" if relative_path == "dataset" else "Review dataset overview"
        highlight_non_empty = relative_path == "review_dataset"
        self.show_info_popup(title, entries, highlight_non_empty)

    def show_info_popup(
        self,
        title: str,
        entries: list[tuple[str, int]],
        highlight_non_empty: bool = False,
    ) -> None:
        popup = tk.Toplevel(self.root)
        popup.title(title)
        popup.transient(self.root)
        popup.resizable(False, False)

        frame = ttk.Frame(popup, padding=12)
        frame.pack(fill="both", expand=True)

        if entries:
            for name, count in entries:
                color = "red" if highlight_non_empty and count > 0 else "black"
                tk.Label(frame, text=f"{name}: {count}", justify="left", fg=color).pack(anchor="w")
        else:
            ttk.Label(frame, text="No subfolders found.", justify="left").pack(anchor="w")

        ttk.Button(frame, text="OK", command=popup.destroy).pack(anchor="e", pady=(12, 0))

        popup.update_idletasks()
        x = self.root.winfo_rootx() + 40
        y = self.root.winfo_rooty() + 40
        popup.geometry(f"+{x}+{y}")

    def count_files_in_subfolders(self, relative_path: str) -> int:
        path = os.path.join(BASE_DIR, relative_path)
        total = 0

        if not os.path.exists(path):
            return total

        for name in os.listdir(path):
            sub_path = os.path.join(path, name)
            if not os.path.isdir(sub_path):
                continue

            total += sum(
                1 for filename in os.listdir(sub_path)
                if os.path.isfile(os.path.join(sub_path, filename))
            )

        return total

    def count_files_in_folder(self, relative_path: str) -> int:
        path = os.path.join(BASE_DIR, relative_path)
        total = 0

        if not os.path.exists(path):
            return total

        for _root, _dirs, files in os.walk(path):
            total += sum(1 for filename in files if is_image_file(filename))

        return total

    def refresh_counts(self) -> None:
        dataset_total = self.count_files_in_subfolders("dataset")
        review_total = self.count_files_in_subfolders("review_dataset")
        debug_total = self.count_files_in_folder("debug_output")
        quarantine_total = self.count_files_in_folder("dataset_quarantine")
        self.dataset_count_var.set(f"Count: {dataset_total}")
        self.review_count_var.set(f"Count: {review_total}")
        self.debug_count_var.set(f"Count: {debug_total}")
        self.quarantine_count_var.set(f"Count: {quarantine_total}")

    def set_running_state(self, running: bool) -> None:
        normal_state = "disabled" if running else "normal"
        stop_state = "normal" if running else "disabled"

        self.train_button.configure(state=normal_state)
        self.run_button.configure(state=normal_state)
        self.curate_button.configure(state=normal_state)
        self.import_button.configure(state=normal_state)
        self.generate_button.configure(state=normal_state)
        self.move_debug_button.configure(state=normal_state)
        self.clear_debug_button.configure(state=normal_state)
        self.clear_reviewed_button.configure(state=normal_state)
        self.stop_button.configure(state=stop_state)

    def run_train(self) -> None:
        self.start_process([PYTHON_EXE, "train.py"], "Training model")

    def run_tests(self) -> None:
        args = [PYTHON_EXE, "batch_solve.py"]
        self.start_process(args, "Running test_images")

    def run_curate(self) -> None:
        self.start_process(
            [PYTHON_EXE, "curate_dataset.py", "--apply", "--check-duplicates"],
            "Checking dataset",
        )

    def import_reviewed(self) -> None:
        self.start_process(
            [PYTHON_EXE, "curate_dataset.py", "--import-reviewed"],
            "Importing reviewed files",
        )

    def clear_reviewed(self) -> None:
        self.start_process(
            [PYTHON_EXE, "curate_dataset.py", "--clear-reviewed"],
            "Clearing reviewed files",
        )

    def generate_variations(self) -> None:
        popup = tk.Toplevel(self.root)
        popup.title("Select classes")
        popup.transient(self.root)
        popup.resizable(False, False)

        frame = ttk.Frame(popup, padding=12)
        frame.pack(fill="both", expand=True)

        ttk.Label(frame, text="Select classes to generate for:").pack(anchor="w")

        checkbox_frame = ttk.Frame(frame)
        checkbox_frame.pack(fill="x", pady=(8, 8))

        variables: dict[str, tk.BooleanVar] = {}
        for index, class_name in enumerate(LABEL_TOKENS):
            variable = tk.BooleanVar(value=True)
            variables[class_name] = variable
            ttk.Checkbutton(
                checkbox_frame,
                text=class_name,
                variable=variable,
            ).grid(row=index // 4, column=index % 4, padx=(0, 12), pady=2, sticky="w")

        def set_all(value: bool) -> None:
            for variable in variables.values():
                variable.set(value)

        def submit() -> None:
            selected_classes = [
                class_name for class_name, variable in variables.items()
                if variable.get()
            ]
            if not selected_classes:
                messagebox.showerror("No selection", "Select at least one class.", parent=popup)
                return

            popup.destroy()
            self.start_process(
                [PYTHON_EXE, "generate_variations.py", "--classes", *selected_classes],
                "Generating variations",
            )

        buttons = ttk.Frame(frame)
        buttons.pack(fill="x")

        ttk.Button(buttons, text="Mark all", command=lambda: set_all(True)).pack(side="left")
        ttk.Button(buttons, text="Unmark all", command=lambda: set_all(False)).pack(side="left", padx=(8, 0))
        ttk.Button(buttons, text="Cancel", command=popup.destroy).pack(side="right")
        ttk.Button(buttons, text="Generate", command=submit).pack(side="right", padx=(0, 8))

        popup.update_idletasks()
        x = self.root.winfo_rootx() + 40
        y = self.root.winfo_rooty() + 40
        popup.geometry(f"+{x}+{y}")
        popup.grab_set()

    def move_debug_to_review(self) -> None:
        debug_dir = os.path.join(BASE_DIR, "debug_output")
        target_dir = os.path.join(BASE_DIR, "review_dataset", UNKNOWN_REVIEW_DIR)

        if not os.path.isdir(debug_dir):
            messagebox.showinfo("Move debug to review", "debug_output was not found.")
            return

        moved = 0

        for filename in sorted(os.listdir(debug_dir)):
            source_path = os.path.join(debug_dir, filename)
            if not os.path.isfile(source_path):
                continue
            if not is_image_file(filename):
                continue
            if "_char_" not in os.path.splitext(filename)[0]:
                continue

            move_file(source_path, target_dir)
            moved += 1

        self.refresh_counts()

        if moved == 0:
            messagebox.showinfo("Move debug to review", "No debug images found to move.")
            return

        messagebox.showinfo(
            "Move debug to review",
            f"Moved {moved} debug image(s) to review_dataset/{UNKNOWN_REVIEW_DIR}.",
        )

    def clear_debug_output(self) -> None:
        debug_dir = os.path.join(BASE_DIR, "debug_output")

        if not os.path.isdir(debug_dir):
            messagebox.showinfo("Clear debug", "debug_output was not found.")
            return

        deleted = 0

        for filename in sorted(os.listdir(debug_dir)):
            file_path = os.path.join(debug_dir, filename)
            if not os.path.isfile(file_path):
                continue

            os.remove(file_path)
            deleted += 1

        self.refresh_counts()

        if deleted == 0:
            messagebox.showinfo("Clear debug", "No files found in debug_output.")
            return

        messagebox.showinfo("Clear debug", f"Deleted {deleted} file(s) from debug_output.")

    def start_process(self, command: list[str], status: str) -> None:
        if self.process is not None:
            return

        self.append_output(f"\n> {' '.join(command)}\n\n")
        self.status_var.set(status)
        self.set_running_state(True)

        self.worker_thread = threading.Thread(
            target=self._run_process,
            args=(command,),
            daemon=True,
        )
        self.worker_thread.start()

    def _run_process(self, command: list[str]) -> None:
        try:
            self.process = subprocess.Popen(
                command,
                cwd=BASE_DIR,
                stdout=subprocess.PIPE,
                stderr=subprocess.STDOUT,
                text=True,
                encoding="utf-8",
                errors="replace",
                bufsize=1,
            )

            assert self.process.stdout is not None
            for line in self.process.stdout:
                self.output_queue.put(line)

            exit_code = self.process.wait()
            self.output_queue.put(f"\nProcess finished with exit code {exit_code}.\n")
        except Exception as exc:
            self.output_queue.put(f"\nFailed to start process: {exc}\n")
        finally:
            self.process = None
            self.output_queue.put("__PROCESS_DONE__")

    def stop_process(self) -> None:
        if self.process is None:
            return

        self.append_output("\nStopping process...\n")
        self.process.terminate()

    def _drain_output(self) -> None:
        process_done = False

        while True:
            try:
                message = self.output_queue.get_nowait()
            except queue.Empty:
                break

            if message == "__PROCESS_DONE__":
                process_done = True
                continue

            self.append_output(message)

        if process_done:
            self.status_var.set("Idle")
            self.set_running_state(False)
            self.refresh_counts()

        self.root.after(100, self._drain_output)


def main() -> None:
    root = tk.Tk()
    style = ttk.Style(root)
    if "vista" in style.theme_names():
        style.theme_use("vista")

    app = CaptchaApp(root)
    root.mainloop()


if __name__ == "__main__":
    main()
