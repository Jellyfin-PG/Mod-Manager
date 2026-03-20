using System.Collections.Generic;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.ModManager.Configuration
{
    public class PluginConfiguration : BasePluginConfiguration
    {
        /// <summary>
        /// URL pointing to the mods repository JSON file.
        /// This is fetched when the user clicks Load Mods in the config page.
        /// </summary>
        public string ModsUrl { get; set; } = "https://raw.githubusercontent.com/Jellyfin-PG/Mod-Manager-Mods/refs/heads/main/mods.json";

        /// <summary>
        /// IDs of mods the user has enabled. Multiple mods can be active simultaneously.
        /// Each entry corresponds to the <c>id</c> field of a mod in the repository JSON.
        /// </summary>
        public List<string> EnabledMods { get; set; } = new List<string>();

        /// <summary>
        /// The full mod list serialized as JSON, cached when the user saves config.
        /// Used by the injector at request time so it does not need to make HTTP calls
        /// on every page load. Refreshed each time the user clicks Save &amp; Apply.
        /// </summary>
        public string CachedMods { get; set; } = string.Empty;

        /// <summary>
        /// Optional custom CSS or JS injected after all enabled mods.
        /// The injector attempts to auto-detect the type based on content.
        /// </summary>
        public string CustomCode { get; set; } = string.Empty;

        /// <summary>
        /// When true, the injector writes detailed diagnostic output to the Jellyfin log.
        /// Useful for debugging mod loading and injection issues.
        /// Has no effect on performance when false.
        /// </summary>
        public bool DebugLogging { get; set; } = false;
    }
}