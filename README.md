# SWYF Custom AI — Installer + Mod

Use your own OpenAI-compatible AI provider in **Scam With Your Friends**.
Only the **lobby host** needs the mod — guests join normally.

Press **F8** or click **AI Settings** in the main or pause menu to configure
your provider in a local browser panel. Guests use the game's existing
networking and local voice synthesis.

- Set the API Base URL, API key, and model without restarting the game.
- Test the connection and the structured response format expected by callers.
- See the saved game provider while testing a draft. **Test Connection** does
  not save; **Save Settings** applies the selection to the game.
- Optionally override temperature, top-p, and response token limits.
- See custom-provider status in the main menu instead of the original AI
  sign-in status.
- With custom AI enabled and saved, play without signing in to the game's AI
  service in a browser. Steam must still be connected for lobbies. Turning
  custom AI off restores the game's original sign-in requirement.

This mod replaces the text-generation provider. Speech recognition and voice
synthesis are handled by the game and are not modified.

## Get started in 3 steps (recommended)

1. **Install this app** — download `SwyfInstaller-Setup-v*-win-x64.exe` from
   [Releases](https://github.com/manifest-dex/SwyfInstaller/releases) and run it
   (per-user, no admin needed).
2. **Find your game** — the app detects your Steam libraries on startup and
   fills in the game folder itself. If it can't, press **Detect** or **Browse**
   to the folder Steam opens via
   right-click game → Properties → Installed Files → Browse. Close the game first.
3. **Press Install, then play** — press **Install**, wait for "Done", start the
   game, host a lobby, and press **F8** (or AI Settings) to enter your provider
   details and **Save Settings**.

Guests don't need anything. New calls use your saved settings.

**Moving from the Playtest:** Installer v1.1.5 detects the full release through
Steam (app 4954910, normally `steamapps/common/Scam With Your Friends`). When
both editions are installed, a saved Playtest selection switches to the full
release. Press **Install** to apply the mod there, then check your settings with
**F8**. Each game folder keeps its own mod settings and backups.

## What the app buttons do

| Button | What happens | Your data |
|---|---|---|
| Install | Downloads the latest verified mod and applies it | Kept |
| Update | Removes old mod files, installs the latest | Settings, sign-in, backups kept |
| Uninstall | Removes the mod, restores original game files | Settings and backups kept |

Downloads are SHA-256 verified before anything touches your game — a mismatch
aborts. The app checks for its own updates silently on startup. The patch step
always runs after download. A copyable details log lives under Details.

## Manual install (without the app)

You need:

- **Windows x64** and a compatible **Scam With Your Friends**
  installation.
- **.NET Runtime 10 and ASP.NET Core Runtime 10, Windows x64**. The installer
  checks both and downloads missing runtimes from Microsoft. Internet access
  and approval of the Windows administrator prompt are needed only if a
  runtime is missing. For offline setup,
  [install both runtimes manually](https://dotnet.microsoft.com/en-us/download/dotnet/10.0)
  first. The base .NET Runtime alone is not enough for the settings panel.
- An OpenAI-compatible **Chat Completions** endpoint and a model capable of
  returning the game's structured JSON responses. Keyless local endpoints are
  supported.

1. Close the game and wait for Steam downloads to finish.
2. In Steam, open **Properties > Installed Files > Browse**.
3. Extract **all contents** of `SWYF-Custom-AI-win-x64.zip` (from the
   `custom-ai-v*` releases on this repo) directly into that folder.
4. Double-click **Install Custom AI.cmd**. If a runtime is missing, wait for
   the Microsoft download and approve the Windows administrator prompt. If a
   restart is requested, restart Windows and run the script again.
5. Start the game and press **F8** to configure your provider.

If **F8 / AI Settings does nothing**, close the game and run
**Install Custom AI.cmd** again.

The installer backs up the original assemblies under
`<game>/CustomAI/backup/`. Custom AI is **disabled on first installation**.
Reinstalling preserves your provider settings.

## Recommended setup (enter in F8)

- **Base URL:** `https://openrouter.ai/api/v1`
- **Model ID:** `meta-llama/llama-3.3-70b-instruct`
- **API key:** create one at
  [openrouter.ai/keys](https://openrouter.ai/workspaces/default/keys).
  It is shown **only once** right after you create it — copy it immediately
  into the F8 panel. If you lost it, create a new key.

The installer app shows this same setup with a one-click copy button.

## Configure your provider

### ManifestDeX AI for Free and Pro members

[ManifestDeX](https://manifestdex.com) provides AI for your callers through
**Use ManifestDeX AI** in the F8 panel. Both **Free** and **Pro** members can
use the service, with **request and token limits specific to their membership
tier**. You do not need to supply your own AI provider API key for this option.

Click **Sign in with ManifestDeX**, approve the requested permissions in your
browser, select **Use ManifestDeX AI**, then click **Save Settings** to activate
it in the game. The service is hosted at `https://swyf-ai.manifestdex.com`.

While selected, private provider fields are locked and their saved values are
preserved. After sign-in the panel shows your name/avatar, Free/Pro
membership, rank, remaining personal request/token allowances and reset dates.
Keep `manifestdex-session.dat` and `member-keys` private. Revoke connections
from [Connected applications](https://manifestdex.com/account/connections).

### Your own provider

1. Launch the game and press **F8**, or click **AI Settings** in the main or
   pause menu.
2. Enter the **API Base URL**, for example `http://127.0.0.1:1234/v1`. The mod
   appends `/chat/completions`; do not include that suffix yourself.
3. Enter the exact **Model ID** accepted by your provider.
4. Enter an **API key** if required. Leave it blank for a keyless local
   server. When a key is already saved, leaving the field blank preserves it;
   **Delete saved API key** removes it.
5. Click **Test Connection**. This uses the form's current values and checks
   that the response matches the game schema. It does not save the form.
   Current builds read the installed game's dialogue schema on each test,
   including fields added by game updates. Older builds use the bundled probe.
   Requests may incur provider charges.
6. Enable **Use custom provider** and click **Save Settings**.
7. Host a lobby and start a call. New requests use the saved settings; an
   in-flight request keeps its existing settings.

The browser panel listens only on `127.0.0.1`, uses a dynamically assigned
port, and closes with the game. Open it through F8 rather than bookmarking an
old session URL.

### More provider options

**Free does not mean unlimited, and API compatibility does not guarantee every
model will work.** After choosing a model, run **Test Connection**, then try
an actual call. Keep responses within the game's **15-second deadline**. Do
not append `/chat/completions` to the Base URLs below.

- **Google Gemini API (cloud free tier):** Base URL
  `https://generativelanguage.googleapis.com/v1beta/openai`, key from
  [Google AI Studio](https://aistudio.google.com/), a current Free Tier Flash
  model that supports structured output, **JSON Schema** mode.
- **OmniRoute / 9Router:** point the mod at your router's OpenAI-compatible
  base address with its client-facing key and exact route model ID. Start with
  **JSON Schema**. A router does not make paid inference free — upstream
  quotas and billing still apply.
- **LM Studio (local):** Base URL `http://127.0.0.1:1234/v1`, blank key,
  loaded model's API identifier, **JSON Schema** with a model supporting
  structured output. No cloud bill; needs enough RAM/VRAM to answer in time.
- **Ollama (local):** Base URL `http://127.0.0.1:11434/v1`, blank key, exact
  name from `ollama list`, **JSON Schema**. Preload the model before playing
  so cold start doesn't eat the deadline.
- **LocalAI (self-hosted):** Base URL `http://127.0.0.1:8080/v1`, your
  server's key, a backend supporting structured output. More setup work than
  the desktop options.

### Advanced settings

- **Temperature / Top P:** leave blank to inherit the game's defaults.
- **Maximum response tokens:** leave blank to retain the game's limit.
  Raising it can increase response time.
- **JSON Schema:** the default; preserves the game's structured output contract.
- **JSON compatibility mode:** uses `json_object` and adds the schema to the
  model instructions. The game's response validation still applies, so this
  cannot make every model compatible.

Slow providers or invalid responses trigger the game's fallback dialogue.
Custom-provider failures never switch to the original paid/game-credit service.

### Multiplayer and voice

AI requests are made by the **host**. Guests do not need the mod or your API
key. The mod changes text generation, not speech recognition or synthesis.

### Connection status

With custom AI enabled, the main menu shows the configured model and one of
these states: **Not tested yet**, **Connecting**,
**Last request succeeded**, **Connection error** (details in the panel).
Disabling custom AI restores the original game's AI provider and status display.

## Update or uninstall

Close the game first and let Steam finish downloads. Keep the
`CustomAI/backup` folder intact.

- **With the app:** press **Update** (repairs patches removed by Steam
  updates) or **Uninstall**. Settings, sign-in and backups are always kept.
- **Manual:** double-click **Play with Custom AI.cmd** to repair compatible
  updates and launch through Steam, or **Uninstall Custom AI.cmd** to remove
  the patch.

**Keep `<game>/CustomAI/settings.json` private. It contains your API key in
plaintext.** Never share your installed `CustomAI` folder, backups, game logs,
or modified game DLLs.

## Troubleshooting

Custom AI v1.1.5 targets full release build **26003210** (Steam app **4954910**).
With custom AI enabled,
prompt preparation uses the game's shipped local rules; turning custom AI off
preserves the original game route. The hosted service accepts bounded caller
schema additions and validates replies against the full game schema. Core API
or protocol changes may still require another update.

- **F8 does nothing** — close the game, press **Update** (or re-run
  **Install Custom AI.cmd** for manual installs), start the game again.
  Confirm the ASP.NET Core 10 x64 runtime is installed (`dotnet
  --list-runtimes` should include `Microsoft.AspNetCore.App 10.x`). Check
  `[CustomAI]` entries in
  `%USERPROFILE%\AppData\LocalLow\Jatater Worldwide\Scam With Your Friends\Player.log`.
- **Sign in with Steam appears instead of Play** — open F8, enable **Use custom
  provider**, and click **Save Settings**. With v1.1.5 installed, the game's AI
  service sign-in is optional while custom AI is enabled. The Steam client must
  remain signed in and connected for multiplayer.
- **"Sorry, what were you saying?"** — the game's fallback response. Open F8
  and inspect the error, then test the connection. Check the API root, model
  ID, credentials, structured output support, and response speed. Leave
  unsupported temperature/top-p overrides blank. Try JSON compatibility mode
  if your model rejects JSON Schema.
- **Panel session invalid or unreachable** — open it again from F8. Session
  tokens and the localhost port change when the panel restarts.
- **Something failed in the app** — open Details → **Copy log** and include it
  when asking for help.
