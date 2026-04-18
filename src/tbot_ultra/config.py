import json
import os
from dataclasses import dataclass
from pathlib import Path


ROOT_DIR = Path(__file__).resolve().parents[2]


@dataclass(frozen=True)
class BotConfig:
    server_name: str
    base_url: str
    login_path: str
    village_overview_path: str
    headless: bool
    timeout_ms: int
    manual_login_timeout_seconds: int
    loop_interval_seconds: int
    loop_tasks: list[str]
    github_releases_url: str
    human_like_enabled: bool
    human_like_speed: str


@dataclass(frozen=True)
class Account:
    name: str
    username: str
    password: str


def load_dotenv(path: Path | None = None) -> None:
    env_path = path or ROOT_DIR / ".env"
    if not env_path.exists():
        return

    for raw_line in env_path.read_text(encoding="utf-8").splitlines():
        line = raw_line.strip()
        if not line or line.startswith("#") or "=" not in line:
            continue

        key, value = line.split("=", 1)
        key = key.strip()
        value = value.strip().strip('"').strip("'")
        os.environ[key] = value


def load_bot_config(path: Path | None = None) -> BotConfig:
    config_path = path or ROOT_DIR / "config" / "bot.json"
    data = json.loads(config_path.read_text(encoding="utf-8"))
    raw_loop_tasks = data.get("loop_tasks", ["status"])
    loop_tasks = raw_loop_tasks if isinstance(raw_loop_tasks, list) else ["status"]

    return BotConfig(
        server_name=str(data["server_name"]),
        base_url=str(data["base_url"]).rstrip("/"),
        login_path=str(data["login_path"]),
        village_overview_path=str(data["village_overview_path"]),
        headless=bool(data.get("headless", False)),
        timeout_ms=int(data.get("timeout_ms", 15000)),
        manual_login_timeout_seconds=int(data.get("manual_login_timeout_seconds", 180)),
        loop_interval_seconds=int(data.get("loop_interval_seconds", 60)),
        loop_tasks=[str(task) for task in loop_tasks],
        github_releases_url=str(data.get("github_releases_url", "")),
        human_like_enabled=bool(data.get("human_like_enabled", False)),
        human_like_speed=str(data.get("human_like_speed", "medium")),
    )


def save_bot_config(config: BotConfig, path: Path | None = None) -> None:
    config_path = path or ROOT_DIR / "config" / "bot.json"
    data = {
        "server_name": config.server_name,
        "base_url": config.base_url.rstrip("/"),
        "login_path": config.login_path,
        "village_overview_path": config.village_overview_path,
        "headless": config.headless,
        "timeout_ms": config.timeout_ms,
        "manual_login_timeout_seconds": config.manual_login_timeout_seconds,
        "loop_interval_seconds": config.loop_interval_seconds,
        "loop_tasks": config.loop_tasks,
        "github_releases_url": config.github_releases_url,
        "human_like_enabled": config.human_like_enabled,
        "human_like_speed": config.human_like_speed,
    }
    config_path.write_text(json.dumps(data, indent=2), encoding="utf-8")


def load_account(account_name: str | None = None) -> Account:
    load_dotenv()

    selected_name = account_name or os.getenv("TBOT_ACTIVE_ACCOUNT", "main")
    env_prefix = f"TBOT_{selected_name.upper()}_"
    username = os.getenv(f"{env_prefix}USERNAME")
    password = os.getenv(f"{env_prefix}PASSWORD")
    example_values = {"your_username_or_email", "your_password", "ditt_anvandarnamn", "ditt_losenord"}

    if not username or not password:
        raise RuntimeError(
            f"Missing credentials for account '{selected_name}'. "
            f"Add {env_prefix}USERNAME and {env_prefix}PASSWORD to .env."
        )

    if username in example_values or password in example_values:
        raise RuntimeError(
            f"Credentials for account '{selected_name}' still look like example values. "
            "Open .env and add your real username and password."
        )

    return Account(name=selected_name, username=username, password=password)
