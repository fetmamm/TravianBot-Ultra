from pathlib import Path
from typing import TYPE_CHECKING

from .config import Account, BotConfig, ROOT_DIR

if TYPE_CHECKING:
    from playwright.sync_api import Browser, BrowserContext, Page


AUTH_DIR = ROOT_DIR / "playwright" / ".auth"


class BrowserSession:
    def __init__(self, config: BotConfig, account: Account, headless_override: bool | None = None) -> None:
        self.config = config
        self.account = account
        self.headless_override = headless_override
        self._playwright = None
        self.browser: Browser | None = None
        self.context: BrowserContext | None = None
        self.page: Page | None = None

    @property
    def storage_state_path(self) -> Path:
        return AUTH_DIR / f"{self.account.name}.json"

    def __enter__(self) -> "Page":
        try:
            from playwright.sync_api import sync_playwright
        except ModuleNotFoundError as exc:
            raise RuntimeError(
                "Playwright is not installed. Run: pip install -r requirements.txt"
            ) from exc

        AUTH_DIR.mkdir(parents=True, exist_ok=True)
        self._playwright = sync_playwright().start()
        headless = self.config.headless if self.headless_override is None else self.headless_override
        self.browser = self._playwright.chromium.launch(headless=headless)

        context_options = {
            "base_url": self.config.base_url,
            "viewport": {"width": 1366, "height": 900},
        }
        if self.storage_state_path.exists():
            context_options["storage_state"] = str(self.storage_state_path)

        self.context = self.browser.new_context(**context_options)
        self.context.set_default_timeout(self.config.timeout_ms)
        self.page = self.context.new_page()
        return self.page

    def save_state(self) -> None:
        if self.context:
            self.context.storage_state(path=str(self.storage_state_path))

    def __exit__(self, exc_type, exc, tb) -> None:
        if self.context:
            self.context.close()
        if self.browser:
            self.browser.close()
        if self._playwright:
            self._playwright.stop()
