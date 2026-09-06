using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using XrayUI.Helpers;
using XrayUI.Models;

namespace XrayUI.Services
{
    public class SettingsService
    {
        private readonly string DataDir;

        private readonly string SettingsFile;
        private readonly string ServersFile;

        private static readonly System.Threading.SemaphoreSlim SettingsGate = new(1, 1);
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<AppSettings, System.Text.Json.Nodes.JsonObject> Baselines = new();

        public SettingsService() : this(AppPaths.LocalAppDataDir) { }

        internal SettingsService(string dataDirectory)
        {
            DataDir = dataDirectory;
            SettingsFile = Path.Combine(DataDir, "settings.json");
            ServersFile = Path.Combine(DataDir, "servers.json");
            Directory.CreateDirectory(DataDir);
        }

        /// <summary>Compatibility hook: every read now uses the current file.</summary>
        public void InvalidateCache() { } // Reads always return fresh snapshots.

        /// <summary>Read a fresh settings snapshot.</summary>
        public async Task<AppSettings> ReloadAsync()
        {
            InvalidateCache();
            return await LoadSettingsAsync().ConfigureAwait(false);
        }

        /// <summary>
        /// Shell-open settings.json in the user's default .json editor. Subsequent reads see its changes.
        /// Throws if the OS reports no association for .json.
        /// </summary>
        public void OpenInExternalEditor()
        {
            InvalidateCache();
            Process.Start(new ProcessStartInfo
            {
                FileName = SettingsFile,
                UseShellExecute = true,
            });
        }

        // ── AppSettings ───────────────────────────────────────────────────────

        public async Task<AppSettings> LoadSettingsAsync()
        {
            await SettingsGate.WaitAsync().ConfigureAwait(false);
            try
            {
                var settings = await ReadSettingsCoreAsync().ConfigureAwait(false);
                Baselines.Add(settings, ToNode(settings));
                return settings;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SettingsService] Failed to load settings: {ex.Message}");
                var settings = new AppSettings();
                Baselines.Add(settings, ToNode(settings));
                return settings;
            }
            finally { SettingsGate.Release(); }
        }

        private static System.Text.Json.Nodes.JsonObject ToNode(AppSettings settings) =>
            (System.Text.Json.Nodes.JsonObject)JsonSerializer.SerializeToNode(settings, AppJsonSerializerContext.Default.AppSettings)!;

        private async Task<AppSettings> ReadSettingsCoreAsync()
        {
            var settings = File.Exists(SettingsFile)
                ? JsonSerializer.Deserialize(await File.ReadAllTextAsync(SettingsFile).ConfigureAwait(false), AppJsonSerializerContext.Default.AppSettings) ?? new AppSettings()
                : new AppSettings { RoutingRegion = InferDefaultRoutingRegion() };
            settings.XrayLogLevel ??= settings.VerboseXrayLog ? XrayLogLevel.Info : XrayLogLevel.Warning;
            return settings;
        }

        /// <summary>
        /// First-run default for <see cref="AppSettings.RoutingRegion"/>. Deliberately narrow:
        /// only an explicit RU/IR Windows home region deviates from "cn" — geosite ships domestic
        /// lists for cn/ru/ir only, and system locale/region is a weak signal for everyone else
        /// (many zh users run en-US Windows or set region to US on VPS/VMs).
        /// </summary>
        private static string InferDefaultRoutingRegion()
        {
            try
            {
                var region = Windows.System.UserProfile.GlobalizationPreferences.HomeGeographicRegion;
                if (string.Equals(region, "RU", StringComparison.OrdinalIgnoreCase)) return "ru";
                if (string.Equals(region, "IR", StringComparison.OrdinalIgnoreCase)) return "ir";
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SettingsService] Region inference failed: {ex.Message}");
            }
            return "cn";
        }

        public async Task SaveSettingsAsync(AppSettings settings)
        {
            await SettingsGate.WaitAsync().ConfigureAwait(false);
            try
            {
                var edited = ToNode(settings);
                var current = ToNode(await ReadSettingsCoreAsync().ConfigureAwait(false));
                var merged = Baselines.TryGetValue(settings, out var baseline)
                    ? SettingsSnapshotMerge.Apply(current, baseline, edited) : edited;
                await WriteAtomicAsync(SettingsFile, merged.ToJsonString()).ConfigureAwait(false);
                Baselines.Remove(settings);
                Baselines.Add(settings, edited);
            }
            finally { SettingsGate.Release(); }
        }

        public async Task UpdateSettingsAsync(Action<AppSettings> update)
        {
            await SettingsGate.WaitAsync().ConfigureAwait(false);
            try
            {
                var settings = await ReadSettingsCoreAsync().ConfigureAwait(false);
                update(settings);
                await WriteAtomicAsync(SettingsFile,
                    JsonSerializer.Serialize(settings, AppJsonSerializerContext.Readable<AppSettings>())).ConfigureAwait(false);
            }
            finally { SettingsGate.Release(); }
        }

        // ── Server list ───────────────────────────────────────────────────────

        public async Task<List<ServerEntry>> LoadServersAsync()
        {
            try
            {
                if (!File.Exists(ServersFile))
                    return new List<ServerEntry>();

                var json = await File.ReadAllTextAsync(ServersFile).ConfigureAwait(false);
                var list = JsonSerializer.Deserialize(json, AppJsonSerializerContext.Default.ListServerEntry)
                           ?? [];

                // Persist once if legacy JSON has no Id keys, so field-initializer-generated
                // Ids don't regenerate on every launch and break LastAutoConnectServerId.
                if (list.Count > 0 && !json.Contains("\"Id\":", StringComparison.Ordinal))
                    await SaveServersAsync(list).ConfigureAwait(false);

                return list;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SettingsService] Failed to load servers: {ex.Message}");
                return [];
            }
        }

        public async Task SaveServersAsync(IEnumerable<ServerEntry> servers)
        {
            var serverList = servers as List<ServerEntry> ?? servers.ToList();
            var json = JsonSerializer.Serialize(serverList, AppJsonSerializerContext.Readable<List<ServerEntry>>());
            await WriteAtomicAsync(ServersFile, json).ConfigureAwait(false);
        }

        // Write-to-temp + atomic swap: a crash or power cut mid-save can never leave a
        // truncated settings/servers file — the previous complete file survives until the
        // replace commits. Temp name is per-call (Guid-suffixed) so concurrent saves of the
        // same file (e.g. two VMs persisting settings.json close together) never write over
        // each other's temp file or race on the replace.
        private static async Task WriteAtomicAsync(string path, string contents)
        {
            var tmp = $"{path}.{Guid.NewGuid():N}.tmp";
            try
            {
                await File.WriteAllTextAsync(tmp, contents).ConfigureAwait(false);

                if (File.Exists(path))
                    File.Replace(tmp, path, destinationBackupFileName: null, ignoreMetadataErrors: true);
                else
                    File.Move(tmp, path);
            }
            catch
            {
                try { File.Delete(tmp); } catch { }
                throw;
            }
        }
    }
}
