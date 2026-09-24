using System.Collections.Generic;
using System.Linq;
using Oxide.Core.Plugins;

namespace Oxide.Plugins
{
    [Info("Skins Catalog", "local", "1.0.2")]
    [Description("Feeds uMod Skins every ItemSkinDirectory and approved Steam Workshop skin id at runtime")]
    public class SkinsCatalog : RustPlugin
    {
        // Skins merges the ids OnSkinsFetch adds with its config list. Approved workshop skins come from the Steam
        // inventory definitions the server downloads at boot. As in ItemDefinition.FindSkin, Item.skin takes a
        // definition's workshopdownload, or its ItemSkinDirectory id when it has none. FindSkin itself reads a
        // PlatformService cache whose OnDefinitionsUpdated handler subscribes after this plugin's on a cold boot.
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
            // Steam keys some workshop skins by the item's prefab name (lr300.item for rifle.lr300).
            // ItemDefinition.skins2 matches either name; ItemManager.FindItemDefinition knows only shortnames.
            var byPrefabName = new Dictionary<string, ItemDefinition>();
            var unmapped = new HashSet<string>();
            var unmappedHits = 0;
            var steamSkins = 0;
            var builtIn = 0;
            var unresolved = new List<int>();

            foreach (var def in ItemManager.itemList)
            {
                if (!byPrefabName.ContainsKey(def.name))
                    byPrefabName[def.name] = def;

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

                // ItemDefinition.skins2, the item's list of Steam skins, takes the definitions with a workshopid.
                if (def.GetProperty<ulong>("workshopid") == 0uL)
                    continue;

                var itemDef = ItemManager.FindItemDefinition(itemShortName);
                if (itemDef == null && !byPrefabName.TryGetValue(itemShortName, out itemDef))
                {
                    unmappedHits++;
                    unmapped.Add(itemShortName);
                    continue;
                }

                List<ulong> list;
                map.TryGetValue(itemDef.shortname, out list);
                var skinId = def.GetProperty<ulong>("workshopdownload");
                if (skinId == 0uL)
                {
                    // Legacy definitions (itemdefid 10032 to 10143), whose skin is the ItemSkinDirectory id ForItem
                    // added above. Their workshopid is an old upload that vanilla never applies; the 13 the client
                    // downloaded here (up to 191 MB each) had no manifest.txt and never loaded.
                    if (list != null && list.Contains((ulong)def.Id))
                        builtIn++;
                    else
                        unresolved.Add(def.Id);
                    continue;
                }

                if (list == null)
                    map[itemDef.shortname] = list = new List<ulong>();
                if (!list.Contains(skinId))
                {
                    list.Add(skinId);
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
                 $"{sinceStartup}); {builtIn} definitions without a workshopdownload use their ItemSkinDirectory id; " +
                 $"{unmappedHits} Steam definitions matched no item shortname or prefab name " +
                 $"({unmapped.Count} distinct: {string.Join(", ", unmapped)})");
            if (unresolved.Count > 0)
                PrintWarning($"skipped {unresolved.Count} definitions with neither a workshopdownload nor an " +
                             $"ItemSkinDirectory entry for their item: {string.Join(", ", unresolved.Take(20))}");
            if (definitions.Length > 0 && steamSkins == 0)
                PrintWarning($"none of {definitions.Length} Steam definitions yielded a workshop skin; " +
                             "check the itemshortname, workshopid, and workshopdownload property names");

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
