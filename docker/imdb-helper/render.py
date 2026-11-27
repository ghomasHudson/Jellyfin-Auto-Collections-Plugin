"""Render a single canonical public IMDb list or chart URL."""

import json
import os
import re
import sys


ROUTE = re.compile(
    r"https://www\.imdb\.com/(?:list/ls[0-9]+/(?:\?page=[1-9][0-9]*)?|chart/(?:top|toptv)/)"
)


class ReadError(Exception):
    """An error with a static, safe-to-log message."""


def capture_document(page, expected_url):
    documents = []

    def observe(response):
        if response.request.is_navigation_request() and response.frame == page.main_frame:
            documents.append((response.url, response.status))

    page.on("response", observe)

    def read():
        before = len(documents)
        # Read URL and script in one browser operation. Use textContent directly
        # so newlines, spaces, character references, and JSON escapes survive.
        url, count, script = page.evaluate(
            """() => {
                const scripts = document.querySelectorAll("script#__NEXT_DATA__");
                const text = scripts.length === 1 ? scripts[0].textContent : "";
                return [window.location.href, scripts.length, text.length <= 8 * 1024 * 1024 ? text : null];
            }"""
        )
        if url != expected_url:
            raise ReadError("IMDb returned an unexpected route.")
        if not documents or len(documents) != before or documents[-1][0] != url:
            raise ReadError("IMDb did not finish loading a document.")
        status = documents[-1][1]
        if status != 200:
            return {"finalUrl": url, "status": status, "pageData": ""}
        if script is None:
            raise ReadError("IMDb page data exceeds the size limit.")
        if count != 1 or not script:
            raise ReadError("IMDb page data is missing or ambiguous.")
        try:
            data = json.loads(script, parse_constant=reject_constant)
        except (ValueError, RecursionError):
            raise ReadError("IMDb page data is not valid JSON.") from None
        if not isinstance(data, dict):
            raise ReadError("IMDb page data is not a JSON object.")
        return {"finalUrl": url, "status": status, "pageData": script}

    return read


def reject_constant(_):
    raise ValueError


def render(url):
    # Prevent Playwright debug output and environment-provided driver overrides.
    for name in ("DEBUG", "NODE_DEBUG", "NODE_DEBUG_NATIVE", "NODE_OPTIONS", "PLAYWRIGHT_NODEJS_PATH"):
        os.environ.pop(name, None)
    from urlrender import Renderer
    from urlrender.exceptions import NavigationError, RenderTimeoutError

    renderer = Renderer()
    try:
        try:
            renderer.start()
        except Exception:
            raise ReadError("IMDb sandboxed browser could not start.") from None
        try:
            return renderer.render(url, capture=lambda page: capture_document(page, url))
        except RenderTimeoutError:
            raise ReadError("IMDb rendering timed out.") from None
        except NavigationError as error:
            if isinstance(error.__cause__, ReadError):
                raise error.__cause__ from None
            raise ReadError("IMDb navigation failed.") from None
    finally:
        renderer.close()


def main():
    sys.dont_write_bytecode = True
    if len(sys.argv) != 2 or not ROUTE.fullmatch(sys.argv[1]):
        print("Invalid IMDb route.", file=sys.stderr)
        return 1
    try:
        result = render(sys.argv[1])
        reply = (json.dumps(result, ensure_ascii=False, separators=(",", ":")) + "\n").encode("utf-8")
        if len(reply) > 16 * 1024 * 1024:
            raise ReadError("IMDb renderer reply exceeds the size limit.")
        sys.stdout.buffer.write(reply)
        if result["status"] != 200:
            print("IMDb document did not return HTTP 200.", file=sys.stderr)
            return 1
        return 0
    except ReadError as error:
        print(error, file=sys.stderr)
    except Exception:
        print("IMDb renderer failed.", file=sys.stderr)
    return 1


if __name__ == "__main__":
    raise SystemExit(main())
