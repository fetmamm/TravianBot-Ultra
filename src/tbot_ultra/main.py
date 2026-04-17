import argparse
import sys
from datetime import datetime
from pathlib import Path

from .browser import BrowserSession
from .config import load_account, load_bot_config
from .travian_client import TravianClient


def login_read_villages(account_name: str | None, keep_open: bool = False) -> None:
    config = load_bot_config()
    account = load_account(account_name)

    print(f"Starting Tbot Ultra for {config.server_name} with account '{account.name}'.")
    browser_session = BrowserSession(config, account)
    with browser_session as page:
        client = TravianClient(page, config, account)
        client.login()
        snapshot = client.read_village_snapshot()
        browser_session.save_state()

        if keep_open:
            print("\nBrowser is kept open for inspection.")
            input("Press Enter here when you want to close the browser...")

    print(f"\nSnapshot: {datetime.now().strftime('%Y-%m-%d %H:%M:%S')}")
    print("Villages:")
    if snapshot.villages:
        for village in snapshot.villages:
            suffix = f" ({village.url})" if village.url else ""
            print(f"- {village.name}{suffix}")
    else:
        print("- No villages found with the current selectors.")

    print("Resources:")
    if snapshot.resources:
        for name, value in snapshot.resources.items():
            print(f"- {name}: {value}")
    else:
        print("- No resources found with the current selectors.")


def read_village_status(account_name: str | None, keep_open: bool = False) -> None:
    config = load_bot_config()
    account = load_account(account_name)

    print(f"Reading village status for {config.server_name} with account '{account.name}'.")
    browser_session = BrowserSession(config, account)
    with browser_session as page:
        client = TravianClient(page, config, account)
        client.login()
        status = client.read_village_status()
        browser_session.save_state()

        if keep_open:
            print("\nBrowser is kept open for inspection.")
            input("Press Enter here when you want to close the browser...")

    print(f"\nVillage status: {datetime.now().strftime('%Y-%m-%d %H:%M:%S')}")
    print(f"Active village: {status.active_village}")

    print("\nVillages:")
    if status.villages:
        for village in status.villages:
            suffix = f" ({village.url})" if village.url else ""
            print(f"- {village.name}{suffix}")
    else:
        print("- No villages found with the current selectors.")

    print("\nResources:")
    if status.resources:
        for name, value in status.resources.items():
            print(f"- {name}: {value}")
    else:
        print("- No resources found with the current selectors.")

    print("\nResource fields:")
    if status.resource_fields:
        for field in status.resource_fields:
            level = field.level if field.level is not None else "?"
            slot = field.slot_id if field.slot_id is not None else "?"
            print(f"- slot {slot}: {field.field_type} level {level}")
    else:
        print("- No resource fields found with the current selectors.")

    print("\nBuildings:")
    if status.buildings:
        for building in status.buildings:
            level = building.level if building.level is not None else "?"
            slot = building.slot_id if building.slot_id is not None else "?"
            print(f"- slot {slot}: {building.name} level {level}")
    else:
        print("- No buildings found with the current selectors.")

    print("\nBuild queue:")
    if status.build_queue:
        for item in status.build_queue:
            suffix = f" ({item.time_left})" if item.time_left else ""
            print(f"- {item.text}{suffix}")
    else:
        print("- No active build queue found.")


def main() -> None:
    parser = argparse.ArgumentParser(description="Tbot Ultra local runner")
    subparsers = parser.add_subparsers(dest="command", required=True)

    read_parser = subparsers.add_parser("login-read-villages", help="Log in and print villages/resources")
    read_parser.add_argument("--account", help="Account name from .env, for example main")
    read_parser.add_argument("--keep-open", action="store_true", help="Keep the browser open until Enter is pressed")

    status_parser = subparsers.add_parser("read-village-status", help="Read villages, resources, fields, buildings and build queue")
    status_parser.add_argument("--account", help="Account name from .env, for example main")
    status_parser.add_argument("--keep-open", action="store_true", help="Keep the browser open until Enter is pressed")

    subparsers.add_parser("ui", help="Open the local Tbot Ultra UI")

    logout_parser = subparsers.add_parser("logout", help="Remove saved browser session for an account")
    logout_parser.add_argument("--account", help="Account name from .env, for example main")

    args = parser.parse_args()

    try:
        if args.command == "login-read-villages":
            login_read_villages(args.account, args.keep_open)
        elif args.command == "read-village-status":
            read_village_status(args.account, args.keep_open)
        elif args.command == "ui":
            from .ui import start_ui

            start_ui()
        elif args.command == "logout":
            account = load_account(args.account)
            state_path = Path("playwright") / ".auth" / f"{account.name}.json"
            if state_path.exists():
                state_path.unlink()
                print(f"Removed saved browser session for account '{account.name}'.")
            else:
                print(f"No saved browser session found for account '{account.name}'.")
    except RuntimeError as exc:
        print(f"Error: {exc}", file=sys.stderr)
        raise SystemExit(1) from exc
