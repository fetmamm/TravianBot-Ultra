import re
from dataclasses import dataclass
from html.parser import HTMLParser
from urllib.parse import urljoin, urlparse
from urllib.request import Request, urlopen


SS_TRAVI_INDEX_URL = "https://ss-travi.com/International/index.php"


@dataclass(frozen=True)
class ServerOption:
    name: str
    base_url: str


class _LinkParser(HTMLParser):
    def __init__(self) -> None:
        super().__init__()
        self.links: list[tuple[str, str]] = []
        self._current_href: str | None = None
        self._text_parts: list[str] = []

    def handle_starttag(self, tag: str, attrs) -> None:
        if tag.lower() != "a":
            return

        attr_map = dict(attrs)
        href = attr_map.get("href")
        if not href:
            return

        self._current_href = href
        self._text_parts = []

    def handle_data(self, data: str) -> None:
        if self._current_href:
            self._text_parts.append(data)

    def handle_endtag(self, tag: str) -> None:
        if tag.lower() != "a" or not self._current_href:
            return

        text = " ".join(" ".join(self._text_parts).split())
        self.links.append((self._current_href, text))
        self._current_href = None
        self._text_parts = []


class _ServerCardParser(HTMLParser):
    def __init__(self) -> None:
        super().__init__()
        self.cards: list[tuple[list[str], list[str]]] = []
        self._depth = 0
        self._in_card = False
        self._card_depth = 0
        self._card_text: list[str] = []
        self._card_hrefs: list[str] = []

    def handle_starttag(self, tag: str, attrs) -> None:
        self._depth += 1
        attr_map = dict(attrs)
        class_name = attr_map.get("class", "")

        if not self._in_card and _looks_like_server_card(class_name):
            self._in_card = True
            self._card_depth = self._depth
            self._card_text = []
            self._card_hrefs = []

        if self._in_card:
            href = attr_map.get("href")
            if tag.lower() == "a" and href:
                self._card_hrefs.append(href)

    def handle_data(self, data: str) -> None:
        if self._in_card:
            cleaned = " ".join(data.split())
            if cleaned:
                self._card_text.append(cleaned)

    def handle_endtag(self, tag: str) -> None:
        if self._in_card and self._depth == self._card_depth:
            self.cards.append((self._card_hrefs, self._card_text))
            self._in_card = False
            self._card_text = []
            self._card_hrefs = []
        self._depth = max(0, self._depth - 1)


def fetch_ss_travi_servers(index_url: str = SS_TRAVI_INDEX_URL, timeout_seconds: int = 15) -> list[ServerOption]:
    request = Request(index_url, headers={"User-Agent": "Tbot Ultra"})
    with urlopen(request, timeout=timeout_seconds) as response:
        html = response.read().decode("utf-8", errors="ignore")

    card_names = _extract_card_server_names(html, index_url)

    parser = _LinkParser()
    parser.feed(html)

    servers: dict[str, ServerOption] = {}
    for href, text in parser.links:
        absolute = urljoin(index_url, href)
        parsed = urlparse(absolute)
        host = parsed.netloc.lower()
        if not host.endswith("ss-travi.com"):
            continue
        if host in {"ss-travi.com", "www.ss-travi.com"}:
            continue

        base_url = f"{parsed.scheme}://{parsed.netloc}".rstrip("/")
        name = card_names.get(base_url) or _clean_server_name(text, parsed.netloc)
        servers[base_url] = ServerOption(name=name, base_url=base_url)

    return sorted(servers.values(), key=lambda server: server.name.lower())


def _extract_card_server_names(html: str, index_url: str) -> dict[str, str]:
    parser = _ServerCardParser()
    parser.feed(html)

    names: dict[str, str] = {}
    for hrefs, text_parts in parser.cards:
        base_urls = []
        for href in hrefs:
            absolute = urljoin(index_url, href)
            parsed = urlparse(absolute)
            host = parsed.netloc.lower()
            if host.endswith("ss-travi.com") and host not in {"ss-travi.com", "www.ss-travi.com"}:
                base_urls.append(f"{parsed.scheme}://{parsed.netloc}".rstrip("/"))

        if not base_urls:
            continue

        card_text = " ".join(text_parts)
        display_name = _server_display_name_from_text(card_text)
        if not display_name:
            continue

        for base_url in base_urls:
            names[base_url] = display_name

    return names


def _looks_like_server_card(class_name: str) -> bool:
    lowered = class_name.lower()
    return "server" in lowered and ("card" in lowered or "box" in lowered or "item" in lowered)


def _server_display_name_from_text(text: str) -> str | None:
    compact = " ".join(text.split())
    if not compact:
        return None

    label_match = re.search(r"SS-Travi\s+([A-Za-z]+)", compact, re.IGNORECASE)
    if not label_match:
        label_match = re.search(r"\b(PRO|ULT|VIP|MGA|ELT|MEGA|ELITE|TOURNAMENT)\b", compact, re.IGNORECASE)

    speed_match = re.search(r"(?:×|&times;|x)\s*([0-9][0-9,.]*)", compact, re.IGNORECASE)
    if not speed_match:
        speed_match = re.search(r"\b([0-9][0-9,.]*)\s*x\b", compact, re.IGNORECASE)

    if label_match and speed_match:
        label = label_match.group(1).upper()
        speed = speed_match.group(1).replace(",", "").replace(".", "")
        return f"{label} {speed}x"

    host_match = re.search(r"\b([a-z0-9-]+)\.ss-travi\.com\b", compact, re.IGNORECASE)
    if host_match and speed_match:
        speed = speed_match.group(1).replace(",", "").replace(".", "")
        return f"{host_match.group(1).upper()} {speed}x"

    return None


def _clean_server_name(text: str, host: str) -> str:
    cleaned = " ".join(text.split())
    generic_names = {
        "",
        "play",
        "play now",
        "join",
        "register",
        "login",
        "server",
        "enter the arena",
    }
    if cleaned.lower() in generic_names or len(cleaned) < 3:
        return host

    return cleaned
