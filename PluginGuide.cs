namespace AgentLimits;

/// <summary>
/// Plugin authoring guide — full text the user copies into their AI assistant
/// to generate new plugin scripts. English, self-contained.
/// Single source of truth: also displayed in HelpWindow's "Plugin Authoring
/// Guide" tab.
/// </summary>
internal static class PluginGuide
{
    public const string Text = "# AgentLimits — plugin authoring guide\n\n" +
"AgentLimits is a small Windows tray app that shows remaining quota for AI coding assistants in one place. You can add a custom source for any model — local or remote — without rebuilding the app.\n\n" +

"# What you are writing\n\n" +
"A plugin is a Python script that prints a single JSON object to stdout. AgentLimits runs your script on a timer, parses the JSON, and displays each item as a row in the tray window.\n\n" +
"No HTTP server, no framework, no package installation. Standard library is enough for most plugins (urllib.request, json, os, datetime).\n\n" +

"# Quick start\n\n" +
"1. Right-click the tray icon → Open plugins folder. This opens %LOCALAPPDATA%\\\\AgentLimits\\\\plugins\\\\.\n" +
"2. Create a new subfolder named after your source (e.g. my_service).\n" +
"3. Inside that folder, create two files: manifest.json and main.py.\n" +
"4. Restart AgentLimits (tray → Exit, then launch again).\n" +
"5. On first run, the new plugin needs approval: tray → Approve new plugins.\n\n" +

"# Manifest (manifest.json)\n\n" +
"```json\n" +
"{\n" +
"  \"id\": \"my_service\",\n" +
"  \"interval_sec\": 180,\n" +
"  \"env\": {\n" +
"    \"MY_SERVICE_API_KEY\": \"paste-your-key-here\"\n" +
"  }\n" +
"}\n" +
"```\n\n" +
"- id — required. Must exactly match the folder name. Host rejects the plugin otherwise.\n" +
"- interval_sec — optional, default 120. Minimum 30. Use longer intervals for rate-limited APIs.\n" +
"- env — optional. Key/value pairs that the host will pass as environment variables to your script.\n" +
"- script — optional, default main.py.\n\n" +
"In addition to your env, the host passes these standard variables:\n" +
"- AGENTLIMITS_VERSION — host version (e.g. 1.0.0).\n" +
"- AGENTLIMITS_PLUGIN_ID — your plugin's id.\n" +
"- AGENTLIMITS_INTERVAL_SEC — current interval.\n" +
"- AGENTLIMITS_TOKEN_ZAI, AGENTLIMITS_TOKEN_MINIMAX — already-configured tokens for those built-in sources, if any.\n\n" +

"# Script contract\n\n" +
"1. Print one JSON object to stdout. Not pretty-printed. Not multiple lines.\n" +
"2. Exit code 0 = success, anything else = error.\n" +
"3. stderr is logged freely — use it for debug output.\n" +
"4. Hard timeout: 30 seconds. The host will kill the process (and any subprocesses it spawned) after this.\n" +
"5. You must exit on your own. The host tracks a rolling average of your last 5 run durations. If that average exceeds your interval_sec, your next start is skipped until the window closes. Long internal loops will not get a pass just because your interval is generous.\n\n" +

"# JSON shape\n\n" +
"```json\n" +
"{\n" +
"  \"version\": 1,\n" +
"  \"blocks\": [\n" +
"    {\n" +
"      \"key\": \"5h\",\n" +
"      \"group\": \"My Service\",\n" +
"      \"prefix\": \"claude-3.5\",\n" +
"      \"suffix\": \"5h\",\n" +
"      \"remaining_percent\": 73.5,\n" +
"      \"resets_at\": \"2026-08-20T15:00:00\",\n" +
"      \"sampled_at\": \"2026-08-20T14:30:00\",\n" +
"      \"error\": null\n" +
"    }\n" +
"  ]\n" +
"}\n" +
"```\n\n" +
"Required fields: version (must be 1), blocks (array), and inside each block key, group, remaining_percent.\n\n" +
"Optional fields: prefix, suffix, resets_at, sampled_at, error.\n\n" +
"remaining_percent is a number from 0 to 100 — how much is REMAINING (not used). Set it to null if you tried but couldn't get the value; the UI will show …. If you also set error, the row goes into stale mode (dimmed number + tooltip with the error).\n\n" +
"resets_at and sampled_at are ISO 8601 timestamps. Naive (no Z, no offset) is interpreted as the host's local time — usually what users want.\n\n" +
"error is a string. When set, the row shows the last good value dimmed, with the error in the tooltip. Useful for transient failures.\n\n" +

"# Minimal example — single pool\n\n" +
"```python\n" +
"#!/usr/bin/env python3\n" +
"# Single credit-pool example. Replace fetch() with your real call.\n" +
"import json\n" +
"import os\n" +
"import sys\n" +
"import urllib.request\n" +
"\n" +
"\n" +
"def fetch():\n" +
"    key = os.environ.get(\"OPENROUTER_API_KEY\", \"\")\n" +
"    req = urllib.request.Request(\n" +
"        \"https://openrouter.ai/api/v1/auth/key\",\n" +
"        headers={\"Authorization\": f\"Bearer {key}\"},\n" +
"    )\n" +
"    with urllib.request.urlopen(req, timeout=10) as r:\n" +
"        return json.load(r).get(\"data\", {})\n" +
"\n" +
"\n" +
"def main():\n" +
"    try:\n" +
"        data = fetch()\n" +
"        remaining = data.get(\"limit_remaining\")\n" +
"        if remaining is None:\n" +
"            raise RuntimeError(\"no limit_remaining in response\")\n" +
"\n" +
"        block = {\n" +
"            \"key\": \"credits\",\n" +
"            \"group\": \"OpenRouter\",\n" +
"            \"prefix\": \"5 models\",\n" +
"            \"suffix\": \"pool\",\n" +
"            \"remaining_percent\": float(remaining),\n" +
"            \"resets_at\": None,\n" +
"            \"error\": None,\n" +
"        }\n" +
"        sys.stdout.write(json.dumps({\"version\": 1, \"blocks\": [block]}))\n" +
"        return 0\n" +
"    except Exception as e:\n" +
"        err_block = {\n" +
"            \"key\": \"credits\",\n" +
"            \"group\": \"OpenRouter\",\n" +
"            \"prefix\": \"\",\n" +
"            \"suffix\": \"pool\",\n" +
"            \"remaining_percent\": None,\n" +
"            \"error\": str(e),\n" +
"        }\n" +
"        sys.stdout.write(json.dumps({\"version\": 1, \"blocks\": [err_block]}))\n" +
"        return 0\n" +
"\n" +
"\n" +
"if __name__ == \"__main__\":\n" +
"    sys.exit(main())\n" +
"```\n\n" +

"# Multi-block example — one row per model\n\n" +
"```python\n" +
"#!/usr/bin/env python3\n" +
"# Per-model example: one row per configured model in one script.\n" +
"import json\n" +
"import os\n" +
"import sys\n" +
"\n" +
"\n" +
"MODELS = [\n" +
"    \"anthropic/claude-3.5-sonnet\",\n" +
"    \"openai/gpt-4o\",\n" +
"    \"google/gemini-pro\",\n" +
"]\n" +
"\n" +
"\n" +
"def usage_for(model, key):\n" +
"    # Real plugins would call the provider's usage endpoint.\n" +
"    # Here we return deterministic fake data so the example runs offline.\n" +
"    return {\"remaining_percent\": 50.0, \"resets_at\": None}\n" +
"\n" +
"\n" +
"def main():\n" +
"    key = os.environ.get(\"OPENROUTER_API_KEY\", \"\")\n" +
"    blocks = []\n" +
"    for model in MODELS:\n" +
"        short_key = model.replace(\"/\", \"_\")[:40]\n" +
"        prefix = model.split(\"/\", 1)[1] if \"/\" in model else model\n" +
"        try:\n" +
"            data = usage_for(model, key) or {}\n" +
"            blocks.append({\n" +
"                \"key\": short_key,\n" +
"                \"group\": \"OpenRouter\",\n" +
"                \"prefix\": prefix,\n" +
"                \"suffix\": \"pool\",\n" +
"                \"remaining_percent\": data.get(\"remaining_percent\"),\n" +
"                \"resets_at\": data.get(\"resets_at\"),\n" +
"                \"error\": None,\n" +
"            })\n" +
"        except Exception as e:\n" +
"            blocks.append({\n" +
"                \"key\": short_key,\n" +
"                \"group\": \"OpenRouter\",\n" +
"                \"prefix\": prefix,\n" +
"                \"suffix\": \"pool\",\n" +
"                \"remaining_percent\": None,\n" +
"                \"error\": str(e),\n" +
"            })\n" +
"    sys.stdout.write(json.dumps({\"version\": 1, \"blocks\": blocks}))\n" +
"    return 0\n" +
"\n" +
"\n" +
"if __name__ == \"__main__\":\n" +
"    sys.exit(main())\n" +
"```\n\n" +

"# Discovery checklist for a new model\n\n" +
"When the user asks \"add a block for &lt;service&gt;\", follow these steps. Most require searching the web for the provider's docs.\n\n" +
"1. Where does &lt;service&gt; expose quota?\n" +
"   - Web dashboard URL? (e.g. https://&lt;service&gt;/account/usage)\n" +
"   - HTTP API endpoint? (often undocumented, find via browser dev tools on the dashboard)\n" +
"   - CLI command? (some CLIs expose &lt;cli&gt; usage or &lt;cli&gt; status)\n" +
"   - Local file or cache? (some CLIs write JSON state files)\n" +
"2. Auth method.\n" +
"   - API key (Authorization: Bearer ...)\n" +
"   - OAuth (token from .credentials.json or similar)\n" +
"   - Cookie / session\n" +
"   - None (rare for paid services)\n" +
"3. Quota windows. Most services have 5-hour and 7-day windows. Some have daily or monthly. Read the API response carefully — names like primary/secondary don't always correspond to 5h/7d.\n" +
"4. Used vs remaining. Some APIs return used; convert: remaining = 100 - used.\n" +
"5. Write the script. Use the examples above as templates. Replace fetch() with your real call. Handle errors by returning a block with error set and remaining_percent: null.\n" +
"6. Pick an interval. Rate-limited APIs (anything with 429 risk) → 5–15 minutes. Local files → 30 s is fine.\n" +
"7. Provide env-var or token instructions. Tell the user which keys to put where.\n\n" +

"# Common pitfalls\n\n" +
"- Pretty-printed JSON. The parser is strict. One object, no extra newlines between fields, no leading text on stdout.\n" +
"- Logging to stdout. Even one print(\"loading...\") before the JSON breaks parsing. Use print(..., file=sys.stderr).\n" +
"- Confusing used and remaining. Some services return used; remember to invert.\n" +
"- Hardcoded timezone. Use ISO 8601 with offset (Z for UTC) or naive (interpreted as local). Pick one and stick to it.\n" +
"- Long internal loops. The host will kill you after 30 s AND throttle you via cumulative-time tracking. Exit fast.\n" +
"- Subprocesses that don't die with you. The host kills the process tree. Make sure your subprocesses don't survive.\n" +
"- error: \"\" (empty string) is NOT null — it'll still mark the row as stale. Use null for \"no error\".\n\n" +

"# Security\n\n" +
"The plugin runs with the same privileges as AgentLimits — full access to your user account. Before approving a plugin:\n\n" +
"- Read the script. Make sure you understand what it does.\n" +
"- Check whether it talks to unexpected hosts or writes files anywhere.\n" +
"- Verify the source. Code from a friend is one thing; random code from the internet is another.\n\n" +
"The host does not sandbox plugins. Treat them like any executable you run on your machine.\n\n" +

"# Testing locally\n\n" +
"Stub your fetch() function to return fixed data while iterating:\n\n" +
"```python\n" +
"def fetch():\n" +
"    return {\"limit_remaining\": 73.5}  # your fake data here\n" +
"```\n\n" +
"Drop the script into %LOCALAPPDATA%\\\\AgentLimits\\\\plugins\\\\test_plugin\\\\main.py (with matching manifest.json), restart AgentLimits, tray → Approve new plugins. Iterate until the row looks right.\n\n" +

"# License\n\n" +
"Plugins inherit the project's license (MIT) by default. If you write a plugin with different terms, mention it in the plugin's README.\n";
}