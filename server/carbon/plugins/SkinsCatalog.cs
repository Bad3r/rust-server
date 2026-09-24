using System.Collections.Generic;
using Oxide.Core.Plugins;

namespace Oxide.Plugins
{
    [Info("Skins Catalog", "local", "1.0.0")]
    [Description("Feeds uMod Skins every ItemSkinDirectory and approved Steam Workshop skin id at runtime")]
    public class SkinsCatalog : RustPlugin
    {
        // Skins merges the ids OnSkinsFetch adds with its config list. Approved workshop skins come from the Steam
        // inventory definitions the server downloads at boot; their workshopid property is the value Item.skin takes.
        // Steamworks.SteamInventory stays qualified: a bare SteamInventory is Rust's per-player ownership component.
        [PluginReference]
        private Plugin Skins;

        private Dictionary<string, List<ulong>> _byShortname = new Dictionary<string, List<ulong>>();
        private bool _ready;
        private float _serverInitializedAt = -1f;

        private void Init()
        {
            // Fires once the definitions arrive on a cold cache, and again when Steam pushes a catalog update.
            Steamworks.SteamInventory.OnDefinitionsUpdated += BuildIndex;
        }

        private void OnServerInitialized()
        {
            _serverInitializedAt = UnityEngine.Time.realtimeSinceStartup;
            if (Steamworks.SteamInventory.Definitions != null)
                BuildIndex();
            else
                Steamworks.SteamInventory.LoadItemDefinitions();
        }

        private void Unload()
        {
            Steamworks.SteamInventory.OnDefinitionsUpdated -= BuildIndex;
        }

        private void BuildIndex()
        {
            var definitions = Steamworks.SteamInventory.Definitions;
            if (definitions == null)
                return;

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var map = new Dictionary<string, List<ulong>>();
            var unmapped = new HashSet<string>();
            var unmappedHits = 0;
            var steamSkins = 0;

            foreach (var def in ItemManager.itemList)
            {
                var skins = ItemSkinDirectory.ForItem(def);
                if (skins.Length == 0)
                    continue;

                List<ulong> list;
                if (!map.TryGetValue(def.shortname, out list))
                    map[def.shortname] = list = new List<ulong>();
                foreach (var skin in skins)
                {
                    var id = (ulong)skin.id;
                    if (!list.Contains(id))
                        list.Add(id);
                }
            }

            foreach (var def in definitions)
            {
                var itemShortName = def.GetProperty<string>("itemshortname");
                if (string.IsNullOrEmpty(itemShortName))
                    continue;

                var workshopId = def.GetProperty<ulong>("workshopid");
                if (workshopId == 0uL)
                    continue;

                var itemDef = ItemManager.FindItemDefinition(itemShortName);
                if (itemDef == null)
                {
                    unmappedHits++;
                    unmapped.Add(itemShortName);
                    continue;
                }

                List<ulong> list;
                if (!map.TryGetValue(itemDef.shortname, out list))
                    map[itemDef.shortname] = list = new List<ulong>();
                if (!list.Contains(workshopId))
                {
                    list.Add(workshopId);
                    steamSkins++;
                }
            }

            _byShortname = map;
            _ready = true;

            var totalSkins = 0;
            foreach (var kv in map)
                totalSkins += kv.Value.Count;
            var sinceStartup = _serverInitializedAt < 0f
                ? "unknown"
                : $"{UnityEngine.Time.realtimeSinceStartup - _serverInitializedAt:0.0}s after OnServerInitialized";
            Puts($"indexed {map.Count} items, {totalSkins} skins ({sw.ElapsedMilliseconds}ms build time, " +
                 $"{sinceStartup}); {unmappedHits} Steam definitions had an itemshortname not found by " +
                 $"ItemManager ({unmapped.Count} distinct: {string.Join(", ", unmapped)})");
            if (definitions.Length > 0 && steamSkins == 0)
                PrintWarning($"none of {definitions.Length} Steam definitions yielded a workshop skin; " +
                             "check the itemshortname and workshopid property names");

            // Skins caches each item's list per player on first open and never refetches it.
            if (Skins != null)
            {
                foreach (var player in BasePlayer.activePlayerList)
                    Skins.Call("PurgeCache", player.userID, null);
            }
        }

        private void OnSkinsFetch(BasePlayer player, ItemDefinition itemDefinition, List<ulong> skins)
        {
            if (!_ready || itemDefinition == null)
                return;

            List<ulong> list;
            if (_byShortname.TryGetValue(itemDefinition.shortname, out list))
                skins.AddRange(list);
        }
    }
}
