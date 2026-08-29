"""Answers an inbound webhook and prints what arrived, one line per delivery.

Runs inside a throwaway container on the harness network so an application under test has somewhere
to call. Both fixture images ship python3, so this needs no image of its own.

Every print is flushed: a line held in a buffer and a delivery that never arrived are the same
observation to a reader tailing the log.
"""

import argparse
import datetime
import http.server
import json

SENTINEL = "@@WEBHOOK@@"
READY = "@@LISTENER-READY@@"
RESPONSE_BODY = b'{"ok":true}'


class CaptureHandler(http.server.BaseHTTPRequestHandler):
    def capture(self, verb):
        length = int(self.headers.get("Content-Length") or 0)
        body = self.rfile.read(length).decode("utf-8", "replace") if length else ""
        # The sentinel is what makes a delivery greppable straight out of the container's log
        # stream, so the capture needs neither a mounted volume nor a published port to come back.
        print(
            SENTINEL
            + " "
            + json.dumps(
                {
                    "ts": datetime.datetime.now(datetime.timezone.utc).isoformat(),
                    "verb": verb,
                    "path": self.path,
                    "headers": dict(self.headers),
                    "body": body,
                }
            ),
            flush=True,
        )
        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(RESPONSE_BODY)))
        self.end_headers()
        self.wfile.write(RESPONSE_BODY)

    def do_POST(self):
        self.capture("POST")

    def do_PUT(self):
        self.capture("PUT")

    def log_message(self, *args):
        """Drops the default access log, so the sentinel lines are the whole of this stdout."""


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--port", required=True, type=int)
    args = parser.parse_args()

    server = http.server.HTTPServer(("0.0.0.0", args.port), CaptureHandler)
    print(f"{READY} port={args.port}", flush=True)
    server.serve_forever()


if __name__ == "__main__":
    main()
