#!/usr/bin/env python3
"""OpenRouter credit pool — example AgentLimits plugin.

This is a DEMO. It does not hit the network, it just returns fake data.
To turn it into a real plugin, replace fetch_credits() with a call to the
OpenRouter API.

Contract:
- print exactly one JSON object to stdout: {"version":1,"blocks":[...]}
- exit 0 = success, non-zero = error
- stderr -> logged
- must exit on its own (the host kills it after 30 s)
"""
import json
import sys


def fetch_credits():
    """Stub: returns fake data. A real plugin would make an HTTP request here."""
    # A real plugin would do something like:
    # import os, urllib.request
    # key = os.environ.get("OPENROUTER_API_KEY", "")
    # req = urllib.request.Request(
    #     "https://openrouter.ai/api/v1/auth/key",
    #     headers={"Authorization": f"Bearer {key}"})
    # with urllib.request.urlopen(req, timeout=10) as r:
    #     return json.load(r)
    return {"limit_remaining": 18.43, "usage": 0.82}


def main():
    try:
        data = fetch_credits()
        remaining = data.get("limit_remaining")
        if remaining is None:
            raise RuntimeError("no limit_remaining in response")

        # One block — OpenRouter has a single shared credit pool.
        block = {
            "key": "credits",
            "group": "OpenRouter",
            "prefix": "5 models",
            "suffix": "pool",
            "remaining_percent": float(remaining),
            "resets_at": None,
            "error": None,
        }
        sys.stdout.write(json.dumps({"version": 1, "blocks": [block]}))
        return 0
    except Exception as e:
        # Return a block with error set — the UI shows stale, not blank.
        err_block = {
            "key": "credits",
            "group": "OpenRouter",
            "prefix": "",
            "suffix": "pool",
            "remaining_percent": None,
            "error": str(e),
        }
        sys.stdout.write(json.dumps({"version": 1, "blocks": [err_block]}))
        return 0


if __name__ == "__main__":
    sys.exit(main())
