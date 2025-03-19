using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using ExileCore;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared.Enums;
using ExileCore.Shared.Helpers;
using ImGuiNET;
using SharpDX;
using Vector2 = System.Numerics.Vector2;

namespace ProximityAlert
{
    public class Proximity : BaseSettingsPlugin<ProximitySettings>
    {
        private const string PathAlertsFileName = "PathAlerts.txt";
        private const string ModAlertsFileName = "ModAlerts.txt";
        private const string ProximityAlertFolderName = "ProximityAlert";
        private Dictionary<string, Warning> _pathDict = new Dictionary<string, Warning>();
        private Dictionary<string, Warning> _modDict = new Dictionary<string, Warning>();
        private Dictionary<string, Warning> _beastDict = new Dictionary<string, Warning>();
        private string _soundDir;
        private DateTime _lastPlayed;
        private readonly object _locker = new object();
        private readonly ConcurrentQueue<Entity> _entityAddedQueue = new ConcurrentQueue<Entity>();

        private string ConfigPathAlertsPath => Path.Combine("config", ProximityAlertFolderName, PathAlertsFileName);
        private string DefaultModAlertPath => Path.Combine(DirectoryFullName, ModAlertsFileName);
        private string ConfigModAlertPath => Path.Combine("config", ProximityAlertFolderName, ModAlertsFileName);
        private string DefaultPathAlertsPath => Path.Combine(DirectoryFullName, PathAlertsFileName);
        private string ConfigBeastAlertsPath => Path.Combine("config", ProximityAlertFolderName, "BeastAlerts.txt");
        private string DefaultBeastAlertsPath => Path.Combine(DirectoryFullName, "BeastAlerts.txt");

        public override bool Initialise()
        {
            base.Initialise();
            Name = "Proximity Alerts";
            Graphics.InitImage(Path.Combine(DirectoryFullName, "textures\\Direction-Arrow.png").Replace('\\', '/'), false);
            Graphics.InitImage(Path.Combine(DirectoryFullName, "textures\\back.png").Replace('\\', '/'), false);
            lock (_locker) _soundDir = Path.Combine(DirectoryFullName, "sounds\\").Replace('\\', '/');
            LoadAlertConfigs();
            Settings.Reload.OnPressed = () =>
            {
                LogMessage("Reloading ModAlerts, PathAlerts, & BeastAlerts.txt...");
                LoadAlertConfigs();
            };
            Settings.CopyDefaultConfigsToConfigFolder.OnPressed = CopyDefaultConfigs;
            return true;
        }

        private void LoadAlertConfigs()
        {
            _pathDict = LoadConfig(ConfigPathAlertsPath, true) ?? 
                        LoadConfig(DefaultPathAlertsPath, false) ??
                        _pathDict ?? 
                        new Dictionary<string, Warning>();
            _modDict = LoadConfig(ConfigModAlertPath, true) ??
                       LoadConfig(DefaultModAlertPath, false) ??
                       _modDict ??
                       new Dictionary<string, Warning>();
            _beastDict = LoadConfig(ConfigBeastAlertsPath, true) ??
                         LoadConfig(DefaultBeastAlertsPath, false) ??
                         _beastDict ??
                         new Dictionary<string, Warning>();
        }

        private void CopyDefaultConfigs()
        {
            if (File.Exists(ConfigPathAlertsPath))
            {
                LogError($"Custom config for path alerts already exists at {ConfigPathAlertsPath}");
            }
            else
            {
                new FileInfo(ConfigPathAlertsPath).Directory?.Create();
                File.Copy(DefaultPathAlertsPath, ConfigPathAlertsPath);
            }

            if (File.Exists(ConfigModAlertPath))
            {
                new FileInfo(ConfigModAlertPath).Directory?.Create();
                LogError($"Custom config for mod alerts already exists at {ConfigModAlertPath}");
            }
            else
            {
                File.Copy(DefaultModAlertPath, ConfigModAlertPath);
            }

            if (File.Exists(ConfigBeastAlertsPath))
            {
                new FileInfo(ConfigBeastAlertsPath).Directory?.Create();
                LogError($"Custom config for beast alerts already exists at {ConfigBeastAlertsPath}");
            }
            else
            {
                File.Copy(DefaultBeastAlertsPath, ConfigBeastAlertsPath);
            }
        }

        private static RectangleF Get64DirectionsUV(double phi, double distance, int rows)
        {
            phi += Math.PI * 0.25; // fix rotation due to projection
            if (phi > 2 * Math.PI) phi -= 2 * Math.PI;

            var xSprite = (float) Math.Round(phi / Math.PI * 32);
            if (xSprite >= 64) xSprite = 0;

            float ySprite = distance > 60 ? distance > 120 ? 2 : 1 : 0;
            var x = xSprite / 64;
            float y = 0;
            if (rows > 0)
            {
                y = ySprite / rows;
                return new RectangleF(x, y, (xSprite + 1) / 64 - x, (ySprite + 1) / rows - y);
            }

            return new RectangleF(x, y, (xSprite + 1) / 64 - x, 1);
        }

        private static Color HexToColor(string value)
        {
            uint.TryParse(value, NumberStyles.HexNumber, null, out var abgr);
            return Color.FromAbgr(abgr);
        }

        private Dictionary<string, Warning> LoadConfig(string path, bool noErrorOnNoFile)
        {
            try
            {
                if (noErrorOnNoFile && !File.Exists(path))
                {
                    return null;
                }

                return File.ReadAllLines(path)
                    .Select(x => x.Trim())
                    .Where(line => !string.IsNullOrWhiteSpace(line)
                                   && line.Contains(';')
                                   && !line.StartsWith("#"))
                    .Select(line => line.Split(new[] { ';' }, 5).Select(parts => parts.Trim()).ToArray())
                    .ToDictionary(line => line[0], line =>
                    {
                        var preloadAlertConfigLine = new Warning
                        {
                            Text = line[1],
                            Color = HexToColor(line[2]),
                            Distance = int.TryParse(line[3], out var tmp) ? tmp : -1,
                            SoundFile = line[4]
                        };
                        return preloadAlertConfigLine;
                    });
            }
            catch (Exception ex)
            {
                LogError($"Unable to load config file {path}: {ex}");
                return null;
            }
        }

        public override void EntityAdded(Entity entity)
        {
            if (!Settings.Enable.Value) return;
            if (entity.Type == EntityType.Monster) _entityAddedQueue.Enqueue(entity);
        }

        public override void AreaChange(AreaInstance area)
        {
            try
            {
                _entityAddedQueue.Clear();
            }
            catch
            {
                // ignored
            }
        }

        public override Job Tick()
        {
            if (Settings.EnableMultithreading)
                return GameController.MultiThreadManager.AddJob(TickLogic, nameof(Proximity));
            TickLogic();
            return null;
        }

        private void TickLogic()
        {
            while (_entityAddedQueue.TryDequeue(out var entity))
            {
                if (entity.IsValid && !entity.IsAlive) continue;
                if (!entity.IsHostile || !entity.IsValid) continue;
                if (!Settings.ShowModAlerts) continue;
                try
                {
                    if (entity.TryGetComponent<ObjectMagicProperties>(out var omp) && entity.IsAlive && 
                        omp.Mods?.Select(x => _modDict.GetValueOrDefault(x)).Any(x => x != null) == true)
                    {
                        entity.SetHudComponent(new ProximityAlert(this, entity));
                    }
                }
                catch
                {
                    // ignored
                }
            }

            // Update valid
            foreach (var entity in GameController.EntityListWrapper.ValidEntitiesByType[EntityType.Monster])
            {
                var drawCmd = entity.GetHudComponent<ProximityAlert>();
                drawCmd?.Update(_modDict);
            }
        }

        private void PlaySound(string path)
        {
            lock (_locker)
            {
                if (!Settings.PlaySoundsForAlerts) return;
                // Sanity Check because I'm too lazy to make a queue
                if ((DateTime.Now - _lastPlayed).TotalMilliseconds > 250)
                {
                    if (path != string.Empty) GameController.SoundController.PlaySound(Path.Combine(_soundDir, path).Replace('\\', '/'));
                    _lastPlayed = DateTime.Now;
                }
            }
        }

        public override void Render()
        {
            try
            {
                var height = ImGui.GetTextLineHeight() * Settings.Scale;
                var margin = height / Settings.Scale / 4;

                if (!Settings.Enable) return;

                if (Settings.DrawALineToRealSirus)
                    foreach (var sEnt in GameController.EntityListWrapper.ValidEntitiesByType[EntityType.Monster]
                        .Where(x => x.Metadata.Equals("Metadata/Monsters/AtlasExiles/AtlasExile5")))
                    {
                        if (sEnt.Path.Contains("Throne") || sEnt.Path.Contains("Apparation")) break;
                        if (sEnt.DistancePlayer > 200) break;
                        var entityScreenPos = GameController.Game.IngameState.Camera.WorldToScreen(sEnt.Pos.Translate(0, 0, 0));
                        var playerPosition = GameController.Game.IngameState.Camera.WorldToScreen(GameController.Player.Pos);
                        Graphics.DrawLine(playerPosition, entityScreenPos, 4, new Color(255, 0, 255, 140));

                        Graphics.DrawText(sEnt.DistancePlayer.ToString(CultureInfo.InvariantCulture), new SharpDX.Vector2(0, 0));
                    }

                var unopened = "";
                var notifiedMods = new HashSet<string>();
                var lines = 0;

                var origin = (GameController.Window.GetWindowRectangleTimeCache with { Location = SharpDX.Vector2.Zero }).Center
                    .Translate(Settings.AlertPositionOffset.Value.X - 96, Settings.AlertPositionOffset.Value.Y);

                // entities
                foreach (var entity in GameController.EntityListWrapper.Entities
                    .Where(x => x.Type == EntityType.Chest ||
                                x.Type == EntityType.Monster ||
                                x.Type == EntityType.IngameIcon ||
                                x.Type == EntityType.MiscellaneousObjects)
                    .Where(entity => !entity.HasComponent<Chest>() || !entity.IsOpened)
                    .Where(entity => !entity.HasComponent<Monster>() || (entity.IsAlive && entity.IsValid))
                    .OrderBy(x => x.DistancePlayer))
                {
                    var match = false;
                    var lineColor = Color.White;
                    var lineText = "";
                    var soundStatus = entity.GetHudComponent<SoundStatus>();
                    soundStatus?.ResetIfInvalid();
                    if (entity.Type == EntityType.IngameIcon &&
                        (!entity.IsValid || (entity.GetComponent<MinimapIcon>()?.IsHide ?? true))) continue;
                    var delta = entity.GridPos - GameController.Player.GridPos;
                    var distance = delta.GetPolarCoordinates(out var phi);

                    var rectDirection = new RectangleF(origin.X - margin - height / 2,
                        origin.Y - margin / 2 - height - lines * height, height, height);
                    var rectUV = Get64DirectionsUV(phi, distance, 3);
                    var ePath = entity.Path;
                    if (ePath.Contains("@")) ePath = ePath.Split('@')[0];
                    var structValue = entity.GetHudComponent<ProximityAlert>();
                    if (structValue != null)
                    {
                        if (structValue.Warnings.Any())
                        {
                            match = true;
                        }

                        foreach (var warning in structValue.Warnings.Where(x => notifiedMods.Add(x.Text)))
                        {
                            lines++;
                            Graphics.DrawText(warning.Text, new Vector2(origin.X + height / 2, origin.Y - lines * height), warning.Color);
                            Graphics.DrawImage("Direction-Arrow.png", rectDirection, rectUV, warning.Color);
                        }
                    }

                    void ProcessFilters(Dictionary<string, Warning> filterDict, Func<Entity, string> keySelector)
                    {
                        foreach (var filterEntry in filterDict.Where(x => keySelector(entity).Contains(x.Key)).Take(1))
                        {
                            var filter = filterEntry.Value;
                            unopened = $"{filter.Text}\n{unopened}";

                            if (filter.Distance == -1 || (filter.Distance == -2 && entity.IsValid) || distance < filter.Distance)
                            {
                                if (soundStatus == null)
                                {
                                    soundStatus = new SoundStatus(this, entity, filter.SoundFile);
                                    entity.SetHudComponent(soundStatus);
                                }

                                soundStatus.PlaySoundOnce();

                                lineText = filter.Text;
                                lineColor = filter.Color;
                                match = true;
                                lines++;
                                break;
                            }
                        }
                    }

                    // Process Path Filters
                    if (Settings.ShowPathAlerts && !match)
                    {
                        ProcessFilters(_pathDict, e => e.Path);
                    }

                    // Process Beast Filters
                    if (Settings.ShowBeastAlerts && !match)
                    {
                        ProcessFilters(_beastDict, e => e.RenderName);
                    }

                    // Hardcoded Chests
                    if (!match && entity.HasComponent<Chest>() && ePath.Contains("Delve"))
                    {
                        var chestName = Regex.Replace(Path.GetFileName(ePath),
                                @"((?<=\p{Ll})\p{Lu})|((?!\A)\p{Lu}(?>\p{Ll}))", " $0")
                            .Replace("Delve Chest ", string.Empty)
                            .Replace("Delve Azurite ", "Azurite ")
                            .Replace("Delve Mining Supplies ", string.Empty)
                            .Replace("_", string.Empty);
                        if (chestName.EndsWith(" Encounter") || chestName.EndsWith(" No Drops")) continue;
                        if (distance > 100 &&
                            (chestName.Contains("Generic")
                             || chestName.Contains("Vein")
                             || chestName.Contains("Flare")
                             || chestName.Contains("Dynamite")
                             || chestName.Contains("Armour")
                             || chestName.Contains("Weapon")) &&
                            (chestName.Contains("Path ") ||
                             !chestName.Contains("Currency")))
                            continue;
                        if (chestName.Contains("Currency") || chestName.Contains("Fossil"))
                            lineColor = new Color(255, 0, 255);
                        if (chestName.Contains("Flares")) lineColor = new Color(0, 200, 255);
                        if (chestName.Contains("Dynamite") || chestName.Contains("Explosives"))
                            lineColor = new Color(255, 50, 50);
                        lineText = chestName;
                        lines++;
                        match = true;
                    }

                    if (match)
                    {
                        Graphics.DrawText(lineText, new Vector2(origin.X + height / 2, origin.Y - lines * height), lineColor);
                        // Graphics.DrawText(lineText, new System.Numerics.Vector2(origin.X + 4, origin.Y - (lines * 15)), lineColor, 10, "FrizQuadrataITC:15", FontAlign.Left);
                        Graphics.DrawImage("Direction-Arrow.png", rectDirection, rectUV, lineColor);
                    }
                }

                if (lines > 0)
                {
                    var widthMultiplier = 1 + height / 100;

                    var box = new RectangleF(origin.X - 2, origin.Y - margin - lines * height,
                        (192 + 4) * widthMultiplier, margin + lines * height + 4);
                    Graphics.DrawLine(new SharpDX.Vector2(origin.X - 15, origin.Y - margin - lines * height),
                        new SharpDX.Vector2(origin.X + (192 + 4) * widthMultiplier,
                            origin.Y - margin - lines * height), 1, Color.White);
                    Graphics.DrawLine(new SharpDX.Vector2(origin.X - 15, origin.Y + 3),
                        new SharpDX.Vector2(origin.X + (192 + 4) * widthMultiplier, origin.Y + 3), 1, Color.White);
                }
            }
            catch
            {
                // ignored
            }
        }

        private class Warning
        {
            public string Text { get; set; }
            public Color Color { get; set; }
            public int Distance { get; set; }
            public string SoundFile { get; set; }
        }

        private record SoundStatus(Proximity Plugin, Entity Entity, string Sound)
        {
            private bool Played { get; set; }

            public void ResetIfInvalid()
            {
                if (Played && !Entity.IsValid) Played = false;
            }

            public void PlaySoundOnce()
            {
                if (!Played && Entity.IsValid)
                {
                    Plugin.PlaySound(Sound);
                    Played = true;
                }
            }
        }

        private record ProximityAlert(Proximity Plugin, Entity Entity)
        {
            public HashSet<Warning> Warnings { get; } = new HashSet<Warning>();
            private bool PlayWarning { get; set; } = true;

            public void Update(IReadOnlyDictionary<string, Warning> modDict)
            {
                if (!Entity.IsValid) PlayWarning = true;
                if (!Entity.HasComponent<ObjectMagicProperties>() || !Entity.IsAlive) return;
                var mods = Entity.GetComponent<ObjectMagicProperties>()?.Mods;
                if (mods is { Count: > 0 })
                {
                    var filter = mods.Select(modDict.GetValueOrDefault).Where(x => x != null).ToList();
                    if (filter.Any())
                    {
                        Warnings.Clear();
                    }

                    foreach (var warning in filter)
                    {
                        Warnings.Add(warning);
                        if (PlayWarning)
                        {
                            Plugin.PlaySound(warning.SoundFile);
                            PlayWarning = false;
                        }
                    }
                }
            }
        }
    }
}