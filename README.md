# Mod Manager

A Jellyfin plugin that lets you browse and enable community JS and CSS mods from the server dashboard. Mods are injected at request time — your Jellyfin install is never modified on disk. Multiple mods can be active simultaneously.

---

## Requirements

- Jellyfin 10.11+
- [File Transformation](https://github.com/IAmParadox27/jellyfin-plugin-file-transformation) plugin

Both plugins are available from the repository below.

---

## Installation

**1. Add the plugin repository**

In Jellyfin, go to **Dashboard > Plugins > Repositories** and add:

```
https://raw.githubusercontent.com/Jellyfin-PG/Repository/refs/heads/main/manifest.json
```

**2. Install the plugins**

Go to the **Catalogue** tab. Install **File Transformation** first, then **Mod Manager**. Restart Jellyfin after each install, or restart once after both.

**3. Open the mod store**

Navigate to **Dashboard > Mod Manager** in the sidebar. Enter a mod repository URL and click **Load Mods**. Enable whichever mods you want, then click **Save & Apply** and hard-refresh your browser (`Ctrl+Shift+R`).

---

## Mod Repository

The default mod list is loaded from:

```
https://raw.githubusercontent.com/Jellyfin-PG/Mod-Manager-Mods/main/mods.json
```

This file is fetched live when you click Load Mods, so new community submissions appear without a plugin update. You can point the URL at any compatible `mods.json` file, including your own.

To submit a mod, open an issue using the **Mod Submission** template in the [mod repository](https://github.com/Jellyfin-PG/Mod-Manager-Mods).

---

## Mod Format

Each entry in `mods.json` uses the following structure:

```json
{
  "id": "my-mod",
  "name": "My Mod",
  "author": "author",
  "description": "What the mod does.",
  "version": "1.0.0",
  "type": "js",
  "jellyfin": "10.11+",
  "tags": ["ui", "player"],
  "previewUrl": "",
  "sourceUrl": "https://github.com/author/my-mod",
  "jsUrl": "https://raw.githubusercontent.com/author/my-mod/main/mod.js",
  "jsInline": null,
  "cssUrl": null,
  "cssInline": null
}
```

The `type` field accepts `"js"`, `"css"`, or `"both"`. For `"both"`, all four URL/inline fields are supported simultaneously.

---

## How it works

On every request for `index.html`, the File Transformation plugin invokes a callback in Mod Manager. The callback reads the list of enabled mods from the plugin configuration and injects the appropriate tags before `</body>`:

- **CSS mods** are injected as `<style>@import url("...");</style>` tags, matching the same approach used by Skin Manager.
- **JS mods** are fetched at runtime via a small inline loader script and injected as `<script>` tags with their content set directly, avoiding CORB restrictions.

The original files on disk are never modified. Disabling all mods clears the injection on the next page load.

---

## License

GPL-3.0
