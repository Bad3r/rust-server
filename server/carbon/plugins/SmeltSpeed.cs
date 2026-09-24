using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace Oxide.Plugins
{
    [Info("Smelt Speed", "local", "1.0.0")]
    [Description("Multiplies the vanilla smeltSpeed of furnaces and refineries")]
    public class SmeltSpeed : RustPlugin
    {
        // Furnaces and refineries cook at 1000-1500; campfires, BBQs, and stoves at 200; lights and the composter below.
        private const float SmeltingTemperature = 1000f;

        private Configuration _config;

        private class Configuration
        {
            // BaseOven.smeltSpeed scales cook progress only, so fuel and charcoal per smelted item drop by the same factor.
            [JsonProperty("Multiplier for ovens cooking at 1000 or hotter (furnaces, refineries)")]
            public int SmeltingMultiplier = 2;

            [JsonProperty("Multiplier overrides by oven prefab short name")]
            public Dictionary<string, int> Overrides = new Dictionary<string, int>();
        }

        protected override void LoadDefaultConfig() => _config = new Configuration();

        protected override void LoadConfig()
        {
            base.LoadConfig();
            _config = Config.ReadObject<Configuration>();
            if (_config?.Overrides == null)
                throw new Exception($"{Name}.json is missing its multiplier settings");
            SaveConfig();
        }

        protected override void SaveConfig() => Config.WriteObject(_config);

        private void OnServerInitialized() => ApplyAll(true);

        private void OnEntitySpawned(BaseOven oven) => Apply(oven, true);

        private void Unload() => ApplyAll(false);

        private void ApplyAll(bool enable)
        {
            foreach (var entity in BaseNetworkable.serverEntities)
            {
                var oven = entity as BaseOven;
                if (oven != null)
                    Apply(oven, enable);
            }
        }

        // Entity saves do not store smeltSpeed, so the prefab value is the vanilla base on every call.
        private void Apply(BaseOven oven, bool enable)
        {
            var prefab = GameManager.server.FindPrefab(oven.PrefabName)?.GetComponent<BaseOven>();
            if (prefab == null)
                return;
            int multiplier;
            if (!_config.Overrides.TryGetValue(oven.ShortPrefabName, out multiplier))
                multiplier = prefab.cookingTemperature >= SmeltingTemperature ? _config.SmeltingMultiplier : 1;
            oven.smeltSpeed = prefab.smeltSpeed * (enable ? Math.Max(multiplier, 1) : 1);
        }
    }
}
