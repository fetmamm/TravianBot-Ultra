from dataclasses import dataclass
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
class VillageSnapshot:
    villages: list[Village]
    resources: dict[str, str]


@dataclass(frozen=True)
class ResourceField:
    slot_id: int | None
    field_type: str
    level: int | None
    url: str | None


@dataclass(frozen=True)
class Building:
    slot_id: int | None
    name: str
    level: int | None
    url: str | None


@dataclass(frozen=True)
class BuildQueueItem:
    text: str
    time_left: str | None


@dataclass(frozen=True)
class VillageStatus:
    active_village: str
    villages: list[Village]
    resources: dict[str, str]
    resource_fields: list[ResourceField]
    buildings: list[Building]
    build_queue: list[BuildQueueItem]


class ManualVerificationRequired(RuntimeError):
    pass


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

    def login(self) -> None:
        self.page.goto(self.config.village_overview_path, wait_until="domcontentloaded")
        self._pause_for_manual_step_if_visible("Manual verification appeared before login.")
        if self._is_logged_in():
            return

        self.page.goto(self.config.login_path, wait_until="domcontentloaded")
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
        self.page.goto(self.config.village_overview_path, wait_until="domcontentloaded")
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
                locator.click()
                self.page.wait_for_load_state("domcontentloaded")
                self._notify("Logged out from Travian.")
                return

        raise RuntimeError("Could not find a Travian logout link.")

    def read_village_snapshot(self) -> VillageSnapshot:
        self.page.goto(self.config.village_overview_path, wait_until="domcontentloaded")
        self._pause_for_manual_step_if_visible("Manual verification appeared while opening the village overview.")
        self._ensure_logged_in()

        self._pause_for_manual_step_if_visible("Manual verification appeared before reading villages.")
        villages = self._read_villages()
        self._pause_for_manual_step_if_visible("Manual verification appeared before reading resources.")
        resources = self._read_resources()
        return VillageSnapshot(villages=villages, resources=resources)

    def read_village_status(self) -> VillageStatus:
        self.page.goto(self.config.village_overview_path, wait_until="domcontentloaded")
        self._pause_for_manual_step_if_visible("Manual verification appeared while opening the village overview.")
        self._ensure_logged_in()

        self._pause_for_manual_step_if_visible("Manual verification appeared before reading village status.")
        return VillageStatus(
            active_village=self._read_active_village_name(),
            villages=self._read_villages(),
            resources=self._read_resources(),
            resource_fields=self._read_resource_fields(),
            buildings=self._read_buildings(),
            build_queue=self._read_build_queue(),
        )

    def _ensure_logged_in(self) -> None:
        if not self._is_logged_in():
            raise RuntimeError("Not logged in. The session may have expired or login failed.")

    def _is_logged_in(self) -> bool:
        current_url = self.page.url.lower()
        if "login.php" in current_url:
            return False

        logged_in_selectors = [
            "a[href*='logout']",
            "a[href*='dorf1.php']",
            "#sidebarBoxVillagelist",
            ".villageList",
            "#villageList",
        ]
        return any(self.page.locator(selector).count() > 0 for selector in logged_in_selectors)

    def _fill_first_available(self, selectors: list[str], value: str) -> None:
        for selector in selectors:
            locator = self.page.locator(selector).first
            if locator.count() > 0:
                locator.fill(value)
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
                locator.click()
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

    def _read_resource_fields(self) -> list[ResourceField]:
        self._pause_for_manual_step_if_visible("Manual verification appeared while reading resource fields.")
        raw_fields = self.page.evaluate(
            """
            () => {
              const fieldNames = {
                1: 'wood',
                2: 'clay',
                3: 'iron',
                4: 'crop'
              };

              const parseSlotId = (href) => {
                if (!href) return null;
                const match = href.match(/[?&]id=(\\d+)/);
                return match ? Number(match[1]) : null;
              };

              const parseLevel = (element) => {
                const candidates = [
                  element.getAttribute('data-level'),
                  element.getAttribute('title'),
                  element.getAttribute('alt'),
                  element.textContent || ''
                ];
                for (const value of candidates) {
                  if (!value) continue;
                  const match = String(value).match(/(?:level|niveau|lvl|niv\\.?)[^0-9]*(\\d+)/i)
                    || String(value).match(/\\b(\\d{1,2})\\b/);
                  if (match) return Number(match[1]);
                }
                return null;
              };

              const parseType = (element) => {
                const className = element.className || '';
                const gidMatch = String(className).match(/gid(\\d+)/);
                if (gidMatch && fieldNames[Number(gidMatch[1])]) return fieldNames[Number(gidMatch[1])];

                const text = `${element.getAttribute('title') || ''} ${element.textContent || ''}`.toLowerCase();
                if (text.includes('wood') || text.includes('lumber') || text.includes('trä')) return 'wood';
                if (text.includes('clay') || text.includes('lera')) return 'clay';
                if (text.includes('iron') || text.includes('järn')) return 'iron';
                if (text.includes('crop') || text.includes('wheat') || text.includes('gröda')) return 'crop';
                return 'unknown';
              };

              const selectors = [
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
                  const key = `${slotId}|${href}`;
                  if (seen.has(key)) continue;
                  seen.add(key);
                  fields.push({
                    slotId,
                    fieldType: parseType(element),
                    level: parseLevel(element),
                    href
                  });
                }
                if (fields.length) return fields;
              }

              return fields;
            }
            """
        )

        return [
            ResourceField(
                slot_id=item.get("slotId"),
                field_type=item.get("fieldType", "unknown"),
                level=item.get("level"),
                url=urljoin(self.config.base_url, item["href"]) if item.get("href") else None,
            )
            for item in raw_fields
        ]

    def _read_buildings(self) -> list[Building]:
        self.page.goto("/dorf2.php", wait_until="domcontentloaded")
        self._pause_for_manual_step_if_visible("Manual verification appeared while opening the building overview.")
        self._ensure_logged_in()

        self._pause_for_manual_step_if_visible("Manual verification appeared while reading buildings.")
        raw_buildings = self.page.evaluate(
            """
            () => {
              const parseSlotId = (href) => {
                if (!href) return null;
                const match = href.match(/[?&]id=(\\d+)/);
                return match ? Number(match[1]) : null;
              };

              const parseLevel = (text) => {
                const match = text.match(/(?:level|niveau|lvl|niv\\.?)[^0-9]*(\\d+)/i)
                  || text.match(/\\b(\\d{1,2})\\b/);
                return match ? Number(match[1]) : null;
              };

              const selectors = [
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

                  const text = `${element.getAttribute('title') || ''} ${element.textContent || ''}`
                    .replace(/\\s+/g, ' ')
                    .trim();
                  const name = text.replace(/(?:level|niveau|lvl|niv\\.?)[^0-9]*\\d+/i, '').trim() || `Slot ${slotId}`;
                  buildings.push({
                    slotId,
                    name,
                    level: parseLevel(text),
                    href
                  });
                }
                if (buildings.length) return buildings;
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
