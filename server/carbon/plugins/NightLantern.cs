using HarmonyLib;
using Newtonsoft.Json;
using Oxide.Core;
using Oxide.Core.Plugins;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;
using VLB;

namespace Oxide.Plugins
{
    [Info("Night Lantern", "k1lly0u", "2.1.3")]
    [Description("Automatically turns ON and OFF lanterns after sunset and sunrise")]
    class NightLantern : RustPlugin
    {
        #region Fields

        [PluginReference]
        private Plugin NoFuelRequirements;

        private static Hash<ulong, Dictionary<EntityType, bool>> _toggleList = new Hash<ulong, Dictionary<EntityType, bool>>();
        private readonly HashSet<LightController> _lightControllers = new HashSet<LightController>();

        private static readonly Hash<BaseOven, LightController> OvenControllers = new Hash<BaseOven, LightController>();

        private bool _lightsOn = false;
        private bool _globalToggle = true;

        private Timer _timeCheck;
        
        private static Func<string, ulong, object> _ignoreFuelConsumptionFunction;

        #endregion Fields

        #region Oxide Hooks

        private void Init()
        {
            permission.RegisterPermission("nightlantern.global", this);
            
            foreach (EntityType type in (EntityType[])Enum.GetValues(typeof(EntityType)))
            {
                if (type == EntityType.CeilingLight)
                    continue;
                
                permission.RegisterPermission($"nightlantern.{type}", this);
            }

            _ignoreFuelConsumptionFunction = NoFuelRequirementsIgnoreFuelConsumption;

            LoadData();
            
            Unsubscribe(nameof(OnEntitySpawned));
            Unsubscribe(nameof(OnEntityKill));
        }

        protected override void LoadDefaultMessages()
            => lang.RegisterMessages(_messages, this);

        private void OnServerInitialized()
        {
            Subscribe(nameof(OnEntitySpawned));
            Subscribe(nameof(OnEntityKill));
            
            ServerMgr.Instance.StartCoroutine(CreateAllLights(BaseNetworkable.serverEntities.Where(x => x is BaseOven or SearchLight)));
        }

        private object OnFuelConsume(BaseOven baseOven, Item fuel, ItemModBurnable burnable)
        {
            if (!baseOven || baseOven.IsDestroyed)
                return null;
            
            if (!OvenControllers.TryGetValue(baseOven, out LightController lightController) || !lightController)
                return null;

            return lightController.OnConsumeFuel();
        }

        private void OnOvenToggle(BaseOven baseOven, BasePlayer player)
        {
            if (!baseOven || baseOven.IsDestroyed)
                return;
            
            if (baseOven.needsBuildingPrivilegeToUse && !player.CanBuild())
                return;
            
            if (!OvenControllers.TryGetValue(baseOven, out LightController lightController) || !lightController)
                return;

            lightController.OnOvenToggled();
        }

        private void OnEntitySpawned(BaseEntity entity) 
            => InitializeLightController(entity);

        private void OnEntityKill(BaseNetworkable entity)
        {
            LightController lightController = entity.GetComponent<LightController>();
            if (lightController)
            {
                _lightControllers.Remove(lightController);
                UnityEngine.Object.Destroy(lightController);
            }
        }

        private void OnServerSave() => SaveData();

        private void Unload()
        {
            foreach (LightController lightController in _lightControllers)
            {
                lightController.ToggleLight(false);
                UnityEngine.Object.DestroyImmediate(lightController);
            }

            _timeCheck?.Destroy();
            _lightControllers.Clear();

            _configData = null;
            _toggleList = null;
        }

        #endregion Oxide Hooks

        #region Functions

        private IEnumerator CreateAllLights(IEnumerable<BaseNetworkable> entities)
        {
            foreach (BaseNetworkable baseNetworkable in entities)
            {
                yield return new WaitForSeconds(UnityEngine.Random.Range(0.1f, 0.25f));
                
                if (!baseNetworkable || baseNetworkable.IsDestroyed)
                    continue;
                
                InitializeLightController(baseNetworkable as BaseEntity);
            }

            CheckCurrentTime();
        }

        private void InitializeLightController(BaseEntity entity)
        {
            if (!entity || entity.IsDestroyed)
                return;

            EntityType entityType = StringToType(entity.ShortPrefabName);

            if (entityType == EntityType.None || !_configData.Types[entityType].Enabled)
                return;

            _lightControllers.Add(entity.GetOrAddComponent<LightController>());
        }

        private void CheckCurrentTime()
        {
            if (_globalToggle)
            {
                float time = TOD_Sky.Instance.Cycle.Hour;
                if (time >= _configData.Sunset || (time >= 0 && time < _configData.Sunrise))
                {
                    if (!_lightsOn)
                    {
                        ServerMgr.Instance.StartCoroutine(ToggleAllLights(_lightControllers, true));
                        _lightsOn = true;
                    }
                }
                else if (time >= _configData.Sunrise && time < _configData.Sunset)
                {
                    if (_lightsOn)
                    {
                        ServerMgr.Instance.StartCoroutine(ToggleAllLights(_lightControllers, false));
                        _lightsOn = false;
                    }
                }
            }
            _timeCheck = timer.Once(20, CheckCurrentTime);
        }

        private static IEnumerator ToggleAllLights(IEnumerable<LightController> lights, bool status)
        {
            foreach (LightController lightController in lights)
            {
                yield return new WaitForSeconds(UnityEngine.Random.Range(0.1f, 0.25f));

                if (lightController)
                    lightController.ToggleLight(status);
            }
        }

        private static EntityType StringToType(string name)
        {
            return name switch
            {
                "campfire" => EntityType.Campfire,
                "skull_fire_pit" => EntityType.Firepit,
                "fireplace.deployed" => EntityType.Fireplace,
                "furnace" => EntityType.Furnace,
                "furnace.large" => EntityType.LargeFurnace,
                "lantern.deployed" => EntityType.Lanterns,
                "jackolantern.angry" => EntityType.JackOLantern,
                "jackolantern.happy" => EntityType.JackOLantern,
                "tunalight.deployed" => EntityType.TunaLight,
                "searchlight.deployed" => EntityType.Searchlight,
                "bbq.deployed" => EntityType.BBQ,
                "refinery_small_deployed" => EntityType.Refinery,
                "cursedcauldron.deployed" => EntityType.CursedCauldren,
                "chineselantern.deployed" => EntityType.ChineseLantern,
                "chineselantern_white.deployed" => EntityType.ChineseLantern,
                _ => EntityType.None
            };
        }

        private static EntityType ParseType(string type)
        {
            try
            {
                return (EntityType)Enum.Parse(typeof(EntityType), type, true);
            }
            catch
            {
                return EntityType.None;
            }
        }

        private static bool ConsumeTypeEnabled(ulong playerId, EntityType entityType)
        {
            if (_toggleList.TryGetValue(playerId, out Dictionary<EntityType, bool> userPreferences))
                return userPreferences[entityType];
            
            return _configData.Types[entityType].Enabled;
        }
        
        private object NoFuelRequirementsIgnoreFuelConsumption(string shortname, ulong playerId)
            => NoFuelRequirements?.Call("IgnoreFuelConsumption", shortname, playerId);
        
        #endregion Functions

        #region Component
        
        private class LightController : MonoBehaviour
        {
            private BaseEntity _entity;
            private ConfigData.LightSettings _config;
            private bool _isSearchlight;
            private bool _ignoreFuelConsumption;
            private bool _automaticallyToggled;
            public EntityType entityType;

            public bool ShouldIgnoreFuelConsumption
            {
                get
                {
                    if (_config.ConsumeFuelWhenToggled && !_automaticallyToggled)
                        return false;
                    
                    return _ignoreFuelConsumption || !_config.ConsumeFuel;
                }
            } 

            private void Awake()
            {
                _entity = GetComponent<BaseEntity>();
                entityType = StringToType(_entity.ShortPrefabName);
                _config = _configData.Types[entityType];
                _isSearchlight = _entity is SearchLight;

                object success = _ignoreFuelConsumptionFunction(entityType.ToString(), _entity.OwnerID);
                if (success != null)
                    _ignoreFuelConsumption = true;
            }

            private void OnEnable()
            {
                if (_entity is BaseOven baseOven)
                    OvenControllers[baseOven] = this;
            }

            private void OnDisable()
            {
                if (_entity is BaseOven baseOven)
                    OvenControllers.Remove(baseOven);
            }

            public void ToggleLight(bool status)
            {
                if (_config.Owner && !ConsumeTypeEnabled(_entity.OwnerID, entityType))
                    status = false;

                object success = Interface.CallHook("OnNightLanternToggle", _entity, status);
                if (success != null)
                    return;

                if (_isSearchlight)
                {
                    SearchLight searchLight = _entity as SearchLight;
                    if (searchLight)
                    {
                        using BaseEntity.FlagsUpdateScope scope = searchLight.StartSetFlags(BaseEntity.FlagsUpdateMode.SendNetworkUpdate);
                        scope.Set(BaseEntity.Flags.On, status);
                    }
                }
                else
                {
                    BaseOven baseOven = _entity as BaseOven;
                    if (baseOven)
                    {
                        if (_config.ConsumeFuel)
                        {       
                            if (status)
                                baseOven.StartCooking();
                            else baseOven.StopCooking();
                        }
                        else
                        {
                            if (baseOven.IsOn() != status)
                            {
                                _automaticallyToggled = true;

                                if (_config.Cook)
                                {
                                    using BaseEntity.FlagsUpdateScope scope = baseOven.StartSetFlags(BaseEntity.FlagsUpdateMode.SendNetworkUpdate);
                                    if (status)
                                    {
                                        baseOven.inventory.temperature = baseOven.cookingTemperature;
                                        baseOven.UpdateAttachmentTemperature();
                                        BaseOven.cookQueue.Add(baseOven);
                                        baseOven.lastCookUpdate = 0f;
                                        baseOven.lastCookVisualsUpdate = 0f;
                                        //baseOven.InvokeRepeating(baseOven.Cook, 0.5f, 0.5f);
                                        scope.Set(BaseEntity.Flags.On, true);
                                        Interface.CallHook("OnOvenStarted", this);
                                        
                                        if (baseOven.hasOpenFlame)
                                            SingletonComponent<NpcFireManager>.Instance.Add(baseOven);
                                    }
                                    else
                                    {
                                        if (_automaticallyToggled)
                                        {
                                            baseOven.UpdateAttachmentTemperature();
                                            if (baseOven.inventory != null)
                                            {
                                                baseOven.inventory.temperature = 15f;
                                                foreach (Item item in baseOven.inventory.itemList)
                                                {
                                                    if (item.HasFlag(global::Item.Flag.OnFire))
                                                    {
                                                        item.SetFlag(global::Item.Flag.OnFire, false);
                                                        item.MarkDirty();
                                                    }
                                                    else if (item.HasFlag(global::Item.Flag.Cooking))
                                                    {
                                                        item.SetFlag(global::Item.Flag.Cooking, false);
                                                        item.MarkDirty();
                                                    }
                                                }
                                            }

                                            BaseOven.cookQueue.Remove(baseOven);
                                            scope.Set(BaseEntity.Flags.On, false);
                                            //baseOven.SetFlag(BaseEntity.Flags.On, false, false, true);
                                            if (baseOven.hasOpenFlame)
                                                SingletonComponent<NpcFireManager>.Instance.Remove(baseOven);
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
                _entity.SendNetworkUpdate();
            }

            public void OnOvenToggled() => _automaticallyToggled = false;
            
            public object OnConsumeFuel() => ShouldIgnoreFuelConsumption ? true : (object)null;
            
            public bool IsOwner(ulong playerId) => _entity.OwnerID == playerId;
        }
        
        [AutoPatch]
        [HarmonyPatch(typeof(BaseOven))]
        [HarmonyPatch(nameof(BaseOven.CanRunWithNoFuel), MethodType.Getter)]
        private static class BaseOven_CanRunWithNoFuelPatch
        {
            [HarmonyPrefix]
            private static bool Prefix(BaseOven __instance, ref bool __result)
            {
                if (!OvenControllers.TryGetValue(__instance, out LightController lightController))
                    return false;

                if (lightController && lightController.ShouldIgnoreFuelConsumption)
                {
                    __result = true;
                    return false;
                }

                return true;
            }
        }

        #endregion Component

        #region Commands

        private StringBuilder _stringBuilder = new StringBuilder();

        [ChatCommand("lantern")]
        private void cmdLantern(BasePlayer player, string command, string[] args)
        {
            if (args.Length == 0)
            {
                _stringBuilder.Clear();
                _stringBuilder.AppendLine(GetMessage("global.title", player.userID));
                
                if (!_toggleList.TryGetValue(player.userID, out Dictionary<EntityType, bool> userPreferences))
                    userPreferences = _configData.Types.ToDictionary(x => x.Key, y => y.Value.Enabled);

                bool canToggle = false;
                foreach (KeyValuePair<EntityType, ConfigData.LightSettings> lightType in _configData.Types)
                {
                    if (lightType.Key == EntityType.CeilingLight)
                        continue;

                    if (lightType.Value.Owner)
                    {
                        if (lightType.Value.Permission && !permission.UserHasPermission(player.UserIDString, $"nightlantern.{lightType.Key}"))
                            continue;

                        _stringBuilder.AppendLine(string.Format(GetMessage("user.type", player.userID), lightType.Key, userPreferences[lightType.Key] ? GetMessage("user.enabled", player.userID) : GetMessage("user.disabled", player.userID)));
                        canToggle = true;
                    }
                }

                if (canToggle)
                    _stringBuilder.AppendLine(GetMessage("user.toggle.command", player.userID));
                
                player.ChatMessage(_stringBuilder.ToString());
                _stringBuilder.Clear();
                
                if (!permission.UserHasPermission(player.UserIDString, "nightlantern.global")) 
                    return;
                
                _stringBuilder.AppendLine(string.Format(GetMessage("global.toggle", player.userID), _globalToggle ? GetMessage("user.enabled", player.userID) : GetMessage("user.disabled", player.userID)));
                _stringBuilder.AppendLine(GetMessage("global.toggle.command", player.userID));
                
                player.ChatMessage(_stringBuilder.ToString());
                _stringBuilder.Clear();
                return;
            }

            if (args[0].ToLower() == "global" && permission.UserHasPermission(player.UserIDString, "nightlantern.global"))
            {
                _globalToggle = !_globalToggle;
                ServerMgr.Instance.StartCoroutine(ToggleAllLights(_lightControllers, _globalToggle));
                player.ChatMessage(string.Format(GetMessage("global.toggle", player.userID), _globalToggle ? GetMessage("user.enabled", player.userID) : GetMessage("user.disabled", player.userID)));
            }
            else
            {
                EntityType entityType = ParseType(args[0]);
                if ((entityType == EntityType.None || entityType == EntityType.CeilingLight) || !permission.UserHasPermission(player.UserIDString, $"nightlantern.{entityType}"))
                {
                    player.ChatMessage(string.Format(GetMessage("toggle.invalid", player.userID), entityType));
                    return;
                }

                if (!_toggleList.ContainsKey(player.userID))
                    _toggleList.Add(player.userID, _configData.Types.ToDictionary(x => x.Key, y => y.Value.Enabled));

                _toggleList[player.userID][entityType] = !_toggleList[player.userID][entityType];

                IEnumerable<LightController> ownedLights = _lightControllers.Where(x => x.IsOwner(player.userID) && x.entityType == entityType).ToList();
                if (ownedLights.Any())
                    ServerMgr.Instance.StartCoroutine(ToggleAllLights(ownedLights, _toggleList[player.userID][entityType]));

                player.ChatMessage(string.Format(GetMessage("user.type", player.userID), entityType, _toggleList[player.userID][entityType] ? GetMessage("user.enabled", player.userID) : GetMessage("user.disabled", player.userID)));
            }
        }

        #endregion Commands

        #region Config

        private enum EntityType { BBQ, Campfire, CeilingLight, ChineseLantern, CursedCauldren, Firepit, Fireplace, Furnace, LargeFurnace, Lanterns, JackOLantern, TunaLight, Searchlight, Refinery, None }

        private static ConfigData _configData;

        class ConfigData
        {
            [JsonProperty(PropertyName = "Light Settings")]
            public Dictionary<EntityType, LightSettings> Types { get; set; }

            [JsonProperty(PropertyName = "Time autolights are disabled")]
            public float Sunrise { get; set; }

            [JsonProperty(PropertyName = "Time autolights are enabled")]
            public float Sunset { get; set; }

            public class LightSettings
            {
                [JsonProperty(PropertyName = "This type is enabled")]
                public bool Enabled { get; set; }

                [JsonProperty(PropertyName = "This type consumes fuel")]
                public bool ConsumeFuel { get; set; }

                [JsonProperty(PropertyName = "This type consumes fuel when toggled by a player")]
                public bool ConsumeFuelWhenToggled { get; set; }

                [JsonProperty(PropertyName = "This type starts cooking items when toggled by plugin")]
                public bool Cook { get; set; }

                [JsonProperty(PropertyName = "This type can be toggled by the owner")]
                public bool Owner { get; set; }

                [JsonProperty(PropertyName = "This type requires permission to be toggled by the owner")]
                public bool Permission { get; set; }
            }

            public VersionNumber Version { get; set; }
        }

        protected override void LoadConfig()
        {
            base.LoadConfig();
            _configData = Config.ReadObject<ConfigData>();

            if (_configData.Version < Version)
                UpdateConfigValues();

            Config.WriteObject(_configData, true);
        }

        protected override void LoadDefaultConfig() => _configData = GetBaseConfig();

        private ConfigData GetBaseConfig()
        {
            return new ConfigData
            {
                Types = new Dictionary<EntityType, ConfigData.LightSettings>
                {
                    [EntityType.BBQ] = new ConfigData.LightSettings
                    {
                        ConsumeFuel = true,
                        Enabled = true,
                        Permission = true,
                        Owner = true
                    },
                    [EntityType.Campfire] = new ConfigData.LightSettings
                    {
                        ConsumeFuel = true,
                        Enabled = true,
                        Permission = true,
                        Owner = true
                    },                    
                    [EntityType.Firepit] = new ConfigData.LightSettings
                    {
                        ConsumeFuel = true,
                        Enabled = true,
                        Permission = true,
                        Owner = true
                    },
                    [EntityType.Fireplace] = new ConfigData.LightSettings
                    {
                        ConsumeFuel = true,
                        Enabled = true,
                        Permission = true,
                        Owner = true
                    },
                    [EntityType.Furnace] = new ConfigData.LightSettings
                    {
                        ConsumeFuel = true,
                        Enabled = true,
                        Permission = false
                    },
                    [EntityType.JackOLantern] = new ConfigData.LightSettings
                    {
                        ConsumeFuel = true,
                        Enabled = true,
                        Permission = true,
                        Owner = true
                    },
                    [EntityType.Lanterns] = new ConfigData.LightSettings
                    {
                        ConsumeFuel = true,
                        Enabled = true,
                        Permission = true,
                        Owner = true
                    },
                    [EntityType.LargeFurnace] = new ConfigData.LightSettings
                    {
                        ConsumeFuel = true,
                        Enabled = true,
                        Permission = true,
                        Owner = true
                    },
                    [EntityType.Searchlight] = new ConfigData.LightSettings
                    {
                        ConsumeFuel = true,
                        Enabled = true,
                        Permission = true,
                        Owner = true
                    },
                    [EntityType.TunaLight] = new ConfigData.LightSettings
                    {
                        ConsumeFuel = true,
                        Enabled = true,
                        Permission = true,
                        Owner = true
                    },
                    [EntityType.Refinery] = new ConfigData.LightSettings
                    {
                        ConsumeFuel = true,
                        Enabled = true,
                        Permission = true,
                        Owner = true
                    },
                    [EntityType.CursedCauldren] = new ConfigData.LightSettings
                    {
                        ConsumeFuel = true,
                        Enabled = true,
                        Permission = true,
                        Owner = true
                    },
                    [EntityType.ChineseLantern] = new ConfigData.LightSettings
                    {
                        ConsumeFuel = true,
                        Enabled = true,
                        Permission = true,
                        Owner = true
                    }
                },
                Sunrise = 7.5f,
                Sunset = 18.5f,
                Version = Version
            };
        }

        protected override void SaveConfig() => Config.WriteObject(_configData, true);

        private void UpdateConfigValues()
        {
            PrintWarning("Config update detected! Updating config values...");

            ConfigData baseConfig = GetBaseConfig();

            if (_configData.Version < new VersionNumber(2, 0, 9))
                _configData = baseConfig;

            foreach (EntityType entityType in (EntityType[])Enum.GetValues(typeof(EntityType)))
            {
                if (!_configData.Types.ContainsKey(entityType))
                {
                    _configData.Types[entityType] = new ConfigData.LightSettings
                    {
                        ConsumeFuel = true,
                        Enabled = true,
                        Cook = false,
                        Permission = true,
                        Owner = true
                    };
                }
            }

            _configData.Version = Version;
            PrintWarning("Config update completed!");
        }

        #endregion Config

        #region Data Management

        private void SaveData() => Interface.Oxide.DataFileSystem.GetFile("nightlantern_data").WriteObject(_toggleList);

        private void LoadData()
        {
            try
            {
                _toggleList = Interface.Oxide.DataFileSystem.GetFile("nightlantern_data")?.ReadObject<Hash<ulong, Dictionary<EntityType, bool>>>() ?? new Hash<ulong, Dictionary<EntityType, bool>>();
            }
            catch
            {
                _toggleList = new Hash<ulong, Dictionary<EntityType, bool>>();
            }
        }

        #endregion Data Management

        #region Localization

        private string GetMessage(string key, ulong playerId = 0U) => lang.GetMessage(key, this, playerId == 0U ? null : playerId.ToString());

        private readonly Dictionary<string, string> _messages = new Dictionary<string, string>
        {
            ["global.title"] = "<color=#FFA500>Night Lantern</color>",
            ["global.toggle"] = "Auto lights are {0} server wide",
            ["global.toggle.command"] = "You can toggle auto lights globally by typing '<color=#FFA500>/lantern global</color>'",
            ["user.disable"] = "You have disabled auto lights that you own of the type {0}",
            ["user.enable"] = "You have enabled auto lights that you own of the type {0}",
            ["user.type"] = "{0} : {1}",
            ["user.enabled"] = "<color=#8ee700>enabled</color>",
            ["user.disabled"] = "<color=#e90000>disabled</color>",
            ["user.toggle.command"] = "You can toggle the various types by typing '<color=#FFA500>/lantern <light type></color>'",
            ["toggle.invalid"] = "{0} is an invalid option!"
        };

        #endregion Localization
    }
}
