"""Offline renderer smoke test; run with the extracted Python in stock Jellyfin."""

import importlib.util
import os
import sys
from pathlib import Path
from unittest.mock import patch

from playwright.sync_api import BrowserContext
from urlrender import Renderer
from urlrender.exceptions import NavigationError


ROOT = Path(sys.executable).resolve().parents[2]
spec = importlib.util.spec_from_file_location("render", ROOT / "render.py")
helper = importlib.util.module_from_spec(spec)
spec.loader.exec_module(helper)

URL = "https://www.imdb.com/list/ls055592025/?page=2"
DATA = '\n {"title":"雪 &amp; \\\\","quoted":"\\u003c"}  \n'
VALID = f'<!DOCTYPE html><script type="application/json" id="__NEXT_DATA__">{DATA}</script>'


def status(pid):
    return dict(
        (name, value.strip())
        for line in Path(f"/proc/{pid}/status").read_text().splitlines()
        for name, _, value in [line.partition(":")]
    )


def check_sandbox(browser, page):
    # The real Chromium process must be in an additional PID namespace with
    # an additional seccomp filter, not merely marked "sandbox requested".
    page.goto("data:text/html,<title>Before</title><script>document.title='JS ran'</script>")
    assert page.title() == "JS ran"
    session = browser.new_browser_cdp_session()
    try:
        processes = session.send("SystemInfo.getProcessInfo")["processInfo"]
        control = status("self")
        renderer_status = []
        for process in processes:
            pid = process["id"]
            try:
                argv = Path(f"/proc/{pid}/cmdline").read_bytes().split(b"\0")
                assert not any(option in argv for option in (
                    b"--no-sandbox", b"--disable-seccomp-filter-sandbox", b"--disable-namespace-sandbox",
                )), "Chromium sandbox was disabled"
                if process["type"] == "renderer":
                    renderer_status.append(status(pid))
            except FileNotFoundError:
                continue
        assert any(
            len(entry["NSpid"].split()) > len(control["NSpid"].split())
            and entry["Seccomp"] == "2"
            and int(entry["Seccomp_filters"]) > int(control["Seccomp_filters"])
            and int(entry["CapEff"], 16) == 0
            for entry in renderer_status
        ), "Chromium renderer sandbox could not be confirmed"
    finally:
        session.detach()


def main():
    assert os.geteuid() != 0
    assert helper.ROUTE.fullmatch(URL)
    for invalid in (
        "https://imdb.com/chart/top/",
        "http://www.imdb.com/chart/top/",
        "https://www.imdb.com/chart/boxoffice/",
        "https://www.imdb.com/chart/top/?page=2",
        "https://www.imdb.com/chart/top/#x",
        "https://www.imdb.com/list/ls055592025/?page=0",
        "https://www.imdb.com/list/ls055592025/?page=2&page=3",
    ):
        assert not helper.ROUTE.fullmatch(invalid)

    original = BrowserContext.new_page
    with Renderer() as renderer:
        def fixture(responses):
            requests = []

            def new_page(context):
                page = original(context)

                def respond(route):
                    if route.request.resource_type != "document":
                        route.abort()
                        return
                    requests.append(route.request.url)
                    code, html = responses[len(requests) - 1]
                    route.fulfill(status=code, content_type="text/html; charset=utf-8", body=html)

                page.route("**/*", respond)
                return page

            with patch.object(BrowserContext, "new_page", new_page):
                return renderer.render(URL, capture=lambda page: helper.capture_document(page, URL))

        with renderer._browser.new_context() as context:
            check_sandbox(renderer._browser, context.new_page())
        print("JavaScript and Chromium namespace/seccomp sandbox verified.")

        # An initial 202 is superseded by a same-URL main-frame 200.
        reply = fixture([
            (202, "<script>setTimeout(() => location.reload(), 50)</script>"),
            (200, VALID),
        ])
        assert reply == {"status": 200, "finalUrl": URL, "pageData": DATA}
        assert fixture([(403, "<title>Blocked</title>")]) == {
            "status": 403, "finalUrl": URL, "pageData": "",
        }
        for html in (
            '<div id="__NEXT_DATA__">{}</div>',
            '<script id="__NEXT_DATA__" type="application/json">{"bad":NaN}</script>',
            '<script id="__NEXT_DATA__" type="application/json">[]</script>',
            VALID + VALID,
        ):
            try:
                fixture([(200, html)])
            except NavigationError as error:
                assert isinstance(error.__cause__, helper.ReadError)
            else:
                raise AssertionError("Unexpected success without an unambiguous JSON object")
    print("Route validation, last-document status, and exact script capture verified.")


if __name__ == "__main__":
    main()
