using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using Jellyfin.Plugin.ModManager.Configuration;
using Jellyfin.Plugin.ModManager.Runtime;
using Jellyfin.Plugin.ModManager.Services;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Controller.Session;
using MediaBrowser.Controller.Subtitles;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ModManager
{
    public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages, IDisposable
    {
        private const int CurrentConfigVersion = 2;

        public override string Name => "ModManager";
        public override Guid Id => Guid.Parse("c3d4e5f6-a7b8-9012-cdef-345678901234");
        public override string Description => "Mod store that injects community JS/CSS mods and server-side scripts via File Transformation.";

        public static Plugin Instance { get; private set; }
        public IServiceProvider ServiceProvider { get; private set; }
        public ServerModLoader ModLoader { get; private set; }
        public IApplicationPaths AppPaths { get; private set; }

        private readonly ILogger<Plugin> _logger;
        private readonly JsonSerializerOptions _jsonOpts = new() { PropertyNameCaseInsensitive = true };
        private bool _disposed;

        public Plugin(
            IApplicationPaths applicationPaths,
            IXmlSerializer xmlSerializer,
            ILogger<Plugin> logger,
            IServiceProvider serviceProvider,
            ILibraryManager libraryManager,
            IUserDataManager userDataManager,
            IUserManager userManager,
            ISessionManager sessionManager,
            ISubtitleManager subtitleManager,
            IMediaEncoder mediaEncoder,
            IPlaylistManager playlistManager,
            IDtoService dtoService,
            ILoggerFactory loggerFactory)
            : base(applicationPaths, xmlSerializer)
        {
            Instance = this;
            ServiceProvider = serviceProvider;
            AppPaths = applicationPaths;
            _logger = logger;

            ModLoader = new ServerModLoader(
                libraryManager,
                userDataManager,
                userManager,
                sessionManager,
                subtitleManager,
                mediaEncoder,
                playlistManager,
                dtoService,
                applicationPaths,
                loggerFactory.CreateLogger<ServerModLoader>());

            MigrateConfig();
            ConfigurationChanged += OnConfigurationChanged;
            _ = InitServerModsAsync();
        }

        // Snapshot of the previous config state used to diff on each change.
        private string _prevCachedMods = string.Empty;
        private string _prevModVars    = "{}";
        private List<string> _prevEnabledMods = new List<string>();

        private void OnConfigurationChanged(object sender, BasePluginConfiguration baseConfig)
        {
            var config = (PluginConfiguration)baseConfig;
            var paths  = AppPaths;

            // ── Determine which mods need cache invalidation ──────────────────

            // Parse current and previous mod lists so we can compare per-mod.
            var prevMods = ParseModList(_prevCachedMods);
            var currMods = ParseModList(config.CachedMods);

            var prevVars = ParseModVars(_prevModVars);
            var currVars = ParseModVars(config.ModVars);

            var prevEnabled = new HashSet<string>(_prevEnabledMods ?? new List<string>(),
                StringComparer.OrdinalIgnoreCase);
            var currEnabled = new HashSet<string>(config.EnabledMods ?? new List<string>(),
                StringComparer.OrdinalIgnoreCase);

            foreach (var mod in currMods.Values)
            {
                bool wasEnabled = prevEnabled.Contains(mod.Id);
                bool isEnabled  = currEnabled.Contains(mod.Id);

                // Version changed → full invalidation (re-download + re-compile).
                prevMods.TryGetValue(mod.Id, out var prevMod);
                bool versionChanged = prevMod == null ||
                    !string.Equals(prevMod.Version, mod.Version, StringComparison.Ordinal);

                if (versionChanged)
                {
                    _logger.LogInformation(
                        "[ModManager] Version changed for '{Id}' ({Prev} → {Curr}) — invalidating cache",
                        mod.Id, prevMod?.Version ?? "new", mod.Version);
                    ModResourceCache.InvalidateMod(mod.Id, paths);
                    continue; // full invalidation covers vars too
                }

                // Vars changed → invalidate only compiled output, keep raw download.
                currVars.TryGetValue(mod.Id, out var currModVars);
                prevVars.TryGetValue(mod.Id, out var prevModVars);
                if (VarsChanged(prevModVars, currModVars))
                {
                    _logger.LogInformation(
                        "[ModManager] Vars changed for '{Id}' — invalidating compiled cache", mod.Id);
                    ModResourceCache.InvalidateCompiled(mod.Id, paths);
                }

                // Toggled on → invalidate compiled output so it re-compiles with
                // current vars (in case vars changed while it was disabled).
                if (!wasEnabled && isEnabled)
                {
                    _logger.LogInformation(
                        "[ModManager] Mod '{Id}' enabled — invalidating compiled cache", mod.Id);
                    ModResourceCache.InvalidateCompiled(mod.Id, paths);
                }

                // Toggled off → invalidate compiled output so stale compiled files
                // don't persist on disk indefinitely after a mod is removed.
                if (wasEnabled && !isEnabled)
                {
                    _logger.LogInformation(
                        "[ModManager] Mod '{Id}' disabled — invalidating compiled cache", mod.Id);
                    ModResourceCache.InvalidateCompiled(mod.Id, paths);
                }
            }

            // Save snapshots for the next diff.
            _prevCachedMods  = config.CachedMods  ?? string.Empty;
            _prevModVars     = config.ModVars      ?? "{}";
            _prevEnabledMods = config.EnabledMods  ?? new List<string>();

            _ = ReloadServerModsAsync();
        }

        // ── Diff helpers ──────────────────────────────────────────────────────

        private Dictionary<string, ModEntry> ParseModList(string json)
        {
            var result = new Dictionary<string, ModEntry>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(json)) return result;
            try
            {
                var list = JsonSerializer.Deserialize<List<ModEntry>>(json, _jsonOpts);
                if (list != null)
                    foreach (var m in list)
                        if (!string.IsNullOrEmpty(m.Id))
                            result[m.Id] = m;
            }
            catch { }
            return result;
        }

        private Dictionary<string, Dictionary<string, string>> ParseModVars(string json)
        {
            if (string.IsNullOrWhiteSpace(json) || json == "{}")
                return new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            try
            {
                return JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(json, _jsonOpts)
                    ?? new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            }
            catch
            {
                return new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            }
        }

        private static bool VarsChanged(
            Dictionary<string, string> prev,
            Dictionary<string, string> curr)
        {
            prev ??= new Dictionary<string, string>();
            curr ??= new Dictionary<string, string>();
            if (prev.Count != curr.Count) return true;
            foreach (var kv in curr)
            {
                if (!prev.TryGetValue(kv.Key, out var prevVal)) return true;
                if (!string.Equals(prevVal, kv.Value, StringComparison.Ordinal)) return true;
            }
            return false;
        }

        private async Task InitServerModsAsync()
        {
            try
            {
                await LoadServerModsFromConfig(forceReload: false);

                // Start the hot-reload watcher after mods are up.
                // The callback receives the safeId-form modId from the filename
                // and must map it back to the real mod entry + vars to reload.
                var cacheDir = System.IO.Path.Combine(AppPaths.DataPath, "ModManager", "cache");
                ModLoader.StartWatcher(cacheDir, HotReloadModAsync);
            }
            catch (Exception ex) { _logger.LogError(ex, "[ModManager] Startup mod load failed"); }
        }

        /// <summary>
        /// Called by the file watcher when a serverjs cache file changes.
        /// The safeModId comes from the filename (underscores replacing unsafe chars).
        /// We scan the current enabled mods to find the matching entry by comparing
        /// the safe form of each mod's real ID.
        /// </summary>
        private async Task HotReloadModAsync(string safeModId)
        {
            var config = Configuration;
            if (string.IsNullOrWhiteSpace(config.CachedMods)) return;

            List<ModEntry> mods;
            try
            {
                mods = JsonSerializer.Deserialize<List<ModEntry>>(config.CachedMods, _jsonOpts)
                       ?? new List<ModEntry>();
            }
            catch { return; }

            // Find the mod whose safe ID matches the filename segment.
            ModEntry target = null;
            foreach (var m in mods)
            {
                if (string.IsNullOrEmpty(m.ServerJs)) continue;
                var safe = System.Text.RegularExpressions.Regex.Replace(
                    m.Id ?? "", @"[^\w\-.]", "_");
                if (string.Equals(safe, safeModId, StringComparison.OrdinalIgnoreCase))
                {
                    target = m;
                    break;
                }
            }

            if (target == null)
            {
                _logger.LogWarning("[ModManager] Hot-reload: no mod found for safe ID '{Safe}'", safeModId);
                return;
            }

            // Only reload if the mod is currently enabled.
            var enabled = new System.Collections.Generic.HashSet<string>(
                config.EnabledMods ?? new List<string>(),
                StringComparer.OrdinalIgnoreCase);
            if (!enabled.Contains(target.Id)) return;

            Dictionary<string, Dictionary<string, string>> modVars;
            try
            {
                modVars = string.IsNullOrWhiteSpace(config.ModVars) || config.ModVars == "{}"
                    ? new Dictionary<string, Dictionary<string, string>>()
                    : JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(
                        config.ModVars, _jsonOpts)
                      ?? new Dictionary<string, Dictionary<string, string>>();
            }
            catch { modVars = new Dictionary<string, Dictionary<string, string>>(); }

            _logger.LogInformation("[ModManager] Hot-reloading mod '{Id}'", target.Id);
            await ModLoader.ReloadModAsync(target, modVars);
        }

        private async Task ReloadServerModsAsync()
        {
            try { await LoadServerModsFromConfig(forceReload: true); }
            catch (Exception ex) { _logger.LogError(ex, "[ModManager] Config reload failed"); }
        }

        private async Task LoadServerModsFromConfig(bool forceReload)
        {
            var config = Configuration;
            if (string.IsNullOrWhiteSpace(config.CachedMods)) return;

            List<ModEntry> mods;
            try
            {
                mods = JsonSerializer.Deserialize<List<ModEntry>>(config.CachedMods, _jsonOpts)
                       ?? new List<ModEntry>();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[ModManager] Failed to deserialize CachedMods");
                return;
            }
            Dictionary<string, Dictionary<string, string>> modVars;
            try
            {
                modVars = string.IsNullOrWhiteSpace(config.ModVars) || config.ModVars == "{}"
                    ? new Dictionary<string, Dictionary<string, string>>()
                    : JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(
                        config.ModVars, _jsonOpts)
                      ?? new Dictionary<string, Dictionary<string, string>>();
            }
            catch { modVars = new Dictionary<string, Dictionary<string, string>>(); }

            await ModLoader.LoadModsAsync(
                mods,
                config.EnabledMods ?? new List<string>(),
                modVars,
                forceReload);
        }

        private void MigrateConfig()
        {
            bool dirty = false;
            for (int v = Configuration.ConfigVersion + 1; v <= CurrentConfigVersion; v++)
            {
                switch (v)
                {
                    case 1:
                        if (Configuration.EnabledMods == null)
                            Configuration.EnabledMods = new List<string>();
                        if (string.IsNullOrWhiteSpace(Configuration.CachedMods))
                            Configuration.CachedMods = string.Empty;
                        break;
                    case 2:
                        if (string.IsNullOrWhiteSpace(Configuration.ModVars))
                            Configuration.ModVars = "{}";
                        break;
                }
                Configuration.ConfigVersion = v;
                dirty = true;
            }
            if (dirty) SaveConfiguration();
        }

        public IEnumerable<PluginPageInfo> GetPages() => new[]
        {
            new PluginPageInfo
            {
                Name                 = this.Name,
                DisplayName          = "Mod Manager",
                EnableInMainMenu     = true,
                EmbeddedResourcePath = $"{GetType().Namespace}.Configuration.configPage.html"
            }
        };

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            ConfigurationChanged -= OnConfigurationChanged;
            ModLoader?.StopWatcher();
            ModLoader?.Dispose();
            _logger.LogInformation("[ModManager] Plugin disposed");
        }
    }
}

