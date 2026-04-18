from dataclasses import dataclass
from datetime import datetime, timedelta, timezone
from email.utils import parsedate_to_datetime
import re
import random
import time
from typing import TYPE_CHECKING, Callable
from urllib.parse import urljoin

from .config import Account, BotConfig

if TYPE_CHECKING:
    from playwright.sync_api import Page


@dataclass(frozen=True)
class Village:
    name: str
    url: str | None


@dataclass(frozen=True)
class AccountSnapshot:
    tribe: str
    active_village: str
    village_count: int
    villages: list[Village]
    server_time_utc: datetime | None = None


@dataclass(frozen=True)
class ResourceField:
    slot_id: int | None
    field_type: str
    name: str
    level: int | None
    url: str | None


@dataclass(frozen=True)
class Building:
    slot_id: int | None
    name: str
    level: int | None
    url: str | None
    gid: int | None = None


@dataclass(frozen=True)
class BuildQueueItem:
    text: str
    time_left: str | None


@dataclass(frozen=True)
class ServerBuildChoice:
    gid: int
    name: str
    available: bool
    reason: str


@dataclass(frozen=True)
class VillageStatus:
    active_village: str
    villages: list[Village]
    resources: dict[str, str]
    resource_fields: list[ResourceField]
    buildings: list[Building]
    build_queue: list[BuildQueueItem]
    server_time_utc: datetime | None = None


class ManualVerificationRequired(RuntimeError):
    pass


BUILDING_MAX_LEVELS_BY_GID = {
    10: 20,
    11: 20,
    15: 20,
    16: 20,
    17: 20,
    18: 20,
    19: 20,
    20: 20,
    21: 20,
    22: 20,
    23: 10,
    24: 20,
    25: 20,
    26: 20,
    27: 20,
    28: 20,
    29: 20,
    30: 20,
    31: 20,
    32: 20,
    33: 20,
    34: 20,
    35: 20,
    36: 20,
    37: 20,
    38: 20,
    39: 20,
    41: 20,
    42: 20,
    43: 20,
    44: 20,
}

BUILDING_REQUIREMENTS = {
    17: [("Main Building", 3), ("Warehouse", 1), ("Granary", 1)],
    18: [("Main Building", 1)],
    19: [("Main Building", 3), ("Rally Point", 1)],
    20: [("Academy", 5), ("Blacksmith", 3)],
    21: [("Academy", 10), ("Main Building", 5)],
    22: [("Barracks", 3), ("Main Building", 3)],
    24: [("Academy", 10), ("Main Building", 10)],
    25: [("Main Building", 5)],
    26: [("Embassy", 1), ("Main Building", 5)],
    27: [("Main Building", 10)],
    28: [("Marketplace", 20), ("Stable", 10)],
    31: [("Rally Point", 1)],
    32: [("Rally Point", 1)],
    33: [("Rally Point", 1)],
    34: [("Main Building", 5)],
    37: [("Main Building", 3), ("Rally Point", 1)],
    41: [("Stable", 20)],
    42: [("Rally Point", 1)],
    43: [("Rally Point", 1)],
}


class TravianClient:
    def __init__(
        self,
        page: "Page",
        config: BotConfig,
        account: Account,
        interactive: bool = True,
        browser_visible: bool = True,
        status_callback: Callable[[str], None] | None = None,
        manual_step_callback: Callable[[str], None] | None = None,
    ) -> None:
        self.page = page
        self.config = config
        self.account = account
        self.interactive = interactive
        self.browser_visible = browser_visible
        self.status_callback = status_callback
        self.manual_step_callback = manual_step_callback
        self.server_time_utc: datetime | None = None

    def login(self) -> None:
        self._goto(self.config.village_overview_path)
        self._pause_for_manual_step_if_visible("Manual verification appeared before login.")
        if self._is_logged_in():
            self._notify("Already logged in.")
            return

        self._goto(self.config.login_path)
        self._pause_for_manual_step_if_visible("Manual verification appeared on the login page.")
        if self._is_logged_in():
            self._notify("Already logged in.")
            return

        self._fill_first_available(
            [
                "input[name='name']",
                "input[name='username']",
                "input[name='user']",
                "input[name='login']",
                "input[type='email']",
                "input[type='text']",
            ],
            self.account.username,
        )
        self._fill_first_available(["input[type='password']", "input[name='password']"], self.account.password)

        if self._captcha_or_manual_step_visible():
            self._notify("Captcha or manual login step detected. Complete it in the browser window.")
            if self.manual_step_callback:
                self.manual_step_callback("Captcha/manual step detected during login.")
            if not self.browser_visible:
                raise ManualVerificationRequired(
                    "Captcha/manual verification appeared while running headless. Open the verification browser, solve it, then run the action again."
                )
            if self.interactive:
                input("Press Enter here after you have logged in manually...")
        else:
            self._click_login_button()

        self._wait_until_logged_in_or_manual()

    def logout(self) -> None:
        self._goto(self.config.village_overview_path)
        self._pause_for_manual_step_if_visible("Manual verification appeared before logout.")

        if not self._is_logged_in():
            self._notify("Account is already logged out.")
            return

        logout_selectors = [
            "a[href*='logout']",
            "a[href*='logoff']",
            "a[href*='logout.php']",
            "a:has-text('Logout')",
            "a:has-text('Log out')",
        ]
        for selector in logout_selectors:
            locator = self.page.locator(selector).first
            if locator.count() > 0:
                self._retry(f"click logout selector {selector}", locator.click)
                self._wait_for_load()
                self._wait_until_logged_out()
                self._notify("Logged out from Travian.")
                return

        raise RuntimeError("Could not find a Travian logout link.")

    def read_account_snapshot(self) -> AccountSnapshot:
        self._goto(self.config.village_overview_path)
        self._pause_for_manual_step_if_visible("Manual verification appeared while reading account info.")
        self._ensure_logged_in()

        villages = self._read_villages()
        return AccountSnapshot(
            tribe=self._read_tribe(),
            active_village=self._read_active_village_name(),
            village_count=len(villages),
            villages=villages,
            server_time_utc=self.server_time_utc,
        )

    def read_village_status(self) -> VillageStatus:
        self._goto(self.config.village_overview_path)
        self._pause_for_manual_step_if_visible("Manual verification appeared while opening the village overview.")
        self._ensure_logged_in()

        return self._read_current_village_status()

    def read_all_village_statuses(self) -> list[VillageStatus]:
        self._goto(self.config.village_overview_path)
        self._pause_for_manual_step_if_visible("Manual verification appeared while opening the village overview.")
        self._ensure_logged_in()

        villages = self._read_villages()
        if not villages:
            return [self._read_current_village_status()]

        statuses = []
        for village in villages:
            if village.url:
                self._goto(village.url)
            else:
                self._goto(self.config.village_overview_path)
            self._pause_for_manual_step_if_visible(f"Manual verification appeared while switching to village '{village.name}'.")
            self._ensure_logged_in()
            self._apply_action_delay()
            statuses.append(self._read_current_village_status())
        return statuses

    def switch_to_village(self, village_name: str = "", village_url: str | None = None) -> None:
        if village_url:
            self._goto(village_url)
        elif village_name:
            self._goto(self.config.village_overview_path)
            villages = self._read_villages()
            match = next((village for village in villages if village.name == village_name and village.url), None)
            if not match:
                raise RuntimeError(f"Could not find village '{village_name}' in the village list.")
            self._goto(match.url or self.config.village_overview_path)
        else:
            return

        self._pause_for_manual_step_if_visible(f"Manual verification appeared while switching to village '{village_name}'.")
        self._ensure_logged_in()

    def _read_current_village_status(self) -> VillageStatus:
        self._pause_for_manual_step_if_visible("Manual verification appeared before reading village status.")
        return VillageStatus(
            active_village=self._read_active_village_name(),
            villages=self._read_villages(),
            resources=self._read_resources(),
            resource_fields=self._read_resource_fields(),
            buildings=self._read_buildings(),
            build_queue=self._read_build_queue(),
            server_time_utc=self.server_time_utc,
        )

    def upgrade_resource_to_level(self, slot_id: int, target_level: int) -> str:
        if slot_id < 1 or slot_id > 18:
            raise RuntimeError(f"Resource slot {slot_id} is outside the resource field range.")
        if target_level < 0:
            raise RuntimeError("Target level must be 0 or higher.")

        upgrades = 0
        while True:
            status = self.read_village_status()
            field = next((item for item in status.resource_fields if item.slot_id == slot_id), None)
            current_level = field.level if field else None
            if current_level is None:
                raise RuntimeError(f"Could not read level for resource slot {slot_id}.")
            if current_level >= target_level:
                return f"Resource slot {slot_id} is level {current_level}. Target {target_level} reached after {upgrades} upgrades."

            clicked = self._upgrade_slot_once(slot_id)
            if not clicked:
                return f"Resource slot {slot_id} stopped at level {current_level}. Upgrade button was not available."
            upgrades += 1

    def upgrade_resource_to_max(self, slot_id: int, max_attempts: int = 30) -> str:
        if slot_id < 1 or slot_id > 18:
            raise RuntimeError(f"Resource slot {slot_id} is outside the resource field range.")

        upgrades = 0
        last_level: int | None = None
        for _ in range(max_attempts):
            status = self.read_village_status()
            field = next((item for item in status.resource_fields if item.slot_id == slot_id), None)
            current_level = field.level if field else None
            if current_level is None:
                raise RuntimeError(f"Could not read level for resource slot {slot_id}.")
            if last_level is not None and current_level <= last_level and upgrades > 0:
                return f"Resource slot {slot_id} stopped at level {current_level}. Level did not increase."

            last_level = current_level
            clicked = self._upgrade_slot_once(slot_id)
            if not clicked:
                return f"Resource slot {slot_id} stopped at level {current_level}. Upgrade button was not available."
            upgrades += 1

        return f"Resource slot {slot_id} reached max attempt limit after {upgrades} upgrades."

    def upgrade_all_resources_to_level(self, target_level: int) -> str:
        if target_level < 0:
            raise RuntimeError("Target level must be 0 or higher.")

        upgrades = 0
        while True:
            status = self.read_village_status()
            candidates = [
                field
                for field in status.resource_fields
                if field.slot_id is not None and field.level is not None and field.level < target_level
            ]
            if not candidates:
                return f"All readable resource fields have reached level {target_level}. Upgrades made: {upgrades}."

            candidates.sort(key=lambda field: (field.level or 0, field.slot_id or 999))
            next_field = candidates[0]
            clicked = self._upgrade_slot_once(next_field.slot_id or 0)
            if not clicked:
                return (
                    f"Stopped after {upgrades} upgrades. Slot {next_field.slot_id} "
                    f"at level {next_field.level} could not be upgraded now."
                )
            upgrades += 1

    def upgrade_building_to_level(self, slot_id: int, target_level: int) -> str:
        if slot_id < 19:
            raise RuntimeError(f"Building slot {slot_id} is outside the building range.")
        if target_level < 0:
            raise RuntimeError("Target level must be 0 or higher.")

        upgrades = 0
        while True:
            status = self.read_village_status()
            building = next((item for item in status.buildings if item.slot_id == slot_id), None)
            current_level = building.level if building else None
            if current_level is None:
                raise RuntimeError(f"Could not read level for building slot {slot_id}.")

            max_level = self._max_level_for_building(building)
            if target_level > max_level:
                raise RuntimeError(f"{building.name} can only be upgraded to level {max_level}. Requested level {target_level}.")
            if current_level >= target_level:
                return f"Building slot {slot_id} is level {current_level}. Target {target_level} reached after {upgrades} upgrades."
            self._ensure_building_requirements_met(status, building.gid, building.name)
            clicked = self._upgrade_slot_once(slot_id)
            if not clicked:
                return f"Building slot {slot_id} stopped at level {current_level}. Upgrade button was not available."
            upgrades += 1

    def upgrade_building_to_max(self, slot_id: int, max_attempts: int = 30) -> str:
        if slot_id < 19:
            raise RuntimeError(f"Building slot {slot_id} is outside the building range.")
        upgrades = 0
        for _ in range(max_attempts):
            status = self.read_village_status()
            building = next((item for item in status.buildings if item.slot_id == slot_id), None)
            if building and building.level is not None:
                max_level = self._max_level_for_building(building)
                if building.level >= max_level:
                    return f"Building slot {slot_id} is already at max level {max_level}. Upgrades made: {upgrades}."
                self._ensure_building_requirements_met(status, building.gid, building.name)
            clicked = self._upgrade_slot_once(slot_id)
            if not clicked:
                return f"Building slot {slot_id} stopped after {upgrades} upgrades. Upgrade button was not available."
            upgrades += 1
        return f"Building slot {slot_id} reached max attempt limit after {upgrades} upgrades."

    def construct_building(self, slot_id: int, gid: int, name: str) -> str:
        if slot_id < 19:
            raise RuntimeError(f"Building slot {slot_id} is outside the building range.")
        if gid <= 0:
            raise RuntimeError("Building gid must be positive.")

        status = self.read_village_status()
        self._ensure_building_can_be_constructed(status, gid, name)

        self._goto(f"/build.php?id={slot_id}")
        self._pause_for_manual_step_if_visible("Manual verification appeared while opening the building slot.")
        self._ensure_logged_in()
        self._apply_action_delay()
        self._ensure_server_allows_construction(slot_id, gid, name)

        clicked = self._retry_truthy("click construct building", lambda: self.page.evaluate(
            """
            ({ gid }) => {
              const gidText = String(gid);
              const candidates = Array.from(document.querySelectorAll('a, button, input[type="submit"]'));
              for (const element of candidates) {
                const href = element.getAttribute('href') || '';
                const value = element.getAttribute('value') || '';
                const title = element.getAttribute('title') || '';
                const text = `${element.textContent || ''} ${value} ${title}`.toLowerCase();
                const classes = (element.className || '').toString().toLowerCase();
                const disabled = element.disabled || classes.includes('disabled') || element.getAttribute('aria-disabled') === 'true';
                const isGold = classes.includes('gold') || text.includes('npc') || text.includes('instant');
                const gidMatches = href.includes(`gid=${gidText}`) || href.includes(`gid%3D${gidText}`) || classes.includes(`gid${gidText}`);
                const looksBuildable = text.includes('build') || text.includes('construct') || classes.includes('green');
                if (!disabled && !isGold && gidMatches && looksBuildable) {
                  element.click();
                  return true;
                }
              }
              return false;
            }
            """,
            {"gid": gid},
        ))
        if not clicked:
            return f"{name} could not be built in slot {slot_id}. Requirements, resources, or queue may block it."

        self._wait_for_load()
        self._pause_for_manual_step_if_visible("Manual verification appeared after starting construction.")
        self._apply_action_delay()
        return f"Started construction of {name} in slot {slot_id}."

    def read_available_buildings_for_slot(self, slot_id: int) -> list[ServerBuildChoice]:
        if slot_id < 19:
            raise RuntimeError(f"Building slot {slot_id} is outside the building range.")

        self._goto(f"/build.php?id={slot_id}")
        self._pause_for_manual_step_if_visible("Manual verification appeared while reading build choices.")
        self._ensure_logged_in()
        return self._read_server_build_choices_on_current_page()

    def _goto(self, path: str) -> None:
        response = self._retry(f"navigate to {path}", lambda: self.page.goto(path, wait_until="domcontentloaded"))
        if response:
            self._record_server_time(response.headers.get("date"))
        self._pause_for_manual_step_if_visible("Manual verification appeared after navigation.")

    def _wait_for_load(self) -> None:
        self._retry("wait for page load", lambda: self.page.wait_for_load_state("domcontentloaded"))

    def _retry(self, label: str, action, attempts: int = 3):
        last_error: Exception | None = None
        for attempt in range(1, attempts + 1):
            try:
                return action()
            except ManualVerificationRequired:
                raise
            except Exception as exc:
                last_error = exc
                if attempt >= attempts:
                    break
                self._notify(f"{label} failed on attempt {attempt}/{attempts}. Retrying...")
                self.page.wait_for_timeout(400 * attempt)
                self._pause_for_manual_step_if_visible(f"Manual verification appeared while retrying {label}.")
        raise RuntimeError(f"{label} failed after {attempts} attempts: {last_error}") from last_error

    def _retry_truthy(self, label: str, action, attempts: int = 3) -> bool:
        for attempt in range(1, attempts + 1):
            result = self._retry(label, action, attempts=1)
            if result:
                return True
            if attempt < attempts:
                self._notify(f"{label} was not available on attempt {attempt}/{attempts}. Retrying...")
                self.page.wait_for_timeout(400 * attempt)
                self._pause_for_manual_step_if_visible(f"Manual verification appeared while retrying {label}.")
        return False

    def _apply_action_delay(self) -> None:
        if not self.config.human_like_enabled:
            return

        ranges = {
            "slow": (2.5, 5.0),
            "medium": (1.0, 2.5),
            "fast": (0.3, 1.0),
        }
        low, high = ranges.get(self.config.human_like_speed, ranges["medium"])
        self.page.wait_for_timeout(int(random.uniform(low, high) * 1000))

    def wait_between_actions(self) -> None:
        self._apply_action_delay()

    def _upgrade_slot_once(self, slot_id: int) -> bool:
        self._goto(f"/build.php?id={slot_id}")
        self._pause_for_manual_step_if_visible("Manual verification appeared while opening the upgrade page.")
        self._ensure_logged_in()
        self._apply_action_delay()

        clicked = self._retry_truthy("click upgrade button", lambda: self.page.evaluate(
            """
            () => {
              const labels = ['upgrade', 'build', 'construct'];
              const candidates = Array.from(document.querySelectorAll('button, input[type="submit"], a'));
              for (const element of candidates) {
                const text = `${element.textContent || ''} ${element.getAttribute('value') || ''} ${element.getAttribute('title') || ''}`.toLowerCase();
                const classes = (element.className || '').toString().toLowerCase();
                const disabled = element.disabled || classes.includes('disabled') || element.getAttribute('aria-disabled') === 'true';
                const isGold = classes.includes('gold') || text.includes('gold') || text.includes('npc') || text.includes('instant');
                const looksLikeUpgrade = labels.some((label) => text.includes(label)) || classes.includes('green');
                if (!disabled && !isGold && looksLikeUpgrade) {
                  element.click();
                  return true;
                }
              }
              return false;
            }
            """
        ))
        if not clicked:
            return False

        self._wait_for_load()
        self._pause_for_manual_step_if_visible("Manual verification appeared after clicking upgrade.")
        self._apply_action_delay()
        return True

    def _max_level_for_building(self, building: Building) -> int:
        if building.gid and building.gid in BUILDING_MAX_LEVELS_BY_GID:
            return BUILDING_MAX_LEVELS_BY_GID[building.gid]
        if "cranny" in building.name.lower():
            return 10
        return 20

    def _ensure_building_requirements_met(self, status: VillageStatus, gid: int | None, name: str) -> None:
        if gid is None:
            return

        missing = self._missing_building_requirements(status, gid)
        if missing:
            requirements = ", ".join(f"{req_name} level {level}" for req_name, level in missing)
            raise RuntimeError(f"{name} cannot be upgraded yet. Missing requirements: {requirements}.")

    def _ensure_building_can_be_constructed(self, status: VillageStatus, gid: int, name: str) -> None:
        existing = [
            building for building in status.buildings
            if building.gid == gid or self._same_building_name(building.name, name)
        ]
        duplicate_allowed = gid in {10, 11, 23, 38, 39}
        wall_gid = gid in {31, 32, 33, 42, 43}
        if existing and not duplicate_allowed and not wall_gid:
            raise RuntimeError(f"{name} already exists in this village.")

        missing = self._missing_building_requirements(status, gid)
        if missing:
            requirements = ", ".join(f"{req_name} level {level}" for req_name, level in missing)
            raise RuntimeError(f"{name} cannot be built yet. Missing requirements: {requirements}.")

    def _ensure_server_allows_construction(self, slot_id: int, gid: int, name: str) -> None:
        choices = self._read_server_build_choices_on_current_page()
        if not choices:
            return

        match = next((choice for choice in choices if choice.gid == gid), None)
        if not match:
            raise RuntimeError(f"{name} is not listed by the server for slot {slot_id}.")
        if not match.available:
            reason = f" Server reason: {match.reason}" if match.reason else ""
            raise RuntimeError(f"{name} cannot be built in slot {slot_id} right now.{reason}")

    def _read_server_build_choices_on_current_page(self) -> list[ServerBuildChoice]:
        raw_choices = self.page.evaluate(
            """
            () => {
              const parseGid = (element) => {
                const text = [
                  element.getAttribute('href') || '',
                  element.getAttribute('onclick') || '',
                  element.getAttribute('class') || '',
                  element.getAttribute('data-gid') || '',
                  element.textContent || ''
                ].join(' ');
                const match = text.match(/(?:gid=|gid%3D|gid\\s*)(\\d+)/i) || text.match(/(?:^|\\s)gid(\\d+)(?:\\s|$)/i);
                return match ? Number(match[1]) : null;
              };

              const clean = (value) => (value || '').replace(/\\s+/g, ' ').trim();
              const rows = Array.from(document.querySelectorAll(
                '.contract, .buildingWrapper, .build_details, .buildingList li, table tr, div'
              ));
              const seen = new Set();
              const choices = [];

              for (const row of rows) {
                const gid = parseGid(row);
                if (!gid || seen.has(gid)) continue;
                seen.add(gid);

                const button = row.querySelector('button, input[type="submit"], a[href*="gid"]') || row;
                const classes = clean(`${row.className || ''} ${button.className || ''}`).toLowerCase();
                const text = clean(row.textContent || '');
                const lowerText = text.toLowerCase();
                const disabled = button.disabled || classes.includes('disabled') || lowerText.includes('not enough')
                  || lowerText.includes('requirements') || lowerText.includes('missing') || lowerText.includes('cannot');
                const isGold = classes.includes('gold') || lowerText.includes('npc') || lowerText.includes('instant');
                const available = !disabled && !isGold && (
                  classes.includes('green') || lowerText.includes('build') || lowerText.includes('construct')
                );
                const heading = row.querySelector('h2, h3, .title, .name, img[alt]');
                const name = clean(heading ? (heading.getAttribute('alt') || heading.textContent) : text.split('\\n')[0]);
                choices.push({
                  gid,
                  name: name || `gid ${gid}`,
                  available,
                  reason: available ? 'Server says available' : text
                });
              }

              return choices;
            }
            """
        )
        return [
            ServerBuildChoice(
                gid=int(item.get("gid")),
                name=str(item.get("name") or f"gid {item.get('gid')}"),
                available=bool(item.get("available")),
                reason=str(item.get("reason") or ""),
            )
            for item in raw_choices
            if item.get("gid")
        ]

    def _missing_building_requirements(self, status: VillageStatus, gid: int) -> list[tuple[str, int]]:
        missing = []
        for required_name, required_level in BUILDING_REQUIREMENTS.get(gid, []):
            current = self._building_level_by_name(status, required_name)
            if current < required_level:
                missing.append((required_name, required_level))
        return missing

    def _building_level_by_name(self, status: VillageStatus, name: str) -> int:
        matches = [
            building.level or 0
            for building in status.buildings
            if self._same_building_name(building.name, name)
        ]
        return max(matches) if matches else 0

    def _same_building_name(self, left: str, right: str) -> bool:
        return self._normalize_building_name(left) == self._normalize_building_name(right)

    def _normalize_building_name(self, name: str) -> str:
        cleaned = re.sub(r"\s+", " ", name).strip().lower()
        aliases = {
            "granary / silo": "granary",
            "silo": "granary",
            "city wall": "wall",
            "earth wall": "wall",
            "palisade": "wall",
            "stone wall": "wall",
            "makeshift wall": "wall",
        }
        return aliases.get(cleaned, cleaned)

    def _record_server_time(self, date_header: str | None) -> None:
        if not date_header:
            return

        try:
            parsed = parsedate_to_datetime(date_header)
        except (TypeError, ValueError):
            return

        if parsed.tzinfo is None:
            return

        self.server_time_utc = parsed.astimezone(timezone.utc)

    def read_visible_server_time(self) -> str | None:
        return self.page.evaluate(
            """
            () => {
              const selectors = [
                '#servertime',
                '#serverTime',
                '#server-time',
                '#serverClock',
                '#stime',
                '#clock',
                '.servertime',
                '.serverTime',
                '.server-time',
                '.serverClock',
                '.stime',
                '.clock',
                '#ltime',
                '.ltime',
                '#time',
                '.time'
              ];

              for (const selector of selectors) {
                const element = document.querySelector(selector);
                const text = element ? (element.textContent || '').replace(/\\s+/g, ' ').trim() : '';
                if (text && /\\d{1,2}:\\d{2}/.test(text)) return text;
              }

              const topLeftElements = Array.from(document.querySelectorAll('body *'))
                .filter((element) => {
                  const rect = element.getBoundingClientRect();
                  return rect.top >= 0 && rect.top < 120 && rect.left >= 0 && rect.left < 320;
                });
              for (const element of topLeftElements) {
                const text = (element.textContent || '').replace(/\\s+/g, ' ').trim();
                const match = text.match(/\\b\\d{1,2}:\\d{2}(?::\\d{2})?\\b/);
                if (match) return match[0];
              }

              const topArea = document.querySelector('#top, #header, #navigation, body');
              const text = topArea ? (topArea.textContent || '').replace(/\\s+/g, ' ').trim() : '';
              const match = text.match(/\\b\\d{1,2}:\\d{2}(?::\\d{2})?\\b/);
              return match ? match[0] : null;
            }
            """
        )

    def read_visible_server_timestamp(self) -> int | None:
        timestamp = self.page.evaluate(
            """
            () => {
              const timers = Array.from(document.querySelectorAll('span.timer[counting="up"][value]'));
              const readTimestamp = (element) => {
                const value = Number(element.getAttribute('value'));
                return Number.isFinite(value) && value > 1000000000 ? Math.floor(value) : null;
              };

              const topLeftTimer = timers.find((element) => {
                const rect = element.getBoundingClientRect();
                return rect.top >= 0 && rect.top < 120 && rect.left >= 0 && rect.left < 320;
              });
              if (topLeftTimer) return readTimestamp(topLeftTimer);

              for (const timer of timers) {
                const value = readTimestamp(timer);
                if (value) return value;
              }

              return null;
            }
            """
        )
        return int(timestamp) if timestamp else None

    def read_visible_server_timer(self) -> dict | None:
        return self.page.evaluate(
            """
            () => {
              const timers = Array.from(document.querySelectorAll('span.timer[counting="up"][value]'));
              const readTimer = (element) => {
                const value = Number(element.getAttribute('value'));
                if (!Number.isFinite(value) || value <= 1000000000) return null;

                const text = (element.textContent || '').replace(/\\s+/g, ' ').trim();
                if (!/\\d{1,2}:\\d{2}/.test(text)) return null;
                return { timestamp: Math.floor(value), text };
              };

              const topLeftTimer = timers.find((element) => {
                const rect = element.getBoundingClientRect();
                return rect.top >= 0 && rect.top < 120 && rect.left >= 0 && rect.left < 320;
              });
              if (topLeftTimer) return readTimer(topLeftTimer);

              for (const timer of timers) {
                const value = readTimer(timer);
                if (value) return value;
              }

              return null;
            }
            """
        )

    def read_visible_server_datetime(self) -> datetime | None:
        timer = self.read_visible_server_timer()
        if timer:
            match = re.search(r"\b(\d{1,2}):(\d{2})(?::(\d{2}))?\b", timer["text"])
            if match:
                hour = int(match.group(1))
                minute = int(match.group(2))
                second = int(match.group(3) or 0)
                base_utc = datetime.fromtimestamp(int(timer["timestamp"]), timezone.utc)
                visible_seconds = hour * 3600 + minute * 60 + second
                utc_seconds = base_utc.hour * 3600 + base_utc.minute * 60 + base_utc.second
                offset_seconds = visible_seconds - utc_seconds
                while offset_seconds < -43200:
                    offset_seconds += 86400
                while offset_seconds > 43200:
                    offset_seconds -= 86400

                rounded_offset = round(offset_seconds / 900) * 900
                server_date = (base_utc + timedelta(seconds=rounded_offset)).date()
                return datetime(server_date.year, server_date.month, server_date.day, hour, minute, second)

        timestamp = self.read_visible_server_timestamp()
        if timestamp:
            return datetime.fromtimestamp(timestamp, timezone.utc).replace(tzinfo=None)

        raw_time = self.read_visible_server_time()
        if not raw_time:
            return None

        match = re.search(r"\b(\d{1,2}):(\d{2})(?::(\d{2}))?\b", raw_time)
        if not match:
            return None

        now = datetime.now().astimezone()
        hour = int(match.group(1))
        minute = int(match.group(2))
        second = int(match.group(3)) if match.group(3) else now.second
        try:
            return now.replace(hour=hour, minute=minute, second=second, microsecond=0)
        except ValueError:
            return None

    def _ensure_logged_in(self) -> None:
        if not self._is_logged_in():
            raise RuntimeError(f"Not logged in. Current page state is '{self._login_state()}'. The session may have expired or login failed.")

    def is_logged_in(self) -> bool:
        return self._is_logged_in()

    def _is_logged_in(self) -> bool:
        return self._login_state() == "logged_in"

    def _login_state(self) -> str:
        if self._captcha_or_manual_step_visible():
            return "manual_step"

        current_url = self.page.url.lower()
        if "login.php" in current_url:
            return "logged_out"

        logged_in_selectors = [
            "a[href*='logout']",
            "a[href*='dorf1.php']",
            "a[href*='dorf2.php']",
            "#sidebarBoxVillagelist",
            ".villageList",
            "#villageList",
            "#resourceFieldContainer",
            "#village_map",
        ]
        if any(self.page.locator(selector).count() > 0 for selector in logged_in_selectors):
            return "logged_in"

        logged_out_selectors = [
            "input[type='password']",
            "input[name='password']",
            "button[type='submit']",
            "input[type='submit']",
            "a[href*='login']",
        ]
        if any(self.page.locator(selector).count() > 0 for selector in logged_out_selectors):
            return "logged_out"

        return "unknown"

    def _fill_first_available(self, selectors: list[str], value: str) -> None:
        for selector in selectors:
            locator = self.page.locator(selector).first
            if locator.count() > 0:
                self._retry(f"fill {selector}", lambda: locator.fill(value))
                return

        raise RuntimeError(f"Could not find any input matching: {', '.join(selectors)}")

    def _click_login_button(self) -> None:
        selectors = [
            "button[type='submit']",
            "input[type='submit']",
            "button:has-text('Login')",
            "input[value='Login']",
        ]
        for selector in selectors:
            locator = self.page.locator(selector).first
            if locator.count() > 0:
                self._retry(f"click login selector {selector}", locator.click)
                return

        raise RuntimeError("Could not find the login button.")

    def _captcha_or_manual_step_visible(self) -> bool:
        selectors = [
            "input[name*='captcha' i]",
            "input[id*='captcha' i]",
            "input[placeholder*='captcha' i]",
            "img[src*='captcha' i]",
            "iframe[src*='captcha' i]",
            "iframe[src*='recaptcha' i]",
            ".g-recaptcha",
            "[class*='captcha' i]",
            "[id*='captcha' i]",
            "text=/captcha/i",
            "text=/recaptcha/i",
            "text=/verification/i",
            "text=/verify/i",
        ]
        return any(self.page.locator(selector).count() > 0 for selector in selectors)

    def _wait_until_logged_in_or_manual(self) -> None:
        deadline = time.monotonic() + self.config.manual_login_timeout_seconds
        manual_message_shown = False

        while time.monotonic() < deadline:
            if self._is_logged_in():
                return

            if self._captcha_or_manual_step_visible() and not manual_message_shown:
                self._notify("Captcha/manual step detected. Solve it in the browser window, then wait here.")
                if self.manual_step_callback:
                    self.manual_step_callback("Captcha/manual step detected during login.")
                if not self.browser_visible:
                    raise ManualVerificationRequired(
                        "Captcha/manual verification appeared while running headless. Open the verification browser, solve it, then run the action again."
                    )
                manual_message_shown = True

            self.page.wait_for_timeout(500)

        if not self.interactive:
            raise RuntimeError(
                "Login was not confirmed before timeout. If captcha is visible, solve it in the browser and try again."
            )

        self._notify("Login is not confirmed yet. Finish login/captcha in the browser if needed.")
        input("Press Enter here after the village overview is visible...")

        self._ensure_logged_in()

    def _wait_until_logged_out(self) -> None:
        deadline = time.monotonic() + 10
        while time.monotonic() < deadline:
            state = self._login_state()
            if state in {"logged_out", "unknown"}:
                return
            if state == "manual_step":
                self._pause_for_manual_step_if_visible("Manual verification appeared while logging out.")
            self.page.wait_for_timeout(300)

        if self._is_logged_in():
            raise RuntimeError("Logout click completed, but the session still looks logged in.")

    def _pause_for_manual_step_if_visible(self, message: str) -> None:
        if not self._captcha_or_manual_step_visible():
            return

        self._notify(f"{message} Solve it in the browser window. The bot is paused.")
        if self.manual_step_callback:
            self.manual_step_callback(message)

        if not self.browser_visible:
            raise ManualVerificationRequired(
                "Captcha/manual verification appeared while running headless. Open the verification browser, solve it, then run the action again."
            )

        if self.interactive:
            input("Press Enter here after the manual step is solved...")

        deadline = time.monotonic() + self.config.manual_login_timeout_seconds
        while time.monotonic() < deadline:
            if not self._captcha_or_manual_step_visible():
                self._notify("Manual verification cleared. Continuing.")
                return
            self.page.wait_for_timeout(500)

        raise RuntimeError(
            "Manual verification was still visible after the timeout. Solve it in the browser and start the action again."
        )

    def _notify(self, message: str) -> None:
        print(message)
        if self.status_callback:
            self.status_callback(message)

    def _read_villages(self) -> list[Village]:
        self._pause_for_manual_step_if_visible("Manual verification appeared while reading villages.")
        raw_villages = self.page.evaluate(
            """
            () => {
              const selectors = [
                '#sidebarBoxVillagelist a[href*="newdid"]',
                '#villageList a[href*="newdid"]',
                '.villageList a[href*="newdid"]',
                'a[href*="newdid"]'
              ];
              const seen = new Set();
              const villages = [];

              for (const selector of selectors) {
                for (const element of document.querySelectorAll(selector)) {
                  const name = (element.textContent || '').replace(/\\s+/g, ' ').trim();
                  const href = element.getAttribute('href');
                  const key = `${name}|${href}`;
                  if (!name || seen.has(key)) continue;
                  seen.add(key);
                  villages.push({ name, href });
                }
                if (villages.length) return villages;
              }

              const heading = document.querySelector('h1, .titleInHeader, #content h2');
              const fallbackName = heading ? heading.textContent.replace(/\\s+/g, ' ').trim() : '';
              return fallbackName ? [{ name: fallbackName, href: null }] : [];
            }
            """
        )

        return [
            Village(
                name=item["name"],
                url=urljoin(self.config.base_url, item["href"]) if item.get("href") else None,
            )
            for item in raw_villages
        ]

    def _read_resources(self) -> dict[str, str]:
        self._pause_for_manual_step_if_visible("Manual verification appeared while reading resources.")
        return self.page.evaluate(
            """
            () => {
              const ids = {
                wood: ['#l1', '#stockBarResource1'],
                clay: ['#l2', '#stockBarResource2'],
                iron: ['#l3', '#stockBarResource3'],
                crop: ['#l4', '#stockBarResource4']
              };
              const resources = {};

              for (const [name, selectors] of Object.entries(ids)) {
                for (const selector of selectors) {
                  const element = document.querySelector(selector);
                  if (!element) continue;
                  const value = (element.textContent || '').replace(/\\s+/g, '').trim();
                  if (value) {
                    resources[name] = value;
                    break;
                  }
                }
              }

              return resources;
            }
            """
        )

    def _read_active_village_name(self) -> str:
        self._pause_for_manual_step_if_visible("Manual verification appeared while reading the active village.")
        return self.page.evaluate(
            """
            () => {
              const selectors = [
                '.villageList .active',
                '#villageList .active',
                '#sidebarBoxVillagelist .active',
                '.villageNameField',
                'h1',
                '.titleInHeader'
              ];

              for (const selector of selectors) {
                const element = document.querySelector(selector);
                const text = element ? (element.textContent || '').replace(/\\s+/g, ' ').trim() : '';
                if (text) return text;
              }

              return 'Unknown village';
            }
            """
        )

    def _read_tribe(self) -> str:
        self._pause_for_manual_step_if_visible("Manual verification appeared while reading tribe.")
        return self.page.evaluate(
            """
            () => {
              const tribeNames = {
                1: 'Romans',
                2: 'Teutons',
                3: 'Gauls',
                4: 'Nature',
                5: 'Natars',
                6: 'Egyptians',
                7: 'Huns',
                8: 'Spartans'
              };

              const selectors = [
                'img.nationBig[alt]',
                'img[src*="/tribes/"][alt]',
                '[class*="tribe" i]',
                '[id*="tribe" i]',
                '.playerInfo',
                '#sidebarBoxActiveVillage',
                '#sidebarBoxVillagelist',
                'body'
              ];

              for (const selector of selectors) {
                const element = document.querySelector(selector);
                if (!element) continue;
                const directAlt = element.getAttribute('alt');
                if (directAlt && directAlt.trim()) return directAlt.trim();
                const text = `${element.className || ''} ${element.getAttribute('title') || ''} ${element.textContent || ''}`.toLowerCase();
                if (text.includes('roman')) return 'Romans';
                if (text.includes('teuton')) return 'Teutons';
                if (text.includes('gaul')) return 'Gauls';
                if (text.includes('egypt')) return 'Egyptians';
                if (text.includes('hun')) return 'Huns';
                if (text.includes('spartan')) return 'Spartans';

                const tribeMatch = text.match(/tribe[^0-9]*(\\d+)/i) || text.match(/tribe(\\d+)/i);
                if (tribeMatch && tribeNames[Number(tribeMatch[1])]) return tribeNames[Number(tribeMatch[1])];
              }

              return 'Unknown';
            }
            """
        )

    def _read_resource_fields(self) -> list[ResourceField]:
        self._pause_for_manual_step_if_visible("Manual verification appeared while reading resource fields.")
        raw_fields = self.page.evaluate(
            """
            () => {
              const fieldTypes = {
                1: 'wood',
                2: 'clay',
                3: 'iron',
                4: 'crop'
              };
              const fieldNames = {
                wood: 'Woodcutter',
                clay: 'Clay pit',
                iron: 'Iron mine',
                crop: 'Cropland',
                unknown: 'Unknown field'
              };
              const slotFallbackTypes = {
                1: 'wood', 2: 'clay', 3: 'iron', 4: 'crop', 5: 'wood', 6: 'clay',
                7: 'iron', 8: 'crop', 9: 'crop', 10: 'wood', 11: 'iron', 12: 'crop',
                13: 'crop', 14: 'iron', 15: 'clay', 16: 'wood', 17: 'crop', 18: 'clay'
              };

              const parseSlotId = (href) => {
                if (!href) return null;
                const match = href.match(/[?&]id=(\\d+)/);
                return match ? Number(match[1]) : null;
              };

              const directText = (element) => {
                const parts = [
                  element.getAttribute('title') || '',
                  element.getAttribute('alt') || '',
                  element.getAttribute('aria-label') || '',
                  element.getAttribute('data-name') || '',
                  element.getAttribute('data-level') || '',
                  element.getAttribute('data-gid') || '',
                  element.getAttribute('data-aid') || '',
                  element.id || '',
                  element.className || '',
                  element.textContent || ''
                ];
                return parts.join(' ').replace(/\\s+/g, ' ').trim();
              };

              const localText = (element) => {
                const parts = [directText(element)];
                for (const child of element.querySelectorAll('img, span, div, area')) {
                  parts.push(directText(child));
                }
                return parts.join(' ').replace(/\\s+/g, ' ').trim();
              };

              const resourceLevelOverlays = Array.from(document.querySelectorAll('#village_map .level'))
                .filter((element) => /(?:^|\\s)gid\\d+(?:\\s|$)/i.test(element.className || ''))
                .slice(0, 18);

              const overlayText = (slotId) => {
                const overlay = resourceLevelOverlays[slotId - 1];
                return overlay ? directText(overlay) : '';
              };

              const collectText = (element) => {
                const parts = [];
                let current = element;
                for (let depth = 0; current && depth < 3; depth += 1) {
                  parts.push(current.getAttribute('title') || '');
                  parts.push(current.getAttribute('alt') || '');
                  parts.push(current.getAttribute('aria-label') || '');
                  parts.push(current.getAttribute('data-name') || '');
                  parts.push(current.getAttribute('data-level') || '');
                  parts.push(current.getAttribute('data-gid') || '');
                  parts.push(current.getAttribute('data-aid') || '');
                  parts.push(current.id || '');
                  parts.push(current.className || '');
                  parts.push(current.textContent || '');
                  for (const child of current.querySelectorAll('img, span, div, area')) {
                    parts.push(child.getAttribute('title') || '');
                    parts.push(child.getAttribute('alt') || '');
                    parts.push(child.getAttribute('aria-label') || '');
                    parts.push(child.getAttribute('data-name') || '');
                    parts.push(child.getAttribute('data-level') || '');
                    parts.push(child.getAttribute('data-gid') || '');
                    parts.push(child.getAttribute('data-aid') || '');
                    parts.push(child.id || '');
                    parts.push(child.className || '');
                    parts.push(child.textContent || '');
                  }
                  current = current.parentElement;
                }
                return parts.join(' ').replace(/\\s+/g, ' ').trim();
              };

              const parseLevel = (element, slotId) => {
                const text = `${localText(element)} ${overlayText(slotId)}`;
                const match = text.match(/(?:^|\\s|_|-)level[_-]?(\\d{1,2})(?:\\s|$|_|-)/i)
                  || text.match(/(?:^|\\s|_|-)lvl(?:e|_)?(\\d{1,2})(?:\\s|$|_|-)/i)
                  || text.match(/(?:level|niveau|lvl|niv\\.?|stufe)[^0-9]*(\\d{1,2})/i);
                if (match) return Number(match[1]);
                return null;
              };

              const parseType = (element, slotId) => {
                const text = `${localText(element)} ${overlayText(slotId)}`.toLowerCase();
                const gidMatch = text.match(/(?:^|\\s|_|-)gid[_-]?(\\d+)(?:\\s|$|_|-)/);
                if (gidMatch && fieldTypes[Number(gidMatch[1])]) return fieldTypes[Number(gidMatch[1])];

                if (text.includes('wood') || text.includes('lumber') || text.includes('trä')) return 'wood';
                if (text.includes('clay') || text.includes('lera')) return 'clay';
                if (text.includes('iron') || text.includes('järn')) return 'iron';
                if (text.includes('crop') || text.includes('wheat') || text.includes('gröda')) return 'crop';
                return slotFallbackTypes[slotId] || 'unknown';
              };

              const parseName = (fieldType, element) => {
                const text = localText(element);
                const isUsefulName = (value) => {
                  if (!value || /^\\d+$/.test(value) || value.length > 40) return false;
                  if (/^(gid|aid|level|lvl)/i.test(value)) return false;
                  if (/(good|resourceField|labelLayer|colorLayer|contractLink|underConstruction)/i.test(value)) return false;
                  if (/^(a|g)\\d+$/i.test(value)) return false;
                  return true;
                };
                const titleLike = text
                  .replace(/(?:^|\\s|_|-)gid[_-]?\\d+(?:\\s|$|_|-)/gi, ' ')
                  .replace(/(?:^|\\s|_|-)aid[_-]?\\d+(?:\\s|$|_|-)/gi, ' ')
                  .replace(/level\\s*\\d+/gi, '')
                  .replace(/level\\d+/gi, '')
                  .replace(/lvl\\s*\\d+/gi, '')
                  .replace(/lvl(?:e|_)?\\d+/gi, '')
                  .replace(/niveau\\s*\\d+/gi, '')
                  .replace(/stufe\\s*\\d+/gi, '')
                  .replace(/\\s+/g, ' ')
                  .trim();
                if (isUsefulName(titleLike)) return titleLike;
                return fieldNames[fieldType] || fieldNames.unknown;
              };

              const selectors = [
                '#resourceFieldContainer area[href*="build.php?id="]',
                '#rx area[href*="build.php?id="]',
                'area[href*="build.php?id="]',
                '#resourceFieldContainer a[href*="build.php?id="]',
                '#rx a[href*="build.php?id="]',
                '.resourceField a[href*="build.php?id="]',
                'a[href*="build.php?id="]'
              ];

              const seen = new Set();
              const fields = [];
              for (const selector of selectors) {
                for (const element of document.querySelectorAll(selector)) {
                  const href = element.getAttribute('href');
                  const slotId = parseSlotId(href);
                  if (slotId === null || slotId > 18) continue;
                  const key = String(slotId);
                  if (seen.has(key)) continue;
                  seen.add(key);
                  const fieldType = parseType(element, slotId);
                  fields.push({
                    slotId,
                    fieldType,
                    name: parseName(fieldType, element),
                    level: parseLevel(element, slotId),
                    href
                  });
                }
              }

              return fields;
            }
            """
        )

        fallback_types = {
            1: "wood",
            2: "clay",
            3: "iron",
            4: "crop",
            5: "wood",
            6: "clay",
            7: "iron",
            8: "crop",
            9: "crop",
            10: "wood",
            11: "iron",
            12: "crop",
            13: "crop",
            14: "iron",
            15: "clay",
            16: "wood",
            17: "crop",
            18: "clay",
        }
        fallback_names = {
            "wood": "Woodcutter",
            "clay": "Clay pit",
            "iron": "Iron mine",
            "crop": "Cropland",
            "unknown": "Unknown field",
        }
        fields = [
            ResourceField(
                slot_id=item.get("slotId"),
                field_type=item.get("fieldType", "unknown"),
                name=item.get("name") or fallback_names.get(item.get("fieldType"), "Unknown field"),
                level=item.get("level"),
                url=urljoin(self.config.base_url, item["href"]) if item.get("href") else None,
            )
            for item in raw_fields
        ]
        seen_slots = {field.slot_id for field in fields if field.slot_id is not None}
        for slot_id in range(1, 19):
            if slot_id in seen_slots:
                continue
            field_type = fallback_types.get(slot_id, "unknown")
            fields.append(
                ResourceField(
                    slot_id=slot_id,
                    field_type=field_type,
                    name=fallback_names.get(field_type, "Unknown field"),
                    level=None,
                    url=urljoin(self.config.base_url, f"build.php?id={slot_id}"),
                )
            )
        return sorted(fields, key=lambda field: field.slot_id or 999)

    def _read_buildings(self) -> list[Building]:
        self._goto("/dorf2.php")
        self._pause_for_manual_step_if_visible("Manual verification appeared while opening the building overview.")
        self._ensure_logged_in()

        self._pause_for_manual_step_if_visible("Manual verification appeared while reading buildings.")
        raw_buildings = self.page.evaluate(
            """
            () => {
              const buildingNames = {
                10: 'Warehouse',
                11: 'Granary',
                15: 'Main Building',
                16: 'Rally Point',
                17: 'Marketplace',
                18: 'Embassy',
                19: 'Barracks',
                20: 'Stable',
                21: 'Workshop',
                22: 'Academy',
                23: 'Cranny',
                24: 'Town Hall',
                25: 'Residence',
                26: 'Palace',
                27: 'Treasury',
                28: 'Trade Office',
                29: 'Great Barracks',
                30: 'Great Stable',
                31: 'City Wall',
                32: 'Earth Wall',
                33: 'Palisade',
                34: 'Stonemason',
                35: 'Brewery',
                36: 'Trapper',
                37: 'Hero Mansion',
                38: 'Great Warehouse',
                39: 'Great Granary',
                40: 'Wonder of the World',
                41: 'Horse Drinking Trough',
                42: 'Stone Wall',
                43: 'Makeshift Wall',
                44: 'Command Center'
              };

              const parseSlotId = (href) => {
                if (!href) return null;
                const match = href.match(/[?&]id=(\\d+)/);
                return match ? Number(match[1]) : null;
              };

              const directText = (element) => {
                const parts = [
                  element.getAttribute('title') || '',
                  element.getAttribute('alt') || '',
                  element.getAttribute('aria-label') || '',
                  element.getAttribute('data-name') || '',
                  element.getAttribute('data-level') || '',
                  element.id || '',
                  element.className || '',
                  element.textContent || ''
                ];
                return parts.join(' ').replace(/\\s+/g, ' ').trim();
              };

              const collectText = (element) => {
                const parts = [];
                let current = element;
                for (let depth = 0; current && depth < 3; depth += 1) {
                  parts.push(current.getAttribute('title') || '');
                  parts.push(current.getAttribute('alt') || '');
                  parts.push(current.getAttribute('aria-label') || '');
                  parts.push(current.getAttribute('data-name') || '');
                  parts.push(current.getAttribute('data-level') || '');
                  parts.push(current.getAttribute('data-gid') || '');
                  parts.push(current.getAttribute('data-aid') || '');
                  parts.push(current.id || '');
                  parts.push(current.className || '');
                  parts.push(current.textContent || '');
                  for (const child of current.querySelectorAll('img, span, div, area')) {
                    parts.push(child.getAttribute('title') || '');
                    parts.push(child.getAttribute('alt') || '');
                    parts.push(child.getAttribute('aria-label') || '');
                    parts.push(child.getAttribute('data-name') || '');
                    parts.push(child.getAttribute('data-level') || '');
                    parts.push(child.getAttribute('data-gid') || '');
                    parts.push(child.getAttribute('data-aid') || '');
                    parts.push(child.id || '');
                    parts.push(child.className || '');
                    parts.push(child.textContent || '');
                  }
                  current = current.parentElement;
                }
                return parts.join(' ').replace(/\\s+/g, ' ').trim();
              };

              const parseGid = (text) => {
                const match = text.match(/(?:^|\\s|_|-)gid[_-]?(\\d+)(?:\\s|$|_|-)/i);
                return match ? Number(match[1]) : null;
              };

              const parseLevel = (text) => {
                const match = text.match(/(?:^|\\s|_|-)level[_-]?(\\d{1,2})(?:\\s|$|_|-)/i)
                  || text.match(/(?:^|\\s|_|-)lvl(?:e|_)?(\\d{1,2})(?:\\s|$|_|-)/i)
                  || text.match(/(?:level|niveau|lvl|niv\\.?|stufe)[^0-9]*(\\d{1,2})/i);
                return match ? Number(match[1]) : null;
              };

              const parseName = (text, direct, gid, slotId) => {
                const source = direct || text;
                const isUsefulName = (value) => {
                  if (!value || /^\\d+$/.test(value) || value.length > 48) return false;
                  if (/^(gid|aid|level|lvl)/i.test(value)) return false;
                  if (/(buildingSlot|labelLayer|colorLayer|contractLink|underConstruction)/i.test(value)) return false;
                  if (/^(a|g)\\d+$/i.test(value)) return false;
                  return true;
                };
                const cleaned = text
                  .replace(/(?:^|\\s|_|-)gid[_-]?\\d+(?:\\s|$|_|-)/gi, ' ')
                  .replace(/(?:^|\\s|_|-)aid[_-]?\\d+(?:\\s|$|_|-)/gi, ' ')
                  .replace(/level\\s*\\d+/gi, '')
                  .replace(/level\\d+/gi, '')
                  .replace(/lvl\\s*\\d+/gi, '')
                  .replace(/lvl(?:e|_)?\\d+/gi, '')
                  .replace(/niveau\\s*\\d+/gi, '')
                  .replace(/stufe\\s*\\d+/gi, '')
                  .replace(/\\s+/g, ' ')
                  .trim();
                const directCleaned = source
                  .replace(/(?:^|\\s|_|-)gid[_-]?\\d+(?:\\s|$|_|-)/gi, ' ')
                  .replace(/(?:^|\\s|_|-)aid[_-]?\\d+(?:\\s|$|_|-)/gi, ' ')
                  .replace(/level\\s*\\d+/gi, '')
                  .replace(/level\\d+/gi, '')
                  .replace(/lvl\\s*\\d+/gi, '')
                  .replace(/lvl(?:e|_)?\\d+/gi, '')
                  .replace(/niveau\\s*\\d+/gi, '')
                  .replace(/stufe\\s*\\d+/gi, '')
                  .replace(/\\s+/g, ' ')
                  .trim();

                if (isUsefulName(directCleaned)) return directCleaned;
                if (isUsefulName(cleaned)) return cleaned;
                if (gid && buildingNames[gid]) return buildingNames[gid];
                return `Slot ${slotId}`;
              };

              const selectors = [
                '#village_map area[href*="build.php?id="]',
                '#villageContent area[href*="build.php?id="]',
                'area[href*="build.php?id="]',
                '#village_map a[href*="build.php?id="]',
                '#villageContent a[href*="build.php?id="]',
                '.buildingSlot a[href*="build.php?id="]',
                'a[href*="build.php?id="]'
              ];

              const seenSlots = new Set();
              const buildings = [];
              for (const selector of selectors) {
                for (const element of document.querySelectorAll(selector)) {
                  const href = element.getAttribute('href');
                  const slotId = parseSlotId(href);
                  if (slotId === null || slotId < 19) continue;
                  if (seenSlots.has(slotId)) continue;
                  seenSlots.add(slotId);

                  const direct = directText(element);
                  const text = collectText(element);
                  const gid = parseGid(text);
                  const name = parseName(text, direct, gid, slotId);
                  buildings.push({
                    slotId,
                    name,
                    level: parseLevel(text),
                    gid,
                    href
                  });
                }
              }

              return buildings;
            }
            """
        )

        return [
            Building(
                slot_id=item.get("slotId"),
                name=item.get("name", "Unknown"),
                level=item.get("level"),
                url=urljoin(self.config.base_url, item["href"]) if item.get("href") else None,
                gid=item.get("gid"),
            )
            for item in raw_buildings
        ]

    def _read_build_queue(self) -> list[BuildQueueItem]:
        self._pause_for_manual_step_if_visible("Manual verification appeared while reading the build queue.")
        raw_items = self.page.evaluate(
            """
            () => {
              const selectors = [
                '.buildingList li',
                '#building_contract li',
                '.underConstruction',
                '.buildDuration',
                'table.buildingList tr'
              ];

              const items = [];
              const seen = new Set();
              for (const selector of selectors) {
                for (const element of document.querySelectorAll(selector)) {
                  const text = (element.textContent || '').replace(/\\s+/g, ' ').trim();
                  if (!text || seen.has(text)) continue;
                  seen.add(text);
                  const timeElement = element.querySelector('.timer, .countdown, [id^="timer"]');
                  const timeLeft = timeElement ? (timeElement.textContent || '').trim() : null;
                  items.push({ text, timeLeft });
                }
                if (items.length) return items;
              }
              return items;
            }
            """
        )

        return [
            BuildQueueItem(
                text=item.get("text", ""),
                time_left=item.get("timeLeft"),
            )
            for item in raw_items
            if item.get("text")
        ]
