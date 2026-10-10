using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Network;
using Newtonsoft.Json;
using Oxide.Core;
using Rust.Ai.Gen2;
using UnityEngine;
using Epoch = Facepunch.Math.Epoch;

namespace Oxide.Plugins
{
    [Info("Livestock Farm", "local", "1.0.0")]
    [Description("Builds one livestock and crop farm per server identity and keeps it running without player upkeep")]
    public class LivestockFarm : RustPlugin
    {
        private const string Cattle = "cattle";
        private const string SheepKind = "sheep";
        private static readonly string[] AllSpecies = { Cattle, SheepKind };

        private const string CowPrefab = "assets/rust.ai/agents/cow/cow.prefab";
        private const string BullPrefab = "assets/rust.ai/agents/bull/bull.prefab";
        private const string CalfPrefab = "assets/rust.ai/agents/calf/calf.prefab";
        private const string CalfMalePrefab = "assets/rust.ai/agents/calf/calfmale.prefab";
        private const string SheepPrefab = "assets/rust.ai/agents/sheep/sheep.prefab";
        private const string LambPrefab = "assets/rust.ai/agents/sheep/lamb.prefab";
        private const string FencePrefab = "assets/prefabs/deployable/animal fence/animalfence.deployed.prefab";
        private const string GatePrefab = "assets/prefabs/deployable/animal fence/animalfencegate.deployed.prefab";
        private const string TroughPrefab = "assets/prefabs/deployable/hitch & trough/hitchtrough.deployed.prefab";
        private const string CoopPrefab = "assets/prefabs/deployable/chickencoop/chickencoop.deployed.prefab";
        private const string CupboardPrefab = "assets/prefabs/deployable/tool cupboard/cupboard.tool.deployed.prefab";
        private const string FoundationPrefab = "assets/prefabs/building core/foundation/foundation.prefab";
        private const string LargeBoxPrefab = "assets/prefabs/deployable/large wood storage/box.wooden.large.prefab";
        private const string SmallCatcherPrefab = "assets/prefabs/deployable/water catcher/water_catcher_small.prefab";
        private const string ComposterPrefab = "assets/prefabs/deployable/composter/composter.prefab";
        private const string FridgePrefab = "assets/prefabs/deployable/fridge/fridge.deployed.prefab";
        private const string BatteryPrefab =
            "assets/prefabs/deployable/playerioents/batteries/smallrechargablebattery.deployed.prefab";
        private const string PlanterPrefab = "assets/prefabs/deployable/planters/planter.large.deployed.prefab";
        private const string SprinklerPrefab =
            "assets/prefabs/deployable/playerioents/sprinkler/electric.sprinkler.deployed.prefab";
        private const string PumpPrefab = "assets/prefabs/deployable/playerioents/waterpump/water.pump.deployed.prefab";
        private const string SolarPrefab =
            "assets/prefabs/deployable/playerioents/generators/solar_panels_roof/solarpanel.large.deployed.prefab";

        private const string RoleFence = "fence";
        private const string RoleGate = "gate";
        private const string RoleFoundation = "foundation";
        private const string RoleCupboard = "cupboard";
        private const string RoleTrough = "trough";
        private const string RoleCoop = "coop";
        private const string RoleComposter = "composter";
        private const string RolePlanter = "planter";
        private const string RolePump = "pump";
        private const string RoleFridge = "fridge";
        private const string RolePlaceholder = "fridge_spot";
        private const string RolePlatform = "platform";
        private const string BoxMilk = "box_milk";
        private const string BoxWool = "box_wool";
        private const string BoxEggs = "box_eggs";
        private const string BoxDung = "box_dung";
        private const string BoxMeat = "box_meat";
        private const string BoxFeed = "box_feed";
        private const string BoxClones = "box_clones";

        private const string SpoiledProduce = "spoiled.produce";
        private const string WoolShortname = "wool";
        private const string EggShortname = "egg";
        private const string DungShortname = "horsedung";
        private const string FertilizerShortname = "fertilizer";

        // ChickenCoop.ServerPetChicken adds this much love per petting.
        private const float PetLove = 15f;
        // The planner refuses a foundation top more than about 2.6 m over the lowest terrain under it.
        private const float FoundationHalfWidth = 1.5f;
        private const float FoundationMaxRise = 2.6f;
        // Planner placement puts fence and gate bases this far above the aim point.
        private const float FenceLift = 0.05f;
        private const float VegetationMargin = 0.5f;
        // Animal fence and gate rails are 2.9 m long; their ends span the pen polygon.
        private const float RailHalfLength = 1.45f;
        // farm.build and farm.repair take over a matching entity this close to an entry's spot.
        private const float AdoptDistance = 0.5f;
        private const float AdoptHeight = 2f;
        // Colliders reaching into this sphere around a spot are checked for adoption; pivots must still be close.
        private const float AdoptSearchRadius = 3f;
        private const float RelinkThrottleSeconds = 600f;
        private const float ReseedBaseMinutes = 10f;
        private const float ReseedMaxMinutes = 120f;
        private const float RepairBaseMinutes = 10f;
        private const float RepairMaxMinutes = 120f;
        // A loss this soon after a repair doubles the wait; standing this long after one clears it.
        private const float ChurnSeconds = 600f;
        private const float RepairResetSeconds = 1800f;
        // Water the pump keeps for the sprinklers when it tops up a trough or coop below its minimum.
        private const int PumpReserve = 500;
        // A stray unled for this long inside the farm radius goes back to the herd point.
        private const float StrayReturnSeconds = 300f;
        private const float NeedLogSeconds = 1800f;

        // Local x/z points of each prefab's GroundWatch area checks (research/farm/plugin-api.md section 5,
        // infrastructure.md section 5), sampled when a role is in "Ground check roles".
        private static readonly Dictionary<string, Vector2[]> GroundChecks = new Dictionary<string, Vector2[]>
        {
            ["chickencoop.deployed"] = Grid(-1.55f, 1.55f, -1.55f, 1.55f, 5, 5),
            ["hitchtrough.deployed"] = Grid(-0.96f, 0.96f, -0.25f, 2.06f, 3, 4),
            ["box.wooden.large"] = Grid(-0.89f, 0.89f, -0.525f, 0.525f, 2, 2),
            ["water_catcher_small"] = Grid(-0.5f, 0.5f, -0.5f, 0.5f, 3, 3),
            ["water_catcher_large"] = Grid(-2f, 2f, -2f, 2f, 3, 3),
            ["composter"] = Grid(-0.6f, 0.6f, -0.6f, 0.6f, 2, 2),
            ["planter.large.deployed"] = Grid(-1.35f, 1.35f, -1.35f, 1.35f, 3, 3),
        };

        // Base rectangle (centre x, centre z, half x, half z) cleared of vegetation before a prefab spawns; prefabs not
        // listed use their own bounds. Fence and gate use the movement-blocking rail, not the wider build volume.
        private static readonly Dictionary<string, Vector4> ClearingAreas = new Dictionary<string, Vector4>
        {
            ["animalfence.deployed"] = new Vector4(0f, 0f, 1.5f, 0.065f),
            ["animalfencegate.deployed"] = new Vector4(0f, 0f, 1.5f, 0.1f),
            ["foundation"] = new Vector4(0f, 0f, 1.5f, 1.5f),
            ["chickencoop.deployed"] = new Vector4(0f, 0f, 1.55f, 1.55f),
            ["hitchtrough.deployed"] = new Vector4(0f, 0.9f, 1.1f, 1.2f),
            ["box.wooden.large"] = new Vector4(0f, 0f, 0.89f, 0.525f),
            ["water_catcher_small"] = new Vector4(0f, 0f, 0.5f, 0.5f),
            ["composter"] = new Vector4(0f, 0f, 0.6f, 0.6f),
            ["planter.large.deployed"] = new Vector4(0f, 0f, 1.35f, 1.36f),
        };

        private static readonly Dictionary<uint, Socket_Specific_Female[]> SlotCache =
            new Dictionary<uint, Socket_Specific_Female[]>();

        private static readonly List<BaseEntity> NoEntities = new List<BaseEntity>();

        private static readonly LivestockAnimal.Need[] TrackedNeeds =
        {
            LivestockAnimal.Need.Fullness, LivestockAnimal.Need.Hydration, LivestockAnimal.Need.PersonalSpace,
        };

        private Configuration _config;
        private StoredData _data;
        // Why upkeep and the farm.* commands refuse to run; null while the config and data loaded.
        private string _disabled;
        // The data file could not be read or archived; nothing is written over it until a reload.
        private bool _dataUnwritable;
        private bool _unloaded;
        // Set once OnServerInitialized has checked the record; farm.* commands other than farm.status wait for it.
        private bool _ready;
        private readonly HashSet<ulong> _structureIds = new HashSet<ulong>();
        // Farm animals by net id, with the prefab each resolve must match.
        private readonly Dictionary<ulong, string> _animals = new Dictionary<ulong, string>();
        // Every LivestockAnimal on the server, kept from OnEntitySpawned and OnEntityKill.
        private readonly HashSet<LivestockAnimal> _livestock = new HashSet<LivestockAnimal>();
        // Dropped items with a swept shortname that landed within the farm radius.
        private readonly HashSet<DroppedItem> _dropped = new HashSet<DroppedItem>();
        private readonly Dictionary<ulong, PendingCull> _pendingCulls = new Dictionary<ulong, PendingCull>();
        private readonly Dictionary<string, HashSet<ulong>> _pregnantLastPass =
            new Dictionary<string, HashSet<ulong>>();
        private readonly Dictionary<string, float> _nextFallbackAttempt = new Dictionary<string, float>();
        private readonly Dictionary<string, float> _failureLoggedAt = new Dictionary<string, float>();
        private readonly Dictionary<string, float> _slotEmptySince = new Dictionary<string, float>();
        private readonly Dictionary<ulong, DryWatch> _dry = new Dictionary<ulong, DryWatch>();
        private readonly Dictionary<string, RepairState> _repairs = new Dictionary<string, RepairState>();
        // Farm animals outside the fence line, by net id: when each was last seen led, mounted, or newly outside.
        private readonly Dictionary<ulong, float> _strays = new Dictionary<ulong, float>();
        // Returned strays awaiting the next pass's check that they stand inside, with the line to log then.
        private readonly Dictionary<ulong, string> _returning = new Dictionary<ulong, string>();
        // When each animal's restore of each need was last logged.
        private readonly Dictionary<string, float> _needLoggedAt = new Dictionary<string, float>();
        // Farm animals within the farm radius at the last pass; a release needs an outward crossing.
        private readonly HashSet<ulong> _withinRadius = new HashSet<ulong>();
        private readonly HashSet<string> _growingNames = new HashSet<string>();
        private readonly List<LivestockAnimal> _justSpawned = new List<LivestockAnimal>();
        private readonly List<string> _clearedVegetation = new List<string>();
        private List<Vector2> _penHull;
        private List<Step> _steps;
        private Timer _upkeepTimer;
        private Timer _herdTimer;
        private float _lastUpkeepAt = -1f;
        private float _lastRelinkAt = float.MinValue;
        private bool _relinkWanted;
        // Set once VerifyRecord or farm.build has vouched for the record; auto-repair waits for it.
        private bool _recordChecked;
        // Auto-repair logs its own failures at most hourly, so PlacementFailed only records them.
        private bool _quietPlacement;
        // Repairs defer a spawn while a player or animal stands in its footprint plus 0.5 m.
        private bool _deferOccupied;
        private bool _removing;

        #region Configuration

        private class Configuration
        {
            // Zero resolves to the first owner in cfg/users.cfg; the config is tracked in git, so it holds no SteamID.
            [JsonProperty("Owner SteamID (0 = first owner in cfg/users.cfg)")]
            public ulong OwnerSteamId = 0;

            [JsonProperty("Upkeep interval (seconds)")]
            public float UpkeepSeconds = 30f;

            [JsonProperty("Farm radius around the layout centre for sweeping and relinking (m)")]
            public float FarmRadius = 50f;

            [JsonProperty("Swept dropped items (shortname to box role)",
                ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public Dictionary<string, string> SweptItems = new Dictionary<string, string>
            {
                [DungShortname] = BoxDung,
                [EggShortname] = BoxEggs,
            };

            // A powered fridge stops spoiling (prefab PoweredFoodSpoilageRateMultiplier 0); a full fridge falls back
            // to the item's box.
            [JsonProperty("Fridge items (shortnames kept in a fridge before their box)",
                ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<string> FridgeItems = new List<string> { "milk", EggShortname, "beefmeat", "muttonmeat" };

            [JsonProperty("Herd spawn point (farm-local metres)",
                ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public XZ HerdPoint = new XZ { X = 0f, Z = 11f };

            [JsonProperty("Herd spawn spread (m)")]
            public float HerdSpread = 4f;

            // Fence spawns rebuild the navmesh tiles under them; animals spawned first can land off the navmesh.
            [JsonProperty("Herd spawn delay after build (seconds)")]
            public float HerdSpawnDelay = 15f;

            [JsonProperty("Starter herd", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<HerdEntry> StarterHerd = DefaultHerd();

            [JsonProperty("Cattle cap (adults and juveniles)")]
            public int CattleCap = 6;

            [JsonProperty("Sheep cap (adults and juveniles)")]
            public int SheepCap = 8;

            [JsonProperty("Breeding fallback after minutes without a new pregnancy")]
            public float BreedingFallbackMinutes = 60f;

            [JsonProperty("Breeding fallback retry minutes")]
            public float BreedingRetryMinutes = 10f;

            // Condition targets InverseLerp(0, livestock.conditionfullcontentment 0.5 / Hardiness scale, lowest need),
            // and breeding pauses at 0.6, so needs held at 0.5 keep even Bad hardiness (0.7) at condition 0.7.
            [JsonProperty("Need threshold (0-1)")]
            public float NeedThreshold = 0.5f;

            [JsonProperty("Need restore value (0-1)")]
            public float NeedRestore = 0.8f;

            [JsonProperty("Fallback feed shortname")]
            public string FallbackFeed = "wheat";

            [JsonProperty("Trough feed target (items)")]
            public int TroughFeedTarget = 120;

            [JsonProperty("Trough feed minimum before creating feed (items)")]
            public int TroughFeedMinimum = 20;

            [JsonProperty("Trough water target (ml)")]
            public int TroughWaterTarget = 5000;

            [JsonProperty("Trough water minimum before creating water (ml)")]
            public int TroughWaterMinimum = 1000;

            [JsonProperty("Coop feed target (items)")]
            public int CoopFeedTarget = 40;

            [JsonProperty("Coop feed minimum before creating feed (items)")]
            public int CoopFeedMinimum = 5;

            [JsonProperty("Coop water target (ml)")]
            public int CoopWaterTarget = 1000;

            [JsonProperty("Coop water minimum before creating water (ml)")]
            public int CoopWaterMinimum = 200;

            [JsonProperty("Water source roles", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<string> WaterSourceRoles =
                new List<string> { "catcher_small", "catcher_large", "barrel", RolePump };

            [JsonProperty("Planter slot plan (layout id to clone shortname per slot)",
                ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public Dictionary<string, List<string>> SlotPlan = DefaultSlotPlan();

            [JsonProperty("Clone genes")]
            public string CloneGenes = "GGYYYY";

            [JsonProperty("Recreate clones after a slot sat empty with none in stock (minutes)")]
            public float CloneFallbackMinutes = 10f;

            [JsonProperty("Planter dry threshold (fraction of soil capacity)")]
            public float DryFraction = 0.25f;

            [JsonProperty("Planter dry rescue after (minutes)")]
            public float DryMinutes = 10f;

            [JsonProperty("Composter horsedung stock")]
            public int ComposterDung = 10;

            [JsonProperty("Ground check roles", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<string> GroundCheckRoles = new List<string> { RoleCoop, RoleTrough };

            [JsonProperty("Ground check tolerance (m)")]
            public float GroundTolerance = 0.15f;

            [JsonProperty("Gate close distance (m)")]
            public float GateCloseDistance = 10f;

            // Each upkeep pass re-places missing layout entries; an entry stays gone only once the layout drops it.
            [JsonProperty("Auto repair missing structures")]
            public bool AutoRepair = true;

            [JsonProperty("Cull corpse timeout (seconds)")]
            public float CorpseTimeout = 60f;

            [JsonProperty("Repeat an identical failure log after minutes")]
            public float FailureRepeatMinutes = 10f;

            [JsonProperty("Repeat a full container log after minutes")]
            public float FullRepeatMinutes = 60f;

            [JsonProperty("Status events kept per kind")]
            public int EventsPerKind = 8;

            [JsonProperty("Layout", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public FarmLayout Layout = DefaultLayout();
        }

        private class XZ
        {
            [JsonProperty("x")]
            public float X;

            [JsonProperty("z")]
            public float Z;
        }

        private class HerdEntry
        {
            [JsonProperty("Species")]
            public string Species;

            [JsonProperty("Prefab")]
            public string Prefab;

            [JsonProperty("Male")]
            public bool Male;

            [JsonProperty("Count")]
            public int Count;
        }

        // Same shape as research/farm/site/layout.json, so that file pastes in as the "Layout" value.
        private class FarmLayout
        {
            [JsonProperty("Centre", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public XZ Centre = new XZ();

            [JsonProperty("Yaw")]
            public float Yaw;

            [JsonProperty("Entries", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<LayoutEntry> Entries = new List<LayoutEntry>();

            [JsonProperty("Connections", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<LayoutConnection> Connections = new List<LayoutConnection>();
        }

        private class LayoutEntry
        {
            [JsonProperty("Id")]
            public string Id;

            [JsonProperty("Role")]
            public string Role;

            [JsonProperty("Prefab")]
            public string Prefab;

            [JsonProperty("X")]
            public float X;

            [JsonProperty("Z")]
            public float Z;

            [JsonProperty("Yaw")]
            public float Yaw;

            // Id of the platform or foundation the entry stands on, upright at its top, placed after it.
            [JsonProperty("On", NullValueHandling = NullValueHandling.Ignore)]
            public string On;
        }

        private class LayoutConnection
        {
            [JsonProperty("From")]
            public string From;

            [JsonProperty("FromSlot")]
            public int FromSlot;

            [JsonProperty("To")]
            public string To;

            [JsonProperty("ToSlot")]
            public int ToSlot;

            [JsonProperty("Kind")]
            public string Kind;

            public override string ToString() => $"{From} out {FromSlot} to {To} in {ToSlot} ({Kind})";
        }

        private static List<HerdEntry> DefaultHerd() => new List<HerdEntry>
        {
            new HerdEntry { Species = Cattle, Prefab = CowPrefab, Male = false, Count = 2 },
            new HerdEntry { Species = Cattle, Prefab = BullPrefab, Male = true, Count = 1 },
            new HerdEntry { Species = Cattle, Prefab = CalfPrefab, Male = false, Count = 1 },
            new HerdEntry { Species = SheepKind, Prefab = SheepPrefab, Male = false, Count = 3 },
            new HerdEntry { Species = SheepKind, Prefab = SheepPrefab, Male = true, Count = 1 },
            new HerdEntry { Species = SheepKind, Prefab = LambPrefab, Male = true, Count = 1 },
        };

        private static Dictionary<string, List<string>> DefaultSlotPlan() => new Dictionary<string, List<string>>
        {
            ["planter-01"] = Enumerable.Repeat("clone.potato", 9).ToList(),
            ["planter-02"] = Enumerable.Repeat("clone.potato", 9).ToList(),
            ["planter-03"] = Enumerable.Range(0, 9).Select(i => i % 2 == 0 ? "clone.corn" : "clone.pumpkin").ToList(),
            ["planter-04"] = Enumerable.Repeat("clone.wheat", 9).ToList(),
        };

        // research/farm/site/layout.json as of 2026-10-09 15:43: E6 meadow, 63 fences and a gate, two troughs, yard,
        // coops, four planters on one solar pump, and a fridge and its battery on a foundation platform, charged by
        // their own solar panel.
        private static FarmLayout DefaultLayout()
        {
            var layout = new FarmLayout { Centre = new XZ { X = -1542.9f, Z = 1227.3f }, Yaw = 0.0f };
            List<LayoutEntry> e = layout.Entries;
            e.Add(Entry("fence-01", "fence", FencePrefab, -24.65f, -20.3f, 90.0f));
            e.Add(Entry("fence-02", "fence", FencePrefab, -21.75f, -20.3f, 90.0f));
            e.Add(Entry("fence-03", "fence", FencePrefab, -18.85f, -20.3f, 90.0f));
            e.Add(Entry("fence-04", "fence", FencePrefab, -15.95f, -20.3f, 90.0f));
            e.Add(Entry("fence-05", "fence", FencePrefab, -13.05f, -20.3f, 90.0f));
            e.Add(Entry("fence-06", "fence", FencePrefab, -10.15f, -20.3f, 90.0f));
            e.Add(Entry("fence-07", "fence", FencePrefab, -7.25f, -20.3f, 90.0f));
            e.Add(Entry("fence-08", "fence", FencePrefab, -4.35f, -20.3f, 90.0f));
            e.Add(Entry("fence-09", "fence", FencePrefab, -1.45f, -20.3f, 90.0f));
            e.Add(Entry("fence-10", "fence", FencePrefab, 1.45f, -20.3f, 90.0f));
            e.Add(Entry("fence-11", "fence", FencePrefab, 4.35f, -20.3f, 90.0f));
            e.Add(Entry("fence-12", "fence", FencePrefab, 7.25f, -20.3f, 90.0f));
            e.Add(Entry("fence-13", "fence", FencePrefab, 10.15f, -20.3f, 90.0f));
            e.Add(Entry("fence-14", "fence", FencePrefab, 13.05f, -20.3f, 90.0f));
            e.Add(Entry("fence-15", "fence", FencePrefab, 15.95f, -20.3f, 90.0f));
            e.Add(Entry("fence-16", "fence", FencePrefab, 18.85f, -20.3f, 90.0f));
            e.Add(Entry("fence-17", "fence", FencePrefab, 21.75f, -20.3f, 90.0f));
            e.Add(Entry("fence-18", "fence", FencePrefab, 24.65f, -20.3f, 90.0f));
            e.Add(Entry("fence-19", "fence", FencePrefab, 26.1f, -18.85f, 180.0f));
            e.Add(Entry("fence-20", "fence", FencePrefab, 26.1f, -15.95f, 180.0f));
            e.Add(Entry("fence-21", "fence", FencePrefab, 26.1f, -13.05f, 180.0f));
            e.Add(Entry("fence-22", "fence", FencePrefab, 26.1f, -10.15f, 180.0f));
            e.Add(Entry("fence-23", "fence", FencePrefab, 26.1f, -7.25f, 180.0f));
            e.Add(Entry("fence-24", "fence", FencePrefab, 26.1f, -4.35f, 180.0f));
            e.Add(Entry("fence-25", "fence", FencePrefab, 26.1f, -1.45f, 180.0f));
            e.Add(Entry("fence-26", "fence", FencePrefab, 26.1f, 1.45f, 180.0f));
            e.Add(Entry("fence-27", "fence", FencePrefab, 26.1f, 4.35f, 180.0f));
            e.Add(Entry("gate-01", "gate", GatePrefab, 26.1f, 7.2f, 180.0f));
            e.Add(Entry("fence-28", "fence", FencePrefab, 26.1f, 10.15f, 180.0f));
            e.Add(Entry("fence-29", "fence", FencePrefab, 26.1f, 13.05f, 180.0f));
            e.Add(Entry("fence-30", "fence", FencePrefab, 26.1f, 15.95f, 180.0f));
            e.Add(Entry("fence-31", "fence", FencePrefab, 26.1f, 18.85f, 180.0f));
            e.Add(Entry("fence-32", "fence", FencePrefab, 24.65f, 20.3f, 90.0f));
            e.Add(Entry("fence-33", "fence", FencePrefab, 21.75f, 20.3f, 90.0f));
            e.Add(Entry("fence-34", "fence", FencePrefab, 18.85f, 20.3f, 90.0f));
            e.Add(Entry("fence-35", "fence", FencePrefab, 15.95f, 20.3f, 90.0f));
            e.Add(Entry("fence-36", "fence", FencePrefab, 13.05f, 20.3f, 90.0f));
            e.Add(Entry("fence-37", "fence", FencePrefab, 10.15f, 20.3f, 90.0f));
            e.Add(Entry("fence-38", "fence", FencePrefab, 7.25f, 20.3f, 90.0f));
            e.Add(Entry("fence-39", "fence", FencePrefab, 4.35f, 20.3f, 90.0f));
            e.Add(Entry("fence-40", "fence", FencePrefab, 1.45f, 20.3f, 90.0f));
            e.Add(Entry("fence-41", "fence", FencePrefab, -1.45f, 20.3f, 90.0f));
            e.Add(Entry("fence-42", "fence", FencePrefab, -4.35f, 20.3f, 90.0f));
            e.Add(Entry("fence-43", "fence", FencePrefab, -7.25f, 20.3f, 90.0f));
            e.Add(Entry("fence-44", "fence", FencePrefab, -10.15f, 20.3f, 90.0f));
            e.Add(Entry("fence-45", "fence", FencePrefab, -13.05f, 20.3f, 90.0f));
            e.Add(Entry("fence-46", "fence", FencePrefab, -15.95f, 20.3f, 90.0f));
            e.Add(Entry("fence-47", "fence", FencePrefab, -18.85f, 20.3f, 90.0f));
            e.Add(Entry("fence-48", "fence", FencePrefab, -21.75f, 20.3f, 90.0f));
            e.Add(Entry("fence-49", "fence", FencePrefab, -24.65f, 20.3f, 90.0f));
            e.Add(Entry("fence-50", "fence", FencePrefab, -26.1f, 18.85f, 180.0f));
            e.Add(Entry("fence-51", "fence", FencePrefab, -26.1f, 15.95f, 180.0f));
            e.Add(Entry("fence-52", "fence", FencePrefab, -26.1f, 13.05f, 180.0f));
            e.Add(Entry("fence-53", "fence", FencePrefab, -26.1f, 10.15f, 180.0f));
            e.Add(Entry("fence-54", "fence", FencePrefab, -26.1f, 7.25f, 180.0f));
            e.Add(Entry("fence-55", "fence", FencePrefab, -26.1f, 4.35f, 180.0f));
            e.Add(Entry("fence-56", "fence", FencePrefab, -26.1f, 1.45f, 180.0f));
            e.Add(Entry("fence-57", "fence", FencePrefab, -26.1f, -1.45f, 180.0f));
            e.Add(Entry("fence-58", "fence", FencePrefab, -26.1f, -4.35f, 180.0f));
            e.Add(Entry("fence-59", "fence", FencePrefab, -26.1f, -7.25f, 180.0f));
            e.Add(Entry("fence-60", "fence", FencePrefab, -26.1f, -10.15f, 180.0f));
            e.Add(Entry("fence-61", "fence", FencePrefab, -26.1f, -13.05f, 180.0f));
            e.Add(Entry("fence-62", "fence", FencePrefab, -26.1f, -15.95f, 180.0f));
            e.Add(Entry("fence-63", "fence", FencePrefab, -26.1f, -18.85f, 180.0f));
            e.Add(Entry("foundation-01", "foundation", FoundationPrefab, 0.0f, 0.0f, 0.0f));
            e.Add(Entry("cupboard-01", "cupboard", CupboardPrefab, 0.0f, 0.0f, 0.0f));
            e.Add(Entry("trough-01", "trough", TroughPrefab, -13.05f, 0.0f, 0.0f));
            e.Add(Entry("trough-02", "trough", TroughPrefab, 13.05f, 0.0f, 0.0f));
            e.Add(Entry("catcher-01", "catcher_small", SmallCatcherPrefab, -15.7f, 1.3f, 0.0f));
            e.Add(Entry("catcher-02", "catcher_small", SmallCatcherPrefab, 10.65f, 0.0f, 0.0f));
            e.Add(Entry("box-milk", "box_milk", LargeBoxPrefab, 28.9f, 13.3f, 0.0f));
            e.Add(Entry("box-wool", "box_wool", LargeBoxPrefab, 31.0f, 13.3f, 0.0f));
            e.Add(Entry("box-eggs", "box_eggs", LargeBoxPrefab, 33.1f, 13.3f, 0.0f));
            e.Add(Entry("box-dung", "box_dung", LargeBoxPrefab, 35.2f, 13.3f, 0.0f));
            e.Add(Entry("box-meat", "box_meat", LargeBoxPrefab, 37.3f, 13.3f, 0.0f));
            e.Add(Entry("box-feed", "box_feed", LargeBoxPrefab, 39.4f, 13.3f, 0.0f));
            e.Add(Entry("box-clones", "box_clones", LargeBoxPrefab, 41.5f, 13.3f, 0.0f));
            e.Add(Entry("composter-01", "composter", ComposterPrefab, 30.0f, 10.0f, 0.0f));
            e.Add(Entry("platform-01", "platform", FoundationPrefab, 33.15f, 10.25f, 0.0f));
            e.Add(Entry("fridge-01", "fridge", FridgePrefab, 32.6f, 10.0f, 0.0f, "platform-01"));
            e.Add(Entry("battery-01", "battery", BatteryPrefab, 33.8f, 10.0f, 0.0f, "platform-01"));
            e.Add(Entry("solar-02", "solar", SolarPrefab, 36.3f, 10.0f, 350.0f));
            e.Add(Entry("coop-01", "coop", CoopPrefab, 42.1f, 6.8f, 0.0f));
            e.Add(Entry("coop-02", "coop", CoopPrefab, 46.1f, 6.8f, 0.0f));
            e.Add(Entry("planter-01", "planter", PlanterPrefab, 29.5f, 19.1f, 0.0f));
            e.Add(Entry("sprinkler-01", "sprinkler", SprinklerPrefab, 29.5f, 21.3f, 180.0f));
            e.Add(Entry("planter-02", "planter", PlanterPrefab, 32.9f, 19.1f, 0.0f));
            e.Add(Entry("sprinkler-02", "sprinkler", SprinklerPrefab, 32.9f, 21.3f, 180.0f));
            e.Add(Entry("planter-03", "planter", PlanterPrefab, 36.3f, 19.1f, 0.0f));
            e.Add(Entry("sprinkler-03", "sprinkler", SprinklerPrefab, 36.3f, 21.3f, 180.0f));
            e.Add(Entry("planter-04", "planter", PlanterPrefab, 39.7f, 19.1f, 0.0f));
            e.Add(Entry("sprinkler-04", "sprinkler", SprinklerPrefab, 39.7f, 21.3f, 180.0f));
            e.Add(Entry("pump-01", "pump", PumpPrefab, 29.5f, 29.3f, 0.0f));
            e.Add(Entry("solar-01", "solar", SolarPrefab, 32.2f, 24.0f, 330.0f));
            List<LayoutConnection> c = layout.Connections;
            c.Add(Link("solar-01", 0, "pump-01", 0, "power"));
            c.Add(Link("pump-01", 0, "sprinkler-01", 0, "water"));
            c.Add(Link("sprinkler-01", 0, "sprinkler-02", 0, "water"));
            c.Add(Link("sprinkler-02", 0, "sprinkler-03", 0, "water"));
            c.Add(Link("sprinkler-03", 0, "sprinkler-04", 0, "water"));
            c.Add(Link("solar-02", 0, "battery-01", 0, "power"));
            c.Add(Link("battery-01", 0, "fridge-01", 0, "power"));
            return layout;
        }

        private static LayoutEntry Entry(string id, string role, string prefab, float x, float z, float yaw,
            string on = null) =>
            new LayoutEntry { Id = id, Role = role, Prefab = prefab, X = x, Z = z, Yaw = yaw, On = on };

        private static LayoutConnection Link(string from, int fromSlot, string to, int toSlot, string kind) =>
            new LayoutConnection { From = from, FromSlot = fromSlot, To = to, ToSlot = toSlot, Kind = kind };

        protected override void LoadDefaultConfig() => _config = new Configuration();

        // A config that does not parse or validate leaves the defaults in memory, unsaved, and disables the farm.
        protected override void LoadConfig()
        {
            base.LoadConfig();
            Configuration config;
            try
            {
                config = Config.ReadObject<Configuration>();
            }
            catch (Exception e)
            {
                RejectConfig($"{Name}.json does not parse: {e.Message}");
                return;
            }
            List<string> problems = config == null ? new List<string> { "the file is empty" } : ConfigProblems(config);
            if (problems.Count > 0)
            {
                RejectConfig($"{Name}.json is invalid: {string.Join("; ", problems)}");
                return;
            }
            _config = config;
            SaveConfig();
        }

        protected override void SaveConfig() => Config.WriteObject(_config);

        private void RejectConfig(string why)
        {
            _config = new Configuration();
            Disable($"{why}; fix it, then run c.reload LivestockFarm");
        }

        private static List<string> ConfigProblems(Configuration c)
        {
            var problems = new List<string>();
            FarmLayout layout = c.Layout;
            if (layout?.Entries == null || layout.Centre == null)
            {
                problems.Add("Layout needs a Centre and Entries");
            }
            else
            {
                if (layout.Connections == null)
                    layout.Connections = new List<LayoutConnection>();
                if (!Finite(layout.Centre.X) || !Finite(layout.Centre.Z) || !Finite(layout.Yaw))
                    problems.Add("the Layout Centre and Yaw must be finite numbers");
                var ids = new HashSet<string>();
                foreach (LayoutEntry entry in layout.Entries)
                {
                    if (entry == null || string.IsNullOrEmpty(entry.Id))
                    {
                        problems.Add("a layout entry has no Id");
                        continue;
                    }
                    if (!ids.Add(entry.Id))
                        problems.Add($"layout id {entry.Id} is used more than once");
                    if (string.IsNullOrEmpty(entry.Role))
                        problems.Add($"{entry.Id} has no Role");
                    else if (entry.Role != RolePlaceholder && string.IsNullOrEmpty(entry.Prefab))
                        problems.Add($"{entry.Id} has no Prefab");
                    if (!Finite(entry.X) || !Finite(entry.Z) || !Finite(entry.Yaw))
                        problems.Add($"{entry.Id} has a non-finite X, Z, or Yaw");
                }
                foreach (LayoutEntry entry in layout.Entries.Where(e => !string.IsNullOrEmpty(e?.On)))
                {
                    LayoutEntry under = layout.Entries.FirstOrDefault(e => e != null && e.Id == entry.On);
                    if (under == null || under == entry || (under.Role != RolePlatform && under.Role != RoleFoundation))
                        problems.Add($"{entry.Id} is On {entry.On}, which is not a platform or foundation entry");
                }
                foreach (LayoutConnection link in layout.Connections)
                    if (link == null || !ids.Contains(link.From ?? string.Empty) ||
                        !ids.Contains(link.To ?? string.Empty))
                        problems.Add($"connection {link} names a layout id that does not exist");
            }
            if (c.StarterHerd == null)
                problems.Add("Starter herd is missing");
            else if (c.StarterHerd.Any(e => e == null || !AllSpecies.Contains(e.Species) ||
                                            string.IsNullOrEmpty(e.Prefab) || e.Count < 0))
                problems.Add("each Starter herd entry needs Species cattle or sheep, a Prefab, and a Count of 0 or " +
                             "more");
            if (c.SweptItems == null || c.HerdPoint == null || c.WaterSourceRoles == null ||
                c.GroundCheckRoles == null || c.SlotPlan == null || c.FridgeItems == null)
                problems.Add("a list or table setting is null");
            else if (!Finite(c.HerdPoint.X) || !Finite(c.HerdPoint.Z))
                problems.Add("the Herd spawn point must be finite");
            if (string.IsNullOrEmpty(c.FallbackFeed))
                problems.Add("Fallback feed shortname is empty");
            if (c.CloneGenes == null || c.CloneGenes.Length != 6 ||
                c.CloneGenes.Any(g => "GYHWX".IndexOf(char.ToUpperInvariant(g)) < 0))
                problems.Add("Clone genes must be six of the letters G, Y, H, W, and X");
            CheckRange(problems, "Upkeep interval (seconds)", c.UpkeepSeconds, 1f);
            CheckRange(problems, "Farm radius", c.FarmRadius, 1f);
            CheckRange(problems, "Herd spawn spread (m)", c.HerdSpread, 0f);
            CheckRange(problems, "Herd spawn delay after build (seconds)", c.HerdSpawnDelay, 0f);
            CheckRange(problems, "Cattle cap", c.CattleCap, 0f);
            CheckRange(problems, "Sheep cap", c.SheepCap, 0f);
            CheckRange(problems, "Breeding fallback after minutes", c.BreedingFallbackMinutes, 0f);
            CheckRange(problems, "Breeding fallback retry minutes", c.BreedingRetryMinutes, 0f);
            CheckRange(problems, "Need threshold (0-1)", c.NeedThreshold, 0f, 1f);
            CheckRange(problems, "Need restore value (0-1)", c.NeedRestore, c.NeedThreshold, 1f);
            CheckRange(problems, "Trough feed minimum", c.TroughFeedMinimum, 0f, c.TroughFeedTarget);
            CheckRange(problems, "Trough water minimum", c.TroughWaterMinimum, 0f, c.TroughWaterTarget);
            CheckRange(problems, "Coop feed minimum", c.CoopFeedMinimum, 0f, c.CoopFeedTarget);
            CheckRange(problems, "Coop water minimum", c.CoopWaterMinimum, 0f, c.CoopWaterTarget);
            CheckRange(problems, "Recreate clones after (minutes)", c.CloneFallbackMinutes, 0f);
            CheckRange(problems, "Planter dry threshold", c.DryFraction, 0f, 1f);
            CheckRange(problems, "Planter dry rescue after (minutes)", c.DryMinutes, 0f);
            CheckRange(problems, "Composter horsedung stock", c.ComposterDung, 0f);
            CheckRange(problems, "Ground check tolerance (m)", c.GroundTolerance, 0f);
            CheckRange(problems, "Gate close distance (m)", c.GateCloseDistance, 0f);
            CheckRange(problems, "Cull corpse timeout (seconds)", c.CorpseTimeout, 1f);
            CheckRange(problems, "Repeat an identical failure log after minutes", c.FailureRepeatMinutes, 0f);
            CheckRange(problems, "Repeat a full container log after minutes", c.FullRepeatMinutes, 0f);
            CheckRange(problems, "Status events kept per kind", c.EventsPerKind, 1f);
            return problems;
        }

        private static void CheckRange(List<string> problems, string name, float value, float min,
            float max = float.MaxValue)
        {
            if (value >= min && value <= max)
                return;
            problems.Add(max >= float.MaxValue
                ? $"{name} must be {min} or more"
                : $"{name} must be between {min} and {max}");
        }

        #endregion

        #region Data

        private class StoredData
        {
            public bool Built;
            // World.SaveFileName and SaveRestore.SaveCreatedTime (epoch seconds) of the world the farm was built in.
            public string SaveFile;
            public int SaveCreated;
            public bool HerdSpawned;
            // Animals attempted per "Starter herd" entry, counted before each spawn.
            public List<int> StarterDone = new List<int>();
            public float CentreX;
            public float CentreZ;
            public float Yaw;
            public ulong Owner;
            public List<StructureRecord> Structures = new List<StructureRecord>();
            public List<AnimalRecord> Herd = new List<AnimalRecord>();
            public Dictionary<string, float> SecondsSincePregnancy = new Dictionary<string, float>();
            // Pregnant females per species at the last pass, so a reload does not count them as new pregnancies.
            public Dictionary<string, List<ulong>> PregnantIds = new Dictionary<string, List<ulong>>();
            public Dictionary<string, ReseedState> Reseed = new Dictionary<string, ReseedState>();
            // Animals a player led or carried past the farm radius; never adopted, returned, or culled again.
            public List<ulong> Released = new List<ulong>();
            // Juveniles sex safety spawned; outside the caps until they grow up.
            public List<ulong> CapExempt = new List<ulong>();
            public Dictionary<string, int> Collected = new Dictionary<string, int>();
            public List<EventRecord> Events = new List<EventRecord>();
        }

        private class StructureRecord
        {
            public string Id;
            public string Role;
            public string Prefab;
            public ulong NetId;
            public float X;
            public float Y;
            public float Z;
            public float Yaw;
            public string Failure;
            public bool Missing;
            // Why the last try deferred the spawn (a player or animal in the spot); not saved.
            [JsonIgnore]
            public string Waiting;
        }

        private class AnimalRecord
        {
            public ulong NetId;
            public string Prefab;
        }

        private class EventRecord
        {
            public string Time;
            public string Kind;
            public string Text;
        }

        private class PendingCull
        {
            public string Description;
            public float Since;
        }

        // Persisted, so a reload or restart cannot end a backoff early.
        private class ReseedState
        {
            public float Wait = ReseedBaseMinutes;
            // Epoch seconds of the last reseed; 0 means none yet.
            public long At;
            public List<ulong> Ids = new List<ulong>();
            public bool Check;
        }

        private class RepairState
        {
            public float Wait;
            public float NextAt;
            // When auto-repair last placed the entry, and when it was last found missing (realtime seconds).
            public float PlacedAt = float.MinValue;
            public float LostAt = float.MinValue;
            public bool LossSeen;
            // A player or animal stood in the spot on the last try.
            public bool Waiting;
        }

        // Soil under the dry threshold since Since, starting at Start ml; Last is the level at the previous pass.
        private class DryWatch
        {
            public float Since;
            public int Start;
            public int Last;
        }

        private string DataPath => $"{Name}/{ConVar.Server.identity}";

        private string DataFile => Interface.Oxide.DataFileSystem.GetFile(DataPath).Filename;

        // A file that does not load is moved aside, never replaced; the farm stays disabled until a reload.
        private void LoadData()
        {
            _data = new StoredData();
            if (!Interface.Oxide.DataFileSystem.ExistsDatafile(DataPath))
                return;
            StoredData loaded = null;
            string problem = "the file is empty";
            try
            {
                loaded = Interface.Oxide.DataFileSystem.ReadObject<StoredData>(DataPath);
            }
            catch (Exception e)
            {
                problem = e.Message;
            }
            if (loaded != null)
            {
                int dropped = Normalize(loaded);
                if (dropped > 0)
                    PrintWarning($"load: dropped {dropped} structure records with no id; they match no layout entry");
                _data = loaded;
                return;
            }
            _dataUnwritable = true;
            try
            {
                string aside = SidePath(DataFile, "corrupt");
                File.Move(DataFile, aside);
                Disable($"data/{DataPath}.json did not load ({problem}) and was moved to {aside}; repair it and " +
                        "move it back, or leave it there and run farm.build after c.reload LivestockFarm to adopt " +
                        "the standing farm");
            }
            catch (Exception e)
            {
                Disable($"data/{DataPath}.json did not load ({problem}) and could not be moved aside ({e.Message}); " +
                        "nothing is written over it until c.reload LivestockFarm");
            }
        }

        // Returns how many structure records without an id it dropped.
        private static int Normalize(StoredData data)
        {
            int before = data.Structures?.Count(r => r != null) ?? 0;
            data.Structures = data.Structures?.Where(r => r != null && !string.IsNullOrEmpty(r.Id)).ToList() ??
                              new List<StructureRecord>();
            data.Herd = data.Herd?.Where(r => r != null).ToList() ?? new List<AnimalRecord>();
            data.StarterDone = data.StarterDone ?? new List<int>();
            data.SecondsSincePregnancy = data.SecondsSincePregnancy ?? new Dictionary<string, float>();
            data.PregnantIds = data.PregnantIds ?? new Dictionary<string, List<ulong>>();
            data.Reseed = data.Reseed ?? new Dictionary<string, ReseedState>();
            data.Released = data.Released ?? new List<ulong>();
            data.Collected = data.Collected ?? new Dictionary<string, int>();
            data.Events = data.Events?.Where(e => e != null).ToList() ?? new List<EventRecord>();
            data.CapExempt = data.CapExempt ?? new List<ulong>();
            return before - data.Structures.Count;
        }

        // Written only on server save and unload; VerifyRecord catches a record that a crash left ahead of the world.
        private void SaveData()
        {
            if (_data == null || _dataUnwritable)
                return;
            _data.Herd = _animals.Select(p => new AnimalRecord { NetId = p.Key, Prefab = p.Value }).ToList();
            _data.PregnantIds = _pregnantLastPass.ToDictionary(p => p.Key, p => p.Value.ToList());
            try
            {
                Interface.Oxide.DataFileSystem.WriteObject(DataPath, _data);
            }
            catch (Exception e)
            {
                PrintError($"could not write data/{DataPath}.json: {e}");
            }
        }

        private static string SidePath(string path, string tag)
        {
            string stamp = DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
            return Path.Combine(Path.GetDirectoryName(path) ?? string.Empty,
                $"{Path.GetFileNameWithoutExtension(path)}.{tag}-{stamp}.json");
        }

        // NetIds are reissued after a crash or a restored backup, so the record is trusted only for the world it was
        // written in, and each id only while it resolves to the recorded prefab and the farm owner.
        private void VerifyRecord()
        {
            string saveFile = World.SaveFileName;
            int created = Epoch.FromDateTime(SaveRestore.SaveCreatedTime);
            if (_data.SaveFile != saveFile || _data.SaveCreated != created)
            {
                Stale($"it was written for {_data.SaveFile ?? "an unrecorded world"} (created " +
                      $"{EpochText(_data.SaveCreated)}), but the server runs {saveFile} (created " +
                      $"{EpochText(created)})");
                return;
            }
            var missing = new List<string>();
            var back = new List<string>();
            int fences = 0;
            int fencesGone = 0;
            bool cupboard = false;
            foreach (StructureRecord rec in _data.Structures)
            {
                if (rec.NetId == 0)
                    continue;
                bool fence = rec.Role == RoleFence || rec.Role == RoleGate;
                if (fence)
                    fences++;
                if (ResolveStructure(rec) == null)
                {
                    if (fence)
                        fencesGone++;
                    missing.Add(rec.Id);
                    rec.Missing = true;
                    MarkLost(rec);
                    continue;
                }
                if (rec.Missing)
                    back.Add(rec.Id);
                rec.Missing = false;
                _structureIds.Add(rec.NetId);
                cupboard |= rec.Role == RoleCupboard;
            }
            if (!cupboard && fences > 0 && 2 * fencesGone > fences)
            {
                _structureIds.Clear();
                Stale($"neither the cupboard nor {fencesGone} of {fences} fences and gates resolve to farm entities " +
                      "(a backup from before farm.build restored?)");
                return;
            }
            var gone = new List<ulong>();
            foreach (AnimalRecord rec in _data.Herd)
            {
                if (ResolveAnimal(rec.NetId, rec.Prefab) != null)
                    _animals[rec.NetId] = rec.Prefab;
                else
                    gone.Add(rec.NetId);
            }
            _pregnantLastPass.Clear();
            foreach (KeyValuePair<string, List<ulong>> pair in _data.PregnantIds)
                if (pair.Value != null)
                    _pregnantLastPass[pair.Key] = new HashSet<ulong>(pair.Value);
            _data.Released.RemoveAll(released => !(FindEntity(released) is LivestockAnimal));
            _recordChecked = true;
            if (missing.Count > 0)
                PrintWarning($"load: {missing.Count} structures do not resolve to their prefab and owner (destroyed, " +
                             $"or their ids reissued), and {RepairHint(true)}: {string.Join(", ", missing)}");
            if (back.Count > 0)
                Puts($"load: {back.Count} structures recorded as missing resolve again: {string.Join(", ", back)}");
            if (gone.Count > 0)
                PrintWarning($"load: {gone.Count} recorded animals do not resolve to their prefab and owner: " +
                             string.Join(", ", gone));
        }

        // Moves the record file aside as loaded, so a crash before the next save cannot find it stale again, then
        // starts clean; farm.build afterwards adopts a standing farm.
        private void Stale(string why)
        {
            string archived;
            try
            {
                string archive = SidePath(DataFile, "stale");
                if (File.Exists(DataFile))
                {
                    File.Move(DataFile, archive);
                    archived = $"moved it to {archive} and started clean";
                }
                else
                {
                    archived = "found no data file to archive and started clean";
                }
            }
            catch (Exception e)
            {
                _dataUnwritable = true;
                archived = $"could not move it aside ({e.Message}), so it stays as it is until c.reload LivestockFarm";
                Disable($"the stale farm record could not be moved aside: {e.Message}");
            }
            ResetState();
            _data = new StoredData();
            string text = $"the farm record of identity {ConVar.Server.identity} is stale: {why}. Livestock Farm " +
                          $"{archived}; the farm is untracked and unprotected until farm.build builds it or adopts " +
                          "the one standing";
            PrintError(text);
            Record("stale", text);
        }

        private static string EpochText(int seconds) =>
            seconds == 0
                ? "unrecorded"
                : Epoch.ToDateTime(seconds).ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture);

        #endregion

        #region Hooks

        private class Step
        {
            public string Name;
            public bool NeedsOwner;
            public Action<UpkeepPass> Run;
        }

        private void Init()
        {
            if (_config == null)
                RejectConfig("the config did not load");
            LoadData();
            // Cull runs last: its room check must see the fridge and boxes as the corpse harvest will find them.
            _steps = new List<Step>
            {
                new Step { Name = "structures", Run = StructureStep },
                new Step { Name = "wiring", Run = WiringStep },
                new Step { Name = "sweep", Run = SweepStep },
                new Step { Name = "herd", NeedsOwner = true, Run = HerdStep },
                new Step { Name = "sex safety", NeedsOwner = true, Run = SexStep },
                new Step { Name = "breeding", NeedsOwner = true, Run = BreedingStep },
                new Step { Name = "crops", Run = CropStep },
                new Step { Name = "compost", Run = CompostStep },
                new Step { Name = "chickens", Run = ChickenStep },
                new Step { Name = "troughs", Run = TroughStep },
                new Step { Name = "spoiled produce", Run = SpoiledStep },
                new Step { Name = "cull", NeedsOwner = true, Run = CullStep },
            };
        }

        // RCON runs commands during world load (SaveRestore calls RCon.Update), so farm.* waits for this to finish.
        private void OnServerInitialized()
        {
            try
            {
                LoadFarm();
            }
            finally
            {
                _ready = true;
            }
        }

        private void LoadFarm()
        {
            _upkeepTimer = timer.Every(Mathf.Max(5f, _config.UpkeepSeconds), Upkeep);
            foreach (BaseNetworkable networkable in BaseNetworkable.serverEntities)
            {
                var animal = networkable as LivestockAnimal;
                if (animal != null)
                    _livestock.Add(animal);
            }
            if (!_data.Built)
                return;
            try
            {
                VerifyRecord();
            }
            catch (Exception e)
            {
                // The record is unproven, so the next save must not write the reset state over it.
                _dataUnwritable = true;
                ResetState();
                Disable($"checking the farm record against the world threw {e}; the record file is left as it was");
                return;
            }
            if (!_data.Built)
                return;
            TrackDroppedItems();
            int linked = 0;
            try
            {
                linked = RelinkAll(BeginPass());
            }
            catch (Exception e)
            {
                Failure("relink", $"relinking animals at load threw {e}");
            }
            Puts($"loaded farm for identity {ConVar.Server.identity}: {_structureIds.Count} structures, " +
                 $"{_animals.Count} animals ({linked} relinked)");
            if (!_data.HerdSpawned)
                ScheduleHerd();
        }

        private void Unload()
        {
            _unloaded = true;
            _upkeepTimer?.Destroy();
            _herdTimer?.Destroy();
            SaveData();
        }

        private void OnServerSave() => SaveData();

        private void OnNewSave(string filename)
        {
            if (_data != null && _data.Built)
            {
                Stale($"the server generated a new world ({filename})");
                return;
            }
            ResetState();
            _data = new StoredData();
        }

        private void OnEntitySpawned(BaseNetworkable entity)
        {
            var animal = entity as LivestockAnimal;
            if (animal != null)
                _livestock.Add(animal);
            if (Rust.Application.isLoadingSave || _data == null || !_data.Built)
                return;
            if (animal != null)
            {
                _justSpawned.Add(animal);
                if (_justSpawned.Count == 1)
                    NextTick(() =>
                    {
                        if (!_unloaded)
                            _justSpawned.Clear();
                    });
                if (!_animals.ContainsKey(IdOf(animal)))
                    NextTick(() =>
                    {
                        if (!_unloaded)
                            Guard("link", Describe(animal), () => ConsiderNewAnimal(animal));
                    });
                return;
            }
            var dropped = entity as DroppedItem;
            if (dropped != null)
            {
                TrackDropped(dropped);
                return;
            }
            var corpse = entity as BaseCorpse;
            if (corpse == null || _pendingCulls.Count == 0)
                return;
            ulong parentId = IdOf(corpse.parentEnt);
            PendingCull cull;
            if (parentId == 0 || !_pendingCulls.TryGetValue(parentId, out cull))
                return;
            _pendingCulls.Remove(parentId);
            NextTick(() =>
            {
                if (!_unloaded)
                    Guard("cull", cull.Description, () => HarvestCorpse(corpse, cull));
            });
        }

        private void OnEntityKill(BaseNetworkable entity)
        {
            var animal = entity as LivestockAnimal;
            if (animal != null)
                _livestock.Remove(animal);
            var dropped = entity as DroppedItem;
            if (dropped != null)
                _dropped.Remove(dropped);
            ulong id = IdOf(entity);
            if (id == 0 || _data == null)
                return;
            if (animal != null && _data.Released.Remove(id))
            {
                // A released juvenile that grows (or swaps sex) keeps its release under the successor's new net id,
                // recorded before the successor's NextTick link check runs.
                LivestockAnimal successor = SuccessorOf(animal);
                if (successor != null)
                    _data.Released.Add(IdOf(successor));
                return;
            }
            if (_animals.Remove(id))
            {
                if (animal == null || _removing || animal.IsDead())
                    return;
                // LivestockAnimal.ReplaceWith (growth, livestock.swapgender) spawns the successor on the same spot
                // in the same frame before it kills the old entity; NextTick links the successor.
                LivestockAnimal successor = SuccessorOf(animal);
                if (successor != null)
                {
                    if (animal.IsInfant())
                        _growingNames.Add(animal.AnimalName ?? string.Empty);
                    // A reseeded juvenile stays outside the cap through a sex swap, and counts once it grows up.
                    if (_data.CapExempt.Remove(id) && successor.IsInfant())
                        _data.CapExempt.Add(IdOf(successor));
                    return;
                }
                _relinkWanted = true;
                Event("death", $"{Describe(animal)} was removed without dying", true);
                return;
            }
            if (!_structureIds.Remove(id) || _removing)
                return;
            StructureRecord rec = _data.Structures.FirstOrDefault(r => r.NetId == id);
            if (rec != null && !rec.Missing)
            {
                rec.Missing = true;
                MarkLost(rec);
                Failure($"gone:{rec.Id}", $"structure {rec.Id} ({rec.Role}) was destroyed, and {RepairHintFor(rec)}");
            }
            // The coop's chickens (its children) and hatching eggs go with it.
            var coop = entity as ChickenCoop;
            int chickens = coop?.Animals.Count(s => s.SpawnedAnimal.IsSet) ?? 0;
            int eggs = (coop?.Animals.Count ?? 0) - chickens;
            if (rec != null && chickens + eggs > 0)
                Event("death", $"{rec.Id} was destroyed with {chickens} chickens" +
                               (eggs > 0 ? $" and {eggs} hatching eggs" : string.Empty), true);
        }

        // The animal LivestockAnimal.ReplaceWith spawned on the same spot in this frame, if any.
        private LivestockAnimal SuccessorOf(LivestockAnimal animal)
        {
            Vector3 spot = animal.transform.position;
            return _justSpawned.FirstOrDefault(a =>
                a != null && a != animal && (a.transform.position - spot).sqrMagnitude < 0.01f);
        }

        private void OnEntityDeath(BaseCombatEntity entity, HitInfo info)
        {
            ulong id = IdOf(entity);
            if (id == 0 || _data == null || !_data.Built)
                return;
            var animal = entity as LivestockAnimal;
            if (animal != null && _animals.ContainsKey(id) && !_pendingCulls.ContainsKey(id))
            {
                BasePlayer killer = info?.InitiatorPlayer;
                Event("death", $"{Describe(animal)} died: {LivestockCensus.CauseOf(info)}" +
                               (killer != null ? $" by {killer.displayName}" : string.Empty));
                return;
            }
            var chicken = entity as FarmableAnimal;
            BaseEntity coop = chicken?.GetParentEntity();
            if (coop != null && _structureIds.Contains(IdOf(coop)))
                Event("death", $"chicken {chicken.AnimalName} [{id}] in {NameOf(coop)} died");
        }

        // Real players may still hurt the farm; NPCs, animals, decay, the environment, and coop chicken unhappiness
        // (Hurt) may not. Livestock neglect never gets here: TickCondition uses SetHealth, the deaths call Die.
        private object OnEntityTakeDamage(BaseCombatEntity entity, HitInfo info)
        {
            if (entity == null || !IsProtected(entity))
                return null;
            BasePlayer attacker = info?.InitiatorPlayer;
            if (attacker != null && !attacker.IsNpc)
                return null;
            return true;
        }

        // Carbon's IOnNpcTarget calls this from Rust.Ai.Gen2.SenseComponent.CanTarget (non-null: false) and
        // BaseNpc.GetWantsToAttack (non-null: 0); HumanNPC and AIBrainSenses skip the target on non-null.
        private object OnNpcTarget(BaseEntity npc, BaseEntity target)
        {
            ulong id = IdOf(target);
            if (id == 0 || !_animals.ContainsKey(id))
                return null;
            return true;
        }

        private object OnEntityGroundMissing(BaseEntity entity)
        {
            if (entity == null || !IsProtected(entity))
                return null;
            Failure($"ground:{IdOf(entity)}", $"kept {NameOf(entity)} after its ground went missing");
            return true;
        }

        private bool IsProtected(BaseEntity entity)
        {
            ulong id = IdOf(entity);
            if (id == 0)
                return false;
            if (_structureIds.Contains(id) || _animals.ContainsKey(id))
                return true;
            BaseEntity parent = entity.GetParentEntity();
            return parent != null && _structureIds.Contains(IdOf(parent));
        }

        #endregion

        #region Commands

        [ConsoleCommand("farm.build")]
        private void BuildCommand(ConsoleSystem.Arg arg)
        {
            if (!Authorized(arg) || Refused(arg))
                return;
            if (_data.Built)
            {
                arg.ReplyWith($"identity {ConVar.Server.identity} already has a farm at ({_data.CentreX:0.0}, " +
                              $"{_data.CentreZ:0.0}); run farm.remove confirm first");
                return;
            }
            int count = arg.Args?.Length ?? 0;
            float x = _config.Layout.Centre.X;
            float z = _config.Layout.Centre.Z;
            float yaw = _config.Layout.Yaw;
            if (count == 1 || count > 3 ||
                (count >= 2 && (!TryParse(arg.GetString(0), out x) || !TryParse(arg.GetString(1), out z))) ||
                (count == 3 && !TryParse(arg.GetString(2), out yaw)))
            {
                arg.ReplyWith("usage: farm.build [x z [yaw]]");
                return;
            }
            string problem = BuildSpotProblem(x, z, yaw);
            if (problem != null)
            {
                arg.ReplyWith($"farm.build refused: {problem}");
                return;
            }
            ulong owner = FarmOwner();
            if (owner == 0)
            {
                Failure("owner", "farm.build refused: no Owner SteamID in the config and no owner in cfg/users.cfg");
                arg.ReplyWith("no owner: set Owner SteamID in the config or add an owner to cfg/users.cfg");
                return;
            }
            arg.ReplyWith(Build(x, z, yaw, owner));
        }

        [ConsoleCommand("farm.status")]
        private void StatusCommand(ConsoleSystem.Arg arg)
        {
            if (Authorized(arg))
                arg.ReplyWith(Status());
        }

        [ConsoleCommand("farm.tick")]
        private void TickCommand(ConsoleSystem.Arg arg)
        {
            if (!Authorized(arg) || Refused(arg))
                return;
            if (!_data.Built)
            {
                arg.ReplyWith("no farm built");
                return;
            }
            Upkeep();
            arg.ReplyWith("upkeep pass done; the log has what it changed");
        }

        [ConsoleCommand("farm.relink")]
        private void RelinkCommand(ConsoleSystem.Arg arg)
        {
            if (!Authorized(arg) || Refused(arg))
                return;
            if (!_data.Built)
            {
                arg.ReplyWith("no farm built");
                return;
            }
            UpkeepPass pass = BeginPass();
            int linked = RelinkAll(pass);
            foreach (LivestockAnimal animal in pass.Animals)
                Bond(animal, pass.Owner, pass.Cupboard);
            arg.ReplyWith($"relinked {linked} animals; tracking {_animals.Count} animals and " +
                          $"{_structureIds.Count} structures");
        }

        [ConsoleCommand("farm.repair")]
        private void RepairCommand(ConsoleSystem.Arg arg)
        {
            if (!Authorized(arg) || Refused(arg))
                return;
            if (!_data.Built)
            {
                arg.ReplyWith("no farm built");
                return;
            }
            ulong owner = FarmOwner();
            if (owner == 0)
            {
                arg.ReplyWith("no owner: set Owner SteamID in the config or add an owner to cfg/users.cfg");
                return;
            }
            arg.ReplyWith(Repair(owner));
        }

        [ConsoleCommand("farm.remove")]
        private void RemoveCommand(ConsoleSystem.Arg arg)
        {
            if (!Authorized(arg) || Refused(arg))
                return;
            if (arg.Args == null || arg.Args.Length != 1 || arg.GetString(0) != "confirm")
            {
                arg.ReplyWith("farm.remove confirm drops every farm container's items, kills every farm entity, " +
                              "animals included, and clears the record");
                return;
            }
            arg.ReplyWith(Remove());
        }

        private static bool Authorized(ConsoleSystem.Arg arg)
        {
            if (arg.Connection == null || arg.Connection.authLevel >= 2)
                return true;
            arg.ReplyWith("farm commands need RCON, the server console, or auth level 2");
            return false;
        }

        private bool Refused(ConsoleSystem.Arg arg)
        {
            if (!_ready)
            {
                arg.ReplyWith(NotReady);
                return true;
            }
            if (_disabled == null)
                return false;
            arg.ReplyWith($"Livestock Farm is disabled: {_disabled}");
            return true;
        }

        private const string NotReady =
            "the farm record is not checked yet; try again after the server finishes loading";

        // Hammer pickup, and Remover Tool's refund, would hand out the item while auto-repair spawns a new one.
        private object CanPickupEntity(BasePlayer player, BaseCombatEntity entity) =>
            entity != null && _structureIds.Contains(IdOf(entity)) ? (object)false : null;

        // Remover Tool (server/carbon/plugins/RemoverTool.cs) shows a returned string as the reason it refuses.
        private object canRemove(BasePlayer player, BaseEntity entity) => RemoveRefusal(entity);

        private object CanAdminRemove(BasePlayer player, BaseEntity entity, string removeType) => RemoveRefusal(entity);

        private object RemoveRefusal(BaseEntity entity) =>
            entity != null && _structureIds.Contains(IdOf(entity))
                ? "part of the livestock farm: remove it from the farm layout in the config"
                : null;

        private static bool TryParse(string text, out float value) =>
            float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);

        // The centre and every layout entry must land on the map; float.TryParse accepts NaN and Infinity.
        private string BuildSpotProblem(float x, float z, float yaw)
        {
            if (!Finite(x) || !Finite(z) || !Finite(yaw))
                return "x, z, and yaw must be finite numbers";
            Vector3 min = TerrainMeta.Position;
            Vector3 max = TerrainMeta.Position + TerrainMeta.Size;
            string bounds = $"the map spans {min.x:0} to {max.x:0} on x and {min.z:0} to {max.z:0} on z";
            if (x < min.x || x > max.x || z < min.z || z > max.z)
                return $"({x:0.0}, {z:0.0}) lies outside the map; {bounds}";
            foreach (LayoutEntry entry in _config.Layout.Entries)
            {
                Vector3 offset = Quaternion.Euler(0f, yaw, 0f) * new Vector3(entry.X, 0f, entry.Z);
                float px = x + offset.x;
                float pz = z + offset.z;
                if (px < min.x || px > max.x || pz < min.z || pz > max.z)
                    return $"layout entry {entry.Id} would land at ({px:0.0}, {pz:0.0}), outside the map; {bounds}";
            }
            return null;
        }

        #endregion

        #region Build, repair, and remove

        private enum PlaceResult
        {
            Skipped,
            Placed,
            Adopted,
            Failed,
            Deferred,
        }

        private class PlaceTally
        {
            public int Placed;
            public int Adopted;
            public int Failed;
            public int Skipped;
            public readonly List<string> FailedIds = new List<string>();
            public readonly List<string> DeferredIds = new List<string>();
            public readonly List<string> InPlaceIds = new List<string>();
            public readonly Dictionary<string, PlaceResult> Results = new Dictionary<string, PlaceResult>();

            public void Add(PlaceResult result, string id)
            {
                Results[id] = result;
                if (result == PlaceResult.Placed || result == PlaceResult.Adopted)
                    InPlaceIds.Add(id);
                switch (result)
                {
                    case PlaceResult.Placed:
                        Placed++;
                        break;
                    case PlaceResult.Adopted:
                        Adopted++;
                        break;
                    case PlaceResult.Failed:
                        Failed++;
                        FailedIds.Add(id);
                        break;
                    case PlaceResult.Deferred:
                        DeferredIds.Add(id);
                        break;
                    default:
                        Skipped++;
                        break;
                }
            }
        }

        // The record is complete and the herd scheduled before the optional wiring and starter clones run.
        private string Build(float x, float z, float yaw, ulong owner)
        {
            _herdTimer?.Destroy();
            _herdTimer = null;
            ResetState();
            _data = new StoredData
            {
                Built = true,
                SaveFile = World.SaveFileName,
                SaveCreated = Epoch.FromDateTime(SaveRestore.SaveCreatedTime),
                CentreX = x,
                CentreZ = z,
                Yaw = yaw,
                Owner = owner,
            };
            _clearedVegetation.Clear();
            List<LayoutEntry> ordered = Ordered(_config.Layout.Entries);
            var tally = new PlaceTally();
            foreach (LayoutEntry entry in ordered)
            {
                var rec = new StructureRecord { Id = entry.Id, Role = entry.Role, Prefab = entry.Prefab };
                _data.Structures.Add(rec);
                tally.Add(PlaceGuarded(entry, rec, owner), entry.Id);
            }
            int inPlace = tally.Placed + tally.Adopted;
            Event("build", $"farm at ({x:0.0}, {z:0.0}) yaw {yaw:0} for owner {OwnerLabel(owner)}: {inPlace} of " +
                           $"{ordered.Count} layout entries in place ({tally.Placed} placed, {tally.Adopted} " +
                           $"adopted), {tally.Failed} failed, {tally.Skipped} placeholders skipped");
            LogClearedVegetation("build");
            foreach (string id in tally.FailedIds)
                BackOff(id, Time.realtimeSinceStartup);
            _recordChecked = true;
            TrackDroppedItems();
            ScheduleHerd();
            Guard("build", "wiring", () => WiringStep(BeginPass()));
            Guard("build", "starter clones", () => StockStarterClones(BeginPass()));
            return $"farm built at ({x:0.0}, {z:0.0}) yaw {yaw:0}: {inPlace} of {ordered.Count} entries in place " +
                   $"({tally.Placed} placed, {tally.Adopted} adopted), {tally.Failed} failed (farm.status lists " +
                   $"them); the starter herd spawns in {Mathf.Max(1f, _config.HerdSpawnDelay):0} s";
        }

        // Respawns, or adopts, every layout entry whose structure is missing, failed, or never recorded, then wires.
        // An explicit repair ignores and clears the auto-repair backoff.
        private string Repair(ulong owner)
        {
            BeginPass();
            List<LayoutEntry> todo = MissingEntries(true);
            var tally = new PlaceTally();
            if (todo.Count > 0)
                tally = PlaceMissing(todo, owner, "repair");
            foreach (string id in tally.InPlaceIds)
                _repairs.Remove(id);
            foreach (string id in tally.FailedIds)
                BackOff(id, Time.realtimeSinceStartup);
            foreach (string id in tally.DeferredIds)
                RepairOf(id).Waiting = true;
            Guard("repair", "wiring", () => WiringStep(BeginPass()));
            string text = todo.Count == 0
                ? "every layout entry is in place"
                : $"{todo.Count} layout entries were missing or had failed: {tally.Placed} placed, " +
                  $"{tally.Adopted} adopted, {tally.Failed} failed" +
                  (tally.FailedIds.Count > 0 ? $" ({string.Join(", ", tally.FailedIds)})" : string.Empty) +
                  (tally.DeferredIds.Count > 0
                      ? $", {tally.DeferredIds.Count} waiting for the spot to clear " +
                        $"({string.Join(", ", tally.DeferredIds)})"
                      : string.Empty);
            Event("repair", text);
            return $"repair: {text}; wiring checked (farm.status shows it)";
        }

        // Layout entries, in build order, whose structure is missing or failed to place; with includeNew (farm.repair)
        // also the entries the farm never placed.
        private List<LayoutEntry> MissingEntries(bool includeNew)
        {
            Dictionary<string, StructureRecord> records = RecordsById();
            return Ordered(_config.Layout.Entries)
                .Where(e =>
                {
                    StructureRecord rec;
                    if (e.Role == RolePlaceholder)
                        return false;
                    if (!includeNew && NeverPlaced(e, records))
                        return false;
                    return !records.TryGetValue(e.Id, out rec) || rec.NetId == 0 || rec.Missing;
                })
                .ToList();
        }

        private Dictionary<string, StructureRecord> RecordsById()
        {
            var records = new Dictionary<string, StructureRecord>();
            foreach (StructureRecord rec in _data.Structures)
                if (rec.Id != null && !records.ContainsKey(rec.Id))
                    records[rec.Id] = rec;
            return records;
        }

        // No record, a record never attempted (an old placeholder), or an "On" entry whose block was never placed:
        // only farm.repair places these, so auto-repair neither tries nor logs them.
        private static bool NeverPlaced(LayoutEntry entry, Dictionary<string, StructureRecord> records)
        {
            StructureRecord rec;
            if (!records.TryGetValue(entry.Id, out rec) || Unplaced(rec))
                return true;
            StructureRecord under;
            return !string.IsNullOrEmpty(entry.On) && (!records.TryGetValue(entry.On, out under) || Unplaced(under));
        }

        private static bool Unplaced(StructureRecord rec) =>
            rec.Role == RolePlaceholder || (rec.NetId == 0 && rec.Failure == null && !rec.Missing);

        // The farm.build placement path for the given entries, reusing their records.
        private PlaceTally PlaceMissing(List<LayoutEntry> entries, ulong owner, string kind)
        {
            _clearedVegetation.Clear();
            var tally = new PlaceTally();
            _deferOccupied = true;
            try
            {
                foreach (LayoutEntry entry in entries)
                {
                    StructureRecord rec = _data.Structures.FirstOrDefault(r => r.Id == entry.Id);
                    if (rec == null)
                    {
                        rec = new StructureRecord { Id = entry.Id };
                        _data.Structures.Add(rec);
                    }
                    tally.Add(PlaceGuarded(entry, rec, owner), entry.Id);
                }
            }
            finally
            {
                _deferOccupied = false;
            }
            _penHull = null;
            LogClearedVegetation(kind);
            Guard(kind, "cupboard", ReattachCupboard);
            return tally;
        }

        // Every upkeep pass re-places layout entries the farm placed before and lost, or failed to place (foundations
        // first, then the cupboard, which is reattached), once VerifyRecord or farm.build vouched for the record.
        // Entries the farm never placed wait for farm.repair. A failed placement waits 10 minutes, doubling up to 2
        // hours; a loss is weighed in WeighLoss; its lines log at most hourly. Fences and the gate never wait: a gap
        // in the line lets the herd out. A spot with a player or animal in it is retried on the next pass.
        private bool AutoRepair(ulong owner)
        {
            if (!_config.AutoRepair || !_recordChecked || owner == 0)
                return false;
            float now = Time.realtimeSinceStartup;
            List<LayoutEntry> missing = MissingEntries(false);
            foreach (LayoutEntry entry in missing.Where(e => !IsPenLine(e.Role)))
                WeighLoss(entry, now);
            List<LayoutEntry> due = missing
                .Where(e => IsPenLine(e.Role) || RepairOf(e.Id).NextAt <= now)
                .ToList();
            if (due.Count == 0)
                return false;
            PlaceTally tally;
            _quietPlacement = true;
            try
            {
                tally = PlaceMissing(due, owner, "auto-repair");
            }
            finally
            {
                _quietPlacement = false;
            }
            var quiet = new HashSet<string>();
            foreach (LayoutEntry entry in due)
            {
                RepairState state = RepairOf(entry.Id);
                PlaceResult result;
                tally.Results.TryGetValue(entry.Id, out result);
                state.Waiting = result == PlaceResult.Deferred;
                if (state.Waiting)
                    continue;
                if (result == PlaceResult.Placed || result == PlaceResult.Adopted)
                {
                    // A fence that keeps coming back reports through this hourly line, not every pass.
                    if (IsPenLine(entry.Role) && now - state.PlacedAt < ChurnSeconds)
                    {
                        quiet.Add(entry.Id);
                        Failure($"autorepair:{entry.Id}", $"auto-repair: {entry.Id} ({entry.Role}) was lost again " +
                                                          $"within {ChurnSeconds / 60f:0} min of its last repair; " +
                                                          "fences and the gate are re-placed every pass", "repair",
                            60f);
                    }
                    state.PlacedAt = now;
                    continue;
                }
                string failure = _data.Structures.FirstOrDefault(r => r.Id == entry.Id)?.Failure ?? "unknown";
                if (IsPenLine(entry.Role))
                {
                    Failure($"autorepair:{entry.Id}", $"auto-repair of {entry.Id} ({entry.Role}) failed: {failure}; " +
                                                      "fences and the gate retry every pass", "repair", 60f);
                    continue;
                }
                BackOff(entry.Id, now);
                Failure($"autorepair:{entry.Id}", $"auto-repair of {entry.Id} ({entry.Role}) failed: {failure}; the " +
                                                  $"next try is in {state.Wait:0} min", "repair", 60f);
            }
            List<string> back = tally.InPlaceIds.Where(id => !quiet.Contains(id)).ToList();
            if (back.Count > 0)
                Event("repair", $"auto-repair: {string.Join(", ", back)} back in place ({tally.Placed} placed, " +
                                $"{tally.Adopted} adopted); to keep an entry gone, remove it from the config layout",
                    true);
            return tally.InPlaceIds.Count > 0;
        }

        // Weighs a fresh loss against the entry's last repair: lost within ChurnSeconds of it, the wait doubles
        // (starting at 10 minutes) and the repair waits that long; having stood RepairResetSeconds or more clears the
        // wait; in between the repair runs now and the wait stays for the next quick loss.
        private void WeighLoss(LayoutEntry entry, float now)
        {
            RepairState state = RepairOf(entry.Id);
            if (state.LossSeen || state.LostAt == float.MinValue)
                return;
            state.LossSeen = true;
            float stood = state.LostAt - state.PlacedAt;
            if (stood < ChurnSeconds)
            {
                state.Wait = state.Wait <= 0f ? RepairBaseMinutes : Mathf.Min(state.Wait * 2f, RepairMaxMinutes);
                state.NextAt = now + state.Wait * 60f;
                Failure($"autorepair:{entry.Id}", $"auto-repair: {entry.Id} ({entry.Role}) was lost " +
                                                  $"{Duration(stood)} after its last repair; it comes back in " +
                                                  $"{state.Wait:0} min, and each loss that soon after a repair " +
                                                  "doubles the wait", "repair", 60f);
                return;
            }
            if (stood >= RepairResetSeconds)
                state.Wait = 0f;
            state.NextAt = 0f;
        }

        private void MarkLost(StructureRecord rec)
        {
            if (string.IsNullOrEmpty(rec.Id))
                return;
            RepairState state = RepairOf(rec.Id);
            state.LostAt = Time.realtimeSinceStartup;
            state.LossSeen = false;
        }

        private RepairState RepairOf(string id)
        {
            RepairState state;
            if (!_repairs.TryGetValue(id, out state))
                _repairs[id] = state = new RepairState();
            return state;
        }

        private static bool IsPenLine(string role) => role == RoleFence || role == RoleGate;

        private void BackOff(string id, float now)
        {
            if (IsPenLine(_config.Layout.Entries.FirstOrDefault(e => e.Id == id)?.Role))
                return;
            RepairState state = RepairOf(id);
            state.Wait = state.Wait <= 0f ? RepairBaseMinutes : Mathf.Min(state.Wait * 2f, RepairMaxMinutes);
            state.NextAt = now + state.Wait * 60f;
        }

        private string RepairHint(bool plural)
        {
            string what = plural ? "them" : "it";
            return _config.AutoRepair
                ? $"auto-repair re-places {what} on the next pass"
                : $"farm.repair respawns {what}";
        }

        // What brings a lost structure back: auto-repair only for an entry it handles (not one the farm never placed).
        private string RepairHintFor(StructureRecord rec)
        {
            LayoutEntry entry = _config.Layout.Entries.FirstOrDefault(e => e.Id == rec.Id);
            if (entry == null)
                return "it is no longer in the layout, so it stays gone";
            return _config.AutoRepair && !NeverPlaced(entry, RecordsById())
                ? "auto-repair re-places it on the next pass"
                : "farm.repair respawns it";
        }

        private string RepairWait(string id)
        {
            RepairState state;
            if (!_config.AutoRepair || string.IsNullOrEmpty(id) || !_repairs.TryGetValue(id, out state))
                return string.Empty;
            if (state.Waiting)
                return ", waiting for the spot to clear";
            float left = state.NextAt - Time.realtimeSinceStartup;
            return left > 0f ? $", next auto-repair in {Duration(left)}" : string.Empty;
        }

        // A respawned foundation starts a new building; the cupboard joins it so its privilege covers the block again.
        private void ReattachCupboard()
        {
            BuildingPrivlidge cupboard = FarmCupboard();
            var foundation = _data.Structures.Where(r => r.Role == RoleFoundation && r.NetId != 0 && !r.Missing)
                .Select(r => ResolveStructure(r) as BuildingBlock)
                .FirstOrDefault(b => b != null);
            if (cupboard == null || foundation == null || cupboard.buildingID == foundation.buildingID)
                return;
            cupboard.AttachToBuilding(foundation.buildingID);
            Puts($"repair: attached {NameOf(cupboard)} to the building of {NameOf(foundation)}");
        }

        private static List<LayoutEntry> Ordered(List<LayoutEntry> entries) =>
            entries.Select((entry, index) => new { entry, index })
                .OrderBy(p => BuildOrder(p.entry.Role))
                .ThenBy(p => p.index)
                .Select(p => p.entry)
                .ToList();

        private static int BuildOrder(string role)
        {
            switch (role)
            {
                case RoleFoundation:
                case RolePlatform:
                    return 0;
                case RoleCupboard:
                    return 1;
                case RoleFence:
                case RoleGate:
                    return 2;
                default:
                    return 3;
            }
        }

        private PlaceResult PlaceGuarded(LayoutEntry entry, StructureRecord rec, ulong owner)
        {
            PlaceResult result;
            bool wasMissing = rec.Missing;
            string oldFailure = rec.Failure;
            try
            {
                result = Place(entry, rec, owner);
            }
            catch (Exception e)
            {
                PrintError(e.ToString());
                return PlacementFailed(rec, $"exception {e.GetType().Name}: {e.Message}");
            }
            if (rec.Waiting != null)
            {
                // Nothing spawned: the record keeps its missing or failed state for the next try.
                rec.Missing = wasMissing;
                rec.Failure = oldFailure;
                return PlaceResult.Deferred;
            }
            // A new planter starts near 0 ml, more than ten minutes of sprinkler flow below the dry threshold.
            var planter = result == PlaceResult.Placed || result == PlaceResult.Adopted
                ? ResolveStructure(rec) as PlanterBox
                : null;
            if (planter != null && planter.soilSaturation < planter.idealSaturation)
            {
                planter.soilSaturation = planter.idealSaturation;
                planter.SendNetworkUpdate();
            }
            return result;
        }

        // research/farm/site/layout.md, "Conventions for the plugin".
        private PlaceResult Place(LayoutEntry entry, StructureRecord rec, ulong owner)
        {
            float yaw = _data.Yaw + entry.Yaw;
            rec.Role = entry.Role;
            rec.Prefab = entry.Prefab;
            rec.NetId = 0;
            rec.Failure = null;
            rec.Missing = false;
            rec.Waiting = null;
            rec.Yaw = yaw;
            if (entry.Role == RolePlaceholder)
            {
                Puts($"build: {entry.Id} is a {RolePlaceholder} placeholder; skipped");
                return PlaceResult.Skipped;
            }
            if (string.IsNullOrEmpty(entry.Prefab))
                return PlacementFailed(rec, "the layout entry has no prefab");
            Vector3 ground = FarmPoint(entry.X, entry.Z);
            if (!string.IsNullOrEmpty(entry.On))
                return PlaceOn(entry, rec, ground, yaw, owner);
            Vector3 normal = TerrainMeta.HeightMap.GetNormal(ground);
            switch (entry.Role)
            {
                case RoleFoundation:
                case RolePlatform:
                    return PlaceFoundation(rec, ground, yaw, owner);
                case RoleCupboard:
                    return PlaceCupboard(rec, ground, yaw, owner);
                case RoleFence:
                case RoleGate:
                {
                    // Yaw is the heading of the length axis (local X); the rail rolls with the ground along it.
                    Vector3 forward = Quaternion.Euler(0f, yaw - 90f, 0f) * Vector3.forward;
                    Quaternion rotation = Quaternion.LookRotation(forward, normal);
                    Vector3 position = ground + rotation * new Vector3(0f, FenceLift, 0f);
                    if (Adopt(rec, position, owner) != null)
                        return PlaceResult.Adopted;
                    BaseEntity fence = SpawnStructure(rec, position, rotation, owner, null);
                    (fence as Door)?.SetOpen(false);
                    return fence != null ? PlaceResult.Placed : PlaceResult.Failed;
                }
                case RolePump:
                {
                    float water = TerrainMeta.WaterMap.GetHeight(ground);
                    if (Adopt(rec, ground.WithY(Mathf.Max(water, ground.y)), owner) != null)
                        return PlaceResult.Adopted;
                    if (water <= ground.y)
                        return PlacementFailed(rec, $"no water over the pump spot (water {water:0.00} m, ground " +
                                                    $"{ground.y:0.00} m)");
                    return SpawnStructure(rec, ground.WithY(water), Quaternion.Euler(0f, yaw, 0f), owner, null) != null
                        ? PlaceResult.Placed
                        : PlaceResult.Failed;
                }
                default:
                {
                    if (Adopt(rec, ground, owner) != null)
                        return PlaceResult.Adopted;
                    Quaternion rotation = Quaternion.FromToRotation(Vector3.up, normal) * Quaternion.Euler(0f, yaw, 0f);
                    if (_config.GroundCheckRoles.Contains(entry.Role))
                    {
                        string problem = CheckFootprint(entry.Prefab, ground, rotation);
                        if (problem != null)
                            return PlacementFailed(rec, problem);
                    }
                    return SpawnStructure(rec, ground, rotation, owner, null) != null
                        ? PlaceResult.Placed
                        : PlaceResult.Failed;
                }
            }
        }

        private PlaceResult PlaceFoundation(StructureRecord rec, Vector3 centre, float yaw, ulong owner)
        {
            Quaternion rotation = Quaternion.Euler(0f, yaw, 0f);
            float highest = float.MinValue;
            float lowest = float.MaxValue;
            foreach (Vector2 p in Grid(-FoundationHalfWidth, FoundationHalfWidth, -FoundationHalfWidth,
                         FoundationHalfWidth, 5, 5))
            {
                float h = TerrainMeta.HeightMap.GetHeight(centre + rotation * new Vector3(p.x, 0f, p.y));
                highest = Mathf.Max(highest, h);
                lowest = Mathf.Min(lowest, h);
            }
            if (Adopt(rec, centre.WithY(highest), owner) != null)
                return PlaceResult.Adopted;
            if (highest - lowest > FoundationMaxRise)
                return PlacementFailed(rec, $"terrain falls {highest - lowest:0.00} m under the foundation, more " +
                                            $"than {FoundationMaxRise} m");
            string buried = BuriedUnder(centre.WithY(highest), rotation);
            if (buried != null)
                return PlacementFailed(rec, $"it would bury {buried}; farm.remove confirm and farm.build apply a " +
                                            "layout that puts a foundation under standing structures");
            var foundation = SpawnStructure(rec, centre.WithY(highest), rotation, owner, e =>
            {
                var block = e as BuildingBlock;
                if (block == null)
                    return "prefab is not a BuildingBlock";
                block.AttachToBuilding((DecayEntity)null);
                return null;
            }) as BuildingBlock;
            if (foundation == null)
                return PlaceResult.Failed;
            foundation.ChangeGrade(BuildingGrade.Enum.Stone);
            foundation.SetHealthToMax();
            foundation.SendNetworkUpdate();
            return PlaceResult.Placed;
        }

        // Deployables and building blocks whose pivot lies inside the footprint below the top, such as a fridge a farm
        // placed on terrain before its layout gained a platform; ones standing on the top (the cupboard) do not count.
        private string BuriedUnder(Vector3 top, Quaternion rotation)
        {
            var box = new OBB(top, rotation, new Bounds(new Vector3(0f, -1.5f, 0f),
                new Vector3(2f * FoundationHalfWidth, 2.8f, 2f * FoundationHalfWidth)));
            var found = new List<BaseEntity>();
            Vis.Entities(box, found, Rust.Layers.Mask.Deployed | Rust.Layers.Mask.Construction);
            var buried = new List<string>();
            foreach (BaseEntity entity in found.Distinct())
            {
                // Item bags and loose items (farm.remove leaves them where containers stood) are not structures.
                if (entity == null || entity.IsDestroyed || !(entity is DecayEntity))
                    continue;
                Vector3 local = Quaternion.Inverse(rotation) * (entity.transform.position - top);
                if (Mathf.Abs(local.x) <= FoundationHalfWidth && Mathf.Abs(local.z) <= FoundationHalfWidth &&
                    local.y < -0.1f)
                    buried.Add(NameOf(entity));
            }
            return buried.Count > 0 ? string.Join(", ", buried) : null;
        }

        private PlaceResult PlaceCupboard(StructureRecord rec, Vector3 point, float yaw, ulong owner)
        {
            BuildingBlock foundation = FoundationUnder(point);
            Vector3 position = point.WithY(foundation != null ? foundation.transform.position.y : point.y);
            BaseEntity adopted = Adopt(rec, position, owner);
            if (adopted != null)
            {
                Authorize(adopted as BuildingPrivlidge, owner);
                return PlaceResult.Adopted;
            }
            if (foundation == null)
                return PlacementFailed(rec, "no placed foundation under the cupboard position");
            uint building = foundation.buildingID;
            var cupboard = SpawnStructure(rec, position, Quaternion.Euler(0f, yaw, 0f), owner, e =>
            {
                var privilege = e as BuildingPrivlidge;
                if (privilege == null)
                    return "prefab is not a BuildingPrivlidge";
                privilege.AttachToBuilding(building);
                return null;
            }) as BuildingPrivlidge;
            if (cupboard == null)
                return PlaceResult.Failed;
            Authorize(cupboard, owner);
            return PlaceResult.Placed;
        }

        private BuildingBlock FoundationUnder(Vector3 point)
        {
            BuildingBlock best = null;
            float bestDistance = float.MaxValue;
            foreach (StructureRecord other in _data.Structures)
            {
                if (other.Role != RoleFoundation || other.NetId == 0 || other.Missing)
                    continue;
                var block = ResolveStructure(other) as BuildingBlock;
                if (block == null)
                    continue;
                Vector3 local = Quaternion.Inverse(block.transform.rotation) * (point - block.transform.position);
                float distance = new Vector2(local.x, local.z).magnitude;
                if (Mathf.Abs(local.x) <= FoundationHalfWidth && Mathf.Abs(local.z) <= FoundationHalfWidth &&
                    distance < bestDistance)
                {
                    bestDistance = distance;
                    best = block;
                }
            }
            return best;
        }

        // An entry with "On" stands upright at the top of its platform or foundation, which must already be in place
        // (BuildOrder places both before it); its spot must lie on that block. A matching entity standing at the
        // spot, on the block or on the terrain where an older layout put it, is adopted first.
        private PlaceResult PlaceOn(LayoutEntry entry, StructureRecord rec, Vector3 ground, float yaw, ulong owner)
        {
            StructureRecord under = _data.Structures
                .FirstOrDefault(r => r.Id == entry.On && r.NetId != 0 && !r.Missing);
            var block = under == null ? null : ResolveStructure(under) as BuildingBlock;
            Vector3 spot = block != null ? ground.WithY(block.transform.position.y) : ground;
            if (Adopt(rec, spot, owner) != null)
                return PlaceResult.Adopted;
            if (block == null)
                return PlacementFailed(rec, $"{entry.On}, which it stands on, is not in place");
            Vector3 local = Quaternion.Inverse(block.transform.rotation) * (spot - block.transform.position);
            float off = Mathf.Max(Mathf.Abs(local.x), Mathf.Abs(local.z));
            if (off > FoundationHalfWidth)
                return PlacementFailed(rec, $"its spot lies {off:0.00} m from the centre of {entry.On}, past its " +
                                            $"{FoundationHalfWidth:0.0} m half width");
            return SpawnStructure(rec, spot, Quaternion.Euler(0f, yaw, 0f), owner, null) != null
                ? PlaceResult.Placed
                : PlaceResult.Failed;
        }

        private static void Authorize(BuildingPrivlidge cupboard, ulong owner)
        {
            if (cupboard == null || owner == 0 || cupboard.IsAuthed(owner))
                return;
            cupboard.AddPlayer(null, owner);
            cupboard.SendNetworkUpdate();
        }

        // prepare checks the type and sets up the entity before it spawns (AttachToBuilding among it, as
        // Construction.CreateConstruction does), so the occupant check runs first; a problem destroys the unspawned
        // instance.
        private BaseEntity SpawnStructure(StructureRecord rec, Vector3 position, Quaternion rotation, ulong owner,
            Func<BaseEntity, string> prepare)
        {
            BaseEntity entity = GameManager.server.CreateEntity(rec.Prefab, position, rotation);
            if (entity == null)
            {
                PlacementFailed(rec, $"prefab {rec.Prefab} did not create an entity");
                return null;
            }
            string occupant = _deferOccupied ? Occupant(Footprint(entity)) : null;
            if (occupant != null)
            {
                DestroyUnspawned(entity);
                rec.Waiting = occupant;
                return null;
            }
            string problem = prepare?.Invoke(entity);
            if (problem != null)
            {
                DestroyUnspawned(entity);
                PlacementFailed(rec, problem);
                return null;
            }
            ClearVegetation(entity);
            entity.OwnerID = owner;
            entity.Spawn();
            Track(rec, entity);
            return entity;
        }

        // AttachToBuilding registers a DecayEntity with BuildingManager before it spawns, and a destroyed one is never
        // dropped (Building.Remove* skip a null entity), so the next split or merge of its building would loop forever
        // in ServerBuildingManager.Split or Merge.
        private static void DestroyUnspawned(BaseEntity entity)
        {
            var decay = entity as DecayEntity;
            if (decay != null)
                BuildingManager.server.Remove(decay);
            GameManager.Destroy(entity.gameObject);
        }

        private void Track(StructureRecord rec, BaseEntity entity)
        {
            Vector3 position = entity.transform.position;
            rec.NetId = IdOf(entity);
            rec.X = position.x;
            rec.Y = position.y;
            rec.Z = position.z;
            rec.Failure = null;
            rec.Missing = false;
            _structureIds.Add(rec.NetId);
        }

        // An entity at an entry's spot (same prefab, within 0.5 m across and 2 m up or down, owned by the farm owner
        // or by nobody) is taken over instead of spawning a duplicate, as over a standing farm after a lost record.
        // A physics query around the spot finds it, so auto-repair never walks every server entity.
        private BaseEntity Adopt(StructureRecord rec, Vector3 spot, ulong owner)
        {
            if (rec.Prefab == null)
                return null;
            var found = new List<BaseEntity>();
            Vis.Entities(spot, AdoptSearchRadius, found);
            BaseEntity best = null;
            float bestDistance = AdoptDistance;
            foreach (BaseEntity entity in found.Distinct())
            {
                if (entity == null || entity.IsDestroyed || !SamePrefab(entity, rec.Prefab) ||
                    _structureIds.Contains(IdOf(entity)) || (entity.OwnerID != 0 && !OwnerOk(entity.OwnerID)))
                    continue;
                Vector3 offset = entity.transform.position - spot;
                float across = new Vector2(offset.x, offset.z).magnitude;
                if (across <= bestDistance && Mathf.Abs(offset.y) <= AdoptHeight)
                {
                    best = entity;
                    bestDistance = across;
                }
            }
            if (best == null)
                return null;
            if (best.OwnerID == 0)
            {
                best.OwnerID = owner;
                best.SendNetworkUpdate();
            }
            Track(rec, best);
            Puts($"adopt: {rec.Id} ({rec.Role}) is the standing {best.ShortPrefabName} [{rec.NetId}] " +
                 $"{bestDistance:0.00} m from its spot");
            return best;
        }

        // Kills trees, bushes, and resource piles (dead logs, ore) whose colliders reach into the entity's base plus
        // 0.5 m, as layout.md asks; trees elsewhere in the pen stay.
        private void ClearVegetation(BaseEntity entity)
        {
            OBB box = Footprint(entity);
            var found = new List<BaseEntity>();
            Vis.Entities(box, found, Rust.Layers.Mask.Tree | Rust.Layers.Mask.Bush | Rust.Layers.Mask.World |
                                     Rust.Layers.Mask.Default | Rust.Layers.Mask.Harvestable);
            foreach (BaseEntity other in found.Distinct().ToList())
            {
                if (other == null || other.IsDestroyed || other == entity || IsProtected(other) ||
                    !(other is TreeEntity || other is BushEntity || other is ResourceEntity))
                    continue;
                _clearedVegetation.Add(other.ShortPrefabName);
                other.Kill();
            }
        }

        // The entity's base (ClearingAreas, or its own bounds) plus 0.5 m, from just under the ground to 3 m up.
        private static OBB Footprint(BaseEntity entity)
        {
            Vector4 area;
            Bounds local;
            if (ClearingAreas.TryGetValue(entity.ShortPrefabName, out area))
                local = new Bounds(new Vector3(area.x, 1.25f, area.y),
                    new Vector3(2f * (area.z + VegetationMargin), 3.5f, 2f * (area.w + VegetationMargin)));
            else
                local = new Bounds(entity.bounds.center, entity.bounds.size + Vector3.one * (2f * VegetationMargin));
            return new OBB(entity.transform.position, entity.transform.rotation, local);
        }

        // A player (awake or asleep) or any livestock standing in the box would be pushed or trapped by a spawn.
        private string Occupant(OBB box)
        {
            foreach (BasePlayer player in BasePlayer.activePlayerList.Concat(BasePlayer.sleepingPlayerList))
                if (player != null && !player.IsDead() && box.Contains(player.transform.position))
                    return $"player {player.displayName}";
            foreach (LivestockAnimal animal in _livestock)
                if (animal != null && !animal.IsDestroyed && !animal.IsDead() &&
                    box.Contains(animal.transform.position))
                    return Describe(animal);
            return null;
        }

        private void LogClearedVegetation(string kind)
        {
            if (_clearedVegetation.Count == 0)
                return;
            Puts($"{kind}: cleared {_clearedVegetation.Count} bushes, trees, and resource piles from the layout " +
                 "footprints: " + string.Join(", ", _clearedVegetation.GroupBy(n => n)
                     .Select(g => $"{g.Key} x{g.Count()}")));
            _clearedVegetation.Clear();
        }

        private string CheckFootprint(string prefab, Vector3 pivot, Quaternion rotation)
        {
            Vector2[] points;
            if (!GroundChecks.TryGetValue(Path.GetFileNameWithoutExtension(prefab), out points))
                return $"no ground check is known for {prefab}";
            float worst = 0f;
            foreach (Vector2 p in points)
            {
                Vector3 world = pivot + rotation * new Vector3(p.x, 0f, p.y);
                float gap = world.y - TerrainMeta.HeightMap.GetHeight(world);
                if (Mathf.Abs(gap) > Mathf.Abs(worst))
                    worst = gap;
            }
            if (Mathf.Abs(worst) <= _config.GroundTolerance)
                return null;
            string tolerance = $"more than the {_config.GroundTolerance:0.000} m tolerance";
            return worst > 0f
                ? $"ground lies {worst:0.000} m below the base, {tolerance}"
                : $"ground rises {-worst:0.000} m above the base, {tolerance}";
        }

        private PlaceResult PlacementFailed(StructureRecord rec, string why)
        {
            rec.Failure = why;
            if (!_quietPlacement)
                Failure($"place:{rec.Id}", $"{rec.Id} ({rec.Role}) failed: {why}", "placement");
            return PlaceResult.Failed;
        }

        // One clone per planned slot plus a reserve of the same size, all with the configured genes; clones already
        // in the box (an adopted farm) count toward it.
        private void StockStarterClones(UpkeepPass pass)
        {
            List<ItemContainer> boxes = pass.Containers(BoxClones);
            foreach (KeyValuePair<string, int> crop in PlannedSlots())
            {
                int want = 2 * crop.Value;
                int have = CountItems(boxes, crop.Key);
                int made = have >= want ? 0 : MakeClones(boxes, crop.Key, want - have);
                if (made > 0)
                    Event("build", $"starter clones: {crop.Key} x{made} ({_config.CloneGenes}) in {BoxClones}" +
                                   (have > 0 ? $", which already held {have}" : string.Empty));
                if (have + made < want)
                    Failure($"starter:{crop.Key}", $"{BoxClones} holds {have + made} of {want} {crop.Key}");
            }
        }

        private int MakeClones(List<ItemContainer> boxes, string shortname, int amount)
        {
            ItemDefinition def = ItemManager.FindItemDefinition(shortname);
            if (def == null)
            {
                Failure($"clonedef:{shortname}", $"the slot plan names {shortname}, which is not an item");
                return 0;
            }
            int genes = GrowableGeneEncoding.EncodeGeneStringToInt(_config.CloneGenes.ToUpperInvariant());
            return GiveNew(boxes, def, amount, -1, item => GrowableGeneEncoding.EncodeGenesToItem(genes, item));
        }

        private Dictionary<string, int> PlannedSlots()
        {
            var slots = new Dictionary<string, int>();
            foreach (List<string> plan in _config.SlotPlan.Values.Where(p => p != null))
                foreach (string clone in plan.Where(c => !string.IsNullOrEmpty(c)))
                {
                    int have;
                    slots.TryGetValue(clone, out have);
                    slots[clone] = have + 1;
                }
            return slots;
        }

        // Only entities that still resolve to their record (prefab and owner) are touched; each container first
        // drops its items the vanilla death way (StorageContainer.DropItems: loose items or an item bag).
        private string Remove()
        {
            if (!_data.Built)
                return "no farm built";
            _herdTimer?.Destroy();
            _herdTimer = null;
            _removing = true;
            int animals = 0;
            int structures = 0;
            int missing = 0;
            int unresolved = 0;
            var dropped = new List<string>();
            var discarded = new List<string>();
            try
            {
                foreach (KeyValuePair<ulong, string> pair in _animals.ToList())
                {
                    LivestockAnimal animal = ResolveAnimal(pair.Key, pair.Value);
                    if (animal == null)
                    {
                        unresolved++;
                        continue;
                    }
                    animal.Kill();
                    animals++;
                }
                List<StructureRecord> records = _data.Structures
                    .Where(r => r.NetId != 0)
                    .OrderByDescending(r => BuildOrder(r.Role))
                    .ToList();
                foreach (StructureRecord rec in records)
                {
                    if (rec.Missing)
                    {
                        missing++;
                        continue;
                    }
                    BaseEntity entity = ResolveStructure(rec);
                    if (entity == null)
                    {
                        unresolved++;
                        continue;
                    }
                    Guard("remove", rec.Id, () => Empty(entity, rec.Id, dropped, discarded));
                    var coop = entity as ChickenCoop;
                    if (coop != null)
                        foreach (ChickenCoop.AnimalStatus status in coop.Animals.ToList())
                            status.SpawnedAnimal.Get(true)?.Kill();
                    entity.Kill();
                    structures++;
                }
            }
            finally
            {
                _removing = false;
            }
            ResetState();
            _data = new StoredData();
            string text = $"removed the farm of identity {ConVar.Server.identity}: {animals} animals and " +
                          $"{structures} structures killed, {missing} missing records skipped, {unresolved} records " +
                          "that no longer resolve to a farm entity left alone, record cleared";
            Puts(text);
            if (dropped.Count > 0)
                Puts("remove: dropped " + string.Join("; ", dropped));
            if (discarded.Count > 0)
                PrintWarning("remove: discarded " + string.Join("; ", discarded));
            return text +
                   (dropped.Count > 0 ? $"\ndropped: {string.Join("; ", dropped)}" : "\ndropped: nothing") +
                   (discarded.Count > 0 ? $"\ndiscarded: {string.Join("; ", discarded)}" : string.Empty);
        }

        // Water is never dropped (vanilla removes a coop's water slot too); everything else goes to the ground.
        private static void Empty(BaseEntity entity, string id, List<string> dropped, List<string> discarded)
        {
            var liquid = entity as LiquidContainer;
            if (liquid != null)
            {
                int ml = liquid.GetLiquidCount();
                if (ml > 0)
                    discarded.Add($"{id} water {ml} ml");
                return;
            }
            int troughWater = (entity as HitchTrough)?.WaterStorage?.GetLiquidCount() ?? 0;
            if (troughWater > 0)
                discarded.Add($"{id} water end {troughWater} ml");
            var container = entity as IItemContainerEntity;
            ItemContainer inventory = container?.inventory;
            if (inventory == null)
                return;
            bool coop = entity is ChickenCoop;
            Item coopWater = coop ? inventory.GetSlot(ChickenCoop.WaterSlot) : null;
            if (coopWater != null && coopWater.amount > 0)
                discarded.Add($"{id} water {coopWater.amount} ml");
            List<Item> items = inventory.itemList.Where(i => i.amount > 0 && i != coopWater).ToList();
            if (items.Count == 0)
                return;
            string contents = Summarize(items);
            if (!container.DropsLoot)
            {
                discarded.Add($"{id} {contents} (its prefab drops no loot)");
                return;
            }
            container.DropItems(null);
            dropped.Add($"{id}: {contents}");
        }

        #endregion

        #region Herd spawning and linking

        private void ScheduleHerd()
        {
            _herdTimer?.Destroy();
            _herdTimer = timer.Once(Mathf.Max(1f, _config.HerdSpawnDelay), () =>
            {
                _herdTimer = null;
                if (_unloaded || _disabled != null || _data == null || !_data.Built || _data.HerdSpawned)
                    return;
                Guard("starter herd", "spawn", SpawnStarterHerd);
            });
        }

        // Species with animals already linked (a farm adopted after a lost record) keep them instead of a starter
        // herd. Progress is recorded per entry before each spawn, so a throw never respawns what already spawned.
        private void SpawnStarterHerd()
        {
            UpkeepPass pass = BeginPass();
            if (pass.Owner == 0)
            {
                Failure("owner", "the starter herd waits: no owner resolves");
                return;
            }
            if (pass.Cupboard == null)
            {
                CupboardMissing();
                return;
            }
            RelinkAll(pass);
            List<HerdEntry> entries = _config.StarterHerd;
            while (_data.StarterDone.Count < entries.Count)
                _data.StarterDone.Add(0);
            var kept = new List<string>();
            foreach (string species in AllSpecies)
            {
                int present = Herd(pass, species).Count;
                if (present == 0)
                    continue;
                for (int i = 0; i < entries.Count; i++)
                    if (entries[i].Species == species)
                        _data.StarterDone[i] = Math.Max(_data.StarterDone[i], entries[i].Count);
                kept.Add($"{present} {species}");
            }
            var spawned = new List<string>();
            for (int i = 0; i < entries.Count; i++)
            {
                HerdEntry entry = entries[i];
                while (_data.StarterDone[i] < entry.Count)
                {
                    _data.StarterDone[i]++;
                    LivestockAnimal animal = null;
                    Guard("starter herd", $"entry {i + 1}", () => animal = SpawnAnimal(entry.Prefab, entry.Male, pass));
                    if (animal != null)
                        spawned.Add(Describe(animal));
                }
            }
            _data.HerdSpawned = true;
            Guard("starter herd", "pen adoption", () => AdoptPenAnimals(pass));
            Event("build", $"starter herd: {spawned.Count} animals spawned" +
                           (spawned.Count > 0 ? $" ({string.Join(", ", spawned)})" : string.Empty) +
                           (kept.Count > 0 ? $"; kept the {string.Join(" and ", kept)} already linked" : string.Empty));
        }

        private List<LivestockAnimal> SpawnSpecies(string species, UpkeepPass pass)
        {
            var spawned = new List<LivestockAnimal>();
            foreach (HerdEntry entry in _config.StarterHerd.Where(e => e.Species == species))
                for (int i = 0; i < entry.Count; i++)
                {
                    LivestockAnimal animal = null;
                    Guard("reseed", entry.Prefab, () => animal = SpawnAnimal(entry.Prefab, entry.Male, pass));
                    if (animal != null)
                        spawned.Add(animal);
                }
            return spawned;
        }

        private LivestockAnimal SpawnAnimal(string prefab, bool male, UpkeepPass pass)
        {
            string problem = HerdPointProblem();
            if (problem != null)
            {
                Failure("herdpoint", $"not spawning {prefab}: {problem}");
                return null;
            }
            Vector2 offset = UnityEngine.Random.insideUnitCircle * _config.HerdSpread;
            Vector3 position = FarmPoint(_config.HerdPoint.X + offset.x, _config.HerdPoint.Z + offset.y);
            BaseEntity entity = GameManager.server.CreateEntity(prefab, position,
                Quaternion.Euler(0f, UnityEngine.Random.Range(0f, 360f), 0f));
            var animal = entity as LivestockAnimal;
            if (animal == null)
            {
                if (entity != null)
                    DestroyUnspawned(entity);
                Failure($"spawn:{prefab}", $"prefab {prefab} did not create a LivestockAnimal");
                return null;
            }
            // Keeps IsMale through ServerInit on the either-sex sheep and lamb prefabs, as the livestock vendor does.
            animal.IsBeingPurchased = true;
            animal.IsMale = male;
            animal.OwnerID = pass.Owner;
            animal.Spawn();
            _animals[IdOf(animal)] = animal.PrefabName;
            pass.Animals.Add(animal);
            Bond(animal, pass.Owner, pass.Cupboard);
            NextTick(() =>
            {
                Vector3 onNavmesh;
                if (!_unloaded && animal != null && !animal.IsDestroyed &&
                    !animal.TrySampleNavmesh(animal.transform.position, 2f, out onNavmesh))
                    Failure($"navmesh:{IdOf(animal)}", $"{Describe(animal)} spawned at {animal.transform.position} " +
                                                      "with no navmesh within 2 m; it may die stranded");
            });
            return animal;
        }

        private string HerdPointProblem()
        {
            List<LayoutEntry> fences = _config.Layout.Entries
                .Where(e => e.Role == RoleFence || e.Role == RoleGate)
                .ToList();
            if (fences.Count == 0)
                return "the layout has no fence or gate entries, so the herd point cannot be checked against the pen";
            float margin = _config.HerdSpread + 1f;
            XZ point = _config.HerdPoint;
            if (point.X - margin < fences.Min(e => e.X) || point.X + margin > fences.Max(e => e.X) ||
                point.Z - margin < fences.Min(e => e.Z) || point.Z + margin > fences.Max(e => e.Z))
                return $"herd point ({point.X}, {point.Z}) with spread {_config.HerdSpread} m is not 1 m inside the " +
                       "fence line";
            return null;
        }

        private void ConsiderNewAnimal(LivestockAnimal animal)
        {
            if (animal == null || animal.IsDestroyed || animal.IsDead() || _data == null || !_data.Built ||
                _animals.ContainsKey(IdOf(animal)))
                return;
            ulong owner = FarmOwner();
            BuildingPrivlidge cupboard = FarmCupboard();
            string reason;
            if (!IsFarmCandidate(animal, cupboard, out reason))
                return;
            _animals[IdOf(animal)] = animal.PrefabName;
            Bond(animal, owner, cupboard);
            LivestockAnimal mother = animal.MotherAnimal;
            if (animal.IsFindingItsFeet && mother != null)
                Event("birth", $"{Describe(animal)} born to {Describe(mother)}");
            else if (_growingNames.Remove(animal.AnimalName ?? string.Empty))
                Puts($"herd: {Describe(animal)} grew up");
            else
                Puts($"herd: linked {Describe(animal)} by {reason}");
        }

        // The farm's own cupboard as home (newborns and farm-tagged animals), a farm mother (juveniles), or the
        // owner's tag anywhere in the farm radius (only this plugin tags livestock); never an animal a player took
        // away. Vanilla TryAdoptHome points any fully bonded animal standing in the cupboard's privilege at the
        // cupboard, so the cupboard alone would take the owner's personal animals. BirthOne copies the mother's home
        // and bonds but not her tag, and IsFindingItsFeet ends once the calf stands, so a calf born while the plugin
        // was unloaded links through its farm mother while it is a juvenile. Strays go back under CheckStray.
        private bool IsFarmCandidate(LivestockAnimal animal, BuildingPrivlidge cupboard, out string reason)
        {
            reason = null;
            if (animal == null || animal.IsDestroyed || animal.IsDead() || _data.Released.Contains(IdOf(animal)))
                return false;
            bool newborn = animal.IsFindingItsFeet;
            if (cupboard != null && animal.HomeTc.uid.Value == IdOf(cupboard) && (newborn || OwnerOk(animal.OwnerID)))
            {
                reason = "home cupboard";
                return true;
            }
            LivestockAnimal mother = animal.MotherAnimal;
            if ((newborn || animal.IsInfant()) && mother != null && _animals.ContainsKey(IdOf(mother)))
            {
                reason = "mother";
                return true;
            }
            if (OwnerOk(animal.OwnerID) && FromCentre(animal.transform.position) <= _config.FarmRadius)
            {
                reason = "owner tag in the farm radius";
                return true;
            }
            return false;
        }

        // Truly wild livestock standing inside the fence line (fenced in at build, or a wild spawn) joins the farm and
        // counts toward the caps, while its species is under its cap. Vanilla never tags livestock; wild means
        // DecayStarted is still false. RefreshHusbandryClocks sets it the moment any player's trust reaches
        // trustToLead (the vendor's SetFamiliarity included), the age timer sets it, the save keeps it, and ReplaceWith
        // copies it, so a bought, tamed, or once-bonded animal never counts as wild again, even after its trust
        // decays. One on a lead or a vehicle, or homed to another cupboard, also stays out, as does one taken away.
        private void AdoptPenAnimals(UpkeepPass pass)
        {
            List<Vector2> pen = PenHull();
            if (pen.Count < 3)
                return;
            _livestock.RemoveWhere(a => a == null || a.IsDestroyed);
            ulong farmCupboard = IdOf(pass.Cupboard);
            List<LivestockAnimal> found = _livestock
                .Where(a => !a.IsDead() && !a.DecayStarted && a.OwnerID == 0 && !a.IsLeading() && !a.IsMounted &&
                            SpeciesOf(a) != null && !_animals.ContainsKey(IdOf(a)) &&
                            !_data.Released.Contains(IdOf(a)) && HomedHereOrNowhere(a, farmCupboard) &&
                            InsidePen(a.transform.position, pen))
                .ToList();
            foreach (LivestockAnimal animal in found)
            {
                string species = SpeciesOf(animal);
                if (CapCount(Herd(pass, species)) >= CapOf(species))
                    continue;
                _animals[IdOf(animal)] = animal.PrefabName;
                pass.Animals.Add(animal);
                Bond(animal, pass.Owner, pass.Cupboard);
                Event("adopt", $"{Describe(animal)} stood unowned inside the fence line; adopted into the farm " +
                               $"(condition {animal.Condition:0.00})");
            }
        }

        private static bool HomedHereOrNowhere(LivestockAnimal animal, ulong farmCupboard)
        {
            BuildingPrivlidge home;
            return !animal.TryGetHomeCupboard(out home) || IdOf(home) == farmCupboard;
        }

        // Outside the fence polygon a farm animal is a stray. One led or carried outward across the farm radius (within
        // it last pass, beyond it now) leaves the farm; one led back in never does. One unled and off any vehicle for
        // StrayReturnSeconds in a row goes back to the herd point from any distance, and the next pass confirms the
        // return before it is logged. Returns whether the animal was released.
        private bool CheckStray(LivestockAnimal animal, List<Vector2> pen, List<string> strays)
        {
            ulong id = IdOf(animal);
            bool within = FromCentre(animal.transform.position) <= _config.FarmRadius;
            bool wasWithin = _withinRadius.Contains(id);
            if (within)
                _withinRadius.Add(id);
            else
                _withinRadius.Remove(id);
            string returned;
            if (pen.Count < 3 || _pendingCulls.ContainsKey(id) || InsidePen(animal.transform.position, pen))
            {
                _strays.Remove(id);
                if (_returning.TryGetValue(id, out returned))
                {
                    _returning.Remove(id);
                    Event("stray", returned);
                }
                return false;
            }
            Vector2 local = LocalOf(animal.transform.position);
            string where = $"{Describe(animal)} at local ({local.x:0.0}, {local.y:0.0})";
            if (_returning.Remove(id))
                Failure($"return:{id}", $"the return of {where} did not hold; retrying", "stray");
            bool taken = animal.IsLeading() || animal.IsMounted;
            if (taken && wasWithin && !within)
            {
                Release(animal);
                return true;
            }
            float now = Time.realtimeSinceStartup;
            float since;
            if (taken || !_strays.TryGetValue(id, out since))
                _strays[id] = since = now;
            if (taken || now - since < StrayReturnSeconds)
            {
                strays.Add($"{where}, " + (taken ? "on a lead or a vehicle" : $"unled for {Duration(now - since)}"));
                return false;
            }
            string problem;
            if (ReturnStray(animal, out problem))
            {
                _returning[id] = $"returned {where} to the herd point after {Duration(now - since)} unled outside " +
                                 "the fence line";
                return false;
            }
            strays.Add($"{where}, unled for {Duration(now - since)}");
            Failure($"return:{id}", $"{Describe(animal)} cannot go back to the herd point yet: {problem}", "stray");
            return false;
        }

        // BaseNPC2.OnDismountFinalized (Rust.Ai.Gen2/BaseNPC2.cs), the end of LivestockMountable's unload, moves a
        // Gen2 animal this way: a navmesh sample, SetPositionAndRotation while its RustNavMeshAgent is paused, then
        // Unpause, whose OnChange warps the agent to the new position (RustNavMeshAgent.Warp).
        private bool ReturnStray(LivestockAnimal animal, out string problem)
        {
            problem = HerdPointProblem();
            if (problem != null)
                return false;
            var agent = animal.GetComponent<RustNavMeshAgent>();
            if (agent == null)
            {
                problem = "it has no RustNavMeshAgent";
                return false;
            }
            // Unpausing warps only when this call removes the last pausing source; a root-motion montage
            // (RootMotionPlayer) holds one and would snap the animal back.
            if (agent.IsPaused)
            {
                problem = "its navmesh agent is paused (a root-motion animation is playing); retrying next pass";
                return false;
            }
            Vector2 offset = UnityEngine.Random.insideUnitCircle * _config.HerdSpread;
            Vector3 target = FarmPoint(_config.HerdPoint.X + offset.x, _config.HerdPoint.Z + offset.y);
            Vector3 onNavmesh;
            if (!animal.TrySampleNavmesh(target, 2f, out onNavmesh))
            {
                problem = $"no navmesh within 2 m of {target}";
                return false;
            }
            agent.Pause(this);
            try
            {
                animal.transform.SetPositionAndRotation(onNavmesh, animal.transform.rotation);
            }
            finally
            {
                agent.Unpause(this);
            }
            animal.SendNetworkUpdate();
            return true;
        }

        // A farm animal a player takes past the farm radius leaves the farm for good: untracked, its owner tag and
        // farm home cleared, its roaming anchor where it left, so neither relink nor pen adoption takes it back.
        private void Release(LivestockAnimal animal)
        {
            ulong id = IdOf(animal);
            BasePlayer player = animal.LeadingPlayer.Get(true) ?? animal.leaderBeforeMount.Get(true);
            _animals.Remove(id);
            _strays.Remove(id);
            if (!_data.Released.Contains(id))
                _data.Released.Add(id);
            animal.OwnerID = 0;
            animal.HomeTc = new EntityRef<BuildingPrivlidge>();
            animal.SetHomePosition(animal.transform.position);
            animal.SendNetworkUpdate();
            Event("released", $"{Describe(animal)} left the farm with " +
                              (player != null ? player.displayName : "a vehicle"));
            // A release must not earn a free reseed: when it leaves the species without a sex, the backoff doubles
            // as after a failed reseed.
            string species = SpeciesOf(animal);
            if (species == null)
                return;
            List<LivestockAnimal> herd = _animals.Keys
                .Select(FindEntity)
                .OfType<LivestockAnimal>()
                .Where(a => !a.IsDestroyed && !a.IsDead() && SpeciesOf(a) == species)
                .ToList();
            bool hasMale = herd.Any(a => a.IsMale);
            bool hasFemale = herd.Any(a => a.IsFemale);
            if (hasMale && hasFemale)
                return;
            ReseedState state = ReseedOf(species);
            state.Wait = Mathf.Min(state.Wait * 2f, ReseedMaxMinutes);
            state.At = Epoch.Current;
            state.Check = false;
            string lack = herd.Count == 0 ? $"no {species}" : $"no {(hasMale ? "female" : "male")} {species}";
            Event("reseed", $"the release left {lack}; the next {species} reseed waits {state.Wait:0} min, as after " +
                            "a failed reseed", true);
        }

        private ReseedState ReseedOf(string species)
        {
            ReseedState state;
            if (!_data.Reseed.TryGetValue(species, out state) || state == null)
                _data.Reseed[species] = state = new ReseedState();
            return state;
        }

        private Vector2 LocalOf(Vector3 world)
        {
            Vector3 offset = Quaternion.Euler(0f, -_data.Yaw, 0f) *
                             new Vector3(world.x - _data.CentreX, 0f, world.z - _data.CentreZ);
            return new Vector2(offset.x, offset.z);
        }

        private float FromCentre(Vector3 world) =>
            new Vector2(world.x - _data.CentreX, world.z - _data.CentreZ).magnitude;

        // Runs at load, on farm.relink, before the starter herd, and at most every 10 minutes after a farm animal
        // vanished without dying; it reads the live LivestockAnimal set instead of every server entity.
        private int RelinkAll(UpkeepPass pass)
        {
            _lastRelinkAt = Time.realtimeSinceStartup;
            _livestock.RemoveWhere(a => a == null || a.IsDestroyed);
            var linked = new List<string>();
            // Calves link through their mother, so each round can add animals the previous one made eligible.
            for (int round = 0; round < 3; round++)
            {
                var found = new List<KeyValuePair<LivestockAnimal, string>>();
                foreach (LivestockAnimal animal in _livestock)
                {
                    string reason;
                    if (!_animals.ContainsKey(IdOf(animal)) &&
                        IsFarmCandidate(animal, pass.Cupboard, out reason))
                        found.Add(new KeyValuePair<LivestockAnimal, string>(animal, reason));
                }
                if (found.Count == 0)
                    break;
                foreach (KeyValuePair<LivestockAnimal, string> pair in found)
                {
                    _animals[IdOf(pair.Key)] = pair.Key.PrefabName;
                    pass.Animals.Add(pair.Key);
                    Bond(pair.Key, pass.Owner, pass.Cupboard);
                    linked.Add($"{Describe(pair.Key)} by {pair.Value}");
                }
            }
            if (linked.Count > 0)
                Puts($"relink: {linked.Count} animals: {string.Join(", ", linked)}");
            return linked.Count;
        }

        private void Bond(LivestockAnimal animal, ulong owner, BuildingPrivlidge cupboard)
        {
            if (animal == null || animal.IsDestroyed || animal.IsDead())
                return;
            bool changed = false;
            if (owner != 0 && animal.OwnerID != owner)
            {
                animal.OwnerID = owner;
                changed = true;
            }
            if (owner != 0 && animal.StoredSecondsFor(owner) < ConVar.Livestock.maxTrust)
            {
                animal.SetFamiliarity(owner, ConVar.Livestock.maxTrust, LivestockAnimal.FamiliarityReason.Debug);
                changed = true;
            }
            if (cupboard != null && animal.HomeTc.uid.Value != IdOf(cupboard))
            {
                animal.HomeTc = new EntityRef<BuildingPrivlidge>(cupboard.net.ID);
                changed = true;
            }
            animal.SetHomePosition(FarmPoint(_config.HerdPoint.X, _config.HerdPoint.Z));
            if (changed)
                animal.SendNetworkUpdate();
        }

        // Convex hull of the recorded fence and gate rail ends in world x/z.
        private List<Vector2> PenHull()
        {
            if (_penHull != null)
                return _penHull;
            var points = new List<Vector2>();
            foreach (StructureRecord rec in _data.Structures)
            {
                if ((rec.Role != RoleFence && rec.Role != RoleGate) || rec.NetId == 0)
                    continue;
                float yaw = rec.Yaw * Mathf.Deg2Rad;
                Vector2 along = new Vector2(Mathf.Sin(yaw), Mathf.Cos(yaw)) * RailHalfLength;
                var centre = new Vector2(rec.X, rec.Z);
                points.Add(centre + along);
                points.Add(centre - along);
            }
            _penHull = ConvexHull(points);
            return _penHull;
        }

        // Andrew's monotone chain, counter-clockwise.
        private static List<Vector2> ConvexHull(List<Vector2> points)
        {
            List<Vector2> sorted = points.Distinct().OrderBy(p => p.x).ThenBy(p => p.y).ToList();
            if (sorted.Count < 3)
                return sorted;
            var hull = new List<Vector2>();
            for (int pass = 0; pass < 2; pass++)
            {
                int start = hull.Count;
                foreach (Vector2 p in sorted)
                {
                    while (hull.Count >= start + 2 && Cross(hull[hull.Count - 2], hull[hull.Count - 1], p) <= 0f)
                        hull.RemoveAt(hull.Count - 1);
                    hull.Add(p);
                }
                hull.RemoveAt(hull.Count - 1);
                sorted.Reverse();
            }
            return hull;
        }

        private static float Cross(Vector2 o, Vector2 a, Vector2 b) =>
            (a.x - o.x) * (b.y - o.y) - (a.y - o.y) * (b.x - o.x);

        private static bool InsidePen(Vector3 position, List<Vector2> hull)
        {
            if (hull == null || hull.Count < 3)
                return false;
            var p = new Vector2(position.x, position.z);
            for (int i = 0; i < hull.Count; i++)
                if (Cross(hull[i], hull[(i + 1) % hull.Count], p) < -0.01f)
                    return false;
            return true;
        }

        #endregion

        #region Upkeep

        private class UpkeepPass
        {
            public ulong Owner;
            public float Elapsed;
            public BuildingPrivlidge Cupboard;
            public readonly Dictionary<string, List<BaseEntity>> Roles = new Dictionary<string, List<BaseEntity>>();
            public readonly Dictionary<ulong, string> Names = new Dictionary<ulong, string>();
            public readonly Dictionary<string, BaseEntity> Ids = new Dictionary<string, BaseEntity>();
            public readonly List<LivestockAnimal> Animals = new List<LivestockAnimal>();
            // Destructive actions and needs top-ups of an upkeep pass, each logged as one line at its end.
            public readonly List<string> Destroyed = new List<string>();
            public readonly List<string> Needs = new List<string>();
            // PersonalSpace restores, logged at INFO: crowding drifts the need back down on its own.
            public readonly List<string> Crowding = new List<string>();

            public List<BaseEntity> Role(string role)
            {
                List<BaseEntity> list;
                return Roles.TryGetValue(role, out list) ? list : NoEntities;
            }

            public List<ItemContainer> Containers(string role) =>
                Role(role).Select(InventoryOf).Where(c => c != null).ToList();

            public BaseEntity ById(string id)
            {
                BaseEntity entity;
                return id != null && Ids.TryGetValue(id, out entity) ? entity : null;
            }

            public string Name(BaseEntity entity)
            {
                string name;
                return entity != null && Names.TryGetValue(IdOf(entity), out name) ? name : entity?.ShortPrefabName;
            }
        }

        private UpkeepPass BeginPass()
        {
            var pass = new UpkeepPass { Owner = FarmOwner() };
            float now = Time.realtimeSinceStartup;
            pass.Elapsed = _lastUpkeepAt < 0f ? 0f : now - _lastUpkeepAt;
            foreach (StructureRecord rec in _data.Structures)
            {
                if (rec.NetId == 0 || rec.Missing)
                    continue;
                BaseEntity entity = ResolveStructure(rec);
                if (entity == null)
                {
                    rec.Missing = true;
                    MarkLost(rec);
                    _structureIds.Remove(rec.NetId);
                    Failure($"gone:{rec.Id}", $"structure {rec.Id} ({rec.Role}) no longer resolves to its prefab and " +
                                              $"owner, and {RepairHintFor(rec)}");
                    continue;
                }
                List<BaseEntity> list;
                if (!pass.Roles.TryGetValue(rec.Role ?? string.Empty, out list))
                    pass.Roles[rec.Role ?? string.Empty] = list = new List<BaseEntity>();
                list.Add(entity);
                pass.Names[rec.NetId] = rec.Id;
                if (rec.Id != null && !pass.Ids.ContainsKey(rec.Id))
                    pass.Ids[rec.Id] = entity;
            }
            pass.Cupboard = pass.Role(RoleCupboard).OfType<BuildingPrivlidge>().FirstOrDefault();
            var lost = new List<ulong>();
            foreach (KeyValuePair<ulong, string> pair in _animals.ToList())
            {
                LivestockAnimal animal = ResolveAnimal(pair.Key, pair.Value);
                if (animal == null)
                {
                    _animals.Remove(pair.Key);
                    lost.Add(pair.Key);
                    continue;
                }
                pass.Animals.Add(animal);
            }
            if (lost.Count > 0)
            {
                _relinkWanted = true;
                Event("death", $"farm animals {string.Join(", ", lost)} no longer resolve to their prefab and owner " +
                               "and left no kill event", true);
            }
            return pass;
        }

        // Carbon destroys a timer whose callback throws, so nothing may escape the pass.
        private void Upkeep()
        {
            try
            {
                RunUpkeep();
            }
            catch (Exception e)
            {
                Failure("upkeep", $"the upkeep pass threw {e}");
            }
        }

        private void RunUpkeep()
        {
            if (_unloaded)
                return;
            if (_disabled != null)
            {
                Failure("disabled", $"upkeep skipped, Livestock Farm is disabled: {_disabled}");
                return;
            }
            if (_data == null || !_data.Built)
                return;
            UpkeepPass pass = BeginPass();
            bool repaired = false;
            Guard("structures", "auto-repair", () => repaired = AutoRepair(pass.Owner));
            if (repaired)
            {
                float elapsed = pass.Elapsed;
                pass = BeginPass();
                pass.Elapsed = elapsed;
            }
            float now = Time.realtimeSinceStartup;
            _lastUpkeepAt = now;
            if (_config.OwnerSteamId != 0 && _data.Owner != 0 && _config.OwnerSteamId != _data.Owner)
                Failure("ownermismatch", $"the config's owner {OwnerLabel(_config.OwnerSteamId)} differs from the " +
                                         $"build owner {OwnerLabel(_data.Owner)}; the config value wins", "owner", 60f);
            if (_relinkWanted && now - _lastRelinkAt >= RelinkThrottleSeconds)
            {
                _relinkWanted = false;
                Guard("relink", "vanished animal", () => RelinkAll(pass));
            }
            if (!_data.HerdSpawned && _herdTimer == null)
                ScheduleHerd();
            bool owner = pass.Owner != 0;
            if (!owner)
                Failure("owner", "no owner resolves (no Owner SteamID, build owner, or cfg/users.cfg owner); the " +
                                 "herd, cull, sex safety, and breeding steps are skipped");
            foreach (Step step in _steps)
            {
                if (step.NeedsOwner && !owner)
                    continue;
                try
                {
                    step.Run(pass);
                }
                catch (Exception e)
                {
                    Failure($"step:{step.Name}", $"upkeep step {step.Name} threw {e}");
                }
            }
            string restored = $"restored to {_config.NeedRestore:0.00} (chickens {_config.NeedRestore * 100f:0}), " +
                              $"below {_config.NeedThreshold:0.00} (chickens {_config.NeedThreshold * 100f:0}): ";
            if (pass.Needs.Count > 0)
                Event("needs", restored + string.Join("; ", pass.Needs), true);
            if (pass.Crowding.Count > 0)
                Event("needs", restored + string.Join("; ", pass.Crowding));
            if (pass.Destroyed.Count > 0)
                Event("destroyed", string.Join("; ", pass.Destroyed), true);
        }

        private void StructureStep(UpkeepPass pass)
        {
            if (pass.Cupboard == null)
            {
                CupboardMissing();
            }
            else if (pass.Owner != 0 && !pass.Cupboard.IsAuthed(pass.Owner))
            {
                Authorize(pass.Cupboard, pass.Owner);
                Puts($"cupboard: authorized owner {OwnerLabel(pass.Owner)}");
            }
            foreach (BaseEntity entity in pass.Role(RoleGate))
                Guard("structures", pass.Name(entity), () =>
                {
                    var gate = entity as Door;
                    if (gate == null || !gate.IsOpen() ||
                        AnyPlayerNear(gate.transform.position, _config.GateCloseDistance))
                        return;
                    gate.SetOpen(false);
                    Puts($"gate: closed {pass.Name(entity)}, no player within {_config.GateCloseDistance:0} m");
                });
        }

        private void CupboardMissing() =>
            Failure("cupboard", "cupboard missing, run farm.repair: animals fall back to the herd point as their " +
                                "home, and reseeding and the starter herd wait until it is back");

        // Builds every layout connection that is not in place, and re-makes one a player or decay removed.
        private void WiringStep(UpkeepPass pass)
        {
            foreach (LayoutConnection link in _config.Layout.Connections)
                Guard("wiring", link.ToString(), () =>
                {
                    string problem = WireProblem(link, pass);
                    if (problem == null)
                        return;
                    if (problem.Length == 0)
                        Puts($"wiring: connected {link}");
                    else
                        Failure($"wire:{link}", $"wiring {link}: {problem}");
                });
        }

        // Null when the link is already in place, empty when this call made it, otherwise why it cannot be made.
        private static string WireProblem(LayoutConnection link, UpkeepPass pass)
        {
            var from = pass.ById(link.From) as IOEntity;
            var to = pass.ById(link.To) as IOEntity;
            if (from == null || to == null)
                return $"{(from == null ? link.From : link.To)} is missing or not an IO entity";
            if (link.FromSlot < 0 || link.FromSlot >= from.outputs.Length || link.ToSlot < 0 ||
                link.ToSlot >= to.inputs.Length)
                return "no such IO slot";
            IOEntity.IOSlot output = from.outputs[link.FromSlot];
            IOEntity.IOSlot input = to.inputs[link.ToSlot];
            if (output.connectedTo.Get() == to && output.connectedToSlot == link.ToSlot)
                return null;
            if (output.connectedTo.Get() != null || input.connectedTo.Get() != null)
                return "a slot is already connected elsewhere";
            if (output.type != input.type)
                return $"slot types differ ({output.type} to {input.type})";
            var pump = from as WaterPump;
            if (pump != null &&
                WaterResource.SV_GetAtPoint(pump.WaterResourceLocation.position) != WaterTypes.WaterItemDef)
                return "the pump would deliver salt or radioactive water, which empties or kills the planters; " +
                       "left unwired";
            if (!Connect(from, link.FromSlot, to, link.ToSlot))
                return "ConnectTo refused";
            return string.Empty;
        }

        // A hose or wire from an output to an input. ConnectTo sets both slots, marks the source dirty, and walks
        // SendChangedToRoot; the two line points (output-local, input end first) only draw it.
        private static bool Connect(IOEntity from, int output, IOEntity to, int input)
        {
            if (from.outputs[output].connectedTo.Get() != null || to.inputs[input].connectedTo.Get() != null)
                return false;
            var points = new List<Vector3>
            {
                from.transform.InverseTransformPoint(to.transform.TransformPoint(to.inputs[input].handlePosition)),
                from.outputs[output].handlePosition,
            };
            from.ConnectTo(to, output, input, points, new List<float> { 0f, 0f }, new IOEntity.LineAnchor[0],
                WireTool.WireColour.Gray);
            return true;
        }

        // Sweeps the dropped items OnEntitySpawned tracked within the farm radius (dung, eggs), no area query.
        private void SweepStep(UpkeepPass pass)
        {
            if (_dropped.Count == 0)
                return;
            Vector3 centre = FarmCentre();
            foreach (DroppedItem dropped in _dropped.ToList())
            {
                bool gone = dropped == null || dropped.IsDestroyed || dropped.item == null;
                Vector3 offset = gone ? Vector3.zero : dropped.transform.position - centre;
                if (gone || new Vector2(offset.x, offset.z).magnitude > _config.FarmRadius)
                {
                    _dropped.Remove(dropped);
                    continue;
                }
                Guard("sweep", $"dropped {dropped.item.info.shortname} [{IdOf(dropped)}]", () => Sweep(dropped, pass));
            }
        }

        private void Sweep(DroppedItem dropped, UpkeepPass pass)
        {
            Item item = dropped.item;
            string role;
            if (!_config.SweptItems.TryGetValue(item.info.shortname, out role))
            {
                _dropped.Remove(dropped);
                return;
            }
            string shortname = item.info.shortname;
            int amount = item.amount;
            int moved = MoveToContainers(item, Targets(pass, shortname, role));
            Collect(shortname, moved);
            if (moved < amount)
                Full(role, $"{role} is full or missing; {amount - moved} {shortname} stay on the ground at " +
                           $"{dropped.transform.position}");
        }

        private void HerdStep(UpkeepPass pass)
        {
            // Before the starter herd spawns, an adopted wild animal would count as that species' herd already linked
            // and stop its starter animals; the starter herd adopts right after it spawns instead.
            if (_data.HerdSpawned)
                Guard("herd", "pen adoption", () => AdoptPenAnimals(pass));
            List<Vector2> pen = PenHull();
            var strays = new List<string>();
            foreach (LivestockAnimal animal in pass.Animals.ToList())
            {
                if (animal == null || animal.IsDestroyed || animal.IsDead() || !_animals.ContainsKey(IdOf(animal)))
                    continue;
                Guard("herd", Describe(animal), () =>
                {
                    if (CheckStray(animal, pen, strays))
                        return;
                    Bond(animal, pass.Owner, pass.Cupboard);
                    NeedsNet(animal, pass);
                    var cow = animal as Cow;
                    if (cow != null)
                        Milk(cow, pass);
                    var sheep = animal as Sheep;
                    if (sheep != null)
                        Shear(sheep, pass);
                });
            }
            if (strays.Count > 0)
                Failure("strays", $"{strays.Count} farm animals outside the fence line: {string.Join("; ", strays)}",
                    "stray");
            _withinRadius.RemoveWhere(id => !_animals.ContainsKey(id));
            foreach (ulong id in _returning.Keys.Where(id => !_animals.ContainsKey(id)).ToList())
                _returning.Remove(id);
        }

        // PersonalSpace drifts toward a crowding target (herd size and packing), so it can trip again soon.
        private void NeedsNet(LivestockAnimal animal, UpkeepPass pass)
        {
            foreach (LivestockAnimal.Need need in TrackedNeeds)
            {
                float value = animal.GetNeed(need).NeedValue;
                if (value >= _config.NeedThreshold)
                    continue;
                animal.SetNeedValue(need, _config.NeedRestore);
                if (!NeedLogDue($"{IdOf(animal)}:{need}"))
                    continue;
                if (need == LivestockAnimal.Need.PersonalSpace)
                    pass.Crowding.Add($"{Describe(animal)} {need} {value:0.00} (crowding)");
                else
                    pass.Needs.Add($"{Describe(animal)} {need} {value:0.00}");
            }
        }

        // A restore always happens; its log line comes at most once per animal and need every NeedLogSeconds.
        private bool NeedLogDue(string key)
        {
            float now = Time.realtimeSinceStartup;
            float last;
            if (_needLoggedAt.TryGetValue(key, out last) && now - last < NeedLogSeconds)
                return false;
            _needLoggedAt[key] = now;
            return true;
        }

        // Cow.TryMilk without the player: milk goes to storage, or stays on the cow when there is no room.
        private void Milk(Cow cow, UpkeepPass pass)
        {
            if (!cow.IsAdult() || cow.IsMale || !cow.HasFlag(Cow.MilkReady))
                return;
            if (cow.MilkDef == null)
            {
                Failure($"milkdef:{cow.ShortPrefabName}", $"{cow.ShortPrefabName} has no MilkDef; cows give no milk");
                return;
            }
            Item milk = ItemManager.Create(cow.MilkDef, cow.MilkedAmount());
            Dictionary<string, int> stored = StoreAll(new List<Item> { milk },
                i => Targets(pass, i.info.shortname, BoxMilk), $"the milk of {Describe(cow)}");
            if (stored == null)
            {
                Full(BoxMilk, $"no room for milk in a fridge or {BoxMilk}; cows stay unmilked");
                return;
            }
            CollectAll(stored);
            cow.milkTimer.Start(cow.MilkCooldown / cow.GeneScale(LivestockGene.Yield));
        }

        // Sheep.Shear for all four quarters at once without the player.
        private void Shear(Sheep sheep, UpkeepPass pass)
        {
            if (!sheep.IsAdult() || sheep.ShornQuadrants == LivestockFleece.AllQuadrants)
                return;
            var fleece = new List<Item>();
            foreach (ItemAmount shearItem in sheep.ShearItems)
            {
                if (shearItem?.itemDef == null)
                    continue;
                int amount = sheep.UnshornAmount(shearItem);
                if (amount > 0)
                    fleece.Add(ItemManager.Create(shearItem.itemDef, amount));
            }
            if (fleece.Count > 0)
            {
                Dictionary<string, int> stored = StoreAll(fleece, i => Targets(pass, i.info.shortname, BoxWool),
                    $"the fleece of {Describe(sheep)}");
                if (stored == null)
                {
                    Full(BoxWool, $"no room for wool in {BoxWool}; sheep stay unshorn");
                    return;
                }
                CollectAll(stored);
            }
            sheep.ShornQuadrants = LivestockFleece.AllQuadrants;
            sheep.shearTimer.Start(sheep.ShearCooldown / sheep.GeneScale(LivestockGene.Yield));
            sheep.SendNetworkUpdate();
        }

        // One cull at a time: nothing new is culled while a cull still waits for its corpse.
        private void CullStep(UpkeepPass pass)
        {
            float now = Time.realtimeSinceStartup;
            _data.CapExempt.RemoveAll(id => !_animals.ContainsKey(id));
            foreach (KeyValuePair<ulong, PendingCull> pair in _pendingCulls.ToList())
            {
                if (now - pair.Value.Since <= _config.CorpseTimeout)
                    continue;
                _pendingCulls.Remove(pair.Key);
                Failure($"corpse:{pair.Key}", $"no corpse appeared for {pair.Value.Description} within " +
                                              $"{_config.CorpseTimeout:0} s; nothing was stored", "cull");
            }
            foreach (string species in AllSpecies)
            {
                if (_pendingCulls.Count > 0)
                    return;
                Guard("cull", species, () => CullSpecies(species, pass));
            }
        }

        private void CullSpecies(string species, UpkeepPass pass)
        {
            List<LivestockAnimal> herd = Herd(pass, species);
            int cap = CapOf(species);
            int count = CapCount(herd);
            if (count <= cap)
                return;
            string reason;
            LivestockAnimal victim = PickCull(herd, out reason);
            if (victim == null)
            {
                Failure($"cull:{species}", $"{species} at {count}/{cap}, but {reason}", "cull");
                return;
            }
            List<Item> expected = ExpectedYield(victim);
            bool room = StoreCheck(expected, i => CorpseTargets(pass, i.info.shortname));
            string yield = Summarize(expected);
            foreach (Item item in expected)
                item.Remove();
            if (!room)
            {
                Full(BoxMeat, $"no room for the yield of {Describe(victim)} ({yield}); {species} stays at " +
                              $"{count}/{cap} until there is");
                return;
            }
            Cull(victim, species, count, cap);
        }

        private static LivestockAnimal PickCull(List<LivestockAnimal> herd, out string reason)
        {
            List<LivestockAnimal> adults = herd.Where(a => a.IsAdult()).ToList();
            List<LivestockAnimal> males = adults.Where(a => a.IsMale).ToList();
            List<LivestockAnimal> females = adults.Where(a => a.IsFemale).ToList();
            reason = null;
            if (males.Count > 1)
                return males.OrderBy(LifeLeft).First();
            if (females.Count <= 1)
            {
                reason = $"only {males.Count} adult male and {females.Count} adult female, which are never culled";
                return null;
            }
            List<LivestockAnimal> open = females.Where(f => !f.IsPregnant() && !f.IsCarrying).ToList();
            if (open.Count == 0)
            {
                reason = "every adult female is pregnant or calving";
                return null;
            }
            return open.OrderBy(LifeLeft).First();
        }

        // The corpse prefab's yield, read without spawning it, plus the unshorn wool Sheep.AddCorpseYield adds. It runs
        // the same gather hooks as the harvest, so GatherManager's multiplier counts in the room check.
        private static List<Item> ExpectedYield(LivestockAnimal animal)
        {
            string path = animal.GetComponent<LivestockAnimalFSM>()?.dead?.Corpse?.resourcePath;
            ResourceDispenser dispenser = string.IsNullOrEmpty(path)
                ? null
                : GameManager.server.FindPrefab(path)?.GetComponent<ResourceDispenser>();
            var wool = new List<ItemAmount>();
            var sheep = animal as Sheep;
            if (sheep != null && !sheep.IsInfant())
                foreach (ItemAmount shearItem in sheep.ShearItems)
                {
                    if (shearItem?.itemDef == null)
                        continue;
                    int amount = sheep.UnshornAmount(shearItem);
                    if (amount > 0)
                        wool.Add(new ItemAmount(shearItem.itemDef, amount));
                }
            return GatherItems(dispenser, wool);
        }

        private void Cull(LivestockAnimal victim, string species, int count, int cap)
        {
            ulong id = IdOf(victim);
            string description = Describe(victim);
            _pendingCulls[id] = new PendingCull { Description = description, Since = Time.realtimeSinceStartup };
            Event("cull", $"{description} with {Duration(LifeLeft(victim))} of life left; {species} {count}/{cap}");
            // BaseNPC2.DieOffNavmesh kills with the same hit, so the FSM plays its death and drops the corpse.
            var hit = new HitInfo();
            hit.damageTypes.Set(Rust.DamageType.Generic, victim.MaxHealth());
            victim.Die(hit);
        }

        private void HarvestCorpse(BaseCorpse corpse, PendingCull cull)
        {
            if (corpse == null || corpse.IsDestroyed)
            {
                Failure($"corpse:{cull.Description}", $"the corpse of {cull.Description} vanished before " +
                                                      "butchering; nothing was stored", "cull");
                return;
            }
            UpkeepPass pass = BeginPass();
            List<Item> items = GatherItems(corpse.GetComponent<ResourceDispenser>(), null);
            Dictionary<string, int> stored = StoreMove(items, i => CorpseTargets(pass, i.info.shortname),
                $"the yield of {cull.Description}");
            CollectAll(stored);
            corpse.Kill();
            Event("cull", $"{cull.Description} butchered: " +
                          (stored.Count > 0 ? Summarize(stored) : "nothing in the corpse"));
        }

        // The whole butchering yield of a destroyFraction-0 tool: containedItems rounded, then finishBonus, each
        // through the hooks vanilla fires for a player (OnDispenserGather, where non-null cancels the item, and
        // OnDispenserBonus, where an Item replaces it) with no player, so GatherManager's multiplier applies.
        private static List<Item> GatherItems(ResourceDispenser dispenser, List<ItemAmount> extra)
        {
            var items = new List<Item>();
            if (dispenser == null)
                return items;
            var contained = new List<ItemAmount>(dispenser.containedItems ?? new List<ItemAmount>());
            if (extra != null)
                contained.AddRange(extra);
            foreach (ItemAmount amount in contained)
            {
                int count = amount == null ? 0 : Mathf.RoundToInt(amount.amount);
                if (amount?.itemDef == null || count <= 0)
                    continue;
                Item item = ItemManager.Create(amount.itemDef, count);
                if (item == null)
                    continue;
                if (Interface.CallHook("OnDispenserGather", dispenser, null, item) != null || item.amount <= 0)
                {
                    item.Remove();
                    continue;
                }
                items.Add(item);
            }
            foreach (ItemAmount bonus in new List<ItemAmount>(dispenser.finishBonus ?? new List<ItemAmount>()))
            {
                int count = bonus == null ? 0 : Mathf.CeilToInt(bonus.amount);
                if (bonus?.itemDef == null || count <= 0)
                    continue;
                Item item = ItemManager.Create(bonus.itemDef, count);
                if (item == null)
                    continue;
                var replacement = Interface.CallHook("OnDispenserBonus", dispenser, null, item) as Item;
                if (replacement != null && replacement != item)
                {
                    item.Remove();
                    item = replacement;
                }
                if (item.amount > 0)
                    items.Add(item);
                else
                    item.Remove();
            }
            return items;
        }

        // Reseeds wait 10 minutes. When the last reseed's animals are gone by the next pass and the herd lacks a sex
        // again, the wait doubles up to 2 hours; a reseed that holds resets it. A juvenile that grew (new net id)
        // leaves the herd complete, so growth never counts as a loss.
        private void SexStep(UpkeepPass pass)
        {
            if (!_data.HerdSpawned)
                return;
            long now = Epoch.Current;
            foreach (string species in AllSpecies)
                Guard("sex safety", species, () => SexSafety(species, pass, now));
        }

        private void SexSafety(string species, UpkeepPass pass, long now)
        {
            ReseedState state = ReseedOf(species);
            List<LivestockAnimal> herd = Herd(pass, species);
            bool hasMale = herd.Any(a => a.IsMale);
            bool hasFemale = herd.Any(a => a.IsFemale);
            bool complete = hasMale && hasFemale;
            string lack = herd.Count == 0 ? $"no {species}" : $"no {(hasMale ? "female" : "male")} {species}";
            if (state.Check)
            {
                state.Check = false;
                int gone = state.Ids.Count(id => !_animals.ContainsKey(id));
                if (gone > 0 && !complete)
                {
                    state.Wait = Mathf.Min(state.Wait * 2f, ReseedMaxMinutes);
                    Event("reseed", $"{gone} of {state.Ids.Count} reseeded {species} were gone by the next pass and " +
                                    $"there is {lack} again; the next {species} reseed waits {state.Wait:0} min", true);
                }
                else
                {
                    state.Wait = ReseedBaseMinutes;
                }
            }
            if (complete)
                return;
            if (pass.Cupboard == null)
            {
                CupboardMissing();
                return;
            }
            long next = state.At + (long)(state.Wait * 60f);
            if (state.At > 0 && now < next)
            {
                Failure($"reseedwait:{species}", $"{lack} left; the next reseed is in {Duration(next - now)}",
                    "reseed");
                return;
            }
            List<LivestockAnimal> spawned;
            if (herd.Count == 0)
            {
                spawned = SpawnSpecies(species, pass);
            }
            else
            {
                string prefab = species == Cattle ? (hasMale ? CalfPrefab : CalfMalePrefab) : LambPrefab;
                LivestockAnimal juvenile = null;
                Guard("reseed", prefab, () => juvenile = SpawnAnimal(prefab, !hasMale, pass));
                spawned = juvenile != null ? new List<LivestockAnimal> { juvenile } : new List<LivestockAnimal>();
                // It counts toward the cap only once grown, so it never forces a cull of a breeding female.
                if (juvenile != null)
                    _data.CapExempt.Add(IdOf(juvenile));
            }
            state.At = now;
            if (spawned.Count == 0)
            {
                Failure($"reseed:{species}", $"{lack} left and the reseed spawned nothing; retrying in " +
                                             $"{state.Wait:0} min", "reseed");
                return;
            }
            state.Ids = spawned.Select(IdOf).ToList();
            state.Check = true;
            Event("reseed", $"{lack} left; spawned {spawned.Count}: {string.Join(", ", spawned.Select(Describe))}",
                true);
        }

        private void BreedingStep(UpkeepPass pass)
        {
            float now = Time.realtimeSinceStartup;
            foreach (string species in AllSpecies)
                Guard("breeding", species, () => Breed(species, pass, now));
        }

        private void Breed(string species, UpkeepPass pass, float now)
        {
            List<LivestockAnimal> herd = Herd(pass, species);
            var pregnant = new HashSet<ulong>(herd.Where(a => a.IsFemale && a.IsPregnant()).Select(IdOf));
            // The set persists in the record; without one (a new build or an older record) the first pass only seeds
            // it, so pregnancies that predate the load never reset the clock.
            HashSet<ulong> before;
            bool fresh = _pregnantLastPass.TryGetValue(species, out before) && pregnant.Any(id => !before.Contains(id));
            _pregnantLastPass[species] = pregnant;
            float since;
            _data.SecondsSincePregnancy.TryGetValue(species, out since);
            since = fresh ? 0f : since + pass.Elapsed;
            _data.SecondsSincePregnancy[species] = since;

            int cap = CapOf(species);
            float nextAttempt;
            if (CapCount(herd) >= cap || since < _config.BreedingFallbackMinutes * 60f ||
                (_nextFallbackAttempt.TryGetValue(species, out nextAttempt) && now < nextAttempt))
                return;
            LivestockAnimal female = herd.Where(a => a.IsAdult() && a.IsFemale && a.CanBePregnant())
                .OrderByDescending(LifeLeft).FirstOrDefault();
            LivestockAnimal male = herd.Where(a => a.CanBreed()).OrderByDescending(LifeLeft).FirstOrDefault();
            if (female == null || male == null || !female.MakePregnant(male))
            {
                _nextFallbackAttempt[species] = now + _config.BreedingRetryMinutes * 60f;
                Failure($"breed:{species}", $"fallback for {species} after {since / 60f:0} min without a new " +
                                            $"pregnancy cannot run: {BreedingBlocker(herd, female, male)}",
                    "breeding");
                return;
            }
            pregnant.Add(IdOf(female));
            _data.SecondsSincePregnancy[species] = 0f;
            Event("breeding", $"fallback after {since / 60f:0} min without a new pregnancy: {Describe(female)} " +
                              $"pregnant by {Describe(male)}");
        }

        private static string BreedingBlocker(List<LivestockAnimal> herd, LivestockAnimal female, LivestockAnimal male)
        {
            var reasons = new List<string>();
            if (female == null)
            {
                List<LivestockAnimal> adults = herd.Where(a => a.IsAdult() && a.IsFemale).ToList();
                reasons.Add($"no eligible female ({adults.Count} adult, {adults.Count(a => a.IsPregnant())} " +
                            $"pregnant, {adults.Count(a => a.OnBreedingCooldown)} on cooldown, " +
                            $"{adults.Count(a => a.BreedingPaused)} breeding paused, " +
                            $"{adults.Count(a => !a.IsFullyBonded)} not fully bonded)");
            }
            if (male == null)
            {
                List<LivestockAnimal> adults = herd.Where(a => a.IsAdult() && a.IsMale).ToList();
                reasons.Add($"no eligible male ({adults.Count} adult, {adults.Count(a => a.OnBreedingCooldown)} on " +
                            $"cooldown, {adults.Count(a => a.IsLeading())} on a lead, " +
                            $"{adults.Count(a => a.BreedingPaused)} breeding paused)");
            }
            if (reasons.Count == 0)
                reasons.Add($"MakePregnant refused {Describe(female)}");
            if (herd.Count > 0 && herd[0].SpeciesAtCap)
                reasons.Add($"livestock.maxperspecies {ConVar.Livestock.maxPerSpecies} reached");
            return string.Join("; ", reasons);
        }

        #endregion

        #region Crops

        // Planter sockets, as entity.spawnplants finds them: female sockets that take the plant's "planter_slot",
        // ordered by socket name so slot indices are stable.
        private static Socket_Specific_Female[] Slots(PlanterBox planter)
        {
            Socket_Specific_Female[] slots;
            if (!SlotCache.TryGetValue(planter.prefabID, out slots))
            {
                slots = PrefabAttribute.server.FindAll<Socket_Base>(planter.prefabID)
                    .OfType<Socket_Specific_Female>()
                    .Where(s => s.allowedMaleSockets != null && s.allowedMaleSockets.Contains("planter_slot"))
                    .OrderBy(s => s.socketName)
                    .ToArray();
                SlotCache[planter.prefabID] = slots;
            }
            return slots;
        }

        // Planted plants sit at their socket's local position, so a child maps back to its slot by position.
        private static GrowableEntity[] PlantsBySlot(PlanterBox planter)
        {
            Socket_Specific_Female[] slots = Slots(planter);
            var bySlot = new GrowableEntity[slots.Length];
            foreach (BaseEntity child in planter.children)
            {
                var plant = child as GrowableEntity;
                if (plant == null || plant.IsDestroyed || plant.IsDead())
                    continue;
                Vector3 local = plant.transform.localPosition;
                for (int i = 0; i < slots.Length; i++)
                {
                    if (bySlot[i] == null && (slots[i].localPosition - local).sqrMagnitude < 0.01f)
                    {
                        bySlot[i] = plant;
                        break;
                    }
                }
            }
            return bySlot;
        }

        private void CropStep(UpkeepPass pass)
        {
            Dictionary<string, int> planned = PlannedSlots();
            foreach (BaseEntity entity in pass.Role(RolePlanter))
                Guard("crops", pass.Name(entity), () => TendPlanter(entity, pass, planned));
        }

        private void TendPlanter(BaseEntity entity, UpkeepPass pass, Dictionary<string, int> planned)
        {
            string id = pass.Name(entity);
            var planter = entity as PlanterBox;
            if (planter == null)
            {
                Failure($"type:{id}", $"{id} is not a planter");
                return;
            }
            List<string> plan;
            if (!_config.SlotPlan.TryGetValue(id, out plan) || plan == null)
            {
                Failure($"plan:{id}", $"{id} has no entry in the slot plan, so nothing is planted in it");
                return;
            }
            TendSoil(planter, id, pass);
            Fertilize(planter, id, pass);
            GrowableEntity[] plants = PlantsBySlot(planter);
            for (int i = 0; i < plants.Length; i++)
            {
                GrowableEntity plant = plants[i];
                if (plant == null)
                    continue;
                switch (plant.State)
                {
                    case PlantProperties.State.Ripe:
                        HarvestPlant(plant, id, i, pass);
                        break;
                    case PlantProperties.State.Dying:
                        ClearDyingPlant(plant, id, i, pass);
                        break;
                    case PlantProperties.State.Sapling:
                        ClonePlant(plant, id, i, pass, planned);
                        break;
                }
            }
            Replant(planter, id, plan, pass, planned);
        }

        // Sprinklers splash until the soil is full, which caps water quality at 0.86 (potato) or 0.71; hold the
        // ideal 60% instead. A planter refills only after its soil stayed under the dry threshold for the whole
        // window without rising once, since sprinklers that reach it raise it between passes.
        private void TendSoil(PlanterBox planter, string id, UpkeepPass pass)
        {
            if (planter.soilSaturation > planter.idealSaturation)
            {
                planter.soilSaturation = planter.idealSaturation;
                planter.SendNetworkUpdate();
            }
            ulong key = IdOf(planter);
            int soil = planter.soilSaturation;
            if (soil >= _config.DryFraction * planter.soilSaturationMax)
            {
                _dry.Remove(key);
                return;
            }
            float now = Time.realtimeSinceStartup;
            DryWatch watch;
            if (!_dry.TryGetValue(key, out watch) || soil > watch.Last)
            {
                _dry[key] = new DryWatch { Since = now, Start = soil, Last = soil };
                return;
            }
            watch.Last = soil;
            if (now - watch.Since < _config.DryMinutes * 60f)
                return;
            planter.soilSaturation = planter.idealSaturation;
            planter.SendNetworkUpdate();
            _dry.Remove(key);
            Event("topup", $"{id}: soil water went from {watch.Start} to {soil} ml, under {_config.DryFraction:P0} " +
                           $"of {planter.soilSaturationMax} ml, for {_config.DryMinutes:0} min without rising " +
                           $"({WaterSupplyProblem(pass)}); set to {planter.idealSaturation} ml", true);
        }

        // What the planter water chain shows: a missing or unpowered pump, or a pump power or water link not in
        // place; otherwise the soil simply fell faster than the sprinklers refilled it.
        private string WaterSupplyProblem(UpkeepPass pass)
        {
            var problems = new List<string>();
            List<BaseEntity> pumps = pass.Role(RolePump);
            if (pumps.Count == 0 && _config.Layout.Entries.Any(e => e.Role == RolePump))
                problems.Add("the pump is missing");
            foreach (WaterPump pump in pumps.OfType<WaterPump>())
                if (!pump.IsPowered())
                    problems.Add($"{pass.Name(pump)} unpowered");
            var pumpIds = new HashSet<string>(_config.Layout.Entries.Where(e => e.Role == RolePump).Select(e => e.Id));
            foreach (LayoutConnection link in _config.Layout.Connections)
                if ((link.Kind == "water" || pumpIds.Contains(link.To)) && !IsWired(link, pass))
                    problems.Add($"link {link} missing");
            return problems.Count > 0
                ? string.Join(", ", problems)
                : "soil falling with the pump powered and every pump link in place";
        }

        private static bool IsWired(LayoutConnection link, UpkeepPass pass)
        {
            var from = pass.ById(link.From) as IOEntity;
            BaseEntity to = pass.ById(link.To);
            return from != null && to != null && link.FromSlot >= 0 && link.FromSlot < from.outputs.Length &&
                   from.outputs[link.FromSlot].connectedTo.Get() == to;
        }

        // One fertilizer per slot in the planter's own inventory: PlanterBox.FertilizeGrowables gives one to each
        // new plant when it is parented or when fertilizer arrives.
        private void Fertilize(PlanterBox planter, string id, UpkeepPass pass)
        {
            int missing = Slots(planter).Length - planter.GetFertilizerCount();
            if (missing <= 0)
                return;
            int moved = 0;
            foreach (Item fertilizer in FertilizerSources(pass))
            {
                if (moved >= missing)
                    break;
                moved += MoveSome(fertilizer, planter.inventory, missing - moved);
            }
            if (moved == 0 && planter.GetFertilizerCount() == 0)
                Failure("fertilizer", $"no fertilizer for {id} in the composter or {BoxDung}; new plants grow " +
                                      "unfertilized", "failure", _config.FullRepeatMinutes);
        }

        private static List<Item> FertilizerSources(UpkeepPass pass)
        {
            var items = new List<Item>();
            foreach (Composter composter in pass.Role(RoleComposter).OfType<Composter>())
            {
                BaseOven.MinMax outputs = composter.GetOutputSlotRange();
                for (int i = outputs.Min; i <= outputs.Max; i++)
                {
                    Item made = composter.inventory.GetSlot(i);
                    if (made != null && made.amount > 0 && made.info.shortname == FertilizerShortname)
                        items.Add(made);
                }
            }
            items.AddRange(pass.Containers(BoxDung).SelectMany(c => c.itemList)
                .Where(i => i.amount > 0 && i.info.shortname == FertilizerShortname).ToList());
            return items;
        }

        // GrowableEntity.PickFruit into the feed box. Only at Ripe: an earlier pick ends the plant for a fraction.
        private void HarvestPlant(GrowableEntity plant, string id, int slot, UpkeepPass pass)
        {
            if (!plant.CanPick(null))
                return;
            var items = new List<Item>();
            int amount = plant.CurrentPickAmount;
            ItemDefinition produceDef = plant.Properties.pickupItem;
            if (amount > 0 && produceDef != null)
            {
                Item produce = ItemManager.Create(produceDef, amount);
                // Carbon's GatherManager multiplies planted crops only in this hook, which vanilla fires only for a
                // player pick (GiveFruit IL index 52, inside "player != null").
                Interface.CallHook("OnGrowableGathered", plant, produce, null);
                items.Add(produce);
            }
            // maxHarvests is 1 for all four crops, so the plant would turn Dying; free the slot now and store the
            // plant fiber "Clear" would give. The worm or grub roll of a player pick is skipped.
            if (!plant.Properties.disappearAfterHarvest && plant.Properties.removeDyingItem != null)
                items.Add(ItemManager.Create(plant.Properties.removeDyingItem, 1));
            Dictionary<string, int> stored = StoreAll(items, i => pass.Containers(BoxFeed),
                $"the {plant.ShortPrefabName} harvest in {id} slot {slot + 1}");
            if (stored == null)
            {
                Full(BoxFeed, $"no room in {BoxFeed}; ripe {plant.ShortPrefabName} in {id} slot {slot + 1} stays " +
                              "unharvested");
                return;
            }
            if (produceDef != null)
                Collect(produceDef.shortname, Amount(stored, produceDef.shortname));
            plant.TellPlanter();
            plant.Die();
        }

        // GrowableEntity.RemoveDying with the plant fiber stored, or cleared without it when there is no room.
        private void ClearDyingPlant(GrowableEntity plant, string id, int slot, UpkeepPass pass)
        {
            ItemDefinition fiber = plant.Properties.removeDyingItem;
            if (fiber != null && StoreAll(new List<Item> { ItemManager.Create(fiber, 1) },
                    i => pass.Containers(BoxFeed), $"the dying plant in {id} slot {slot + 1}") == null)
                pass.Destroyed.Add($"the dying {plant.ShortPrefabName} in {id} slot {slot + 1} cleared without its " +
                                   $"{fiber.shortname} ({BoxFeed} full)");
            plant.TellPlanter();
            plant.Die();
        }

        // GrowableEntity.TakeClones without a player, while the clone stock is below its planned slot count.
        private void ClonePlant(GrowableEntity plant, string id, int slot, UpkeepPass pass,
            Dictionary<string, int> planned)
        {
            ItemDefinition cloneDef = plant.Properties.CloneItem;
            int reserve;
            if (cloneDef == null || !planned.TryGetValue(cloneDef.shortname, out reserve) ||
                CountItems(pass.Containers(BoxClones), cloneDef.shortname) >= reserve || !plant.CanClone(null))
                return;
            int count = plant.Properties.BaseCloneCount +
                        plant.Genes.GetGeneTypeCount(GrowableGenetics.GeneType.Yield) / 2;
            if (count <= 0)
                return;
            Item clones = ItemManager.Create(cloneDef, count);
            GrowableGeneEncoding.EncodeGenesToItem(plant, clones);
            if (StoreAll(new List<Item> { clones }, i => pass.Containers(BoxClones),
                    $"the clones of {id} slot {slot + 1}") == null)
            {
                Full(BoxClones, $"no room in {BoxClones}; the {plant.ShortPrefabName} sapling in {id} slot " +
                                $"{slot + 1} is not cloned");
                return;
            }
            plant.TellPlanter();
            plant.Die();
        }

        private void Replant(PlanterBox planter, string id, List<string> plan, UpkeepPass pass,
            Dictionary<string, int> planned)
        {
            GrowableEntity[] plants = PlantsBySlot(planter);
            List<ItemContainer> boxes = pass.Containers(BoxClones);
            float now = Time.realtimeSinceStartup;
            // The clone fallback runs first, so every empty slot of a recreated crop is planted in this pass.
            var due = new Dictionary<string, int>();
            for (int i = 0; i < plants.Length; i++)
            {
                string key = $"{IdOf(planter)}:{i}";
                string clone = i < plan.Count ? plan[i] : null;
                if (plants[i] != null || string.IsNullOrEmpty(clone) || FirstStock(boxes, clone) != null)
                {
                    _slotEmptySince.Remove(key);
                    continue;
                }
                float since;
                if (!_slotEmptySince.TryGetValue(key, out since))
                    _slotEmptySince[key] = now;
                else if (now - since >= _config.CloneFallbackMinutes * 60f && !due.ContainsKey(clone))
                    due[clone] = i;
            }
            foreach (KeyValuePair<string, int> pair in due)
            {
                int wanted;
                planned.TryGetValue(pair.Key, out wanted);
                int made = MakeClones(boxes, pair.Key, 2 * Math.Max(1, wanted));
                if (made == 0)
                {
                    Full(BoxClones, $"no {pair.Key} left and no room in {BoxClones} to recreate them; {id} slot " +
                                    $"{pair.Value + 1} stays empty");
                    continue;
                }
                Event("reseed", $"no {pair.Key} left and {id} slot {pair.Value + 1} sat empty for " +
                                $"{_config.CloneFallbackMinutes:0} min; created {made} {pair.Key} " +
                                $"({_config.CloneGenes})", true);
            }
            for (int i = 0; i < plants.Length; i++)
            {
                string clone = i < plan.Count ? plan[i] : null;
                Item stock = plants[i] != null || string.IsNullOrEmpty(clone) ? null : FirstStock(boxes, clone);
                if (stock == null)
                    continue;
                if (PlantInto(planter, i, stock) == null)
                    Failure($"plant:{id}:{i}", $"{id} slot {i + 1}: {clone} could not be planted");
                else
                    _slotEmptySince.Remove($"{IdOf(planter)}:{i}");
            }
        }

        private static Item FirstStock(List<ItemContainer> boxes, string shortname) =>
            boxes.SelectMany(c => c.itemList).FirstOrDefault(s => s.amount > 0 && s.info.shortname == shortname);

        // The Planner path for a clone (SpawnConstruction, SetParent with worldPositionStays, ReceiveInstanceData
        // for the genes), minus GrowableEntity.OnDeployed, which dereferences the player.
        private GrowableEntity PlantInto(PlanterBox planter, int slot, Item clone)
        {
            Socket_Specific_Female[] slots = Slots(planter);
            var deployable = clone.info.GetComponent<ItemModDeployable>();
            if (slot < 0 || slot >= slots.Length || deployable == null)
                return null;
            Vector3 position = planter.transform.TransformPoint(slots[slot].localPosition);
            Quaternion rotation = planter.transform.rotation * slots[slot].localRotation;
            string path = deployable.entityPrefab.resourcePath;
            BaseEntity entity = GameManager.server.CreateEntity(path, position, rotation);
            var plant = entity as GrowableEntity;
            if (plant == null)
            {
                if (entity != null)
                    DestroyUnspawned(entity);
                Failure($"plantprefab:{path}", $"{clone.info.shortname} deploys {path}, which is not a GrowableEntity");
                return null;
            }
            plant.OwnerID = planter.OwnerID;
            plant.Spawn();
            plant.SetParent(planter, true);
            if (clone.instanceData != null)
                plant.ReceiveInstanceData(clone.instanceData);
            plant.SendNetworkUpdate();
            clone.UseItem();
            return plant;
        }

        // Composter: fertilizer out to the dung box, horsedung in up to its stock while the dung boxes have room for
        // what it will make. It needs no fuel and turns one item a minute (prefab smeltSpeed 5 against a 300 s
        // compost time); ItemModCookable.CycleCooking drops fertilizer that its own output slots cannot take.
        private void CompostStep(UpkeepPass pass)
        {
            List<ItemContainer> dungBoxes = pass.Containers(BoxDung);
            foreach (BaseEntity entity in pass.Role(RoleComposter))
                Guard("compost", pass.Name(entity), () => TendComposter(entity, pass, dungBoxes));
        }

        private void TendComposter(BaseEntity entity, UpkeepPass pass, List<ItemContainer> dungBoxes)
        {
            string id = pass.Name(entity);
            var composter = entity as Composter;
            if (composter == null)
            {
                Failure($"type:{id}", $"{id} is not a composter");
                return;
            }
            BaseOven.MinMax outputs = composter.GetOutputSlotRange();
            for (int i = outputs.Min; i <= outputs.Max; i++)
            {
                Item made = composter.inventory.GetSlot(i);
                if (made == null || made.amount <= 0 || made.info != composter.FertilizerDef)
                    continue;
                int amount = made.amount;
                if (MoveToContainers(made, dungBoxes) < amount)
                    Full(BoxDung, $"no room in {BoxDung} for the fertilizer {id} made; it stays in the composter");
            }
            int dung = CountItems(new List<ItemContainer> { composter.inventory }, DungShortname);
            List<Item> stock = dungBoxes.SelectMany(c => c.itemList)
                .Where(i => i.amount > 0 && i.info.shortname == DungShortname).ToList();
            if (dung >= _config.ComposterDung || stock.Count == 0)
                return;
            int allowed = Math.Min(_config.ComposterDung - dung, CompostRoom(composter, stock[0].info, dungBoxes));
            if (allowed <= 0)
            {
                Full(BoxDung, $"no room in {BoxDung} for the fertilizer more {DungShortname} would make; {id} is " +
                              "not fed");
                return;
            }
            int added = 0;
            foreach (Item item in stock)
            {
                if (added >= allowed)
                    break;
                added += MoveSome(item, composter.inventory, allowed - added);
            }
        }

        // How many more of def the composter may take while the dung boxes keep room for every fertilizer it holds or
        // will make, counting ceil(TotalFertilizerProduced) per item, the most one composting cycle yields.
        private static int CompostRoom(Composter composter, ItemDefinition def, List<ItemContainer> dungBoxes)
        {
            CookableItemInfo compost = def.ItemModCompostable;
            if (compost == null || compost.amountOfBecome <= 0f)
                return int.MaxValue;
            ItemDefinition fertilizer = composter.FertilizerDef;
            long room = fertilizer == null ? 0L : dungBoxes.Sum(c => RoomFor(c, fertilizer));
            long pending = 0;
            foreach (Item item in composter.inventory.itemList)
            {
                if (item.amount <= 0)
                    continue;
                if (item.info == fertilizer)
                {
                    pending += item.amount;
                    continue;
                }
                CookableItemInfo info = item.info.ItemModCompostable;
                if (info != null && info.amountOfBecome > 0f)
                    pending += (long)Mathf.CeilToInt(info.amountOfBecome) * item.amount;
            }
            long allowed = (room - pending) / Math.Max(1, Mathf.CeilToInt(compost.amountOfBecome));
            return (int)Math.Max(0L, Math.Min(int.MaxValue, allowed));
        }

        #endregion

        #region Chickens, troughs, spoiled produce

        private void ChickenStep(UpkeepPass pass)
        {
            foreach (BaseEntity entity in pass.Role(RoleCoop))
                Guard("chickens", pass.Name(entity), () => TendCoop(entity, pass));
        }

        private void TendCoop(BaseEntity entity, UpkeepPass pass)
        {
            string label = pass.Name(entity);
            var coop = entity as ChickenCoop;
            if (coop == null)
            {
                Failure($"type:{label}", $"{label} is not a chicken coop");
                return;
            }
            Item eggs = coop.inventory.GetSlot(ChickenCoop.FoodProductionSlot);
            if (eggs != null && eggs.amount > 0)
            {
                int amount = eggs.amount;
                int moved = MoveToContainers(eggs, Targets(pass, EggShortname, BoxEggs));
                Collect(EggShortname, moved);
                if (moved < amount)
                    Full(BoxEggs, $"no room for eggs in a fridge or {BoxEggs}; {amount - moved} stay in {label}, " +
                                  "where a full output slot destroys new eggs");
            }
            DropStaleChickens(coop, label, pass);
            StockFood(coop.inventory, ChickenCoop.FoodSlot, IsCoopFood, _config.CoopFeedTarget,
                _config.CoopFeedMinimum, pass, $"{label} food slot", true);
            StockWater(coop.inventory, ChickenCoop.WaterSlot, _config.CoopWaterTarget, _config.CoopWaterMinimum,
                pass, $"{label} water slot");
            foreach (ChickenCoop.AnimalStatus status in coop.Animals)
            {
                FarmableAnimal chicken = status.SpawnedAnimal.Get(true);
                if (chicken != null && !chicken.IsDead())
                    TendChicken(chicken, label, pass);
            }
            RefillCoop(coop, pass, label);
        }

        private static void DropStaleChickens(ChickenCoop coop, string label, UpkeepPass pass)
        {
            int stale = coop.Animals.RemoveAll(s => s.SpawnedAnimal.IsSet && s.SpawnedAnimal.Get(true) == null);
            if (stale == 0)
                return;
            using (BaseEntity.FlagsUpdateScope flags = coop.StartSetFlags(BaseEntity.FlagsUpdateMode.SendNetworkUpdate))
                flags.Set(ChickenCoop.Full, coop.Animals.Count >= coop.MaxChickens);
            pass.Destroyed.Add($"{label}: {stale} chicken entries whose chicken no longer exists");
        }

        private void TendChicken(FarmableAnimal chicken, string label, UpkeepPass pass)
        {
            bool changed = false;
            if (chicken.AnimalLove < 100f)
            {
                chicken.AnimalLove = Mathf.Min(100f, chicken.AnimalLove + PetLove);
                changed = true;
            }
            float threshold = _config.NeedThreshold * 100f;
            float restore = _config.NeedRestore * 100f;
            if (chicken.AnimalHunger < threshold)
            {
                if (NeedLogDue($"{IdOf(chicken)}:hunger"))
                    pass.Needs.Add($"chicken {chicken.AnimalName} in {label} hunger {chicken.AnimalHunger:0}");
                chicken.AnimalHunger = restore;
                changed = true;
            }
            if (chicken.AnimalThirst < threshold)
            {
                if (NeedLogDue($"{IdOf(chicken)}:thirst"))
                    pass.Needs.Add($"chicken {chicken.AnimalName} in {label} thirst {chicken.AnimalThirst:0}");
                chicken.AnimalThirst = restore;
                changed = true;
            }
            if (changed)
                chicken.SendNetworkUpdate();
        }

        private void RefillCoop(ChickenCoop coop, UpkeepPass pass, string label)
        {
            if (coop.Animals.Count >= coop.MaxChickens || coop.HasFlag(ChickenCoop.Hatching))
                return;
            Item egg = coop.inventory.GetSlot(ChickenCoop.EggInsertSlot);
            if (egg == null || egg.amount <= 0 || egg.info.shortname != EggShortname)
                egg = Targets(pass, EggShortname, BoxEggs).SelectMany(c => c.itemList)
                    .FirstOrDefault(i => i.info.shortname == EggShortname && i.amount > 0);
            if (egg == null)
            {
                int before = coop.Animals.Count;
                coop.DebugFillCoop();
                Event("reseed", $"{label}: no egg in a fridge or {BoxEggs}; DebugFillCoop added " +
                                $"{coop.Animals.Count - before} chickens ({coop.Animals.Count}/{coop.MaxChickens})",
                    true);
                return;
            }
            // ChickenCoop.SubmitEggForHatching without the looting player.
            egg.UseItem();
            coop.Animals.Add(new ChickenCoop.AnimalStatus { TimeUntilHatch = coop.ChickenHatchTimeMinutes * 60f });
            using (BaseEntity.FlagsUpdateScope flags = coop.StartSetFlags(BaseEntity.FlagsUpdateMode.SendNetworkUpdate))
            {
                flags.Set(ChickenCoop.Hatching, true);
                flags.Set(ChickenCoop.Full, coop.Animals.Count >= coop.MaxChickens);
            }
            if (!coop.IsInvoking(coop.CheckEggHatchState))
                coop.InvokeRepeating(coop.CheckEggHatchState, 10f, 10f);
            coop.SendNetworkUpdate();
            Event("birth", $"{label}: hatching an egg ({coop.Animals.Count}/{coop.MaxChickens}, " +
                           $"{coop.ChickenHatchTimeMinutes:0} min)");
        }

        private void TroughStep(UpkeepPass pass)
        {
            foreach (BaseEntity entity in pass.Role(RoleTrough))
                Guard("troughs", pass.Name(entity), () =>
                {
                    string label = pass.Name(entity);
                    var trough = entity as HitchTrough;
                    if (trough == null)
                    {
                        Failure($"type:{label}", $"{label} is not a Hitch & Trough");
                        return;
                    }
                    StockFood(trough.inventory, -1, IsTroughFood, _config.TroughFeedTarget,
                        _config.TroughFeedMinimum, pass, label, false);
                    LiquidContainer water = trough.WaterStorage;
                    if (water == null)
                    {
                        Failure($"water:{label}", $"{label} has no water end");
                        return;
                    }
                    StockWater(water.inventory, 0, _config.TroughWaterTarget, _config.TroughWaterMinimum, pass,
                        $"{label} water end");
                });
        }

        // spoiled.produce is chicken feed (chickenCoopFood) and compost (0.2 fertilizer); livestock skip it.
        private void SpoiledStep(UpkeepPass pass)
        {
            List<ChickenCoop> coops = pass.Role(RoleCoop).OfType<ChickenCoop>().ToList();
            List<Composter> composters = pass.Role(RoleComposter).OfType<Composter>().ToList();
            List<ItemContainer> dungBoxes = pass.Containers(BoxDung);
            var left = new Dictionary<string, int>();
            foreach (KeyValuePair<Item, string> pair in SpoiledItems(pass))
                Guard("spoiled produce", pair.Value, () =>
                {
                    Item item = pair.Key;
                    ItemContainer origin = item.parent;
                    int amount = item.amount;
                    int moved = 0;
                    foreach (ChickenCoop coop in coops)
                    {
                        if (item.amount <= 0 || item.parent != origin)
                            break;
                        int room = _config.CoopFeedTarget - CountFood(coop.inventory, ChickenCoop.FoodSlot, IsCoopFood);
                        moved += MoveSome(item, coop.inventory, room, ChickenCoop.FoodSlot);
                    }
                    foreach (Composter composter in composters)
                    {
                        if (item.amount <= 0 || item.parent != origin)
                            break;
                        int allowed = CompostRoom(composter, item.info, dungBoxes);
                        moved += MoveSome(item, composter.inventory, Math.Min(allowed, item.amount));
                    }
                    if (moved < amount)
                        Add(left, pair.Value, amount - moved);
                });
            if (left.Count > 0)
                Full(RoleComposter, $"no room for {SpoiledProduce} in a coop food slot or a composter, or for its " +
                                    $"fertilizer in {BoxDung}; {left.Values.Sum()} stay in storage (" +
                                    string.Join(", ", left.Select(p => $"{p.Key} x{p.Value}")) + ")");
        }

        // Every spoiled.produce stack in farm storage other than coops, composters, planters, and liquid containers.
        private static List<KeyValuePair<Item, string>> SpoiledItems(UpkeepPass pass)
        {
            var items = new List<KeyValuePair<Item, string>>();
            foreach (KeyValuePair<string, List<BaseEntity>> role in pass.Roles)
            {
                if (role.Key == RoleCoop || role.Key == RoleComposter || role.Key == RolePlanter)
                    continue;
                foreach (BaseEntity entity in role.Value)
                {
                    ItemContainer container = InventoryOf(entity);
                    if (container == null || entity is LiquidContainer)
                        continue;
                    foreach (Item item in container.itemList)
                        if (item.amount > 0 && item.info.shortname == SpoiledProduce)
                            items.Add(new KeyValuePair<Item, string>(item, pass.Name(entity)));
                }
            }
            return items;
        }

        // Moves food in (spoiled produce first for coops, then spoiling produce from the feed box, oldest first,
        // then the rest), and creates the fallback feed only when the stock is still under its minimum.
        private void StockFood(ItemContainer container, int slot, Func<ItemDefinition, bool> accepts, int target,
            int minimum, UpkeepPass pass, string label, bool spoiledFirst)
        {
            var skipped = new HashSet<ItemDefinition>();
            int have = CountFood(container, slot, accepts);
            have += FillFood(container, slot, accepts, target - have, pass, skipped, spoiledFirst);
            if (have >= minimum)
                return;
            ItemDefinition fallback = ItemManager.FindItemDefinition(_config.FallbackFeed);
            if (fallback == null || !accepts(fallback))
            {
                Failure("fallback", $"fallback feed '{_config.FallbackFeed}' is not an item {label} accepts");
                return;
            }
            // A short stack of one food blocks every other kind from the single coop food slot, so it goes back to
            // the feed box and the slot takes the next kind there, or the fallback feed.
            while (slot >= 0 && have < minimum)
            {
                Item current = container.GetSlot(slot);
                if (current == null || current.info == fallback || !skipped.Add(current.info))
                    break;
                int amount = current.amount;
                if (MoveToContainers(current, pass.Containers(BoxFeed)) < amount)
                    break;
                have = FillFood(container, slot, accepts, target, pass, skipped, spoiledFirst);
            }
            if (have >= minimum)
                return;
            int want = target - have;
            int made = GiveNew(new List<ItemContainer> { container }, fallback, want, slot);
            if (made > 0)
                Event("topup", $"{label}: feed {have} below {minimum}, created {fallback.shortname} x{made}");
            if (made < want)
            {
                Item blocking = slot >= 0 ? container.GetSlot(slot) : null;
                Failure($"topup:{label}", $"{label}: room for only {made} of {want} {fallback.shortname}" +
                                          (blocking != null && blocking.info != fallback
                                              ? $"; the slot holds {blocking.info.shortname} x{blocking.amount}"
                                              : string.Empty));
            }
        }

        private static int FillFood(ItemContainer container, int slot, Func<ItemDefinition, bool> accepts, int want,
            UpkeepPass pass, HashSet<ItemDefinition> skipped, bool spoiledFirst)
        {
            if (want <= 0)
                return 0;
            var sources = new List<Item>();
            if (spoiledFirst)
                sources.AddRange(SpoiledItems(pass).Select(p => p.Key).Where(i => !skipped.Contains(i.info)));
            sources.AddRange(pass.Containers(BoxFeed)
                .SelectMany(c => c.itemList)
                .Where(i => i.amount > 0 && i.info.shortname != SpoiledProduce && accepts(i.info) &&
                            !skipped.Contains(i.info))
                .OrderBy(i => Spoils(i.info) ? 0 : 1)
                .ThenBy(i => i.instanceData?.dataFloat ?? float.MaxValue));
            int moved = 0;
            foreach (Item source in sources)
            {
                if (moved >= want)
                    break;
                moved += MoveSome(source, container, want - moved, slot);
            }
            return moved;
        }

        private static int CountFood(ItemContainer container, int slot, Func<ItemDefinition, bool> accepts)
        {
            if (slot >= 0)
            {
                Item item = container.GetSlot(slot);
                return item != null && accepts(item.info) ? item.amount : 0;
            }
            return container.itemList.Where(i => accepts(i.info)).Sum(i => i.amount);
        }

        // Catcher and barrel water fills a stock up to its target. The pump feeds the sprinklers, which go dry while it
        // is empty, so it only lifts a stock that is below its minimum, to that minimum, and keeps PumpReserve ml.
        // Created water stays the last resort.
        private void StockWater(ItemContainer container, int slot, int target, int minimum, UpkeepPass pass,
            string label)
        {
            ItemDefinition water = WaterTypes.WaterItemDef;
            Item current = container.GetSlot(slot);
            if (current != null && current.info != water)
            {
                pass.Destroyed.Add($"{label}: {current.amount} {current.info.shortname}, which blocks fresh water");
                current.RemoveFromContainer();
                current.Remove();
                current = null;
            }
            int have = current?.amount ?? 0;
            List<LiquidContainer> sources = _config.WaterSourceRoles
                .SelectMany(pass.Role)
                .OfType<LiquidContainer>()
                .Distinct()
                .OrderByDescending(FreshWater)
                .ToList();
            have += DrawWater(sources.Where(s => !(s is WaterPump)), container, slot, target - have, 0);
            if (have < minimum)
                have += DrawWater(sources.OfType<WaterPump>(), container, slot, minimum - have, PumpReserve);
            if (have >= minimum)
                return;
            int want = target - have;
            int made = GiveNew(new List<ItemContainer> { container }, water, want, slot);
            if (made > 0)
                Event("topup", $"{label}: water {have} ml below {minimum} ml, created {made} ml");
            if (made < want)
                Failure($"topup:{label}", $"{label}: room for only {made} of {want} ml of water");
        }

        private static int FreshWater(LiquidContainer source)
        {
            Item liquid = source.GetLiquidItem();
            return liquid != null && liquid.info == WaterTypes.WaterItemDef ? liquid.amount : 0;
        }

        // Moves up to want ml of fresh water from the sources in order, leaving each at least keep ml.
        private static int DrawWater(IEnumerable<LiquidContainer> sources, ItemContainer container, int slot, int want,
            int keep)
        {
            int moved = 0;
            foreach (LiquidContainer source in sources)
            {
                if (moved >= want)
                    break;
                Item liquid = source.GetLiquidItem();
                if (liquid == null || liquid.info != WaterTypes.WaterItemDef || liquid.amount <= keep)
                    continue;
                int n = MoveSome(liquid, container, Math.Min(want - moved, liquid.amount - keep), slot);
                moved += n;
                if (n == 0)
                    break;
            }
            return moved;
        }

        #endregion

        #region Items

        private static ItemContainer InventoryOf(BaseEntity entity) => (entity as IItemContainerEntity)?.inventory;

        private static bool Spoils(ItemDefinition def) => def.GetComponent<ItemModFoodSpoiling>() != null;

        private static bool IsCoopFood(ItemDefinition def)
        {
            ItemModConsumable consumable;
            return def.TryGetComponent(out consumable) && consumable.chickenCoopFood;
        }

        // LivestockAnimal.CanEatFrom also wants positive calories, and the trough takes only the Food category.
        private static bool IsTroughFood(ItemDefinition def)
        {
            ItemModConsumable consumable;
            return def.category == ItemCategory.Food && def.TryGetComponent(out consumable) &&
                   consumable.chickenCoopFood && consumable.GetIfType(MetabolismAttribute.Type.Calories) > 0f;
        }

        // Fridge items go to a fridge first, then the role's boxes.
        private List<ItemContainer> Targets(UpkeepPass pass, string shortname, string role)
        {
            var targets = new List<ItemContainer>();
            if (_config.FridgeItems.Contains(shortname))
                targets.AddRange(pass.Containers(RoleFridge));
            targets.AddRange(pass.Containers(role));
            return targets;
        }

        private List<ItemContainer> CorpseTargets(UpkeepPass pass, string shortname) =>
            Targets(pass, shortname, shortname == WoolShortname ? BoxWool : BoxMeat);

        private static int CountItems(List<ItemContainer> containers, string shortname) =>
            containers.SelectMany(c => c.itemList).Where(i => i.info.shortname == shortname).Sum(i => i.amount);

        private static int StackCap(ItemContainer container, ItemDefinition def)
        {
            int cap = def.stackable;
            if (container.maxStackSize > 0)
                cap = container.allowItemsToIncreaseToMaxStackSize
                    ? Math.Max(container.maxStackSize, cap)
                    : Math.Min(container.maxStackSize, cap);
            return Math.Max(cap, 1);
        }

        // Item.MoveToContainer neither respects the item's own stack size in an empty slot nor reports a partial
        // merge, and drops what does not fit, so every move goes through this exact per-slot room. An empty slot
        // also needs a free itemList entry: ItemContainer.Insert refuses a full list, and Item.SetParent then
        // removes the item.
        private static int SlotRoom(ItemContainer container, Item item, int slot)
        {
            Item existing = slot >= 0 && slot < container.capacity ? container.GetSlot(slot) : null;
            if (existing == null)
                return container.itemList.Count >= container.capacity ? 0 : EmptySlotRoom(container, item, slot);
            if (existing == item || !existing.CanStack(item) || !item.CanMoveTo(null, container, slot))
                return 0;
            return Math.Max(0, existing.MaxStackable() - existing.amount);
        }

        private static int EmptySlotRoom(ItemContainer container, Item item, int slot)
        {
            if (slot < 0 || slot >= container.capacity || container.GetSlot(slot) != null ||
                container.SlotTaken(item, slot) || !item.CanMoveTo(null, container, slot))
                return 0;
            return StackCap(container, item.info);
        }

        private static int PickSlot(ItemContainer container, Item item, out int room)
        {
            int empty = -1;
            int emptyRoom = 0;
            for (int i = 0; i < container.capacity; i++)
            {
                int r = SlotRoom(container, item, i);
                if (r <= 0)
                    continue;
                if (container.GetSlot(i) != null)
                {
                    room = r;
                    return i;
                }
                if (empty < 0)
                {
                    empty = i;
                    emptyRoom = r;
                }
            }
            room = emptyRoom;
            return empty;
        }

        // Simulates MoveToContainers for StoreCheck: partial stacks, including stacks the check itself started, then
        // empty slots while the simulated itemList has a free entry.
        private class SimContainer
        {
            private readonly ItemContainer _container;
            private readonly Dictionary<int, long> _added = new Dictionary<int, long>();
            private readonly Dictionary<int, Item> _started = new Dictionary<int, Item>();
            private int _listed;

            public SimContainer(ItemContainer container)
            {
                _container = container;
                _listed = container.itemList.Count;
            }

            public long Take(Item item, long want)
            {
                long taken = 0;
                for (int i = 0; i < _container.capacity && taken < want; i++)
                {
                    long added;
                    _added.TryGetValue(i, out added);
                    Item started;
                    long room;
                    if (_container.GetSlot(i) != null)
                        room = SlotRoom(_container, item, i) - added;
                    else if (_started.TryGetValue(i, out started) && started.CanStack(item))
                        room = StackCap(_container, item.info) - added;
                    else
                        continue;
                    long n = Math.Min(want - taken, Math.Max(0L, room));
                    _added[i] = added + n;
                    taken += n;
                }
                for (int i = 0; i < _container.capacity && taken < want && _listed < _container.capacity; i++)
                {
                    if (_started.ContainsKey(i) || EmptySlotRoom(_container, item, i) <= 0)
                        continue;
                    long n = Math.Min(want - taken, StackCap(_container, item.info));
                    _started[i] = item;
                    _added[i] = n;
                    _listed++;
                    taken += n;
                }
                return taken;
            }
        }

        // Whether the containers take every item, walked in the order and the way the moves will walk them.
        private static bool StoreCheck(List<Item> items, Func<Item, List<ItemContainer>> targetsOf)
        {
            var sims = new Dictionary<ItemContainer, SimContainer>();
            foreach (Item item in items)
            {
                long left = item.amount;
                foreach (ItemContainer container in targetsOf(item))
                {
                    if (left <= 0)
                        break;
                    SimContainer sim;
                    if (!sims.TryGetValue(container, out sim))
                        sims[container] = sim = new SimContainer(container);
                    left -= sim.Take(item, left);
                }
                if (left > 0)
                    return false;
            }
            return true;
        }

        // Stores new items all or none: a failed room check removes every item, changes nothing, and returns null.
        // Otherwise it returns the amounts stored by shortname.
        private Dictionary<string, int> StoreAll(List<Item> items, Func<Item, List<ItemContainer>> targetsOf,
            string what)
        {
            if (StoreCheck(items, targetsOf))
                return StoreMove(items, targetsOf, what);
            foreach (Item item in items)
                item.Remove();
            return null;
        }

        // Moves new items into their targets and returns the amounts stored by shortname; a remainder the moves
        // could not place is destroyed and logged every time.
        private Dictionary<string, int> StoreMove(List<Item> items, Func<Item, List<ItemContainer>> targetsOf,
            string what)
        {
            var stored = new Dictionary<string, int>();
            foreach (Item item in items)
            {
                string shortname = item.info.shortname;
                int amount = item.amount;
                int moved = MoveToContainers(item, targetsOf(item));
                Add(stored, shortname, moved);
                if (moved >= amount)
                    continue;
                if (item.parent == null)
                    item.Remove();
                Failure($"destroyed:{shortname}", $"{amount - moved} {shortname} of {what} found no room after the " +
                                                  "room check passed and were removed", "destroyed", 0f);
            }
            return stored;
        }

        // Moves up to max of item into one slot (slot >= 0) or the best slots, and returns how many moved. The rest
        // stays where it was; a parentless item keeps it for the caller.
        private static int MoveSome(Item item, ItemContainer container, int max, int slot = -1)
        {
            int moved = 0;
            while (item != null && container != null && moved < max && item.amount > 0)
            {
                int room;
                int target;
                if (slot >= 0)
                {
                    target = slot;
                    room = SlotRoom(container, item, slot);
                }
                else
                {
                    target = PickSlot(container, item, out room);
                }
                if (target < 0 || room <= 0)
                    break;
                int n = Math.Min(Math.Min(max - moved, item.amount), room);
                if (n < item.amount)
                {
                    Item part = item.SplitItem(n);
                    if (part == null)
                        break;
                    if (!part.MoveToContainer(container, target))
                    {
                        int left = part.amount;
                        moved += n - left;
                        item.amount += left;
                        item.MarkDirty();
                        part.Remove();
                        break;
                    }
                    moved += n;
                    continue;
                }
                int before = item.amount;
                moved += item.MoveToContainer(container, target) ? before : before - item.amount;
                break;
            }
            return moved;
        }

        private static int MoveToContainers(Item item, List<ItemContainer> containers)
        {
            int total = item.amount;
            int moved = 0;
            ItemContainer origin = item.parent;
            foreach (ItemContainer container in containers)
            {
                if (moved >= total || item.amount <= 0 || item.parent != origin)
                    break;
                moved += MoveSome(item, container, total - moved);
            }
            return moved;
        }

        // Creates amount of def across the containers in order and returns how many were placed.
        private static int GiveNew(List<ItemContainer> containers, ItemDefinition def, int amount, int slot = -1,
            Action<Item> prepare = null)
        {
            int given = 0;
            foreach (ItemContainer container in containers)
            {
                if (given >= amount)
                    break;
                Item item = ItemManager.Create(def, amount - given);
                if (item == null)
                    break;
                prepare?.Invoke(item);
                given += MoveSome(item, container, item.amount, slot);
                if (item.parent == null && item.amount > 0)
                    item.Remove();
            }
            return given;
        }

        // Room for more of def: its partial stacks plus empty slots, the latter capped by free itemList entries.
        private static long RoomFor(ItemContainer container, ItemDefinition def)
        {
            long room = 0;
            int empty = 0;
            for (int i = 0; i < container.capacity; i++)
            {
                Item existing = container.GetSlot(i);
                if (existing == null)
                    empty++;
                else if (existing.info == def && existing.skin == 0UL)
                    room += Math.Max(0, existing.MaxStackable() - existing.amount);
            }
            empty = Math.Min(empty, Math.Max(0, container.capacity - container.itemList.Count));
            return room + (long)empty * StackCap(container, def);
        }

        private void Collect(string shortname, int amount)
        {
            if (amount <= 0)
                return;
            int have;
            _data.Collected.TryGetValue(shortname, out have);
            _data.Collected[shortname] = have + amount;
        }

        private void CollectAll(Dictionary<string, int> stored)
        {
            foreach (KeyValuePair<string, int> pair in stored)
                Collect(pair.Key, pair.Value);
        }

        private static void Add(Dictionary<string, int> totals, string key, int amount)
        {
            int have;
            totals.TryGetValue(key, out have);
            totals[key] = have + amount;
        }

        private static int Amount(Dictionary<string, int> totals, string key)
        {
            int amount;
            return totals.TryGetValue(key, out amount) ? amount : 0;
        }

        private static string Summarize(IEnumerable<Item> items)
        {
            var totals = new Dictionary<string, int>();
            foreach (Item item in items)
                if (item != null && item.amount > 0)
                    Add(totals, item.info.shortname, item.amount);
            return Summarize(totals);
        }

        private static string Summarize(Dictionary<string, int> totals) =>
            totals.Count == 0
                ? "nothing"
                : string.Join(", ", totals.Where(p => p.Value > 0).Select(p => p.Key == "water"
                    ? $"water {p.Value} ml"
                    : $"{p.Key} x{p.Value}"));

        #endregion

        #region Helpers

        // A non-zero config value wins; otherwise the build owner, or before a build the first owner in users.cfg.
        private ulong FarmOwner()
        {
            if (_config.OwnerSteamId != 0)
                return _config.OwnerSteamId;
            if (_data != null && _data.Built && _data.Owner != 0)
                return _data.Owner;
            ServerUsers.User owner = ServerUsers.GetAll(ServerUsers.UserGroup.Owner).FirstOrDefault();
            return owner?.steamid ?? 0;
        }

        private bool OwnerOk(ulong id) => id != 0 && ((_data != null && id == _data.Owner) || id == FarmOwner());

        // Logs and RCON replies name the owner and show only the SteamID's last four digits: "Alice (...1234)".
        private static string OwnerLabel(ulong id)
        {
            if (id == 0)
                return "none";
            string name = ServerUsers.Get(id)?.username;
            if (string.IsNullOrEmpty(name))
                name = (BasePlayer.FindByID(id) ?? BasePlayer.FindSleeping(id))?.displayName;
            string digits = id.ToString(CultureInfo.InvariantCulture);
            string tail = $"(...{digits.Substring(Math.Max(0, digits.Length - 4))})";
            return string.IsNullOrEmpty(name) ? tail : $"{name} {tail}";
        }

        // Case-insensitive, so a layout path in other casing cannot make every spawn look lost to auto-repair.
        private static bool SamePrefab(BaseEntity entity, string prefab) =>
            string.Equals(entity.PrefabName, prefab, StringComparison.OrdinalIgnoreCase);

        private BaseEntity ResolveStructure(StructureRecord rec)
        {
            var entity = FindEntity(rec.NetId) as BaseEntity;
            if (entity == null || entity.IsDestroyed || !SamePrefab(entity, rec.Prefab) || !OwnerOk(entity.OwnerID))
                return null;
            return entity;
        }

        // A farm animal keeps its prefab until it grows into a new entity, and carries the owner's tag or the farm
        // cupboard as its home.
        private LivestockAnimal ResolveAnimal(ulong id, string prefab)
        {
            var animal = FindEntity(id) as LivestockAnimal;
            if (animal == null || animal.IsDestroyed || !SamePrefab(animal, prefab))
                return null;
            if (OwnerOk(animal.OwnerID))
                return animal;
            ulong cupboard = _data.Structures
                .Where(r => r.Role == RoleCupboard && r.NetId != 0 && !r.Missing && _structureIds.Contains(r.NetId))
                .Select(r => r.NetId)
                .FirstOrDefault();
            return cupboard != 0 && animal.HomeTc.uid.Value == cupboard ? animal : null;
        }

        private BuildingPrivlidge FarmCupboard() =>
            _data.Structures.Where(r => r.Role == RoleCupboard && r.NetId != 0 && !r.Missing)
                .Select(r => ResolveStructure(r) as BuildingPrivlidge)
                .FirstOrDefault(c => c != null);

        private List<LivestockAnimal> Herd(UpkeepPass pass, string species) =>
            pass.Animals.Where(a => a != null && !a.IsDestroyed && !a.IsDead() && SpeciesOf(a) == species &&
                                    _animals.ContainsKey(IdOf(a)) && !_pendingCulls.ContainsKey(IdOf(a)))
                .ToList();

        private int CapOf(string species) => species == Cattle ? _config.CattleCap : _config.SheepCap;

        // Herd size against the cap: reseeded juveniles are left out until they grow up.
        private int CapCount(List<LivestockAnimal> herd) => herd.Count(a => !_data.CapExempt.Contains(IdOf(a)));

        private Vector3 FarmCentre() => FarmPoint(0f, 0f);

        private Vector3 FarmPoint(float x, float z)
        {
            Vector3 offset = Quaternion.Euler(0f, _data.Yaw, 0f) * new Vector3(x, 0f, z);
            var point = new Vector3(_data.CentreX + offset.x, 0f, _data.CentreZ + offset.z);
            point.y = TerrainMeta.HeightMap.GetHeight(point);
            return point;
        }

        private void TrackDroppedItems()
        {
            _dropped.Clear();
            foreach (BaseNetworkable networkable in BaseNetworkable.serverEntities)
            {
                var dropped = networkable as DroppedItem;
                if (dropped != null)
                    TrackDropped(dropped);
            }
        }

        private void TrackDropped(DroppedItem dropped)
        {
            Item item = dropped?.item;
            if (item?.info == null || dropped.IsDestroyed || !_config.SweptItems.ContainsKey(item.info.shortname))
                return;
            Vector3 offset = dropped.transform.position - FarmCentre();
            if (new Vector2(offset.x, offset.z).magnitude <= _config.FarmRadius)
                _dropped.Add(dropped);
        }

        // Clears what belongs to one farm record; the server-wide livestock set stays.
        private void ResetState()
        {
            _structureIds.Clear();
            _animals.Clear();
            _dropped.Clear();
            _pendingCulls.Clear();
            _pregnantLastPass.Clear();
            _nextFallbackAttempt.Clear();
            _failureLoggedAt.Clear();
            _slotEmptySince.Clear();
            _dry.Clear();
            _repairs.Clear();
            _strays.Clear();
            _returning.Clear();
            _withinRadius.Clear();
            _needLoggedAt.Clear();
            _growingNames.Clear();
            _penHull = null;
            _relinkWanted = false;
            _recordChecked = false;
        }

        private string NameOf(BaseEntity entity)
        {
            ulong id = IdOf(entity);
            StructureRecord rec = _data?.Structures.FirstOrDefault(r => r.NetId == id);
            return rec != null ? rec.Id : $"{entity?.ShortPrefabName} [{id}]";
        }

        private static bool AnyPlayerNear(Vector3 position, float distance)
        {
            foreach (BasePlayer player in BasePlayer.activePlayerList)
                if (player != null && !player.IsDead() &&
                    (player.transform.position - position).sqrMagnitude <= distance * distance)
                    return true;
            return false;
        }

        private static BaseNetworkable FindEntity(ulong id) =>
            id == 0 ? null : BaseNetworkable.serverEntities.Find(new NetworkableId(id));

        private static ulong IdOf(BaseNetworkable entity) => entity?.net?.ID.Value ?? 0;

        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

        private static string SpeciesOf(LivestockAnimal animal) =>
            animal is Cow ? Cattle : animal is Sheep ? SheepKind : null;

        private static float LifeLeft(LivestockAnimal animal) =>
            animal.ageTimer.IsRunning ? animal.ageTimer.Remaining : float.MaxValue;

        private static string KindOf(LivestockAnimal animal)
        {
            bool adult = animal.IsAdult();
            if (animal is Cow)
                return adult ? (animal.IsMale ? "bull" : "cow") : (animal.IsMale ? "bull calf" : "heifer calf");
            if (animal is Sheep)
                return adult ? (animal.IsMale ? "ram" : "ewe") : (animal.IsMale ? "ram lamb" : "ewe lamb");
            return animal.ShortPrefabName;
        }

        private static string Describe(LivestockAnimal animal) =>
            animal == null ? "unknown animal" : $"{KindOf(animal)} {animal.AnimalName} [{IdOf(animal)}]";

        private static string Duration(float seconds)
        {
            if (seconds >= float.MaxValue / 2f)
                return "unlimited";
            if (seconds >= 3600f)
                return $"{seconds / 3600f:0.0}h";
            if (seconds >= 60f)
                return $"{seconds / 60f:0}m";
            return $"{seconds:0}s";
        }

        private static Vector2[] Grid(float minX, float maxX, float minZ, float maxZ, int countX, int countZ)
        {
            var points = new Vector2[countX * countZ];
            for (int i = 0; i < countX; i++)
                for (int j = 0; j < countZ; j++)
                    points[i * countZ + j] = new Vector2(
                        countX == 1 ? (minX + maxX) / 2f : Mathf.Lerp(minX, maxX, i / (float)(countX - 1)),
                        countZ == 1 ? (minZ + maxZ) / 2f : Mathf.Lerp(minZ, maxZ, j / (float)(countZ - 1)));
            return points;
        }

        // One entity's share of a step: a throw skips that entity only and is logged with its stack trace.
        private void Guard(string step, string what, Action action)
        {
            try
            {
                action();
            }
            catch (Exception e)
            {
                Failure($"error:{step}:{what}", $"{step}: {what} threw {e}");
            }
        }

        private void Disable(string why)
        {
            _disabled = _disabled == null ? why : $"{_disabled}; {why}";
            PrintError($"disabled: {why}");
        }

        private void Event(string kind, string text, bool warning = false)
        {
            if (warning)
                PrintWarning($"{kind}: {text}");
            else
                Puts($"{kind}: {text}");
            Record(kind, text);
        }

        // Repeats of one failure key are logged at most every repeat interval, so a stuck box or missing structure
        // shows up periodically instead of on every pass; an interval of 0 logs every time.
        private void Failure(string key, string text, string kind = "failure", float repeatMinutes = -1f)
        {
            float now = Time.realtimeSinceStartup;
            float interval = (repeatMinutes < 0f ? _config.FailureRepeatMinutes : repeatMinutes) * 60f;
            float last;
            if (interval > 0f && _failureLoggedAt.TryGetValue(key, out last) && now - last < interval)
                return;
            _failureLoggedAt[key] = now;
            PrintWarning($"{kind}: {text}");
            Record(kind, text);
        }

        // A full target container skips the action that needed it; logged at most once per interval per container.
        private void Full(string container, string text) =>
            Failure($"full:{container}", text, "full", _config.FullRepeatMinutes);

        private void Record(string kind, string text)
        {
            if (_data == null)
                return;
            _data.Events.Add(new EventRecord
            {
                Time = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                Kind = kind,
                Text = text,
            });
            int excess = _data.Events.Count(e => e.Kind == kind) - Math.Max(1, _config.EventsPerKind);
            for (int i = 0; excess > 0 && i < _data.Events.Count;)
            {
                if (_data.Events[i].Kind == kind)
                {
                    _data.Events.RemoveAt(i);
                    excess--;
                }
                else
                {
                    i++;
                }
            }
        }

        #endregion

        #region Status

        private static readonly string[] EventKinds =
        {
            "stale", "topup", "cull", "birth", "adopt", "stray", "released", "reseed", "breeding", "death", "needs",
            "destroyed", "full", "placement", "repair", "owner", "failure", "build",
        };

        private static string ReseedLeft(ReseedState reseed)
        {
            long left = reseed == null || reseed.At <= 0
                ? 0
                : reseed.At + (long)(reseed.Wait * 60f) - Epoch.Current;
            return left > 0 ? $", next reseed no sooner than in {Duration(left)}" : string.Empty;
        }

        // Each section is guarded, so a broken part prints an error line and the rest still prints.
        private string Status()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Livestock Farm, identity {ConVar.Server.identity}");
            if (!_ready)
            {
                // Resolving net ids before VerifyRecord would mark a sound record missing.
                sb.Append(NotReady);
                return sb.ToString();
            }
            if (_disabled != null)
                sb.AppendLine($"DISABLED: {_disabled}");
            if (!_data.Built)
            {
                sb.AppendLine($"no farm built; farm.build [x z [yaw]] builds one at ({_config.Layout.Centre.X:0.0}, " +
                              $"{_config.Layout.Centre.Z:0.0}) yaw {_config.Layout.Yaw:0} by default");
                StatusSection(sb, "events", () => AppendEvents(sb, "stale"));
                return sb.ToString().TrimEnd();
            }
            UpkeepPass pass = null;
            StatusSection(sb, "record", () => pass = BeginPass());
            if (pass == null)
                return sb.ToString().TrimEnd();
            StatusSection(sb, "header", () =>
            {
                Vector3 herdPoint = FarmPoint(_config.HerdPoint.X, _config.HerdPoint.Z);
                var foundation = pass.Role(RoleFoundation).FirstOrDefault() as BuildingBlock;
                string authorized = pass.Cupboard != null && pass.Cupboard.IsAuthed(pass.Owner)
                    ? "authorized"
                    : "NOT authorized";
                string cupboard = pass.Cupboard == null ? "missing" : $"{IdOf(pass.Cupboard)} (owner {authorized})";
                string grade = foundation != null ? foundation.grade.ToString() : "missing";
                string owner = _data.Owner == pass.Owner
                    ? OwnerLabel(pass.Owner)
                    : $"{OwnerLabel(pass.Owner)} (config; built for {OwnerLabel(_data.Owner)})";
                sb.AppendLine($"centre ({_data.CentreX:0.0}, {_data.CentreZ:0.0}) yaw {_data.Yaw:0}, owner {owner}, " +
                              $"cupboard {cupboard}, foundation {grade}, " +
                              $"herd point ({herdPoint.x:0.0}, {herdPoint.y:0.0}, {herdPoint.z:0.0}), starter herd " +
                              (_data.HerdSpawned ? "spawned" : "pending") +
                              $", {_pendingCulls.Count} culls awaiting a corpse, world {_data.SaveFile}");
            });
            StatusSection(sb, "herd", () =>
            {
                foreach (string species in AllSpecies)
                {
                    List<LivestockAnimal> herd = Herd(pass, species);
                    float since;
                    _data.SecondsSincePregnancy.TryGetValue(species, out since);
                    ReseedState reseed;
                    _data.Reseed.TryGetValue(species, out reseed);
                    int exempt = herd.Count - CapCount(herd);
                    sb.AppendLine($"{species}: {CapCount(herd)}/{CapOf(species)}" +
                                  (exempt > 0 ? $" (+{exempt} reseeded juvenile outside the cap)" : string.Empty) +
                                  " (adult male " +
                                  $"{herd.Count(a => a.IsAdult() && a.IsMale)}, " +
                                  $"adult female {herd.Count(a => a.IsAdult() && a.IsFemale)}, juvenile male " +
                                  $"{herd.Count(a => a.IsInfant() && a.IsMale)}, juvenile female " +
                                  $"{herd.Count(a => a.IsInfant() && a.IsFemale)}), last new pregnancy " +
                                  $"{Duration(since)} ago, reseed wait {(reseed?.Wait ?? ReseedBaseMinutes):0} min" +
                                  ReseedLeft(reseed));
                    foreach (LivestockAnimal animal in herd.OrderBy(a => a.IsInfant()).ThenBy(a => a.IsFemale))
                        sb.AppendLine("  " + AnimalLine(animal, pass.Owner));
                }

                List<Vector2> pen = PenHull();
                var strays = new List<string>();
                foreach (LivestockAnimal animal in pass.Animals)
                {
                    if (animal == null || animal.IsDestroyed || animal.IsDead() || pen.Count < 3 ||
                        InsidePen(animal.transform.position, pen))
                        continue;
                    Vector2 local = LocalOf(animal.transform.position);
                    float since;
                    string state = animal.IsLeading() || animal.IsMounted
                        ? "on a lead or a vehicle"
                        : _strays.TryGetValue(IdOf(animal), out since)
                            ? $"unled for {Duration(Time.realtimeSinceStartup - since)}, returned at " +
                              Duration(StrayReturnSeconds)
                            : "seen on the next pass";
                    strays.Add($"{Describe(animal)} at local ({local.x:0.0}, {local.y:0.0}), {state}");
                }
                sb.AppendLine("strays outside the fence line: " +
                              (strays.Count == 0 ? "none" : string.Join("; ", strays)));
                if (_data.Released.Count > 0)
                    sb.AppendLine($"released by a player: {_data.Released.Count} animals (net ids " +
                                  $"{string.Join(", ", _data.Released)})");
            });
            StatusSection(sb, "chickens", () =>
            {
                sb.AppendLine("chickens:");
                foreach (BaseEntity entity in pass.Role(RoleCoop))
                {
                    var coop = entity as ChickenCoop;
                    if (coop == null)
                        continue;
                    sb.AppendLine($"  {pass.Name(entity)} [{IdOf(entity)}] {coop.Animals.Count}/{coop.MaxChickens}" +
                                  (coop.HasFlag(ChickenCoop.Hatching) ? " hatching" : string.Empty) +
                                  (coop.IsInSun ? " in sun" : " no sun"));
                    foreach (ChickenCoop.AnimalStatus status in coop.Animals)
                    {
                        FarmableAnimal chicken = status.SpawnedAnimal.Get(true);
                        sb.AppendLine(chicken == null
                            ? $"    egg hatching in {Duration(Mathf.Max(0f, status.TimeUntilHatch))}"
                            : $"    {chicken.AnimalName} [{IdOf(chicken)}] hunger {chicken.AnimalHunger:0} thirst " +
                              $"{chicken.AnimalThirst:0} love {chicken.AnimalLove:0} sun " +
                              $"{chicken.AnimalSunlight:0} hp {chicken.Health():0}/{chicken.MaxHealth():0}");
                    }
                }
            });
            StatusSection(sb, "crops", () =>
            {
                sb.AppendLine("crops:");
                foreach (PlanterBox planter in pass.Role(RolePlanter).OfType<PlanterBox>())
                {
                    GrowableEntity[] plants = PlantsBySlot(planter);
                    DryWatch dry;
                    string drying = _dry.TryGetValue(IdOf(planter), out dry)
                        ? $" (under {_config.DryFraction:P0} for {Duration(Time.realtimeSinceStartup - dry.Since)})"
                        : string.Empty;
                    // A planter hands its fertilizer to unfertilized plants as soon as it arrives.
                    sb.AppendLine($"  {pass.Name(planter)} [{IdOf(planter)}] soil {planter.soilSaturation}/" +
                                  $"{planter.soilSaturationMax} ml{drying}, fertilizer " +
                                  $"{planter.GetFertilizerCount()}, " +
                                  $"fertilized {plants.Count(p => p != null && p.Fertilized)}/" +
                                  $"{plants.Count(p => p != null)}: " +
                                  string.Join(", ", plants.Select(p => p == null
                                      ? "empty"
                                      : $"{p.ShortPrefabName.Replace(".entity", string.Empty)} {p.State} " +
                                        $"q{p.OverallQuality:0.00}")));
                }
                foreach (WaterPump pump in pass.Role(RolePump).OfType<WaterPump>())
                {
                    string source = WaterResource.SV_GetAtPoint(pump.WaterResourceLocation.position)?.shortname ??
                                    "none";
                    string power = pump.IsPowered() ? "powered" : "unpowered";
                    sb.AppendLine($"  {pass.Name(pump)} [{IdOf(pump)}] {power}, {pump.GetLiquidCount()} ml, source " +
                                  source);
                }
            });
            StatusSection(sb, "wiring", () =>
            {
                sb.AppendLine("wiring:");
                foreach (LayoutConnection link in _config.Layout.Connections)
                    sb.AppendLine($"  {link}: {(IsWired(link, pass) ? "connected" : "NOT connected")}");
            });
            StatusSection(sb, "storage", () =>
            {
                sb.AppendLine("storage:");
                foreach (KeyValuePair<string, List<BaseEntity>> role in pass.Roles)
                    foreach (BaseEntity entity in role.Value)
                    {
                        ItemContainer inventory = InventoryOf(entity);
                        if (inventory == null || entity is PlanterBox)
                            continue;
                        string line = $"  {pass.Name(entity)} [{IdOf(entity)}]: {Contents(inventory)}";
                        var trough = entity as HitchTrough;
                        if (trough?.WaterStorage != null)
                            line += $"; water end {Contents(trough.WaterStorage.inventory)}";
                        sb.AppendLine(line);
                    }

                sb.AppendLine("collected: " + (_data.Collected.Count == 0
                    ? "nothing yet"
                    : string.Join(", ", _data.Collected.OrderBy(p => p.Key).Select(p => $"{p.Key} x{p.Value}"))));
            });
            StatusSection(sb, "structures", () =>
            {
                // Entries auto-repair (or farm.repair) handles; never-placed ones show only under "needs farm.repair".
                Dictionary<string, StructureRecord> records = RecordsById();
                var entries = new Dictionary<string, LayoutEntry>();
                foreach (LayoutEntry entry in _config.Layout.Entries)
                    if (entry.Id != null && !entries.ContainsKey(entry.Id))
                        entries[entry.Id] = entry;
                LayoutEntry layoutEntry;
                List<StructureRecord> missing = _data.Structures
                    .Where(r => r.Missing && entries.TryGetValue(r.Id, out layoutEntry) &&
                                !NeverPlaced(layoutEntry, records))
                    .ToList();
                if (missing.Count > 0)
                    sb.AppendLine("missing structures (" + (_config.AutoRepair
                                      ? "auto-repair re-places each on the next pass unless a wait is shown"
                                      : "farm.repair respawns them") + "): " +
                                  string.Join(", ", missing.Select(r => $"{r.Id} ({r.Role}{RepairWait(r.Id)})")));
                List<StructureRecord> orphans = _data.Structures.Where(r => r.Missing && !entries.ContainsKey(r.Id))
                    .ToList();
                if (orphans.Count > 0)
                    sb.AppendLine("missing structures no longer in the layout (they stay gone): " +
                                  string.Join(", ", orphans.Select(r => $"{r.Id} ({r.Role})")));
                // Only entries not standing now: an old-layout fridge on terrain stays put until a rebuild.
                List<string> unplaced = _config.Layout.Entries
                    .Where(e =>
                    {
                        StructureRecord rec;
                        return e.Role != RolePlaceholder && NeverPlaced(e, records) &&
                               (!records.TryGetValue(e.Id, out rec) || rec.NetId == 0 || rec.Missing);
                    })
                    .Select(e =>
                    {
                        StructureRecord rec;
                        string why = !records.TryGetValue(e.Id, out rec)
                            ? "added to the layout after the build"
                            : Unplaced(rec)
                                ? "an old placeholder record"
                                : $"stands on {e.On}, which the farm never placed";
                        return $"{e.Id} ({e.Role}, {why})";
                    })
                    .ToList();
                if (unplaced.Count > 0)
                    sb.AppendLine("needs farm.repair: " + string.Join(", ", unplaced));
                var damaged = new List<string>();
                foreach (KeyValuePair<string, List<BaseEntity>> role in pass.Roles)
                    foreach (BaseCombatEntity entity in role.Value.OfType<BaseCombatEntity>())
                        if (entity.Health() < entity.MaxHealth() - 0.5f)
                            damaged.Add($"{pass.Name(entity)} {entity.Health():0}/{entity.MaxHealth():0}");
                sb.AppendLine("damaged structures: " + (damaged.Count == 0 ? "none" : string.Join(", ", damaged)));
                List<StructureRecord> failed = _data.Structures.Where(r => r.Failure != null).ToList();
                sb.AppendLine(failed.Count == 0 ? "placement failures: none" : "placement failures:");
                foreach (StructureRecord rec in failed)
                    sb.AppendLine($"  {rec.Id} ({rec.Role}, {rec.Prefab}): {rec.Failure}{RepairWait(rec.Id)}");
            });
            StatusSection(sb, "events", () =>
            {
                foreach (string kind in EventKinds)
                    AppendEvents(sb, kind);
            });
            return sb.ToString().TrimEnd();
        }

        private void StatusSection(StringBuilder sb, string name, Action write)
        {
            try
            {
                write();
            }
            catch (Exception e)
            {
                sb.AppendLine($"[{name} section failed: {e.GetType().Name}: {e.Message}]");
                PrintError($"farm.status {name} section threw {e}");
            }
        }

        private void AppendEvents(StringBuilder sb, string kind)
        {
            List<EventRecord> events = _data.Events.Where(e => e.Kind == kind).ToList();
            if (events.Count == 0)
                return;
            sb.AppendLine($"recent {kind}:");
            foreach (EventRecord e in events)
                sb.AppendLine($"  {e.Time} {e.Text}");
        }

        private static string AnimalLine(LivestockAnimal animal, ulong owner)
        {
            var line = new StringBuilder(Describe(animal));
            line.Append(animal.IsAdult()
                ? $" life left {Duration(LifeLeft(animal))}"
                : $" grows up in {Duration(LifeLeft(animal))}");
            line.Append($" trust {animal.StoredSecondsFor(owner):0}" +
                        $" food {animal.GetNeed(LivestockAnimal.Need.Fullness).NeedValue:0.00}" +
                        $" water {animal.GetNeed(LivestockAnimal.Need.Hydration).NeedValue:0.00}" +
                        $" room {animal.GetNeed(LivestockAnimal.Need.PersonalSpace).NeedValue:0.00}" +
                        $" condition {animal.Condition:0.00}");
            if (animal.IsPregnant())
                line.Append(animal.IsCarrying
                    ? $" pregnant, due in {Duration(animal.pregnancyTimer.Remaining)}"
                    : " calving");
            if (animal.OnBreedingCooldown)
                line.Append($" breeding cooldown {Duration(animal.breedCooldown.Remaining)}");
            if (animal.BreedingPaused)
                line.Append(" breeding paused");
            return line.ToString();
        }

        private static string Contents(ItemContainer inventory)
        {
            string text = Summarize(inventory.itemList);
            return text == "nothing" ? "empty" : text;
        }

        #endregion
    }
}
