from dataclasses import dataclass
from pathlib import Path

from .config import ROOT_DIR


ENV_PATH = ROOT_DIR / ".env"


@dataclass(frozen=True)
class StoredAccount:
    name: str
    username: str
    password: str


def normalize_account_name(name: str) -> str:
    normalized = "".join(char.lower() if char.isalnum() else "_" for char in name.strip())
    normalized = "_".join(part for part in normalized.split("_") if part)
    if not normalized:
        raise RuntimeError("Account name cannot be empty.")
    return normalized


def load_env_values(path: Path = ENV_PATH) -> dict[str, str]:
    if not path.exists():
        return {}

    values: dict[str, str] = {}
    for raw_line in path.read_text(encoding="utf-8").splitlines():
        line = raw_line.strip()
        if not line or line.startswith("#") or "=" not in line:
            continue

        key, value = line.split("=", 1)
        values[key.strip()] = value.strip().strip('"').strip("'")

    return values


def list_accounts(path: Path = ENV_PATH) -> list[StoredAccount]:
    values = load_env_values(path)
    names = [name.strip() for name in values.get("TBOT_ACCOUNTS", "").split(",") if name.strip()]
    if not names and values.get("TBOT_ACTIVE_ACCOUNT"):
        names = [values["TBOT_ACTIVE_ACCOUNT"]]

    accounts: list[StoredAccount] = []
    for name in names:
        prefix = f"TBOT_{name.upper()}_"
        accounts.append(
            StoredAccount(
                name=name,
                username=values.get(f"{prefix}USERNAME", ""),
                password=values.get(f"{prefix}PASSWORD", ""),
            )
        )

    return accounts


def active_account_name(path: Path = ENV_PATH) -> str:
    return load_env_values(path).get("TBOT_ACTIVE_ACCOUNT", "main")


def save_account(account: StoredAccount, set_active: bool = True, path: Path = ENV_PATH) -> None:
    values = load_env_values(path)
    account_name = normalize_account_name(account.name)
    names = [name.strip() for name in values.get("TBOT_ACCOUNTS", "").split(",") if name.strip()]

    if account_name not in names:
        names.append(account_name)

    values["TBOT_ACCOUNTS"] = ",".join(names)
    if set_active:
        values["TBOT_ACTIVE_ACCOUNT"] = account_name

    prefix = f"TBOT_{account_name.upper()}_"
    values[f"{prefix}USERNAME"] = account.username.strip()
    values[f"{prefix}PASSWORD"] = account.password

    write_env(values, path)


def delete_account(account_name: str, path: Path = ENV_PATH) -> None:
    values = load_env_values(path)
    normalized_name = normalize_account_name(account_name)
    names = [name.strip() for name in values.get("TBOT_ACCOUNTS", "").split(",") if name.strip()]

    if normalized_name not in names:
        raise RuntimeError(f"Account '{normalized_name}' does not exist.")

    names = [name for name in names if name != normalized_name]
    prefix = f"TBOT_{normalized_name.upper()}_"
    values.pop(f"{prefix}USERNAME", None)
    values.pop(f"{prefix}PASSWORD", None)
    values["TBOT_ACCOUNTS"] = ",".join(names)

    if values.get("TBOT_ACTIVE_ACCOUNT") == normalized_name:
        values["TBOT_ACTIVE_ACCOUNT"] = names[0] if names else "main"

    write_env(values, path)


def write_env(values: dict[str, str], path: Path = ENV_PATH) -> None:
    accounts = [name.strip() for name in values.get("TBOT_ACCOUNTS", "").split(",") if name.strip()]
    active = values.get("TBOT_ACTIVE_ACCOUNT", accounts[0] if accounts else "main")

    lines = [
        "# Tbot Ultra local account settings",
        "# Do not commit this file to GitHub.",
        "",
        f"TBOT_ACTIVE_ACCOUNT={active}",
        f"TBOT_ACCOUNTS={','.join(accounts)}",
        "",
    ]

    for name in accounts:
        prefix = f"TBOT_{name.upper()}_"
        lines.append(f"{prefix}USERNAME={values.get(f'{prefix}USERNAME', '')}")
        lines.append(f"{prefix}PASSWORD={values.get(f'{prefix}PASSWORD', '')}")
        lines.append("")

    path.write_text("\n".join(lines), encoding="utf-8")
