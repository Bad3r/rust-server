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
        private const string RoleBlock = "block";
        private const string RoleDoor = "door";
        private const string RoleInsert = "insert";
        private const string RoleDecor = "decor";
        private const string RoleSign = "sign";
        private const string RoleBed = "bed";
        private const string RolePlanterStatic = "planter_static";
        private const string RoleWindmill = "windmill";
        private const string RoleBattery = "battery";
        private const string RoleSolar = "solar";
        private const string BoxMilk = "box_milk";
        private const string BoxWool = "box_wool";
        private const string BoxEggs = "box_eggs";
        private const string BoxDung = "box_dung";
        private const string BoxMeat = "box_meat";
        private const string BoxFeed = "box_feed";
        private const string BoxClones = "box_clones";
        // The roles of the original layout (94 entries, no buildings); IsGuarded leaves them as open to players as
        // they always were.
        private static readonly HashSet<string> V1Roles = new HashSet<string>
        {
            RoleFence, RoleGate, RoleFoundation, RolePlatform, RoleCupboard, RoleTrough, RoleCoop, RoleComposter,
            RolePlanter, RolePump, RoleFridge, RolePlaceholder, RoleBattery, RoleSolar, "sprinkler", "catcher_small",
            BoxMilk, BoxWool, BoxEggs, BoxDung, BoxMeat, BoxFeed, BoxClones,
        };

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
        // Rail ends this close belong to one fence line (a gate's ends sit 0.05 m from its neighbours').
        private const float RailJoin = 0.3f;
        // farm.build and farm.repair take over a matching entity this close to an entry's spot.
        private const float AdoptDistance = 0.5f;
        private const float AdoptHeight = 2f;
        // Colliders reaching into this sphere around a spot are checked for adoption; pivots must still be close.
        private const float AdoptSearchRadius = 3f;
        // Blocks and inserts link only within 0.02 m of their sockets, and stacked half walls stand 1.5 m apart.
        private const float AdoptBlockDistance = 0.1f;
        // A block rests on lower-layer blocks of its building within this distance across: a roof 1.5 m from its
        // wall, a floor from its walls, foundation steps 4.5 m from their foundation's centre.
        private const float SupportRadius = 4.6f;
        // Triangle foundations and floors: apex this far from the base edge's midpoint (the pivot).
        private const float TriangleDepth = 2.598f;
        // An entry "On" a block may stand this far below the block's top (pivot plane) and OnMaxRise above it.
        private const float OnTolerance = 0.05f;
        private const float OnMaxRise = 2f;
        // Window inserts (shutters, bars, glass) sit this far up their window wall.
        private const float WindowInsertHeight = 1f;
        // A sign's image is checked this often; FileStorage reads a row it does not cache from SQLite.
        private const float SignCheckSeconds = 300f;
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
        // A breeding hold lasts this many upkeep intervals, so one skipped pass cannot open a gap.
        private const float HoldPasses = 3f;
        // Heads over a species' cap at which BreedingHold stops new pregnancies; the personal-space budget allows it.
        private const int BirthOvershoot = 2;
        // A stray unled for this long inside the farm radius goes back to the herd point.
        private const float StrayReturnSeconds = 300f;
        private const float NeedLogSeconds = 1800f;
        // A structure standing farther than this from its recorded position means the record outran the world.
        private const float RecordDrift = 1f;

        // Local x/z points of each prefab's GroundWatch area checks, sampled when a role is in "Ground check roles".
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

        // Veto results boxed once: OnNpcTarget runs for every predator sensing every farm animal.
        private static readonly object True = true;
        private static readonly object False = false;

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
        private bool _reattachQueued;
        // Net ids of old parts farm.upgrade lifts, moves, or retires while it places; a new foundation may cover them.
        private readonly HashSet<ulong> _retiring = new HashSet<ulong>();
        // Each building's base height while PlanUpgrade runs, worked out once (a plan changes nothing it rests on);
        // null otherwise, so placement, which changes what stands, works it out on every call.
        private Dictionary<string, BaseHeight> _planBases;
        // Species over their cap whose cull is due but cannot run, and why; their mating is held (BreedingHold).
        private readonly Dictionary<string, string> _cullBlocked = new Dictionary<string, string>();
        // Species over their cap only by their juveniles, logged once per wait.
        private readonly HashSet<string> _cullWaiting = new HashSet<string>();
        // Why each species' mating is held, as last logged.
        private readonly Dictionary<string, string> _breedHeld = new Dictionary<string, string>();
        // When each farm sign's image and lock were last checked, by net id.
        private readonly Dictionary<ulong, float> _signCheckedAt = new Dictionary<ulong, float>();
        // Blocks ApplyGrade gave the plain grade since the last LogPlainSkins, by the skin their prefab lacks.
        private readonly Dictionary<string, List<string>> _plainSkins = new Dictionary<string, List<string>>();

        #region Configuration

        private class Configuration
        {
            // Zero resolves to the first owner in cfg/users.cfg; the config is tracked in git, so it holds no SteamID.
            [JsonProperty("Owner SteamID (0 = first owner in cfg/users.cfg)")]
            public ulong OwnerSteamId = 0;

            [JsonProperty("Upkeep interval (seconds)")]
            public float UpkeepSeconds = 30f;

            [JsonProperty("Automatically milk cows")]
            public bool AutoMilk = true;

            [JsonProperty("Automatically shear sheep")]
            public bool AutoShear = true;

            [JsonProperty("Automatically cull surplus livestock")]
            public bool AutoCull = true;

            [JsonProperty("Player-managed planter layout IDs", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<string> ManualPlanterIds = new List<string>();

            [JsonProperty("Assisted farming absence fallback after minutes (0 = disabled)")]
            public float AbsenceMinutes = 60f;

            [JsonProperty("Schedule farm lighting at dusk and dawn")]
            public bool ScheduleLighting = true;

            [JsonProperty("Farm lights sunrise hour (0-24)")]
            public float LightSunrise = 7.5f;

            [JsonProperty("Farm lights sunset hour (0-24)")]
            public float LightSunset = 18.5f;

            [JsonProperty("Farm lighting switch layout ID")]
            public string LightSwitchId = "barn-light-switch";

            [JsonProperty("Scheduled decorative light layout IDs", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<string> ScheduledLightIds = new List<string>
            {
                "lantern-01-timed", "lantern-02-timed", "lantern-03-timed", "lantern-04-timed",
                "lantern-05-timed", "lantern-06-timed", "lantern-07-timed", "lantern-08-timed",
                "house-lantern-1-timed", "house-lantern-2-timed",
            };

            [JsonProperty("Farm radius around the layout centre for sweeping and relinking (m)")]
            public float FarmRadius = 75f;

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
            public int CattleCap = 9;

            [JsonProperty("Sheep cap (adults and juveniles)")]
            public int SheepCap = 9;

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

            // Defaults for layout entries of role block or with a Building; 10232 is the Frontier wood skin.
            [JsonProperty("Building grade")]
            public string BuildingGrade = "wood";

            [JsonProperty("Building skin")]
            public ulong BuildingSkin = 10232;

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

        // The "Layout" value: the farm centre and yaw, the entries, and the IO connections.
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

            // Id of the foundation or floor the entry stands on, upright at its top, placed after it.
            [JsonProperty("On", NullValueHandling = NullValueHandling.Ignore)]
            public string On;

            // The fields below are optional; absent, an entry behaves as in a layout without buildings, and they stay
            // out of the saved config.
            [JsonProperty("Building", NullValueHandling = NullValueHandling.Ignore)]
            public string Building = null;

            // Above the building's base height T with Building (even with On), above On's top, else above the terrain.
            [JsonProperty("Y", DefaultValueHandling = DefaultValueHandling.Ignore)]
            public float Y = 0f;

            [JsonProperty("Parent", NullValueHandling = NullValueHandling.Ignore)]
            public string Parent = null;

            [JsonProperty("Mount", DefaultValueHandling = DefaultValueHandling.Ignore)]
            public bool Mount = false;

            [JsonProperty("Pitch", DefaultValueHandling = DefaultValueHandling.Ignore)]
            public float Pitch = 0f;

            [JsonProperty("Roll", DefaultValueHandling = DefaultValueHandling.Ignore)]
            public float Roll = 0f;

            [JsonProperty("Grade", NullValueHandling = NullValueHandling.Ignore)]
            public string Grade = null;

            [JsonProperty("Skin", NullValueHandling = NullValueHandling.Ignore)]
            public ulong? Skin = null;

            [JsonProperty("Layer", NullValueHandling = NullValueHandling.Ignore)]
            public int? Layer = null;

            [JsonProperty("Image", NullValueHandling = NullValueHandling.Ignore)]
            public string Image = null;

            // Id of an old record this entry takes over at farm.upgrade: the same prefab moves there with what it
            // holds; another prefab gets its items.
            [JsonProperty("Replaces", NullValueHandling = NullValueHandling.Ignore)]
            public string Replaces = null;
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

            [JsonProperty("Route", NullValueHandling = NullValueHandling.Ignore,
                ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<WirePoint> Route;

            public override string ToString() => $"{From} out {FromSlot} to {To} in {ToSlot} ({Kind})";
        }

        private class WirePoint
        {
            [JsonProperty("Anchor", NullValueHandling = NullValueHandling.Ignore)]
            public string Anchor;

            public float X;
            public float Y;
            public float Z;
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
            ["planter-03"] = new List<string>
            {
                "clone.corn", "clone.pumpkin", "clone.corn", "clone.pumpkin", "clone.corn", "clone.pumpkin",
                "clone.corn", "clone.pumpkin", "clone.corn",
            },
            ["planter-04"] = Enumerable.Repeat("clone.wheat", 9).ToList(),
            ["planter-05"] = Enumerable.Repeat("clone.corn", 9).ToList(),
            ["planter-06"] = Enumerable.Repeat("clone.pumpkin", 9).ToList(),
            ["planter-07"] = Enumerable.Repeat("clone.wheat", 9).ToList(),
            ["planter-08"] = new List<string>
            {
                "clone.corn", "clone.pumpkin", "clone.corn", "clone.pumpkin", "clone.corn", "clone.pumpkin",
                "clone.corn", "clone.pumpkin", "clone.corn",
            },
        };

        // research/farm/v2/design/config-v2.json as of 2026-10-10 23:25: 384 entries (buildings barn, deck, house,
        // shelter, tower) and 29 connections. research/farm/v2/plugin/tools/make_defaults.py writes this method,
        // DefaultSlotPlan, and the setting defaults from that file; edit the file and rerun it.
        private static FarmLayout DefaultLayout()
        {
            const string wallHalf = "assets/prefabs/building core/wall.half/wall.half.prefab";
            const string roof = "assets/prefabs/building core/roof/roof.prefab";
            const string wall = "assets/prefabs/building core/wall/wall.prefab";
            const string wallDoorway = "assets/prefabs/building core/wall.doorway/wall.doorway.prefab";
            const string floorFrame = "assets/prefabs/building core/floor.frame/floor.frame.prefab";
            const string wallFrame = "assets/prefabs/building core/wall.frame/wall.frame.prefab";
            const string floor = "assets/prefabs/building core/floor/floor.prefab";
            const string doorHingedWood = "assets/prefabs/building/door.hinged/door.hinged.wood.prefab";
            const string floorLadderHatch = "assets/prefabs/building/floor.ladder.hatch/floor.ladder.hatch.prefab";
            const string waterCatcherLarge = "assets/prefabs/deployable/water catcher/water_catcher_large.prefab";
            const string fluidsplitter = "assets/prefabs/deployable/playerioents/fluidsplitter/fluidsplitter.prefab";
            const string torchholderOnStatic = "assets/bundled/prefabs/static/torchholder_on.static.prefab";
            const string wallWindow = "assets/prefabs/building core/wall.window/wall.window.prefab";
            const string doorDoubleHingedWood =
                "assets/prefabs/building/door.double.hinged/door.double.hinged.wood.prefab";
            const string wallWindowBarsWood = "assets/prefabs/building/wall.window.bars/wall.window.bars.wood.prefab";
            const string signHugeWood = "assets/prefabs/deployable/signs/sign.huge.wood.prefab";
            const string signSmallWood = "assets/prefabs/deployable/signs/sign.small.wood.prefab";
            const string signMediumWood = "assets/prefabs/deployable/signs/sign.medium.wood.prefab";
            const string componentBoxFoodLarge =
                "assets/prefabs/deployable/large wood storage/skins/component_storage_boxes_dlc/box_food/" +
                "component.box.food.large.prefab";
            const string storageBarrelC = "assets/prefabs/misc/decor_dlc/storagebarrel/storage_barrel_c.prefab";
            const string storageBarrelB = "assets/prefabs/misc/decor_dlc/storagebarrel/storage_barrel_b.prefab";
            const string largeRechargableBatteryDeployed =
                "assets/prefabs/deployable/playerioents/batteries/large/large.rechargable.battery.deployed.prefab";
            const string electricalCombinerDeployed =
                "assets/prefabs/deployable/playerioents/gates/combiner/electrical.combiner.deployed.prefab";
            const string splitter = "assets/prefabs/deployable/playerioents/splitter/splitter.prefab";
            const string ceilinglightDeployed = "assets/prefabs/deployable/ceiling light/ceilinglight.deployed.prefab";
            const string wallWindowGlassReinforced =
                "assets/prefabs/building/wall.window.reinforcedglass/wall.window.glass.reinforced.prefab";
            const string shutterWoodA = "assets/prefabs/building/wall.window.shutter/shutter.wood.a.prefab";
            const string bedDeployed = "assets/prefabs/deployable/bed/bed_deployed.prefab";
            const string rugDeployed = "assets/prefabs/deployable/rug/rug.deployed.prefab";
            const string candlesetSmallOnStatic = "assets/bundled/prefabs/static/candleset_small_on.static.prefab";
            const string fireplaceOnStatic = "assets/bundled/prefabs/static/fireplace_on.static.prefab";
            const string rugBearDeployed = "assets/prefabs/deployable/rug/rug.bear.deployed.prefab";
            const string rockingchairDeployed =
                "assets/prefabs/misc/decor_dlc/rockingchair/rockingchair.deployed.prefab";
            const string tableDeployed = "assets/prefabs/deployable/table/table.deployed.prefab";
            const string chairBStatic = "assets/bundled/prefabs/static/chair_b.static.prefab";
            const string cookingworkbenchDeployed =
                "assets/prefabs/deployable/cookingworkbench/cookingworkbench.deployed.prefab";
            const string candlesetLargeOnStatic = "assets/bundled/prefabs/static/candleset_large_on.static.prefab";
            const string woodenBenchAMountable = "assets/content/props/wooden_bench/wooden_bench_a_mountable.prefab";
            const string lanternDeployed = "assets/prefabs/deployable/lantern/lantern.deployed.prefab";
            const string scarecrowDeployed = "assets/prefabs/misc/halloween/scarecrow/scarecrow.deployed.prefab";
            const string electricalHeater =
                "assets/prefabs/deployable/playerioents/electricheater/electrical.heater.prefab";
            const string beehiveDeployed = "assets/prefabs/deployable/beehive/beehive.deployed.prefab";
            const string electricWindmillSmall = "assets/prefabs/deployable/windmill/electric.windmill.small.prefab";
            const string signPostTownRoof = "assets/prefabs/deployable/signs/sign.post.town.roof.prefab";
            const string mailboxDeployed = "assets/prefabs/deployable/mailbox/mailbox.deployed.prefab";
            const string railroadplanterRespawningStatic =
                "assets/prefabs/misc/decor_dlc/rail road planter/railroadplanter.respawning.static.prefab";
            const string minecartPlanterRespawningStatic =
                "assets/prefabs/misc/decor_dlc/minecart planter/minecart.planter.respawning.static.prefab";
            const string waterwellstatic = "assets/prefabs/deployable/water well/waterwellstatic.prefab";
            const string woodPile = "assets/bundled/prefabs/autospawn/resource/wood_log_pile/wood-pile.prefab";
            var layout = new FarmLayout { Centre = new XZ { X = -1542.9f, Z = 1227.3f }, Yaw = 0.0f };
            List<LayoutEntry> e = layout.Entries;
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
            e.Add(Entry("foundation-01", "foundation", FoundationPrefab, 0.0f, 0.0f, 0.0f, building: "shelter",
                layer: 0));
            e.Add(Entry("shelter-half-x0z0-S-s0", "block", wallHalf, 0.0f, -1.5f, 90.0f, building: "shelter",
                layer: 2));
            e.Add(Entry("shelter-half-x0z0-W-s0", "block", wallHalf, -1.5f, 0.0f, 180.0f, building: "shelter",
                layer: 2));
            e.Add(Entry("shelter-half-x0z0-N-s0", "block", wallHalf, 0.0f, 1.5f, 270.0f, building: "shelter",
                layer: 2));
            e.Add(Entry("shelter-roof-x0z0-h1", "block", roof, 0.0f, 0.0f, 270.0f, y: 1.5f, building: "shelter",
                layer: 5));
            e.Add(Entry("shelter-gable-x0z0-S-h1", "block", wall, 0.0f, -1.5f, 90.0f, y: 1.5f, building: "shelter",
                layer: 6));
            e.Add(Entry("shelter-gable-x0z0-N-h1", "block", wall, 0.0f, 1.5f, 270.0f, y: 1.5f, building: "shelter",
                layer: 6));
            e.Add(Entry("cupboard-01", "cupboard", CupboardPrefab, 0.0f, 0.0f, 0.0f, on: "foundation-01"));
            e.Add(Entry("trough-01", "trough", TroughPrefab, -13.05f, 0.0f, 0.0f));
            e.Add(Entry("trough-02", "trough", TroughPrefab, 13.05f, 0.0f, 0.0f));
            e.Add(Entry("catcher-01", "catcher_small", SmallCatcherPrefab, -15.7f, 1.3f, 0.0f));
            e.Add(Entry("catcher-02", "catcher_small", SmallCatcherPrefab, 10.65f, 0.0f, 0.0f));
            e.Add(Entry("fence-64", "fence", FencePrefab, 26.1f, -21.75f, 180.0f, layer: 2));
            e.Add(Entry("fence-65", "fence", FencePrefab, 26.1f, -24.65f, 180.0f, layer: 2));
            e.Add(Entry("fence-66", "fence", FencePrefab, 26.1f, -27.55f, 180.0f, layer: 2));
            e.Add(Entry("fence-67", "fence", FencePrefab, 26.1f, -30.45f, 180.0f, layer: 2));
            e.Add(Entry("fence-68", "fence", FencePrefab, 24.65f, -31.9f, 90.0f, layer: 2));
            e.Add(Entry("fence-69", "fence", FencePrefab, 21.75f, -31.9f, 90.0f, layer: 2));
            e.Add(Entry("fence-70", "fence", FencePrefab, 18.85f, -31.9f, 90.0f, layer: 2));
            e.Add(Entry("fence-71", "fence", FencePrefab, 15.95f, -31.9f, 90.0f, layer: 2));
            e.Add(Entry("fence-72", "fence", FencePrefab, 13.05f, -31.9f, 90.0f, layer: 2));
            e.Add(Entry("fence-73", "fence", FencePrefab, 10.15f, -31.9f, 90.0f, layer: 2));
            e.Add(Entry("fence-74", "fence", FencePrefab, 7.25f, -31.9f, 90.0f, layer: 2));
            e.Add(Entry("fence-75", "fence", FencePrefab, 4.35f, -31.9f, 90.0f, layer: 2));
            e.Add(Entry("fence-76", "fence", FencePrefab, 1.45f, -31.9f, 90.0f, layer: 2));
            e.Add(Entry("fence-77", "fence", FencePrefab, -1.45f, -31.9f, 90.0f, layer: 2));
            e.Add(Entry("fence-78", "fence", FencePrefab, -4.35f, -31.9f, 90.0f, layer: 2));
            e.Add(Entry("fence-79", "fence", FencePrefab, -7.25f, -31.9f, 90.0f, layer: 2));
            e.Add(Entry("fence-80", "fence", FencePrefab, -10.15f, -31.9f, 90.0f, layer: 2));
            e.Add(Entry("fence-81", "fence", FencePrefab, -13.05f, -31.9f, 90.0f, layer: 2));
            e.Add(Entry("fence-82", "fence", FencePrefab, -15.95f, -31.9f, 90.0f, layer: 2));
            e.Add(Entry("fence-83", "fence", FencePrefab, -18.85f, -31.9f, 90.0f, layer: 2));
            e.Add(Entry("fence-84", "fence", FencePrefab, -21.75f, -31.9f, 90.0f, layer: 2));
            e.Add(Entry("fence-85", "fence", FencePrefab, -24.65f, -31.9f, 90.0f, layer: 2));
            e.Add(Entry("fence-86", "fence", FencePrefab, -26.1f, -30.45f, 180.0f, layer: 2));
            e.Add(Entry("fence-87", "fence", FencePrefab, -26.1f, -27.55f, 180.0f, layer: 2));
            e.Add(Entry("fence-88", "fence", FencePrefab, -26.1f, -24.65f, 180.0f, layer: 2));
            e.Add(Entry("fence-89", "fence", FencePrefab, -26.1f, -21.75f, 180.0f, layer: 2));
            e.Add(Entry("trough-03", "trough", TroughPrefab, -13.05f, -26.0f, 0.0f, layer: 10));
            e.Add(Entry("trough-04", "trough", TroughPrefab, 15.75f, -26.0f, 0.0f, layer: 10));
            e.Add(Entry("catcher-03", "catcher_small", SmallCatcherPrefab, -15.7f, -24.7f, 0.0f, layer: 10));
            e.Add(Entry("catcher-04", "catcher_small", SmallCatcherPrefab, 13.35f, -26.0f, 0.0f, layer: 10));
            e.Add(Entry("tower-fnd-x0z0", "block", FoundationPrefab, 0.0f, 24.25f, 0.0f, building: "tower", layer: 0));
            e.Add(Entry("tower-wall-x0z0-S-s0", "block", wall, 0.0f, 22.75f, 90.0f, building: "tower", layer: 2));
            e.Add(Entry("tower-wall-x0z0-N-s0", "block", wall, 0.0f, 25.75f, 270.0f, building: "tower", layer: 2));
            e.Add(Entry("tower-wall-x0z0-E-s0", "block", wall, 1.5f, 24.25f, 0.0f, building: "tower", layer: 2));
            e.Add(Entry("tower-doorway-x0z0-W-s0", "block", wallDoorway, -1.5f, 24.25f, 180.0f, building: "tower",
                layer: 2));
            e.Add(Entry("tower-fframe-x0z0-s1", "block", floorFrame, 0.0f, 24.25f, 0.0f, y: 3.0f, building: "tower",
                layer: 3));
            e.Add(Entry("tower-frame-x0z0-S-s1", "block", wallFrame, 0.0f, 22.75f, 90.0f, y: 3.0f, building: "tower",
                layer: 4));
            e.Add(Entry("tower-frame-x0z0-W-s1", "block", wallFrame, -1.5f, 24.25f, 180.0f, y: 3.0f, building: "tower",
                layer: 4));
            e.Add(Entry("tower-frame-x0z0-N-s1", "block", wallFrame, 0.0f, 25.75f, 270.0f, y: 3.0f, building: "tower",
                layer: 4));
            e.Add(Entry("tower-frame-x0z0-E-s1", "block", wallFrame, 1.5f, 24.25f, 0.0f, y: 3.0f, building: "tower",
                layer: 4));
            e.Add(Entry("tower-floor-x0z0-s2", "block", floor, 0.0f, 24.25f, 0.0f, y: 6.0f, building: "tower",
                layer: 5));
            e.Add(Entry("tower-door-x0z0-W-s0", "door", doorHingedWood, -1.5f, 24.25f, 0.0f, building: "tower",
                parent: "tower-doorway-x0z0-W-s0", layer: 8));
            e.Add(Entry("tower-hatch-x0z0-s1", "door", floorLadderHatch, 0.0f, 24.25f, 0.0f, y: 3.0f, building: "tower",
                parent: "tower-fframe-x0z0-s1", layer: 8));
            e.Add(Entry("catcher-05", "catcher_large", waterCatcherLarge, 0.0f, 24.25f, 0.0f, on: "tower-floor-x0z0-s2",
                y: 6.1f, building: "tower", layer: 9));
            e.Add(Entry("tower-splitter", "decor", fluidsplitter, 0.0f, 25.65f, 180.0f, y: 2.0f, building: "tower",
                parent: "tower-wall-x0z0-N-s0", layer: 9));
            e.Add(Entry("tower-torch", "decor", torchholderOnStatic, -1.6f, 25.2f, 270.0f, y: 1.9f, building: "tower",
                parent: "tower-doorway-x0z0-W-s0", layer: 9));
            e.Add(Entry("barn-fnd-x0z0", "block", FoundationPrefab, 31.0f, 3.9f, 0.0f, building: "barn", layer: 0));
            e.Add(Entry("barn-fnd-x0z1", "block", FoundationPrefab, 31.0f, 6.9f, 0.0f, building: "barn", layer: 0));
            e.Add(Entry("barn-fnd-x0z2", "block", FoundationPrefab, 31.0f, 9.9f, 0.0f, building: "barn", layer: 0));
            e.Add(Entry("barn-fnd-x0z3", "block", FoundationPrefab, 31.0f, 12.9f, 0.0f, building: "barn", layer: 0));
            e.Add(Entry("barn-fnd-x1z0", "block", FoundationPrefab, 34.0f, 3.9f, 0.0f, building: "barn", layer: 0));
            e.Add(Entry("barn-fnd-x1z1", "block", FoundationPrefab, 34.0f, 6.9f, 0.0f, building: "barn", layer: 0));
            e.Add(Entry("barn-fnd-x1z2", "block", FoundationPrefab, 34.0f, 9.9f, 0.0f, building: "barn", layer: 0));
            e.Add(Entry("barn-fnd-x1z3", "block", FoundationPrefab, 34.0f, 12.9f, 0.0f, building: "barn", layer: 0));
            e.Add(Entry("barn-fnd-x2z0", "block", FoundationPrefab, 37.0f, 3.9f, 0.0f, building: "barn", layer: 0));
            e.Add(Entry("barn-fnd-x2z1", "block", FoundationPrefab, 37.0f, 6.9f, 0.0f, building: "barn", layer: 0));
            e.Add(Entry("barn-fnd-x2z2", "block", FoundationPrefab, 37.0f, 9.9f, 0.0f, building: "barn", layer: 0));
            e.Add(Entry("barn-fnd-x2z3", "block", FoundationPrefab, 37.0f, 12.9f, 0.0f, building: "barn", layer: 0));
            e.Add(Entry("barn-fnd-x3z0", "block", FoundationPrefab, 40.0f, 3.9f, 0.0f, building: "barn", layer: 0));
            e.Add(Entry("barn-fnd-x3z1", "block", FoundationPrefab, 40.0f, 6.9f, 0.0f, building: "barn", layer: 0));
            e.Add(Entry("barn-fnd-x3z2", "block", FoundationPrefab, 40.0f, 9.9f, 0.0f, building: "barn", layer: 0));
            e.Add(Entry("barn-fnd-x3z3", "block", FoundationPrefab, 40.0f, 12.9f, 0.0f, building: "barn", layer: 0));
            e.Add(Entry("barn-fnd-x4z0", "block", FoundationPrefab, 43.0f, 3.9f, 0.0f, building: "barn", layer: 0));
            e.Add(Entry("barn-fnd-x4z1", "block", FoundationPrefab, 43.0f, 6.9f, 0.0f, building: "barn", layer: 0));
            e.Add(Entry("barn-fnd-x4z2", "block", FoundationPrefab, 43.0f, 9.9f, 0.0f, building: "barn", layer: 0));
            e.Add(Entry("barn-fnd-x4z3", "block", FoundationPrefab, 43.0f, 12.9f, 0.0f, building: "barn", layer: 0));
            e.Add(Entry("barn-wall-x0z0-W-s0", "block", wall, 29.5f, 3.9f, 180.0f, building: "barn", layer: 2));
            e.Add(Entry("barn-wall-x4z0-E-s0", "block", wall, 44.5f, 3.9f, 0.0f, building: "barn", layer: 2));
            e.Add(Entry("barn-frame-x0z1-W-s0", "block", wallFrame, 29.5f, 6.9f, 180.0f, building: "barn", layer: 2));
            e.Add(Entry("barn-frame-x4z1-E-s0", "block", wallFrame, 44.5f, 6.9f, 0.0f, building: "barn", layer: 2));
            e.Add(Entry("barn-frame-x0z2-W-s0", "block", wallFrame, 29.5f, 9.9f, 180.0f, building: "barn", layer: 2));
            e.Add(Entry("barn-frame-x4z2-E-s0", "block", wallFrame, 44.5f, 9.9f, 0.0f, building: "barn", layer: 2));
            e.Add(Entry("barn-wall-x0z3-W-s0", "block", wall, 29.5f, 12.9f, 180.0f, building: "barn", layer: 2));
            e.Add(Entry("barn-wall-x4z3-E-s0", "block", wall, 44.5f, 12.9f, 0.0f, building: "barn", layer: 2));
            e.Add(Entry("barn-wall-x0z0-S-s0", "block", wall, 31.0f, 2.4f, 90.0f, building: "barn", layer: 2));
            e.Add(Entry("barn-wall-x0z3-N-s0", "block", wall, 31.0f, 14.4f, 270.0f, building: "barn", layer: 2));
            e.Add(Entry("barn-window-x1z0-S-s0", "block", wallWindow, 34.0f, 2.4f, 90.0f, building: "barn", layer: 2));
            e.Add(Entry("barn-window-x1z3-N-s0", "block", wallWindow, 34.0f, 14.4f, 270.0f, building: "barn",
                layer: 2));
            e.Add(Entry("barn-wall-x2z0-S-s0", "block", wall, 37.0f, 2.4f, 90.0f, building: "barn", layer: 2));
            e.Add(Entry("barn-wall-x2z3-N-s0", "block", wall, 37.0f, 14.4f, 270.0f, building: "barn", layer: 2));
            e.Add(Entry("barn-window-x3z0-S-s0", "block", wallWindow, 40.0f, 2.4f, 90.0f, building: "barn", layer: 2));
            e.Add(Entry("barn-window-x3z3-N-s0", "block", wallWindow, 40.0f, 14.4f, 270.0f, building: "barn",
                layer: 2));
            e.Add(Entry("barn-wall-x4z0-S-s0", "block", wall, 43.0f, 2.4f, 90.0f, building: "barn", layer: 2));
            e.Add(Entry("barn-wall-x4z3-N-s0", "block", wall, 43.0f, 14.4f, 270.0f, building: "barn", layer: 2));
            e.Add(Entry("barn-floor-x0z0-s1", "block", floor, 31.0f, 3.9f, 0.0f, y: 3.0f, building: "barn", layer: 3));
            e.Add(Entry("barn-floor-x0z1-s1", "block", floor, 31.0f, 6.9f, 0.0f, y: 3.0f, building: "barn", layer: 3));
            e.Add(Entry("barn-floor-x0z2-s1", "block", floor, 31.0f, 9.9f, 0.0f, y: 3.0f, building: "barn", layer: 3));
            e.Add(Entry("barn-floor-x0z3-s1", "block", floor, 31.0f, 12.9f, 0.0f, y: 3.0f, building: "barn", layer: 3));
            e.Add(Entry("barn-floor-x1z0-s1", "block", floor, 34.0f, 3.9f, 0.0f, y: 3.0f, building: "barn", layer: 3));
            e.Add(Entry("barn-floor-x1z1-s1", "block", floor, 34.0f, 6.9f, 0.0f, y: 3.0f, building: "barn", layer: 3));
            e.Add(Entry("barn-floor-x1z2-s1", "block", floor, 34.0f, 9.9f, 0.0f, y: 3.0f, building: "barn", layer: 3));
            e.Add(Entry("barn-floor-x1z3-s1", "block", floor, 34.0f, 12.9f, 0.0f, y: 3.0f, building: "barn", layer: 3));
            e.Add(Entry("barn-floor-x2z0-s1", "block", floor, 37.0f, 3.9f, 0.0f, y: 3.0f, building: "barn", layer: 3));
            e.Add(Entry("barn-floor-x2z1-s1", "block", floor, 37.0f, 6.9f, 0.0f, y: 3.0f, building: "barn", layer: 3));
            e.Add(Entry("barn-floor-x2z2-s1", "block", floor, 37.0f, 9.9f, 0.0f, y: 3.0f, building: "barn", layer: 3));
            e.Add(Entry("barn-floor-x2z3-s1", "block", floor, 37.0f, 12.9f, 0.0f, y: 3.0f, building: "barn", layer: 3));
            e.Add(Entry("barn-floor-x3z0-s1", "block", floor, 40.0f, 3.9f, 0.0f, y: 3.0f, building: "barn", layer: 3));
            e.Add(Entry("barn-floor-x3z1-s1", "block", floor, 40.0f, 6.9f, 0.0f, y: 3.0f, building: "barn", layer: 3));
            e.Add(Entry("barn-floor-x3z2-s1", "block", floor, 40.0f, 9.9f, 0.0f, y: 3.0f, building: "barn", layer: 3));
            e.Add(Entry("barn-floor-x3z3-s1", "block", floor, 40.0f, 12.9f, 0.0f, y: 3.0f, building: "barn", layer: 3));
            e.Add(Entry("barn-floor-x4z0-s1", "block", floor, 43.0f, 3.9f, 0.0f, y: 3.0f, building: "barn", layer: 3));
            e.Add(Entry("barn-floor-x4z1-s1", "block", floor, 43.0f, 6.9f, 0.0f, y: 3.0f, building: "barn", layer: 3));
            e.Add(Entry("barn-fframe-x4z2-s1", "block", floorFrame, 43.0f, 9.9f, 0.0f, y: 3.0f, building: "barn",
                layer: 3));
            e.Add(Entry("barn-floor-x4z3-s1", "block", floor, 43.0f, 12.9f, 0.0f, y: 3.0f, building: "barn", layer: 3));
            e.Add(Entry("barn-roof-x0z0-s1", "block", roof, 31.0f, 3.9f, 180.0f, y: 3.0f, building: "barn", layer: 5));
            e.Add(Entry("barn-gable-x0z0-W-s1", "block", wall, 29.5f, 3.9f, 180.0f, y: 3.0f, building: "barn",
                layer: 7));
            e.Add(Entry("barn-roof-x0z3-s1", "block", roof, 31.0f, 12.9f, 0.0f, y: 3.0f, building: "barn", layer: 5));
            e.Add(Entry("barn-gable-x0z3-W-s1", "block", wall, 29.5f, 12.9f, 180.0f, y: 3.0f, building: "barn",
                layer: 7));
            e.Add(Entry("barn-roof-x0z1-s2", "block", roof, 31.0f, 6.9f, 180.0f, y: 6.0f, building: "barn", layer: 6));
            e.Add(Entry("barn-frame-x0z1-W-s1", "block", wallFrame, 29.5f, 6.9f, 180.0f, y: 3.0f, building: "barn",
                layer: 7));
            e.Add(Entry("barn-gable-x0z1-W-s2", "block", wall, 29.5f, 6.9f, 180.0f, y: 6.0f, building: "barn",
                layer: 7));
            e.Add(Entry("barn-roof-x0z2-s2", "block", roof, 31.0f, 9.9f, 0.0f, y: 6.0f, building: "barn", layer: 6));
            e.Add(Entry("barn-gable-x0z2-W-s1", "block", wall, 29.5f, 9.9f, 180.0f, y: 3.0f, building: "barn",
                layer: 7));
            e.Add(Entry("barn-gable-x0z2-W-s2", "block", wall, 29.5f, 9.9f, 180.0f, y: 6.0f, building: "barn",
                layer: 7));
            e.Add(Entry("barn-roof-x1z0-s1", "block", roof, 34.0f, 3.9f, 180.0f, y: 3.0f, building: "barn", layer: 5));
            e.Add(Entry("barn-roof-x1z3-s1", "block", roof, 34.0f, 12.9f, 0.0f, y: 3.0f, building: "barn", layer: 5));
            e.Add(Entry("barn-roof-x1z1-s2", "block", roof, 34.0f, 6.9f, 180.0f, y: 6.0f, building: "barn", layer: 6));
            e.Add(Entry("barn-roof-x1z2-s2", "block", roof, 34.0f, 9.9f, 0.0f, y: 6.0f, building: "barn", layer: 6));
            e.Add(Entry("barn-roof-x2z0-s1", "block", roof, 37.0f, 3.9f, 180.0f, y: 3.0f, building: "barn", layer: 5));
            e.Add(Entry("barn-roof-x2z3-s1", "block", roof, 37.0f, 12.9f, 0.0f, y: 3.0f, building: "barn", layer: 5));
            e.Add(Entry("barn-roof-x2z1-s2", "block", roof, 37.0f, 6.9f, 180.0f, y: 6.0f, building: "barn", layer: 6));
            e.Add(Entry("barn-roof-x2z2-s2", "block", roof, 37.0f, 9.9f, 0.0f, y: 6.0f, building: "barn", layer: 6));
            e.Add(Entry("barn-roof-x3z0-s1", "block", roof, 40.0f, 3.9f, 180.0f, y: 3.0f, building: "barn", layer: 5));
            e.Add(Entry("barn-roof-x3z3-s1", "block", roof, 40.0f, 12.9f, 0.0f, y: 3.0f, building: "barn", layer: 5));
            e.Add(Entry("barn-roof-x3z1-s2", "block", roof, 40.0f, 6.9f, 180.0f, y: 6.0f, building: "barn", layer: 6));
            e.Add(Entry("barn-roof-x3z2-s2", "block", roof, 40.0f, 9.9f, 0.0f, y: 6.0f, building: "barn", layer: 6));
            e.Add(Entry("barn-roof-x4z0-s1", "block", roof, 43.0f, 3.9f, 180.0f, y: 3.0f, building: "barn", layer: 5));
            e.Add(Entry("barn-gable-x4z0-E-s1", "block", wall, 44.5f, 3.9f, 0.0f, y: 3.0f, building: "barn", layer: 7));
            e.Add(Entry("barn-roof-x4z3-s1", "block", roof, 43.0f, 12.9f, 0.0f, y: 3.0f, building: "barn", layer: 5));
            e.Add(Entry("barn-gable-x4z3-E-s1", "block", wall, 44.5f, 12.9f, 0.0f, y: 3.0f, building: "barn",
                layer: 7));
            e.Add(Entry("barn-roof-x4z1-s2", "block", roof, 43.0f, 6.9f, 180.0f, y: 6.0f, building: "barn", layer: 6));
            e.Add(Entry("barn-gable-x4z1-E-s1", "block", wall, 44.5f, 6.9f, 0.0f, y: 3.0f, building: "barn", layer: 7));
            e.Add(Entry("barn-gable-x4z1-E-s2", "block", wall, 44.5f, 6.9f, 0.0f, y: 6.0f, building: "barn", layer: 7));
            e.Add(Entry("barn-roof-x4z2-s2", "block", roof, 43.0f, 9.9f, 0.0f, y: 6.0f, building: "barn", layer: 6));
            e.Add(Entry("barn-gable-x4z2-E-s1", "block", wall, 44.5f, 9.9f, 0.0f, y: 3.0f, building: "barn", layer: 7));
            e.Add(Entry("barn-gable-x4z2-E-s2", "block", wall, 44.5f, 9.9f, 0.0f, y: 6.0f, building: "barn", layer: 7));
            e.Add(Entry("barn-ddoor-x0z1-W-s0", "door", doorDoubleHingedWood, 29.5f, 6.9f, 0.0f, building: "barn",
                parent: "barn-frame-x0z1-W-s0", layer: 8));
            e.Add(Entry("barn-ddoor-x4z1-E-s0", "door", doorDoubleHingedWood, 44.5f, 6.9f, 0.0f, building: "barn",
                parent: "barn-frame-x4z1-E-s0", layer: 8));
            e.Add(Entry("barn-ddoor-x0z2-W-s0", "door", doorDoubleHingedWood, 29.5f, 9.9f, 0.0f, building: "barn",
                parent: "barn-frame-x0z2-W-s0", layer: 8));
            e.Add(Entry("barn-ddoor-x4z2-E-s0", "door", doorDoubleHingedWood, 44.5f, 9.9f, 0.0f, building: "barn",
                parent: "barn-frame-x4z2-E-s0", layer: 8));
            e.Add(Entry("barn-bars-x1z0-S-s0", "insert", wallWindowBarsWood, 34.0f, 2.4f, 90.0f, y: 1.0f,
                building: "barn", parent: "barn-window-x1z0-S-s0", layer: 8));
            e.Add(Entry("barn-bars-x1z3-N-s0", "insert", wallWindowBarsWood, 34.0f, 14.4f, 270.0f, y: 1.0f,
                building: "barn", parent: "barn-window-x1z3-N-s0", layer: 8));
            e.Add(Entry("barn-bars-x3z0-S-s0", "insert", wallWindowBarsWood, 40.0f, 2.4f, 90.0f, y: 1.0f,
                building: "barn", parent: "barn-window-x3z0-S-s0", layer: 8));
            e.Add(Entry("barn-bars-x3z3-N-s0", "insert", wallWindowBarsWood, 40.0f, 14.4f, 270.0f, y: 1.0f,
                building: "barn", parent: "barn-window-x3z3-N-s0", layer: 8));
            e.Add(Entry("barn-ddoor-x0z1-W-s1", "door", doorDoubleHingedWood, 29.5f, 6.9f, 180.0f, y: 3.0f,
                building: "barn", parent: "barn-frame-x0z1-W-s1", layer: 8));
            e.Add(Entry("barn-hatch-x4z2-s1", "door", floorLadderHatch, 43.0f, 9.9f, 0.0f, y: 3.0f, building: "barn",
                parent: "barn-fframe-x4z2-s1", layer: 8));
            e.Add(Entry("barn-solar-01", "solar", SolarPrefab, 34.0f, 9.9148f, 90.0f, y: 7.7648f, building: "barn",
                parent: "barn-roof-x1z2-s2", roll: 45.0f, layer: 9, replaces: "solar-02"));
            e.Add(Entry("barn-solar-02", "solar", SolarPrefab, 40.0f, 9.9148f, 90.0f, y: 7.7648f, building: "barn",
                parent: "barn-roof-x3z2-s2", roll: 45.0f, layer: 9, replaces: "solar-01"));
            e.Add(Entry("barn-sign", "sign", signHugeWood, 44.65f, 8.4f, 90.0f, y: 3.55f, building: "barn",
                parent: "barn-gable-x4z1-E-s1", mount: true, layer: 9, image: "pandora-farm-sign.huge.wood-1024x256"));
            e.Add(Entry("barn-guide", "sign", signMediumWood, 44.65f, 13.2f, 90.0f, y: 1.1f,
                building: "barn", parent: "barn-wall-x4z3-E-s0", mount: true, layer: 10,
                image: "farm-guide"));
            e.Add(Entry("label-feed", "sign", signSmallWood, 31.7f, 14.25f, 180.0f, y: 2.3f,
                building: "barn", parent: "barn-wall-x0z3-N-s0", mount: true, layer: 10, image: "farm-feed"));
            e.Add(Entry("label-clones", "sign", signSmallWood, 34.87f, 14.25f, 180.0f, y: 2.3f,
                building: "barn", parent: "barn-window-x1z3-N-s0", mount: true, layer: 10, image: "farm-clones"));
            e.Add(Entry("label-dung", "sign", signSmallWood, 36.71f, 14.25f, 180.0f, y: 2.3f,
                building: "barn", parent: "barn-wall-x2z3-N-s0", mount: true, layer: 10, image: "farm-compost"));
            e.Add(Entry("label-wool", "sign", signSmallWood, 39.11f, 14.25f, 180.0f, y: 2.3f,
                building: "barn", parent: "barn-window-x3z3-N-s0", mount: true, layer: 10, image: "farm-wool"));
            e.Add(Entry("label-cold", "sign", signSmallWood, 30.9f, 2.55f, 0.0f, y: 2.3f,
                building: "barn", parent: "barn-wall-x0z0-S-s0", mount: true, layer: 10, image: "farm-cold"));
            e.Add(Entry("label-milk", "sign", signSmallWood, 32.76f, 2.55f, 0.0f, y: 2.3f,
                building: "barn", parent: "barn-window-x1z0-S-s0", mount: true, layer: 10, image: "farm-milk"));
            e.Add(Entry("label-eggs", "sign", signSmallWood, 34.58f, 2.55f, 0.0f, y: 2.3f,
                building: "barn", parent: "barn-window-x1z0-S-s0", mount: true, layer: 10, image: "farm-eggs"));
            e.Add(Entry("label-meat", "sign", signSmallWood, 38.83f, 2.55f, 0.0f, y: 2.3f,
                building: "barn", parent: "barn-window-x3z0-S-s0", mount: true, layer: 10, image: "farm-meat"));
            e.Add(Entry("barn-feed-1", "box_feed", componentBoxFoodLarge, 30.695f, 13.725f, 180.0f, on: "barn-fnd-x0z3",
                y: 0.099f, building: "barn", layer: 9, replaces: "box-feed"));
            e.Add(Entry("barn-feed-2", "box_feed", componentBoxFoodLarge, 32.785f, 13.725f, 180.0f, on: "barn-fnd-x1z3",
                y: 0.099f, building: "barn", layer: 9));
            e.Add(Entry("barn-clones", "box_clones", LargeBoxPrefab, 34.87f, 13.725f, 180.0f, on: "barn-fnd-x1z3",
                y: 0.099f, building: "barn", layer: 9, replaces: "box-clones"));
            e.Add(Entry("barn-dung", "box_dung", storageBarrelC, 36.71f, 13.76f, 180.0f, on: "barn-fnd-x2z3", y: 0.099f,
                building: "barn", layer: 9, replaces: "box-dung"));
            e.Add(Entry("barn-wool-1", "box_wool", storageBarrelC, 38.31f, 13.76f, 180.0f, on: "barn-fnd-x2z3",
                y: 0.099f, building: "barn", layer: 9, replaces: "box-wool"));
            e.Add(Entry("barn-wool-2", "box_wool", storageBarrelC, 39.91f, 13.76f, 180.0f, on: "barn-fnd-x3z3",
                y: 0.099f, building: "barn", layer: 9));
            e.Add(Entry("fridge-02", "fridge", FridgePrefab, 30.295f, 3.035f, 0.0f, on: "barn-fnd-x0z0", y: 0.119f,
                building: "barn", layer: 9, replaces: "fridge-01"));
            e.Add(Entry("fridge-03", "fridge", FridgePrefab, 31.535f, 3.035f, 0.0f, on: "barn-fnd-x0z0", y: 0.119f,
                building: "barn", layer: 9));
            e.Add(Entry("barn-milk", "box_milk", storageBarrelB, 32.76f, 3.03f, 0.0f, on: "barn-fnd-x1z0", y: 0.099f,
                building: "barn", layer: 9, replaces: "box-milk"));
            e.Add(Entry("barn-eggs-1", "box_eggs", storageBarrelB, 33.97f, 3.03f, 0.0f, on: "barn-fnd-x1z0", y: 0.099f,
                building: "barn", layer: 9, replaces: "box-eggs"));
            e.Add(Entry("barn-eggs-2", "box_eggs", storageBarrelB, 35.18f, 3.03f, 0.0f, on: "barn-fnd-x1z0", y: 0.099f,
                building: "barn", layer: 9));
            e.Add(Entry("barn-meat-1", "box_meat", LargeBoxPrefab, 36.8f, 3.075f, 0.0f, on: "barn-fnd-x2z0", y: 0.099f,
                building: "barn", layer: 9, replaces: "box-meat"));
            e.Add(Entry("barn-meat-2", "box_meat", LargeBoxPrefab, 38.83f, 3.075f, 0.0f, on: "barn-fnd-x3z0", y: 0.099f,
                building: "barn", layer: 9));
            e.Add(Entry("barn-meat-3", "box_meat", LargeBoxPrefab, 40.86f, 3.075f, 0.0f, on: "barn-fnd-x3z0", y: 0.099f,
                building: "barn", layer: 9));
            e.Add(Entry("battery-02", "battery", largeRechargableBatteryDeployed, 42.485f, 3.2f, 0.0f,
                on: "barn-fnd-x4z0", y: 0.099f, building: "barn", layer: 9, replaces: "battery-01"));
            e.Add(Entry("barn-combiner-01", "decor", electricalCombinerDeployed, 44.4f, 3.0f, 270.0f, y: 1.35f,
                building: "barn", parent: "barn-wall-x4z0-E-s0", layer: 9));
            e.Add(Entry("barn-combiner-02", "decor", electricalCombinerDeployed, 44.4f, 3.45f, 270.0f, y: 1.35f,
                building: "barn", parent: "barn-wall-x4z0-E-s0", layer: 9));
            e.Add(Entry("barn-splitter-01", "decor", splitter, 44.4f, 4.05f, 270.0f, y: 1.35f, building: "barn",
                parent: "barn-wall-x4z0-E-s0", layer: 9));
            e.Add(Entry("barn-splitter-02", "decor", splitter, 44.4f, 4.65f, 270.0f, y: 1.35f, building: "barn",
                parent: "barn-wall-x4z0-E-s0", layer: 9));
            e.Add(Entry("barn-light-switch", "decor",
                "assets/prefabs/deployable/playerioents/simpleswitch/switch.prefab", 44.4f, 4.05f, 270.0f,
                y: 2.0f, building: "barn", parent: "barn-wall-x4z0-E-s0", layer: 9));
            e.Add(Entry("barn-light-01-timed", "decor", ceilinglightDeployed, 34.0f, 6.9f, 0.0f, y: 2.973f, building: "barn",
                parent: "barn-floor-x1z1-s1", layer: 9, replaces: "barn-light-01"));
            e.Add(Entry("barn-light-02", "decor", ceilinglightDeployed, 40.0f, 6.9f, 0.0f, y: 2.973f, building: "barn",
                parent: "barn-floor-x3z1-s1", layer: 9));
            e.Add(Entry("barn-light-03", "decor", ceilinglightDeployed, 34.0f, 9.9f, 0.0f, y: 2.973f, building: "barn",
                parent: "barn-floor-x1z2-s1", layer: 9));
            e.Add(Entry("barn-light-04", "decor", ceilinglightDeployed, 40.0f, 9.9f, 0.0f, y: 2.973f, building: "barn",
                parent: "barn-floor-x3z2-s1", layer: 9));
            e.Add(Entry("barn-light-05", "decor", ceilinglightDeployed, 37.0f, 3.9f, 0.0f, y: 2.973f, building: "barn",
                parent: "barn-floor-x2z0-s1", layer: 9));
            e.Add(Entry("barn-light-06", "decor", ceilinglightDeployed, 37.0f, 12.9f, 0.0f, y: 2.973f, building: "barn",
                parent: "barn-floor-x2z3-s1", layer: 9));
            e.Add(Entry("barn-torch-e1", "decor", torchholderOnStatic, 44.6f, 4.9f, 90.0f, y: 2.0f, building: "barn",
                parent: "barn-wall-x4z0-E-s0", layer: 9));
            e.Add(Entry("barn-torch-e2", "decor", torchholderOnStatic, 44.6f, 11.9f, 90.0f, y: 2.0f, building: "barn",
                parent: "barn-wall-x4z3-E-s0", layer: 9));
            e.Add(Entry("barn-torch-w1", "decor", torchholderOnStatic, 29.4f, 4.9f, 270.0f, y: 2.0f, building: "barn",
                parent: "barn-wall-x0z0-W-s0", layer: 9));
            e.Add(Entry("barn-torch-w2", "decor", torchholderOnStatic, 29.4f, 11.9f, 270.0f, y: 2.0f, building: "barn",
                parent: "barn-wall-x0z3-W-s0", layer: 9));
            e.Add(Entry("house-fnd-x0z0", "block", FoundationPrefab, 43.0f, -16.5f, 0.0f, building: "house", layer: 0));
            e.Add(Entry("house-fnd-x0z1", "block", FoundationPrefab, 43.0f, -13.5f, 0.0f, building: "house", layer: 0));
            e.Add(Entry("house-fnd-x0z2", "block", FoundationPrefab, 43.0f, -10.5f, 0.0f, building: "house", layer: 0));
            e.Add(Entry("house-fnd-x1z0", "block", FoundationPrefab, 46.0f, -16.5f, 0.0f, building: "house", layer: 0));
            e.Add(Entry("house-fnd-x1z1", "block", FoundationPrefab, 46.0f, -13.5f, 0.0f, building: "house", layer: 0));
            e.Add(Entry("house-fnd-x1z2", "block", FoundationPrefab, 46.0f, -10.5f, 0.0f, building: "house", layer: 0));
            e.Add(Entry("house-fnd-x2z0", "block", FoundationPrefab, 49.0f, -16.5f, 0.0f, building: "house", layer: 0));
            e.Add(Entry("house-fnd-x2z1", "block", FoundationPrefab, 49.0f, -13.5f, 0.0f, building: "house", layer: 0));
            e.Add(Entry("house-fnd-x2z2", "block", FoundationPrefab, 49.0f, -10.5f, 0.0f, building: "house", layer: 0));
            e.Add(Entry("house-window-x0z0-W-s0", "block", wallWindow, 41.5f, -16.5f, 180.0f, building: "house",
                layer: 2));
            e.Add(Entry("house-wall-x0z1-W-s0", "block", wall, 41.5f, -13.5f, 180.0f, building: "house", layer: 2));
            e.Add(Entry("house-window-x0z2-W-s0", "block", wallWindow, 41.5f, -10.5f, 180.0f, building: "house",
                layer: 2));
            e.Add(Entry("house-wall-x0z0-S-s0", "block", wall, 43.0f, -18.0f, 90.0f, building: "house", layer: 2));
            e.Add(Entry("house-window-x1z0-S-s0", "block", wallWindow, 46.0f, -18.0f, 90.0f, building: "house",
                layer: 2));
            e.Add(Entry("house-window-x0z2-N-s0", "block", wallWindow, 43.0f, -9.0f, 270.0f, building: "house",
                layer: 2));
            e.Add(Entry("house-wall-x1z2-N-s0", "block", wall, 46.0f, -9.0f, 270.0f, building: "house", layer: 2));
            e.Add(Entry("house-window-x1z0-E-s0", "block", wallWindow, 47.5f, -16.5f, 0.0f, building: "house",
                layer: 2));
            e.Add(Entry("house-doorway-x1z1-E-s0", "block", wallDoorway, 47.5f, -13.5f, 0.0f, building: "house",
                layer: 2));
            e.Add(Entry("house-window-x1z2-E-s0", "block", wallWindow, 47.5f, -10.5f, 0.0f, building: "house",
                layer: 2));
            e.Add(Entry("house-wall-x0z0-N-s0", "block", wall, 43.0f, -15.0f, 270.0f, building: "house", layer: 2));
            e.Add(Entry("house-doorway-x1z0-N-s0", "block", wallDoorway, 46.0f, -15.0f, 270.0f, building: "house",
                layer: 2));
            e.Add(Entry("house-frame-x2z0-E-s0", "block", wallFrame, 50.5f, -16.5f, 0.0f, building: "house", layer: 2));
            e.Add(Entry("house-frame-x2z1-E-s0", "block", wallFrame, 50.5f, -13.5f, 0.0f, building: "house", layer: 2));
            e.Add(Entry("house-frame-x2z2-E-s0", "block", wallFrame, 50.5f, -10.5f, 0.0f, building: "house", layer: 2));
            e.Add(Entry("house-frame-x2z0-S-s0", "block", wallFrame, 49.0f, -18.0f, 90.0f, building: "house",
                layer: 2));
            e.Add(Entry("house-frame-x2z2-N-s0", "block", wallFrame, 49.0f, -9.0f, 270.0f, building: "house",
                layer: 2));
            e.Add(Entry("house-floor-x2z0-s1", "block", floor, 49.0f, -16.5f, 0.0f, y: 3.0f, building: "house",
                layer: 3));
            e.Add(Entry("house-floor-x2z1-s1", "block", floor, 49.0f, -13.5f, 0.0f, y: 3.0f, building: "house",
                layer: 3));
            e.Add(Entry("house-floor-x2z2-s1", "block", floor, 49.0f, -10.5f, 0.0f, y: 3.0f, building: "house",
                layer: 3));
            e.Add(Entry("house-roof-x0z0-s1", "block", roof, 43.0f, -16.5f, 270.0f, y: 3.0f, building: "house",
                layer: 5));
            e.Add(Entry("house-gable-x0z0-S-s1", "block", wall, 43.0f, -18.0f, 90.0f, y: 3.0f, building: "house",
                layer: 6));
            e.Add(Entry("house-roof-x1z0-s1", "block", roof, 46.0f, -16.5f, 90.0f, y: 3.0f, building: "house",
                layer: 5));
            e.Add(Entry("house-gable-x1z0-S-s1", "block", wall, 46.0f, -18.0f, 90.0f, y: 3.0f, building: "house",
                layer: 6));
            e.Add(Entry("house-roof-x0z1-s1", "block", roof, 43.0f, -13.5f, 270.0f, y: 3.0f, building: "house",
                layer: 5));
            e.Add(Entry("house-roof-x1z1-s1", "block", roof, 46.0f, -13.5f, 90.0f, y: 3.0f, building: "house",
                layer: 5));
            e.Add(Entry("house-roof-x0z2-s1", "block", roof, 43.0f, -10.5f, 270.0f, y: 3.0f, building: "house",
                layer: 5));
            e.Add(Entry("house-gable-x0z2-N-s1", "block", wall, 43.0f, -9.0f, 270.0f, y: 3.0f, building: "house",
                layer: 6));
            e.Add(Entry("house-roof-x1z2-s1", "block", roof, 46.0f, -10.5f, 90.0f, y: 3.0f, building: "house",
                layer: 5));
            e.Add(Entry("house-gable-x1z2-N-s1", "block", wall, 46.0f, -9.0f, 270.0f, y: 3.0f, building: "house",
                layer: 6));
            e.Add(Entry("house-door-x1z1-E-s0", "door", doorHingedWood, 47.5f, -13.5f, 0.0f, building: "house",
                parent: "house-doorway-x1z1-E-s0", layer: 8));
            e.Add(Entry("house-door-x1z0-N-s0", "door", doorHingedWood, 46.0f, -15.0f, 270.0f, building: "house",
                parent: "house-doorway-x1z0-N-s0", layer: 8));
            e.Add(Entry("house-glass-x0z0-W-s0", "insert", wallWindowGlassReinforced, 41.5f, -16.5f, 180.0f, y: 1.0f,
                building: "house", parent: "house-window-x0z0-W-s0", layer: 8));
            e.Add(Entry("house-shutter-x0z0-W-s0", "door", shutterWoodA, 41.5f, -16.5f, 180.0f, y: 1.0f,
                building: "house", parent: "house-window-x0z0-W-s0", layer: 8));
            e.Add(Entry("house-glass-x0z2-W-s0", "insert", wallWindowGlassReinforced, 41.5f, -10.5f, 180.0f, y: 1.0f,
                building: "house", parent: "house-window-x0z2-W-s0", layer: 8));
            e.Add(Entry("house-shutter-x0z2-W-s0", "door", shutterWoodA, 41.5f, -10.5f, 180.0f, y: 1.0f,
                building: "house", parent: "house-window-x0z2-W-s0", layer: 8));
            e.Add(Entry("house-glass-x1z0-S-s0", "insert", wallWindowGlassReinforced, 46.0f, -18.0f, 90.0f, y: 1.0f,
                building: "house", parent: "house-window-x1z0-S-s0", layer: 8));
            e.Add(Entry("house-shutter-x1z0-S-s0", "door", shutterWoodA, 46.0f, -18.0f, 90.0f, y: 1.0f,
                building: "house", parent: "house-window-x1z0-S-s0", layer: 8));
            e.Add(Entry("house-glass-x0z2-N-s0", "insert", wallWindowGlassReinforced, 43.0f, -9.0f, 270.0f, y: 1.0f,
                building: "house", parent: "house-window-x0z2-N-s0", layer: 8));
            e.Add(Entry("house-shutter-x0z2-N-s0", "door", shutterWoodA, 43.0f, -9.0f, 270.0f, y: 1.0f,
                building: "house", parent: "house-window-x0z2-N-s0", layer: 8));
            e.Add(Entry("house-glass-x1z0-E-s0", "insert", wallWindowGlassReinforced, 47.5f, -16.5f, 0.0f, y: 1.0f,
                building: "house", parent: "house-window-x1z0-E-s0", layer: 8));
            e.Add(Entry("house-shutter-x1z0-E-s0", "door", shutterWoodA, 47.5f, -16.5f, 0.0f, y: 1.0f,
                building: "house", parent: "house-window-x1z0-E-s0", layer: 8));
            e.Add(Entry("house-glass-x1z2-E-s0", "insert", wallWindowGlassReinforced, 47.5f, -10.5f, 0.0f, y: 1.0f,
                building: "house", parent: "house-window-x1z2-E-s0", layer: 8));
            e.Add(Entry("house-shutter-x1z2-E-s0", "door", shutterWoodA, 47.5f, -10.5f, 0.0f, y: 1.0f,
                building: "house", parent: "house-window-x1z2-E-s0", layer: 8));
            e.Add(Entry("house-bed", "bed", bedDeployed, 44.45f, -17.13f, 0.0f, on: "house-fnd-x0z0", y: 0.099f,
                building: "house", layer: 9));
            e.Add(Entry("house-rug-bedroom", "decor", rugDeployed, 44.45f, -16.05f, 90.0f, on: "house-fnd-x0z0",
                y: 0.099f, building: "house", layer: 9));
            e.Add(Entry("house-candles-bedroom", "decor", candlesetSmallOnStatic, 41.95f, -15.35f, 0.0f,
                on: "house-fnd-x0z0", y: 0.099f, building: "house", layer: 9));
            e.Add(Entry("house-hearth", "decor", fireplaceOnStatic, 42.46f, -13.1f, 90.0f, on: "house-fnd-x0z1",
                y: 0.099f, building: "house", layer: 9));
            e.Add(Entry("house-rug-hearth", "decor", rugBearDeployed, 44.45f, -13.1f, 90.0f, on: "house-fnd-x0z1",
                y: 0.099f, building: "house", layer: 9));
            e.Add(Entry("house-rockingchair-hearth", "decor", rockingchairDeployed, 45.0f, -13.1f, 270.0f,
                on: "house-fnd-x1z1", y: 0.099f, building: "house", layer: 9));
            e.Add(Entry("house-table", "decor", tableDeployed, 43.3f, -10.3f, 0.0f, on: "house-fnd-x0z2", y: 0.099f,
                building: "house", layer: 9));
            e.Add(Entry("house-chair-1", "decor", chairBStatic, 42.7f, -11.45f, 0.0f, on: "house-fnd-x0z2", y: 0.099f,
                building: "house", layer: 9));
            e.Add(Entry("house-chair-2", "decor", chairBStatic, 43.9f, -11.45f, 0.0f, on: "house-fnd-x0z2", y: 0.099f,
                building: "house", layer: 9));
            e.Add(Entry("house-cookingbench", "decor", cookingworkbenchDeployed, 46.0f, -9.9f, 180.0f,
                on: "house-fnd-x1z2", y: 0.099f, building: "house", layer: 9));
            e.Add(Entry("house-candles-table", "decor", candlesetLargeOnStatic, 43.3f, -10.3f, 0.0f, y: 0.929f,
                building: "house", parent: "house-table", layer: 9));
            e.Add(Entry("house-rockingchair-porch", "decor", rockingchairDeployed, 49.6f, -16.9f, 90.0f,
                on: "house-fnd-x2z0", y: 0.099f, building: "house", layer: 9));
            e.Add(Entry("house-bench-porch", "decor", woodenBenchAMountable, 47.95f, -10.5f, 90.0f,
                on: "house-fnd-x2z2", y: 0.099f, building: "house", layer: 9));
            e.Add(Entry("house-lantern-1-timed", "decor", lanternDeployed, 50.15f, -17.65f, 0.0f, on: "house-fnd-x2z0",
                y: 0.099f, building: "house", layer: 9, replaces: "house-lantern-1"));
            e.Add(Entry("house-lantern-2-timed", "decor", lanternDeployed, 50.15f, -9.35f, 0.0f, on: "house-fnd-x2z2",
                y: 0.099f, building: "house", layer: 9, replaces: "house-lantern-2"));
            e.Add(Entry("house-torch-1", "decor", torchholderOnStatic, 47.6f, -12.55f, 90.0f, y: 1.9f,
                building: "house", parent: "house-doorway-x1z1-E-s0", layer: 9));
            e.Add(Entry("house-torch-2", "decor", torchholderOnStatic, 47.6f, -14.45f, 90.0f, y: 1.9f,
                building: "house", parent: "house-doorway-x1z1-E-s0", layer: 9));
            e.Add(Entry("gate-02", "gate", GatePrefab, 27.55f, 14.5f, 90.0f, layer: 2));
            e.Add(Entry("fence-90", "fence", FencePrefab, 26.1f, 21.6f, 180.0f, layer: 2));
            e.Add(Entry("fence-91", "fence", FencePrefab, 26.1f, 24.2f, 180.0f, layer: 2));
            e.Add(Entry("fence-92", "fence", FencePrefab, 27.605f, 25.5f, 90.0f, layer: 2));
            e.Add(Entry("fence-93", "fence", FencePrefab, 30.615f, 25.5f, 90.0f, layer: 2));
            e.Add(Entry("fence-94", "fence", FencePrefab, 33.625f, 25.5f, 90.0f, layer: 2));
            e.Add(Entry("fence-95", "fence", FencePrefab, 36.635f, 25.5f, 90.0f, layer: 2));
            e.Add(Entry("fence-96", "fence", FencePrefab, 39.645f, 25.5f, 90.0f, layer: 2));
            e.Add(Entry("fence-97", "fence", FencePrefab, 42.655f, 25.5f, 90.0f, layer: 2));
            e.Add(Entry("fence-98", "fence", FencePrefab, 45.665f, 25.5f, 90.0f, layer: 2));
            e.Add(Entry("fence-99", "fence", FencePrefab, 48.675f, 25.5f, 90.0f, layer: 2));
            e.Add(Entry("fence-100", "fence", FencePrefab, 51.685f, 25.5f, 90.0f, layer: 2));
            e.Add(Entry("fence-101", "fence", FencePrefab, 54.695f, 25.5f, 90.0f, layer: 2));
            e.Add(Entry("fence-102", "fence", FencePrefab, 56.2f, 24.125f, 180.0f, layer: 2));
            e.Add(Entry("fence-103", "fence", FencePrefab, 56.2f, 21.375f, 180.0f, layer: 2));
            e.Add(Entry("fence-104", "fence", FencePrefab, 56.2f, 18.625f, 180.0f, layer: 2));
            e.Add(Entry("fence-105", "fence", FencePrefab, 56.2f, 15.875f, 180.0f, layer: 2));
            e.Add(Entry("fence-106", "fence", FencePrefab, 54.75f, 14.5f, 90.0f, layer: 2));
            e.Add(Entry("fence-107", "fence", FencePrefab, 51.85f, 14.5f, 90.0f, layer: 2));
            e.Add(Entry("fence-108", "fence", FencePrefab, 48.95f, 14.5f, 90.0f, layer: 2));
            e.Add(Entry("gate-03", "gate", GatePrefab, 46.05f, 14.5f, 90.0f, layer: 2));
            e.Add(Entry("planter-01", "planter", PlanterPrefab, 29.5f, 19.1f, 0.0f));
            e.Add(Entry("planter-02", "planter", PlanterPrefab, 32.9f, 19.1f, 0.0f));
            e.Add(Entry("planter-03", "planter", PlanterPrefab, 36.3f, 19.1f, 0.0f));
            e.Add(Entry("planter-04", "planter", PlanterPrefab, 39.7f, 19.1f, 0.0f));
            e.Add(Entry("sprinkler-01", "sprinkler", SprinklerPrefab, 29.5f, 21.3f, 180.0f));
            e.Add(Entry("sprinkler-02", "sprinkler", SprinklerPrefab, 32.9f, 21.3f, 180.0f));
            e.Add(Entry("sprinkler-03", "sprinkler", SprinklerPrefab, 36.3f, 21.3f, 180.0f));
            e.Add(Entry("sprinkler-04", "sprinkler", SprinklerPrefab, 39.7f, 21.3f, 180.0f));
            e.Add(Entry("deck-fnd-x0z0", "block", FoundationPrefab, 50.2f, 19.9f, 0.0f, building: "deck", layer: 0));
            e.Add(Entry("scarecrow-01", "decor", scarecrowDeployed, 50.2f, 19.9f, 225.0f, on: "deck-fnd-x0z0",
                y: 0.099f, building: "deck", layer: 9));
            e.Add(Entry("heater-01", "decor", electricalHeater, 50.2f, 21.4f, 0.0f, y: -0.022f, building: "deck",
                parent: "deck-fnd-x0z0", layer: 9));
            e.Add(Entry("heater-02", "decor", electricalHeater, 51.7f, 19.9f, 90.0f, y: -0.022f, building: "deck",
                parent: "deck-fnd-x0z0", layer: 9));
            e.Add(Entry("heater-03", "decor", electricalHeater, 50.2f, 18.4f, 180.0f, y: -0.0178f, building: "deck",
                parent: "deck-fnd-x0z0", layer: 9));
            e.Add(Entry("heater-04", "decor", electricalHeater, 48.7f, 19.9f, 270.0f, y: -0.022f, building: "deck",
                parent: "deck-fnd-x0z0", layer: 9));
            e.Add(Entry("planter-05", "planter", PlanterPrefab, 46.77f, 19.9f, 0.0f, layer: 10));
            e.Add(Entry("planter-06", "planter", PlanterPrefab, 50.2f, 23.33f, 0.0f, layer: 10));
            e.Add(Entry("planter-07", "planter", PlanterPrefab, 53.63f, 19.9f, 0.0f, layer: 10));
            e.Add(Entry("planter-08", "planter", PlanterPrefab, 50.2f, 16.47f, 0.0f, layer: 10));
            e.Add(Entry("sprinkler-05", "sprinkler", SprinklerPrefab, 47.45f, 22.7f, 180.0f, layer: 10));
            e.Add(Entry("sprinkler-06", "sprinkler", SprinklerPrefab, 52.95f, 17.1f, 180.0f, layer: 10));
            e.Add(Entry("composter-02", "composter", ComposterPrefab, 28.0f, 23.6f, 0.0f, layer: 10,
                replaces: "composter-01"));
            e.Add(Entry("beehive-01", "decor", beehiveDeployed, 43.2f, 24.3f, 180.0f, layer: 10));
            e.Add(Entry("gate-04", "gate", GatePrefab, 29.45f, -16.3f, 90.0f, layer: 2));
            e.Add(Entry("fence-109", "fence", FencePrefab, 32.35f, -16.3f, 90.0f, layer: 2));
            e.Add(Entry("fence-110", "fence", FencePrefab, 35.25f, -16.3f, 90.0f, layer: 2));
            e.Add(Entry("fence-111", "fence", FencePrefab, 38.15f, -16.3f, 90.0f, layer: 2));
            e.Add(Entry("fence-112", "fence", FencePrefab, 39.6f, -17.75f, 180.0f, layer: 2));
            e.Add(Entry("fence-113", "fence", FencePrefab, 39.6f, -20.65f, 180.0f, layer: 2));
            e.Add(Entry("fence-114", "fence", FencePrefab, 39.6f, -23.55f, 180.0f, layer: 2));
            e.Add(Entry("fence-115", "fence", FencePrefab, 38.15f, -25.0f, 90.0f, layer: 2));
            e.Add(Entry("fence-116", "fence", FencePrefab, 35.25f, -25.0f, 90.0f, layer: 2));
            e.Add(Entry("fence-117", "fence", FencePrefab, 32.35f, -25.0f, 90.0f, layer: 2));
            e.Add(Entry("fence-118", "fence", FencePrefab, 29.45f, -25.0f, 90.0f, layer: 2));
            e.Add(Entry("fence-119", "fence", FencePrefab, 28.0f, -23.55f, 180.0f, layer: 2));
            e.Add(Entry("fence-120", "fence", FencePrefab, 28.0f, -20.65f, 180.0f, layer: 2));
            e.Add(Entry("fence-121", "fence", FencePrefab, 28.0f, -17.75f, 180.0f, layer: 2));
            e.Add(Entry("coop-03", "coop", CoopPrefab, 33.6f, -18.65f, 0.0f, layer: 10, replaces: "coop-01"));
            e.Add(Entry("coop-04", "coop", CoopPrefab, 37.6f, -18.65f, 0.0f, layer: 10, replaces: "coop-02"));
            e.Add(Entry("coop-05", "coop", CoopPrefab, 33.6f, -22.65f, 0.0f, layer: 10));
            e.Add(Entry("coop-06", "coop", CoopPrefab, 37.6f, -22.65f, 0.0f, layer: 10));
            e.Add(Entry("windmill-01", "windmill", electricWindmillSmall, 61.0f, 19.0f, 0.0f, layer: 10));
            e.Add(Entry("sign-town", "sign", signPostTownRoof, 52.9f, -0.9f, 106.6f, layer: 10,
                image: "pandora-farm-sign.post.town-512x256"));
            e.Add(Entry("mailbox-01", "decor", mailboxDeployed, 51.0f, -3.6f, 286.6f, layer: 10));
            e.Add(Entry("planter-static-01", "planter_static", railroadplanterRespawningStatic, 44.5f, -6.1f, 0.0f,
                layer: 10));
            e.Add(Entry("planter-static-02", "planter_static", minecartPlanterRespawningStatic, 46.4f, 3.2f, 90.0f,
                layer: 10));
            e.Add(Entry("well-01", "decor", waterwellstatic, 51.5f, 7.6f, 0.0f, layer: 10));
            e.Add(Entry("bench-01", "decor", woodenBenchAMountable, 51.6f, 10.9f, 180.0f, layer: 10));
            e.Add(Entry("logpile-01", "decor", woodPile, 43.0f, -21.0f, 90.0f, layer: 10));
            e.Add(Entry("lantern-01-timed", "decor", lanternDeployed, 50.6f, 1.6f, 0.0f, layer: 10,
                replaces: "lantern-01"));
            e.Add(Entry("lantern-02-timed", "decor", lanternDeployed, 48.4f, 4.6f, 0.0f, layer: 10,
                replaces: "lantern-02"));
            e.Add(Entry("lantern-03-timed", "decor", lanternDeployed, 48.6f, 13.3f, 0.0f, layer: 10,
                replaces: "lantern-03"));
            e.Add(Entry("lantern-04-timed", "decor", lanternDeployed, 27.5f, 3.6f, 0.0f, layer: 10,
                replaces: "lantern-04"));
            e.Add(Entry("lantern-05-timed", "decor", lanternDeployed, -3.2f, 22.0f, 0.0f, layer: 10,
                replaces: "lantern-05"));
            e.Add(Entry("lantern-06-timed", "decor", lanternDeployed, 27.4f, -12.5f, 0.0f, layer: 10,
                replaces: "lantern-06"));
            e.Add(Entry("lantern-07-timed", "decor", lanternDeployed, 40.5f, -13.6f, 0.0f, layer: 10,
                replaces: "lantern-07"));
            e.Add(Entry("lantern-08-timed", "decor", lanternDeployed, 30.0f, -0.6f, 0.0f, layer: 10,
                replaces: "lantern-08"));
            e.Add(Entry("pump-01", "pump", PumpPrefab, 29.5f, 29.3f, 0.0f));
            List<LayoutConnection> c = layout.Connections;
            c.Add(Link("windmill-01", 0, "barn-combiner-01", 0, "power",
                GroundWire(61f, 19f), GroundWire(44.65f, 19f),
                GroundWire(44.65f, 3f), BarnWire(44.35f, 0.08f, 3f)));
            c.Add(Link("barn-solar-01", 0, "barn-combiner-01", 1, "power",
                BarnWire(34f, 7.8f, 9.915f), BarnWire(44.35f, 7.8f, 9.915f),
                BarnWire(44.35f, 4.865f, 12.85f), BarnWire(44.35f, 0.08f, 12.85f),
                BarnWire(44.35f, 0.08f, 3f)));
            c.Add(Link("barn-combiner-01", 0, "barn-combiner-02", 0, "power",
                BarnWire(44.35f, 0.08f, 3f), BarnWire(44.35f, 0.08f, 3.45f)));
            c.Add(Link("barn-solar-02", 0, "barn-combiner-02", 1, "power",
                BarnWire(40f, 7.8f, 9.915f), BarnWire(44.35f, 7.8f, 9.915f),
                BarnWire(44.35f, 4.865f, 12.85f), BarnWire(44.35f, 0.08f, 12.85f),
                BarnWire(44.35f, 0.08f, 3.45f)));
            c.Add(Link("barn-combiner-02", 0, "battery-02", 0, "power",
                BarnWire(44.35f, 0.08f, 3.45f), BarnWire(44.35f, 0.08f, 3.2f),
                BarnWire(42.485f, 0.08f, 3.2f)));
            c.Add(Link("battery-02", 0, "barn-splitter-01", 0, "power",
                BarnWire(42.485f, 0.08f, 3.2f), BarnWire(44.35f, 0.08f, 3.2f),
                BarnWire(44.35f, 0.08f, 4.05f)));
            c.Add(Link("barn-splitter-01", 0, "barn-splitter-02", 0, "power",
                BarnWire(44.35f, 0.08f, 4.05f), BarnWire(44.35f, 0.08f, 4.65f)));
            c.Add(Link("barn-splitter-02", 0, "fridge-02", 0, "power",
                BarnWire(44.35f, 0.08f, 4.65f), BarnWire(44.35f, 0.08f, 2.6f),
                BarnWire(30.295f, 0.08f, 2.6f), BarnWire(30.295f, 0.08f, 3.035f)));
            c.Add(Link("barn-splitter-02", 1, "fridge-03", 0, "power",
                BarnWire(44.35f, 0.08f, 4.65f), BarnWire(44.35f, 0.08f, 2.6f),
                BarnWire(31.535f, 0.08f, 2.6f), BarnWire(31.535f, 0.08f, 3.035f)));
            c.Add(Link("barn-splitter-01", 1, "barn-light-switch", 0, "power",
                BarnWire(44.35f, 1.35f, 4.05f), BarnWire(44.35f, 2f, 4.05f)));
            c.Add(Link("barn-light-switch", 0, "barn-light-01-timed", 0, "power",
                BarnWire(44.35f, 2.96f, 4.05f), BarnWire(44.35f, 2.96f, 6.9f),
                BarnWire(34f, 2.96f, 6.9f)));
            c.Add(Link("barn-light-01-timed", 0, "barn-light-02", 0, "power",
                BarnWire(34f, 2.96f, 6.9f), BarnWire(40f, 2.96f, 6.9f)));
            c.Add(Link("barn-light-02", 0, "barn-light-03", 0, "power",
                BarnWire(40f, 2.96f, 6.9f), BarnWire(40f, 2.96f, 9.9f),
                BarnWire(34f, 2.96f, 9.9f)));
            c.Add(Link("barn-light-03", 0, "barn-light-04", 0, "power",
                BarnWire(34f, 2.96f, 9.9f), BarnWire(40f, 2.96f, 9.9f)));
            c.Add(Link("barn-light-04", 0, "barn-light-05", 0, "power",
                BarnWire(40f, 2.96f, 9.9f), BarnWire(37f, 2.96f, 9.9f),
                BarnWire(37f, 2.96f, 3.9f)));
            c.Add(Link("barn-light-05", 0, "barn-light-06", 0, "power",
                BarnWire(37f, 2.96f, 3.9f), BarnWire(37f, 2.96f, 12.9f)));
            c.Add(Link("barn-splitter-01", 2, "heater-01", 0, "power",
                BarnWire(44.35f, 0.08f, 4.05f), BarnWire(44.35f, 0.08f, 14.1f),
                GroundWire(44.65f, 14.6f), GroundWire(44.65f, 24.8f),
                GroundWire(50.2f, 24.8f), GroundWire(50.2f, 21.65f)));
            c.Add(Link("heater-01", 0, "heater-02", 0, "power",
                GroundWire(50.2f, 21.65f), GroundWire(51.95f, 21.65f),
                GroundWire(51.95f, 19.9f)));
            c.Add(Link("heater-02", 0, "heater-03", 0, "power",
                GroundWire(51.95f, 19.9f), GroundWire(51.95f, 18.15f),
                GroundWire(50.2f, 18.15f)));
            c.Add(Link("heater-03", 0, "heater-04", 0, "power",
                GroundWire(50.2f, 18.15f), GroundWire(48.45f, 18.15f),
                GroundWire(48.45f, 19.9f)));
            c.Add(Link("heater-04", 0, "pump-01", 0, "power",
                GroundWire(48.45f, 19.9f), GroundWire(45f, 19.9f),
                GroundWire(45f, 25f), GroundWire(29.5f, 25f),
                GroundWire(29.5f, 29.3f)));
            c.Add(Link("pump-01", 0, "sprinkler-01", 0, "water",
                GroundWire(29.5f, 29.3f), GroundWire(29.5f, 21.3f)));
            c.Add(Link("sprinkler-01", 0, "sprinkler-02", 0, "water",
                GroundWire(29.5f, 21.3f), GroundWire(32.9f, 21.3f)));
            c.Add(Link("sprinkler-02", 0, "sprinkler-03", 0, "water",
                GroundWire(32.9f, 21.3f), GroundWire(36.3f, 21.3f)));
            c.Add(Link("sprinkler-03", 0, "sprinkler-04", 0, "water",
                GroundWire(36.3f, 21.3f), GroundWire(39.7f, 21.3f)));
            c.Add(Link("sprinkler-04", 0, "sprinkler-05", 0, "water",
                GroundWire(39.7f, 21.3f), GroundWire(43f, 24.8f),
                GroundWire(47.45f, 24.8f), GroundWire(47.45f, 22.7f)));
            c.Add(Link("sprinkler-05", 0, "sprinkler-06", 0, "water",
                GroundWire(47.45f, 22.7f), GroundWire(47.45f, 24.8f),
                GroundWire(55.6f, 24.8f), GroundWire(55.6f, 17.1f),
                GroundWire(52.95f, 17.1f)));
            c.Add(Link("catcher-05", 0, "tower-splitter", 0, "water",
                TowerWire(1.4f, 6.05f, 25.65f), TowerWire(1.4f, 2f, 25.65f),
                TowerWire(0f, 2f, 25.65f)));
            c.Add(Link("tower-splitter", 0, "trough-01", 0, "water",
                TowerWire(0f, 2f, 25.65f), TowerWire(-1.65f, 2f, 25.65f),
                GroundWire(-1.65f, 25.65f), GroundWire(-13.05f, 0f)));
            c.Add(Link("tower-splitter", 1, "trough-02", 0, "water",
                TowerWire(0f, 2f, 25.65f), TowerWire(1.65f, 2f, 25.65f),
                GroundWire(1.65f, 25.65f), GroundWire(13.05f, 0f)));
            return layout;
        }

        private static LayoutEntry Entry(string id, string role, string prefab, float x, float z, float yaw,
            string on = null, float y = 0f, string building = null, string parent = null, bool mount = false,
            float pitch = 0f, float roll = 0f, string grade = null, ulong? skin = null, int? layer = null,
            string image = null, string replaces = null) =>
            new LayoutEntry
            {
                Id = id, Role = role, Prefab = prefab, X = x, Z = z, Yaw = yaw, On = on, Y = y, Building = building,
                Parent = parent, Mount = mount, Pitch = pitch, Roll = roll, Grade = grade, Skin = skin, Layer = layer,
                Image = image, Replaces = replaces,
            };

        private static LayoutConnection Link(string from, int fromSlot, string to, int toSlot, string kind,
            params WirePoint[] route) =>
            new LayoutConnection
            {
                From = from, FromSlot = fromSlot, To = to, ToSlot = toSlot, Kind = kind,
                Route = route.Length == 0 ? null : route.ToList(),
            };

        private static WirePoint BarnWire(float x, float y, float z) =>
            new WirePoint { Anchor = "barn-fnd-x0z0", X = x - 31f, Y = y, Z = z - 3.9f };

        private static WirePoint TowerWire(float x, float y, float z) =>
            new WirePoint { Anchor = "tower-fnd-x0z0", X = x, Y = y, Z = z - 24.25f };

        private static WirePoint GroundWire(float x, float z) => new WirePoint { X = x, Y = 0.025f, Z = z };

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
            ValidateLightingConfig(c, problems);
            ValidateAssistedConfig(c, problems);
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
                    bool surface = under != null && (under.Role == RolePlatform || under.Role == RoleFoundation ||
                                                     (under.Role == RoleBlock && IsSurfacePrefab(under.Prefab)));
                    if (under == entry || !surface)
                        problems.Add($"{entry.Id} is On {entry.On}, which is not a platform, foundation, or floor " +
                                     "entry");
                    else if (LayerOf(under) > LayerOf(entry) ||
                             (LayerOf(under) == LayerOf(entry) &&
                              layout.Entries.IndexOf(under) > layout.Entries.IndexOf(entry)))
                        problems.Add($"{entry.Id} is On {entry.On}, which is placed after it (Layer {LayerOf(under)} " +
                                     $"against its own {LayerOf(entry)}, or later in the same layer)");
                }
                LayoutProblems(layout, problems);
                foreach (LayoutConnection link in layout.Connections)
                {
                    if (link == null || !ids.Contains(link.From ?? string.Empty) ||
                        !ids.Contains(link.To ?? string.Empty))
                        problems.Add($"connection {link} names a layout id that does not exist");
                    if (link?.Route == null)
                        continue;
                    if (link.Route.Count > 64)
                        problems.Add($"connection {link} has more than 64 route points");
                    foreach (WirePoint point in link.Route)
                        if (point == null || !Finite(point.X) || !Finite(point.Y) || !Finite(point.Z) ||
                            (point.Anchor != null && !ids.Contains(point.Anchor)))
                            problems.Add($"connection {link} has an invalid route point or unknown anchor");
                }
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
            BuildingGrade.Enum grade;
            if (!TryParseGrade(c.BuildingGrade, out grade))
                problems.Add($"Building grade {c.BuildingGrade} is not one of {GradeNames}");
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

        // The building fields (Building, Y, Parent, Mount, Pitch, Roll, Grade, Skin, Layer, Image, Replaces); a layout
        // without buildings sets none of them and passes unchanged.
        private static void LayoutProblems(FarmLayout layout, List<string> problems)
        {
            Dictionary<string, LayoutEntry> byId = EntriesById(layout.Entries);
            var foundations = new HashSet<string>(layout.Entries
                .Where(e => e != null && !string.IsNullOrEmpty(e.Building) && IsFoundationPrefab(e.Prefab))
                .Select(e => e.Building));
            var replaced = new Dictionary<string, string>();
            foreach (LayoutEntry entry in layout.Entries.Where(e => e != null && !string.IsNullOrEmpty(e.Id)))
            {
                string id = entry.Id;
                string other;
                if (entry.Replaces != null && entry.Replaces.Length == 0)
                    problems.Add($"{id} has an empty Replaces; name the old record it takes over, or leave Replaces " +
                                 "out");
                else if (entry.Replaces != null && byId.ContainsKey(entry.Replaces))
                    problems.Add($"{id} Replaces {entry.Replaces}, which stays in the layout; an Id that stays keeps " +
                                 "its record where it stands");
                else if (entry.Replaces != null && replaced.TryGetValue(entry.Replaces, out other))
                    problems.Add($"{id} and {other} both Replace {entry.Replaces}");
                else if (entry.Replaces != null)
                    replaced[entry.Replaces] = id;
                if (!Finite(entry.Y) || !Finite(entry.Pitch) || !Finite(entry.Roll))
                    problems.Add($"{id} has a non-finite Y, Pitch, or Roll");
                else if ((IsBlockRole(entry.Role) || entry.Role == RoleDoor || entry.Role == RoleInsert) &&
                         (entry.Pitch != 0f || entry.Roll != 0f))
                    problems.Add($"{id} needs Pitch and Roll 0: blocks, doors, and inserts sit level in their sockets");
                else if (Mathf.Abs(Mathf.DeltaAngle(0f, entry.Pitch)) > 90f || Mathf.Abs(entry.Pitch) > 360f ||
                         Mathf.Abs(entry.Roll) > 360f)
                    problems.Add($"{id} needs a Pitch within 90 degrees of level, and Pitch and Roll between " +
                                 "-360 and 360 degrees");
                BuildingGrade.Enum grade;
                if (entry.Grade != null && !TryParseGrade(entry.Grade, out grade))
                    problems.Add($"{id} has Grade {entry.Grade}, which is not one of {GradeNames}");
                if (entry.Image != null && entry.Role != RoleSign)
                    problems.Add($"{id} has an Image but Role {entry.Role}; only sign entries are painted");
                else if (entry.Image != null && !HasSignImage(entry.Image))
                    problems.Add($"{id} names Image {entry.Image}, which the plugin does not carry (it carries " +
                                 $"{string.Join(", ", SignImages.Keys)})");
                if (entry.Role == RoleBlock && string.IsNullOrEmpty(entry.Building))
                    problems.Add($"{id} is a block without a Building, so it has no base height");
                if (!string.IsNullOrEmpty(entry.Building) && !foundations.Contains(entry.Building))
                    problems.Add($"{id} names Building {entry.Building}, which has no foundation entry to set its " +
                                 "base height");
                if ((entry.Role == RoleDoor || entry.Role == RoleInsert || entry.Mount) &&
                    string.IsNullOrEmpty(entry.Parent))
                    problems.Add($"{id} needs a Parent (Role {entry.Role}{(entry.Mount ? ", Mount" : string.Empty)})");
                if (string.IsNullOrEmpty(entry.Parent))
                    continue;
                LayoutEntry parent;
                if (!byId.TryGetValue(entry.Parent, out parent) || parent == entry)
                {
                    problems.Add($"{id} names Parent {entry.Parent}, which is not another layout entry");
                    continue;
                }
                if (LayerOf(parent) > LayerOf(entry) ||
                    (LayerOf(parent) == LayerOf(entry) &&
                     layout.Entries.IndexOf(parent) > layout.Entries.IndexOf(entry)))
                    problems.Add($"{id} has Parent {entry.Parent}, which is placed after it (Layer {LayerOf(parent)} " +
                                 $"against its own {LayerOf(entry)}, or later in the same layer)");
                if (!string.IsNullOrEmpty(entry.Building) && !string.IsNullOrEmpty(parent.Building) &&
                    entry.Building != parent.Building)
                    problems.Add($"{id} is in Building {entry.Building} but its Parent {parent.Id} is in " +
                                 parent.Building);
                if (entry.Role != RoleDoor && entry.Role != RoleInsert)
                    continue;
                if (!IsBlockRole(parent.Role))
                {
                    problems.Add($"{id} is a {entry.Role} whose Parent {parent.Id} is not a building block entry");
                    continue;
                }
                string name = PrefabName(entry.Prefab);
                if (name.StartsWith("floor.triangle.ladder.hatch"))
                {
                    problems.Add($"{id} is a {name}, which the plugin cannot place (it has no recipe for the hatch " +
                                 "spots of a floor.triangle.frame); use a floor.ladder.hatch in a floor.frame");
                    continue;
                }
                float turnStep;
                string opening = InsertOpening(entry.Prefab, out turnStep);
                if (opening != null && PrefabName(parent.Prefab) != opening)
                {
                    problems.Add($"{id} is a {name}, which fits a {opening}, but its Parent {parent.Id} is a " +
                                 PrefabName(parent.Prefab));
                    continue;
                }
                float turn = Mathf.DeltaAngle(parent.Yaw, entry.Yaw);
                if (Mathf.Abs(turn - Mathf.Round(turn / turnStep) * turnStep) > 1f)
                    problems.Add($"{id} turns {turn:0.#} degrees from its Parent {parent.Id}; a {name} takes " +
                                 (turnStep < 180f ? "a multiple of 90" : "0 or 180") + " degrees there");
                Vector3 offset;
                if (!InsertOffset(entry.Prefab, out offset) || BuildingOf(entry, byId) != BuildingOf(parent, byId))
                    continue;
                Vector3 recipe = LayoutPoint(parent) + Quaternion.Euler(0f, parent.Yaw, 0f) * offset;
                float off = (LayoutPoint(entry) - recipe).magnitude;
                if (off > 0.1f)
                    problems.Add($"{id} lies {off:0.00} m from where {parent.Id}'s opening puts a {name}; check its " +
                                 "Parent and pose");
            }
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
            public long LastFarmVisit;
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
            // Base height T of each layout Building as a placement last worked it out; reused only while a block of the
            // building stands.
            public Dictionary<string, float> Buildings = new Dictionary<string, float>();
        }

        private class StructureRecord
        {
            // A player's removal of the colony must not generate another starter nucleus for the same hive.
            public ulong BeeColonySeededNetId;
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
            // The grade and skin the farm gave this block, restored on every pass; null for other entities and for
            // the stone foundations of entries without a Building, which nothing restores.
            public string Grade;
            public ulong? Skin;
            // farm.repair or farm.upgrade tried the entry and it waited for its support or for a player or animal in
            // its spot; auto-repair retries it until it stands.
            [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)]
            public bool Pending;
            // farm.upgrade retired the entry but could not empty it or hand its chickens over; it stays protected,
            // takes no new items but food and water for chickens it still holds, and the next farm.upgrade confirm
            // tries again.
            public bool Retired;
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
            // A player or animal stood in the spot on the last try, or something it rests on was not in place.
            public bool Waiting;
            public string WaitingFor;
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
            data.Buildings = data.Buildings ?? new Dictionary<string, float>();
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
            // A kept Id takes its role from the layout; only a move needs farm.upgrade (a new Id with Replaces).
            Dictionary<string, LayoutEntry> entries = EntriesById();
            var roles = new List<string>();
            foreach (StructureRecord rec in _data.Structures)
            {
                LayoutEntry entry;
                if (rec.Id == null || !entries.TryGetValue(rec.Id, out entry) || entry.Role == rec.Role)
                    continue;
                roles.Add($"{rec.Id} {rec.Role} to {entry.Role}");
                rec.Role = entry.Role;
            }
            if (roles.Count > 0)
                Puts($"load: {roles.Count} records take their layout role: {string.Join(", ", roles)}");
            var missing = new List<string>();
            var back = new List<string>();
            var off = new List<string>();
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
                BaseEntity standing = ResolveStructure(rec);
                if (standing == null)
                {
                    if (fence)
                        fencesGone++;
                    missing.Add(rec.Id);
                    rec.Missing = true;
                    MarkLost(rec);
                    continue;
                }
                float drift = Vector3.Distance(standing.transform.position, new Vector3(rec.X, rec.Y, rec.Z));
                if (drift > RecordDrift)
                    off.Add($"{rec.Id} {drift:0.0} m");
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
            _recordChecked = off.Count == 0;
            if (off.Count > 0)
                Failure("record", $"load: {off.Count} structures stand away from where the record has them, so " +
                                  "the record is ahead of the world (a world restored from an older save, or a crash " +
                                  "after a farm command and before the next save); auto-repair stays off until " +
                                  "farm.repair or farm.build vouches for it, and farm.remove confirm then farm.build " +
                                  $"starts afresh: {string.Join(", ", off)}", "failure", 0f);
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
                new Step { Name = "lighting", Run = LightingStep },
                new Step { Name = "attendance", Run = AttendanceStep },
                new Step { Name = "sweep", Run = SweepStep },
                new Step { Name = "herd", NeedsOwner = true, Run = HerdStep },
                new Step { Name = "sex safety", NeedsOwner = true, Run = SexStep },
                new Step { Name = "breeding", NeedsOwner = true, Run = BreedingStep },
                new Step { Name = "crops", Run = CropStep },
                new Step { Name = "compost", Run = CompostStep },
                new Step { Name = "bees", Run = BeeStep },
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
            UnlockScheduledLights();
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
                string parentId = rec.Id;
                NextTick(() =>
                {
                    if (!_unloaded && _data != null && _data.Built)
                        Guard("structures", $"children of {parentId}", () => RemoveOrphans(parentId));
                });
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
            return True;
        }

        // Carbon's IOnNpcTarget calls this from Rust.Ai.Gen2.SenseComponent.CanTarget (non-null: false) and
        // BaseNpc.GetWantsToAttack (non-null: 0); HumanNPC and AIBrainSenses skip the target on non-null.
        private object OnNpcTarget(BaseEntity npc, BaseEntity target)
        {
            ulong id = IdOf(target);
            if (id == 0 || !_animals.ContainsKey(id))
                return null;
            return True;
        }

        private object OnEntityGroundMissing(BaseEntity entity)
        {
            if (entity == null || !IsProtected(entity))
                return null;
            Failure($"ground:{IdOf(entity)}", $"kept {NameOf(entity)} after its ground went missing");
            return True;
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

        private bool IsFarmStructure(BaseNetworkable entity) => entity != null && _structureIds.Contains(IdOf(entity));

        // No player upgrades, reskins, demolishes, rotates, papers, or builds onto a guarded farm part; auto-repair
        // would put a lost one back. BuildingSkins reskins without a hook, so QueueGradeCheck undoes it.
        private object CanChangeGrade(BasePlayer player, BuildingBlock block, BuildingGrade.Enum grade, ulong skin) =>
            IsGuarded(block) ? False : null;

        private object CanDemolish(BasePlayer player, DecayEntity entity) => IsGuarded(entity) ? False : null;

        // Carbon passes Oxide's third argument (immediate) as null, so the two-parameter form binds.
        private object OnStructureDemolish(DecayEntity entity, BasePlayer player) => IsGuarded(entity) ? True : null;

        private object OnStructureRotate(BuildingBlock block, BasePlayer player) => IsGuarded(block) ? True : null;

        private object OnWallpaperSet(BuildingBlock block, ulong id, int side, float rotation) =>
            IsGuarded(block) ? True : null;

        private object OnWallpaperRemove(BuildingBlock block, int side) => IsGuarded(block) ? True : null;

        // Building pieces and wallpaper only: deployables still go onto farm blocks (the owner furnishes the barn and
        // the house), and seeds and clones into farm planters. Locks and door closers never come here.
        private object CanBuild(Planner planner, Construction construction, Construction.Target target) =>
            target.entity is BuildingBlock && (construction?.deployable == null || planner is WallpaperPlanner) &&
            IsGuarded(target.entity)
                ? True
                : null;

        // Locks and door closers deploy through Deployer.DoDeploy_Slot, which asks only for building privilege, and
        // most farm buildings have no cupboard to withhold it: only the owner may put one on a farm part.
        private object CanDeployItem(BasePlayer player, Deployer deployer, NetworkableId entityId)
        {
            if (player == null || !_structureIds.Contains(entityId.Value) || player.userID.Get() == FarmOwner())
                return null;
            player.ShowToast(GameTip.Styles.Error, ConstructionErrors.NoPermission, false);
            return False;
        }

        // SprayCan.ChangeItemSkin kills an entity whose new skin is another prefab's (a component box for a large box,
        // a retro cupboard, a railroad planter) and spawns that prefab with its contents, untracked; auto-repair would
        // then place a second one. Every farm part keeps its prefab and skin.
        private object OnEntityReskin(BaseEntity entity, ulong skin, BasePlayer player) =>
            IsFarmStructure(entity) ? True : null;

        // Blocks the farm grades (TargetGrade), entries with a Parent, and roles outside V1Roles. A layout without
        // buildings leaves its foundation, platform, and deployables as open to players as they always were.
        private bool IsGuarded(BaseNetworkable entity)
        {
            if (!IsFarmStructure(entity) || _data == null)
                return false;
            ulong id = IdOf(entity);
            StructureRecord rec = _data.Structures.FirstOrDefault(r => r.NetId == id);
            LayoutEntry entry = rec == null ? null : _config.Layout.Entries.FirstOrDefault(e => e.Id == rec.Id);
            if (entry == null)
                return false;
            BuildingGrade.Enum grade;
            ulong skin;
            return TargetGrade(entry, out grade, out skin) || !string.IsNullOrEmpty(entry.Parent) ||
                   !V1Roles.Contains(entry.Role);
        }

        // The farm paints and owns its signs: nobody repaints, locks, or unlocks them, admins included.
        private object CanUpdateSign(BasePlayer player, BaseEntity sign) => IsFarmStructure(sign) ? False : null;

        // A public bed is anyone's to claim (SleepingBag.RPC_MakeBed). The farm bed stays the owner's respawn point,
        // and StructureStep assigns it back after a hand-over to a friend.
        private object CanSetBedPublic(BasePlayer player, SleepingBag bag) => IsFarmStructure(bag) ? False : null;

        // A farm block or insert that passed its first check keeps its stability and never collapses, so losing a wall
        // costs only the wall and auto-repair re-places it under the roof that still stands.
        private object OnEntityStabilityCheck(StabilityEntity entity) =>
            entity != null && entity.cachedStability >= ConVar.Stability.collapse && IsFarmStructure(entity)
                ? True
                : null;

        // BuildingSkins reskins in its own OnHammerHit, and in a NextTick from OnStructureUpgrade, which CanChangeGrade
        // lets run on a graded farm block only once its layout entry is gone or no longer grades it. QueueGradeCheck's
        // NextTick runs after both.
        private void OnHammerHit(BasePlayer player, HitInfo info) => QueueGradeCheck(info?.HitEntity as BuildingBlock);

        private void OnStructureUpgraded(BuildingBlock block, BasePlayer player, BuildingGrade.Enum grade,
            ulong skin) => QueueGradeCheck(block);

        // A split gives every part a new building id, and the cupboard follows the nearest block or drops to none.
        private void OnBuildingSplit(BuildingManager.Building building, uint newId)
        {
            if (_reattachQueued || _data == null || !_data.Built || !_ready)
                return;
            _reattachQueued = true;
            NextTick(() =>
            {
                _reattachQueued = false;
                if (!_unloaded && _data != null && _data.Built)
                    Guard("structures", "cupboard", ReattachCupboard);
            });
        }

        private void QueueGradeCheck(BuildingBlock block)
        {
            if (!IsFarmStructure(block))
                return;
            NextTick(() =>
            {
                if (_unloaded || block == null || block.IsDestroyed)
                    return;
                Guard("structures", NameOf(block), () =>
                {
                    ulong id = IdOf(block);
                    StructureRecord rec = _data.Structures.FirstOrDefault(r => r.NetId == id);
                    LayoutEntry entry = rec == null ? null : EntryById(rec.Id);
                    string was = RestoreGrade(block, rec, entry);
                    if (was == null)
                        return;
                    string building = entry?.Building ?? rec.Id;
                    LogRestores(new Dictionary<string, List<string>>
                    {
                        [building] = new List<string> { $"{rec.Id} (was {was})" },
                    });
                });
            });
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
                arg.ReplyWith(NoOwner);
                return;
            }
            string reply = Build(x, z, yaw, owner);
            SaveWorld("farm.build");
            arg.ReplyWith(reply);
        }

        [ConsoleCommand("farm.status")]
        private void StatusCommand(ConsoleSystem.Arg arg)
        {
            if (Authorized(arg))
                arg.ReplyWith(Status());
        }

        [ConsoleCommand("farm.summary")]
        private void SummaryCommand(ConsoleSystem.Arg arg)
        {
            if (Authorized(arg))
                arg.ReplyWith(PlayerFarmSummary());
        }

        private readonly Dictionary<ulong, float> _farmStatusRequestedAt = new Dictionary<ulong, float>();

        private void OnPlayerDisconnected(BasePlayer player, string reason)
        {
            if (player != null)
                _farmStatusRequestedAt.Remove(player.userID);
        }

        [ChatCommand("farm")]
        private void FarmChatCommand(BasePlayer player, string command, string[] args)
        {
            if (player == null)
                return;
            float now = Time.realtimeSinceStartup;
            float previous;
            if (_farmStatusRequestedAt.TryGetValue(player.userID, out previous) && now - previous < 5f)
                return;
            _farmStatusRequestedAt[player.userID] = now;
            if (args != null && args.Length > 0)
            {
                SendReply(player, args[0] == "help"
                    ? "Milk cows through their interaction prompt once they trust you; shear sheep with a cutting tool. " +
                      "Player beds keep automatic watering and fertilizer. Plant and harvest them yourself. " +
                      "After the configured absence grace, milk, wool, and ripe crops go to barn storage; player beds " +
                      "are not replanted. Other beds remain automatic. Coordinate shared supplies with the owner."
                    : "Use /farm for status or /farm help for farming instructions.");
                return;
            }
            foreach (string message in FarmChatMessages(PlayerFarmSummary()))
                SendReply(player, message);
        }

        private static IEnumerable<string> FarmChatMessages(string summary)
        {
            const int limit = 480;
            var message = new StringBuilder();
            foreach (string line in summary.Split('\n'))
            {
                if (message.Length > 0 && message.Length + line.Length + 1 > limit)
                {
                    yield return message.ToString();
                    message.Clear();
                }
                string remaining = line;
                while (remaining.Length > limit)
                {
                    yield return remaining.Substring(0, limit);
                    remaining = remaining.Substring(limit);
                }
                if (message.Length > 0)
                    message.Append('\n');
                message.Append(remaining);
            }
            if (message.Length > 0)
                yield return message.ToString();
        }

        private string PlayerFarmSummary()
        {
            if (!_ready)
                return "Pandora Farm is starting. Try /farm again shortly.";
            if (_disabled != null)
                return "Pandora Farm upkeep is unavailable. Ask an administrator to check farm.status.";
            if (!_data.Built)
                return "Pandora Farm has not been built yet.";
            UpkeepPass pass = BeginPass(false);
            var sb = new StringBuilder("PANDORA FARM\n");
            bool absence = AbsenceAutomationActive(pass);
            sb.AppendLine($"Production: milking {(_config.AutoMilk ? "automatic" : "player managed")}; " +
                          $"shearing {(_config.AutoShear ? "automatic" : "player managed")}. " +
                          $"Absence fallback: {(absence ? "active" : "inactive")}.");
            int readyCows = Herd(pass, Cattle).OfType<Cow>().Count(CowReadyToMilk);
            int readySheep = Herd(pass, SheepKind).OfType<Sheep>().Count(SheepReadyToShear);
            sb.AppendLine($"Ready: {readyCows} cows to milk; {readySheep} sheep to shear.");
            sb.AppendLine("Lighting: " + FarmLightingSummary(pass));
            sb.AppendLine(BeeSummary(pass));
            sb.AppendLine("Herd: " + string.Join("; ", AllSpecies.Select(s => $"{s} {Herd(pass, s).Count}")));
            List<string> surplus = AllSpecies.Select(s => new
                {
                    Species = s, Count = CapCount(Herd(pass, s)), Cap = CapOf(s),
                }).Where(s => s.Count > s.Cap)
                .Select(s => $"{s.Species} {s.Count}/{s.Cap}").ToList();
            sb.AppendLine($"Culling: {(_config.AutoCull ? "automatic (breeder and infant protections apply)" : "disabled; owner manages surplus")}. " +
                          (surplus.Count == 0 ? "Herd within caps." : "Above cap: " + string.Join("; ", surplus) + "."));
            List<ChickenCoop> coops = LiveRole(pass, RoleCoop).OfType<ChickenCoop>().ToList();
            sb.AppendLine($"Coops: {coops.Sum(c => c.Animals.Count)} chickens / hatching eggs");
            List<HitchTrough> troughs = LiveRole(pass, RoleTrough).OfType<HitchTrough>().ToList();
            int dryTroughs = troughs.Count(t => t.WaterStorage == null || t.WaterStorage.GetLiquidCount() <= 0);
            sb.AppendLine(troughs.Count == 0 ? "Troughs: missing; ask an administrator to inspect the farm."
                : dryTroughs > 0 ? $"Troughs: {dryTroughs}/{troughs.Count} empty; check the water supply."
                    : $"Troughs: all {troughs.Count} contain water.");
            List<PlanterBox> planters = LiveRole(pass, RolePlanter).OfType<PlanterBox>().ToList();
            int dryPlanters = planters.Count(p => p.soilSaturation < _config.DryFraction * p.soilSaturationMax);
            sb.AppendLine(planters.Count == 0 ? "Garden: no planters found."
                : dryPlanters > 0 ? $"Garden: {dryPlanters}/{planters.Count} beds dry; check irrigation."
                    : $"Garden: all {planters.Count} beds above the dry threshold.");
            List<PlanterBox> manualBeds = planters.Where(p => IsManualPlanter(pass.Name(p))).ToList();
            if (_config.ManualPlanterIds.Count > 0)
            {
                GrowableEntity[][] manualPlants = manualBeds.Select(PlantsBySlot).ToArray();
                int ripe = manualPlants.Sum(slots => slots.Count(p => p != null && p.State == PlantProperties.State.Ripe));
                int empty = manualPlants.Sum(slots => slots.Count(p => p == null));
                string locations = string.Join(", ", manualBeds.Select(p => PlayerBedName(pass.Name(p))));
                sb.AppendLine($"Player beds: {locations} " +
                              $"({manualBeds.Count}/{_config.ManualPlanterIds.Count} present); {ripe} ripe crops, {empty} empty slots. " +
                              "Plant and harvest here; soil and fertilizer stay automatic.");
                sb.AppendLine("Absence care harvests ripe crops to Feed; player beds are not replanted. /farm help for instructions.");
            }
            List<Fridge> fridges = LiveRole(pass, RoleFridge).OfType<Fridge>().ToList();
            sb.AppendLine(fridges.Count == 0 ? "Cold storage: no fridges found; ask an administrator to inspect."
                : $"Cold storage: {fridges.Count(f => f.IsPowered())}/{fridges.Count} fridges powered."
                    + (fridges.Any(f => !f.IsPowered()) ? " Check the barn power supply." : string.Empty));
            var stores = new Dictionary<string, string>
            {
                [BoxFeed] = "Feed", [BoxClones] = "Clones", [BoxDung] = "Manure",
                [BoxWool] = "Wool", [BoxMilk] = "Milk", [BoxEggs] = "Eggs", [BoxMeat] = "Meat",
            };
            var crowded = new List<string>();
            foreach (KeyValuePair<string, string> store in stores)
            {
                List<ItemContainer> containers = pass.Containers(store.Key);
                if (containers.Count > 0 && containers.All(c => c.itemList.Count >= c.capacity))
                    crowded.Add(store.Value);
            }
            sb.AppendLine(crowded.Count == 0 ? "Stores: free slots available in the barn stores that are present."
                : "No free slots: " + string.Join(", ", crowded) + ". Collect supplies; stack space may remain.");
            int missing = _config.Layout.Entries.Count(e => e.Role != RolePlaceholder && pass.ById(e.Id) == null);
            if (missing > 0)
                sb.AppendLine($"Maintenance: {missing} layout parts absent; ask an administrator to check farm.status.");
            sb.Append("Barn: labeled supplies. Grazing pen: west. Garden: north. House and coops: south.\n"
                + "Containers are shared; coordinate with the farm owner before taking supplies.");
            return sb.ToString();
        }

        // A role's parts without those farm.upgrade retired, which the upkeep steps skip.
        private static IEnumerable<BaseEntity> LiveRole(UpkeepPass pass, string role) =>
            pass.Role(role).Where(e => !pass.Retired.Contains(IdOf(e)));

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
                arg.ReplyWith(NoOwner);
                return;
            }
            if (!_recordChecked)
                RecordWhereStanding();
            _recordChecked = true;
            arg.ReplyWith(Repair(owner));
        }

        // farm.repair vouches for a record whose structures VerifyRecord found standing away from it: each standing
        // structure's record takes the spot where it stands, so the next load does not hold auto-repair off again.
        private void RecordWhereStanding()
        {
            foreach (StructureRecord rec in _data.Structures)
            {
                BaseEntity standing = rec.NetId == 0 || rec.Missing ? null : ResolveStructure(rec);
                if (standing == null)
                    continue;
                Vector3 at = standing.transform.position;
                rec.X = at.x;
                rec.Y = at.y;
                rec.Z = at.z;
            }
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
            string reply = Remove();
            SaveWorld("farm.remove");
            arg.ReplyWith(reply);
        }

        // OnServerSave writes the record with the world, so a crash before the next autosave cannot leave a record
        // that a farm command changed ahead of the world.
        private void SaveWorld(string after) =>
            Guard("save", after, () => ConsoleSystem.Run(ConsoleSystem.Option.Server.Quiet(), "server.save"));

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

        private const string NoOwner = "no owner: set Owner SteamID in the config or add an owner to cfg/users.cfg";

        private const string NotReady =
            "the farm record is not checked yet; try again after the server finishes loading";

        // Hammer pickup, and Remover Tool's refund, would hand out the item while auto-repair spawns a new one. A
        // refusal without pickupErrorToFormat logs a warning in RPC_PickupStart; the toast it sends carries phrase
        // tokens only, so it gives a vanilla reason, and the chat line the real one.
        private object CanPickupEntity(BasePlayer player, BaseCombatEntity entity)
        {
            if (entity == null || !_structureIds.Contains(IdOf(entity)))
                return null;
            if (entity.pickup.itemTarget != null)
                entity.pickupErrorToFormat = (BaseCombatEntity.PickupErrors.ItemIsBeingUsed,
                    entity.pickup.itemTarget.displayName);
            player?.ChatMessage($"{NameOf(entity)} is {FarmPartRefusal}");
            return False;
        }

        // Remover Tool (server/carbon/plugins/RemoverTool.cs) shows a returned string as the reason it refuses.
        private object canRemove(BasePlayer player, BaseEntity entity) => RemoveRefusal(entity);

        private object CanAdminRemove(BasePlayer player, BaseEntity entity, string removeType) => RemoveRefusal(entity);

        private object RemoveRefusal(BaseEntity entity) =>
            entity != null && _structureIds.Contains(IdOf(entity)) ? FarmPartRefusal : null;

        private const string FarmPartRefusal =
            "part of the livestock farm: remove it from the farm layout in the config";

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
            // Why each deferred entry waits: the player or animal in its spot, or what it rests on.
            public readonly Dictionary<string, string> Waits = new Dictionary<string, string>();

            public string WaitReason(string id)
            {
                string why;
                return Waits.TryGetValue(id, out why) ? why : null;
            }

            // The original wording while every wait is for an occupied spot; a support wait names the support.
            public string WaitingText() =>
                DeferredIds.All(id => !IsSupportWait(WaitReason(id)))
                    ? $"waiting for the spot to clear ({string.Join(", ", DeferredIds)})"
                    : "waiting (" + string.Join(", ", DeferredIds.Select(id => IsSupportWait(WaitReason(id))
                        ? $"{id}: {WaitReason(id)}"
                        : $"{id}: waiting for the spot to clear")) + ")";

            public void Add(PlaceResult result, string id, string waiting = null)
            {
                Results[id] = result;
                if (waiting != null)
                    Waits[id] = waiting;
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
            PlaceContext context = NewPlaceContext();
            foreach (LayoutEntry entry in ordered)
            {
                var rec = new StructureRecord { Id = entry.Id, Role = entry.Role, Prefab = entry.Prefab };
                _data.Structures.Add(rec);
                context.Records[entry.Id] = rec;
                string support = MissingSupport(entry, context);
                PlaceResult result = support == null
                    ? PlaceGuarded(entry, rec, owner)
                    : PlacementFailed(rec, $"it rests on {support}, which is not in place");
                tally.Add(result, entry.Id);
                if (result == PlaceResult.Placed || result == PlaceResult.Adopted)
                    context.Placed.Add(entry.Id);
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
            {
                RepairState state = RepairOf(id);
                state.Waiting = true;
                state.WaitingFor = tally.WaitReason(id);
            }
            Guard("repair", "wiring", () => WiringStep(BeginPass()));
            string text = todo.Count == 0
                ? "every layout entry is in place"
                : $"{todo.Count} layout entries were missing or had failed: {tally.Placed} placed, " +
                  $"{tally.Adopted} adopted, {tally.Failed} failed" +
                  (tally.FailedIds.Count > 0 ? $" ({string.Join(", ", tally.FailedIds)})" : string.Empty) +
                  (tally.DeferredIds.Count > 0
                      ? $", {tally.DeferredIds.Count} {tally.WaitingText()}"
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

        // No record, a record never attempted (an old placeholder), or an "On" or "Parent" entry whose block was never
        // placed: only farm.repair places these, so auto-repair neither tries nor logs them.
        private static bool NeverPlaced(LayoutEntry entry, Dictionary<string, StructureRecord> records)
        {
            StructureRecord rec;
            if (!records.TryGetValue(entry.Id, out rec) || Unplaced(rec))
                return true;
            StructureRecord under;
            if (!string.IsNullOrEmpty(entry.On) && (!records.TryGetValue(entry.On, out under) || Unplaced(under)))
                return true;
            return !string.IsNullOrEmpty(entry.Parent) &&
                   (!records.TryGetValue(entry.Parent, out under) || Unplaced(under));
        }

        private static bool IsSupportWait(string why) => why != null && why.StartsWith("waits for");

        // Never stood and never failed: a new entry not tried yet, or one that waits (Pending).
        private static bool NotYetPlaced(StructureRecord rec) => rec.NetId == 0 && rec.Failure == null && !rec.Missing;

        private static bool Unplaced(StructureRecord rec) =>
            rec.Role == RolePlaceholder || (rec.NetId == 0 && rec.Failure == null && !rec.Missing && !rec.Pending);

        // The farm.build placement path for the given entries, reusing their records. An entry whose support neither
        // stands nor was placed earlier in this call waits, and so does everything resting on it.
        private PlaceTally PlaceMissing(List<LayoutEntry> entries, ulong owner, string kind)
        {
            _clearedVegetation.Clear();
            var tally = new PlaceTally();
            PlaceContext context = NewPlaceContext();
            _deferOccupied = true;
            try
            {
                foreach (LayoutEntry entry in entries)
                {
                    StructureRecord rec;
                    if (!context.Records.TryGetValue(entry.Id, out rec))
                    {
                        rec = new StructureRecord { Id = entry.Id };
                        _data.Structures.Add(rec);
                        context.Records[entry.Id] = rec;
                    }
                    string support = MissingSupport(entry, context);
                    if (support != null)
                    {
                        rec.Pending = true;
                        tally.Add(PlaceResult.Deferred, entry.Id, $"waits for {support}");
                        continue;
                    }
                    PlaceResult result = PlaceGuarded(entry, rec, owner);
                    string held = result == PlaceResult.Deferred ? $"its spot holds {rec.Waiting}" : null;
                    if (result == PlaceResult.Deferred)
                        rec.Pending = true;
                    tally.Add(result, entry.Id, held);
                    if (result == PlaceResult.Placed || result == PlaceResult.Adopted)
                        context.Placed.Add(entry.Id);
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

        // What one placement call knows: the layout, the records, what it placed so far, and each building's blocks.
        private class PlaceContext
        {
            public Dictionary<string, LayoutEntry> Entries;
            public Dictionary<string, StructureRecord> Records;
            public readonly HashSet<string> Placed = new HashSet<string>();
            public readonly Dictionary<string, List<LayoutEntry>> Blocks = new Dictionary<string, List<LayoutEntry>>();

            public bool Standing(string id)
            {
                StructureRecord rec;
                return Placed.Contains(id) ||
                       (Records.TryGetValue(id, out rec) && rec.NetId != 0 && !rec.Missing);
            }
        }

        private PlaceContext NewPlaceContext()
        {
            var context = new PlaceContext
            {
                Entries = EntriesById(_config.Layout.Entries),
                Records = RecordsById(),
            };
            foreach (LayoutEntry entry in _config.Layout.Entries.Where(IsBuildingBlock))
            {
                List<LayoutEntry> blocks;
                if (!context.Blocks.TryGetValue(entry.Building, out blocks))
                    context.Blocks[entry.Building] = blocks = new List<LayoutEntry>();
                blocks.Add(entry);
            }
            return context;
        }

        // The first support of the entry that neither stands nor was placed earlier in this call: its Parent, the
        // building block it is On, and for a building block every lower-layer block of its building within
        // SupportRadius across (an over-approximation of the pieces it rests on). An entry On a platform or
        // foundation without a Building does not wait: Place adopts what stands on the spot and fails without it.
        private static string MissingSupport(LayoutEntry entry, PlaceContext context)
        {
            if (!string.IsNullOrEmpty(entry.Parent) && !context.Standing(entry.Parent))
                return entry.Parent;
            LayoutEntry under;
            if (!string.IsNullOrEmpty(entry.On) && context.Entries.TryGetValue(entry.On, out under) &&
                IsBuildingBlock(under) && !context.Standing(entry.On))
                return entry.On;
            List<LayoutEntry> blocks;
            if (!IsBuildingBlock(entry) || !context.Blocks.TryGetValue(entry.Building, out blocks))
                return null;
            int layer = LayerOf(entry);
            foreach (LayoutEntry other in blocks)
                if (other != entry && LayerOf(other) < layer && Across(other, entry) <= SupportRadius &&
                    !context.Standing(other.Id))
                    return other.Id;
            return null;
        }

        private static float Across(LayoutEntry a, LayoutEntry b) => new Vector2(a.X - b.X, a.Z - b.Z).magnitude;

        // Every upkeep pass re-places layout entries the farm placed before and lost, or failed to place (in layer
        // order, supports first; the cupboard is reattached), once VerifyRecord or farm.build vouched for the record.
        // Entries the farm never placed wait for farm.repair. A failed placement waits 10 minutes, doubling up to 2
        // hours; a loss is weighed in WeighLoss; its lines log at most hourly. Fences and the gate never wait: a gap
        // in the line lets the herd out. A spot with a player or animal in it, or an entry whose support is not in
        // place, is retried on the next pass.
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
                state.WaitingFor = state.Waiting ? tally.WaitReason(entry.Id) : null;
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

        // Why the configured auto-repair does not run (AutoRepair returns at once), or null while it does.
        private string AutoRepairHold() =>
            !_config.AutoRepair ? null
            : !_recordChecked ? "auto-repair is held until farm.repair or farm.build vouches for the record"
            : FarmOwner() == 0 ? "auto-repair is held while the farm has no owner"
            : null;

        private bool AutoRepairRuns => _config.AutoRepair && AutoRepairHold() == null;

        private string RepairHint(bool plural)
        {
            string what = plural ? "them" : "it";
            return AutoRepairRuns
                ? $"auto-repair re-places {what} on the next pass"
                : $"farm.repair respawns {what}";
        }

        // What brings a lost structure back: auto-repair only for an entry it handles (not one the farm never placed).
        private string RepairHintFor(StructureRecord rec)
        {
            LayoutEntry entry = _config.Layout.Entries.FirstOrDefault(e => e.Id == rec.Id);
            if (entry == null)
                return "it is no longer in the layout, so it stays gone";
            return AutoRepairRuns && !NeverPlaced(entry, RecordsById())
                ? "auto-repair re-places it on the next pass"
                : "farm.repair respawns it";
        }

        private string RepairWait(string id)
        {
            RepairState state;
            if (!AutoRepairRuns || string.IsNullOrEmpty(id) || !_repairs.TryGetValue(id, out state))
                return string.Empty;
            if (state.Waiting)
                return IsSupportWait(state.WaitingFor) ? $", {state.WaitingFor}" : ", waiting for the spot to clear";
            float left = state.NextAt - Time.realtimeSinceStartup;
            return left > 0f ? $", next auto-repair in {Duration(left)}" : string.Empty;
        }

        // Building ids are fluid: a respawned foundation starts a new building, a split renumbers every part, and the
        // cupboard follows the nearest block or none. It rejoins the block under it so its privilege covers the
        // building again.
        private void ReattachCupboard()
        {
            BuildingPrivlidge cupboard = FarmCupboard();
            BuildingBlock block = cupboard == null ? null : BlockUnder(cupboard.transform.position, true);
            if (block == null || cupboard.buildingID == block.buildingID)
                return;
            cupboard.AttachToBuilding(block.buildingID);
            Puts($"repair: attached {NameOf(cupboard)} to the building of {NameOf(block)}");
        }

        // Entries sort by (layer, index in the list); a layout without Layer values keeps its foundation, cupboard,
        // fence, other order.
        private static List<LayoutEntry> Ordered(List<LayoutEntry> entries) =>
            entries.Select((entry, index) => new { entry, index })
                .OrderBy(p => LayerOf(p.entry))
                .ThenBy(p => p.index)
                .Select(p => p.entry)
                .ToList();

        private static int LayerOf(LayoutEntry entry) => entry.Layer ?? DefaultLayer(entry.Role);

        private int LayerOfRecord(StructureRecord rec)
        {
            LayoutEntry entry = _config.Layout.Entries.FirstOrDefault(e => e.Id == rec.Id);
            return entry != null ? LayerOf(entry) : DefaultLayer(rec.Role);
        }

        private static int DefaultLayer(string role)
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
                    return 10;
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
                // Spawned and tracked before the throw, so not set up: it goes, and auto-repair places it again.
                BaseEntity spawned = rec.NetId != 0 ? ResolveStructure(rec) : null;
                if (spawned != null)
                {
                    _structureIds.Remove(rec.NetId);
                    spawned.Kill();
                }
                rec.NetId = 0;
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

        // Inserts at their parent's opening, building blocks at T + Y, then the cupboard, entries On a block, entries
        // in a building, and terrain entries Y above the ground. Locate and PlaceCheck are the rules farm.upgrade's
        // plan runs too; an untracked match standing at the pose is adopted before the checks.
        private PlaceResult Place(LayoutEntry entry, StructureRecord rec, ulong owner)
        {
            rec.Role = entry.Role;
            rec.Prefab = entry.Prefab;
            rec.NetId = 0;
            rec.Failure = null;
            rec.Missing = false;
            rec.Waiting = null;
            rec.Yaw = _data.Yaw + entry.Yaw;
            if (entry.Role == RolePlaceholder)
            {
                Puts($"build: {entry.Id} is a {RolePlaceholder} placeholder; skipped");
                return PlaceResult.Skipped;
            }
            if (string.IsNullOrEmpty(entry.Prefab))
                return PlacementFailed(rec, "the layout entry has no prefab");
            Vector3 position;
            Quaternion rotation;
            Rest rest;
            string problem;
            if (!Locate(entry, true, 0, out position, out rotation, out rest, out problem))
                return PlacementFailed(rec, problem);
            BaseEntity adopted = Adopt(rec, position, owner, AdoptWithin(entry));
            if (adopted != null)
            {
                BuildingGrade.Enum grade;
                ulong skin;
                if (adopted is BuildingBlock && IsBlockRole(entry.Role) && TargetGrade(entry, out grade, out skin))
                    ApplyGrade((BuildingBlock)adopted, rec, grade, skin, false);
                Authorize(adopted as BuildingPrivlidge, owner);
                return PlaceResult.Adopted;
            }
            problem = PlaceCheck(entry, true, position, rotation, rest, _retiring);
            if (problem != null)
                return PlacementFailed(rec, problem);
            BaseEntity entity = SpawnStructure(rec, position, rotation, owner, e => Prepare(entry, e, owner, rest));
            if (entity == null)
                return PlaceResult.Failed;
            AfterSpawn(entry, rec, entity, owner);
            return PlaceResult.Placed;
        }

        // Blocks and inserts stand socket-exact, so only one within 0.1 m is theirs; anything else within 0.5 m across.
        private static float AdoptWithin(LayoutEntry entry) =>
            IsBuildingBlock(entry) || entry.Role == RoleDoor || entry.Role == RoleInsert ? AdoptBlockDistance : 0f;

        // What an entry rests on, standing or, in a plan, at its layout pose: an insert's parent, a Parent entry's
        // parent, or the block an On entry or the cupboard stands on.
        private class Rest
        {
            public string Id;
            public BaseEntity Entity;
            public Vector3 Position;
            public Quaternion Rotation;
            public string Prefab;

            public static Rest Of(string id, BaseEntity entity) => new Rest
            {
                Id = id,
                Entity = entity,
                Position = entity.transform.position,
                Rotation = entity.transform.rotation,
                Prefab = entity.PrefabName,
            };
        }

        // Where an entry goes. live is Place: what it rests on must stand, and BuildingBase fixes a building's T. A
        // plan (live false) takes a support that does not stand yet at its own layout pose and previews T. False only
        // where no pose exists: a refused base height, or an insert whose parent is missing.
        private bool Locate(LayoutEntry entry, bool live, int depth, out Vector3 position, out Quaternion rotation,
            out Rest rest, out string problem)
        {
            position = Vector3.zero;
            rotation = Quaternion.identity;
            rest = null;
            problem = null;
            if (depth > 8)
            {
                problem = "its supports nest more than 8 deep";
                return false;
            }
            if (entry.Role == RolePlaceholder || string.IsNullOrEmpty(entry.Prefab))
            {
                problem = "it has no prefab";
                return false;
            }
            float yaw = _data.Yaw + entry.Yaw;
            Vector3 ground = FarmPoint(entry.X, entry.Z);
            string building = BuildingOf(entry);
            float t;
            if (entry.Role == RoleDoor || entry.Role == RoleInsert)
            {
                LayoutEntry parentEntry = EntryById(entry.Parent);
                if (parentEntry == null)
                {
                    problem = $"its parent {entry.Parent} is not in place";
                    return false;
                }
                if (!Support(parentEntry, ParentEntity(entry), live, depth, out rest, out problem))
                {
                    problem = live
                        ? $"its parent {entry.Parent} is not in place"
                        : $"its parent {parentEntry.Id}: {problem}";
                    return false;
                }
                float turn = Mathf.Round(Mathf.DeltaAngle(parentEntry.Yaw, entry.Yaw) / 90f) * 90f;
                Vector3 offset;
                if (!InsertOffset(entry.Prefab, out offset))
                    offset = Quaternion.Inverse(Quaternion.Euler(0f, parentEntry.Yaw, 0f)) *
                             (LayoutPoint(entry) - LayoutPoint(parentEntry));
                position = rest.Position + rest.Rotation * offset;
                rotation = rest.Rotation * Quaternion.Euler(0f, turn, 0f);
                return true;
            }
            if (IsBuildingBlock(entry) ||
                (building != null && string.IsNullOrEmpty(entry.On) && entry.Role != RoleCupboard))
            {
                if (!BaseOf(building, live, out t, out problem))
                    return false;
                position = ground.WithY(t + entry.Y);
                rotation = EntryRotation(entry);
                DecayEntity parent = ParentEntity(entry);
                if (parent != null)
                    rest = Rest.Of(entry.Parent, parent);
                return true;
            }
            if (entry.Role == RoleCupboard || !string.IsNullOrEmpty(entry.On))
            {
                BuildingBlock block = string.IsNullOrEmpty(entry.On)
                    ? BlockUnder(ground, false)
                    : StandingBlock(entry.On);
                LayoutEntry under = EntryById(entry.On);
                if (block != null)
                    rest = Rest.Of(entry.On, block);
                else if (!live && under != null && !Support(under, null, false, depth, out rest, out problem))
                {
                    problem = $"{under.Id}, which it stands on: {problem}";
                    return false;
                }
                float y = (rest != null ? rest.Position.y : ground.y) + entry.Y;
                if (building != null)
                {
                    if (!BaseOf(building, live, out t, out problem))
                        return false;
                    y = t + entry.Y;
                }
                position = ground.WithY(y);
                rotation = EntryRotation(entry);
                return true;
            }
            Vector3 normal = TerrainMeta.HeightMap.GetNormal(ground);
            ground.y += entry.Y;
            switch (entry.Role)
            {
                case RoleFoundation:
                case RolePlatform:
                {
                    rotation = Quaternion.Euler(0f, yaw, 0f);
                    float lowest;
                    position = ground.WithY(TerrainSpan(ground, rotation, out lowest) + entry.Y);
                    return true;
                }
                case RoleFence:
                case RoleGate:
                {
                    // Yaw is the heading of the length axis (local X); the rail rolls with the ground along it.
                    Vector3 forward = Quaternion.Euler(0f, yaw - 90f, 0f) * Vector3.forward;
                    rotation = Quaternion.LookRotation(forward, normal);
                    position = ground + rotation * new Vector3(0f, FenceLift, 0f);
                    return true;
                }
                case RolePump:
                    rotation = Quaternion.Euler(0f, yaw, 0f);
                    position = ground.WithY(Mathf.Max(TerrainMeta.WaterMap.GetHeight(ground), ground.y));
                    return true;
                default:
                    rotation = entry.Pitch != 0f || entry.Roll != 0f
                        ? EntryRotation(entry)
                        : Quaternion.FromToRotation(Vector3.up, normal) * Quaternion.Euler(0f, yaw, 0f);
                    position = ground;
                    return true;
            }
        }

        // A support entry, standing (given), or in a plan at its own layout pose.
        private bool Support(LayoutEntry entry, BaseEntity standing, bool live, int depth, out Rest rest,
            out string problem)
        {
            rest = null;
            problem = null;
            if (standing != null)
            {
                rest = Rest.Of(entry.Id, standing);
                return true;
            }
            if (live)
                return false;
            Vector3 position;
            Quaternion rotation;
            Rest ignored;
            if (!Locate(entry, false, depth + 1, out position, out rotation, out ignored, out problem))
                return false;
            rest = new Rest { Id = entry.Id, Position = position, Rotation = rotation, Prefab = entry.Prefab };
            return true;
        }

        private bool BaseOf(string building, bool live, out float t, out string problem)
        {
            if (live)
                return BuildingBase(building, out t, out problem);
            string how;
            bool ok = PreviewBuildingBase(building, out t, out how);
            problem = ok ? null : $"base height of building {building}: {how}";
            return ok;
        }

        // The highest ground over a 3 x 3 m square (5 x 5 samples) and, out, the lowest.
        private static float TerrainSpan(Vector3 centre, Quaternion rotation, out float lowest)
        {
            float highest = float.MinValue;
            lowest = float.MaxValue;
            foreach (Vector2 p in Grid(-FoundationHalfWidth, FoundationHalfWidth, -FoundationHalfWidth,
                         FoundationHalfWidth, 5, 5))
            {
                float h = TerrainMeta.HeightMap.GetHeight(centre + rotation * new Vector3(p.x, 0f, p.y));
                highest = Mathf.Max(highest, h);
                lowest = Mathf.Min(lowest, h);
            }
            return highest;
        }

        // What Place refuses at a pose, after trying to adopt what stands there: a Parent or support that is not in
        // place, a spot off its block, a foundation over terrain it cannot span or over parts it would bury (aside
        // lists the parts a confirm lifts, moves, or retires), a pump with no water, or a failed ground check.
        private string PlaceCheck(LayoutEntry entry, bool live, Vector3 position, Quaternion rotation, Rest rest,
            ICollection<ulong> aside)
        {
            if (entry.Role == RoleDoor || entry.Role == RoleInsert)
                return null;
            string building = BuildingOf(entry);
            if (IsBuildingBlock(entry) ||
                (building != null && string.IsNullOrEmpty(entry.On) && entry.Role != RoleCupboard))
            {
                if (live && !IsBuildingBlock(entry) && !string.IsNullOrEmpty(entry.Parent) && rest == null)
                    return $"its parent {entry.Parent} is not in place";
                if (!IsFoundationPrefab(entry.Prefab))
                    return null;
                float top = position.y;
                float rise = top - GroundUnder(entry, false);
                if (rise >= FoundationMaxRise)
                    return $"it would stand {rise:0.00} m over the lowest ground at its corners, " +
                           $"{FoundationMaxRise} m or more";
                float above = GroundUnder(entry, true) - top;
                if (above > OnTolerance)
                    return $"the ground under it rises {above:0.00} m above its top";
                string buried = BuriedUnder(position, rotation, IsTrianglePrefab(entry.Prefab), aside);
                return buried == null ? null : $"it would bury {buried}; move them or the building in the layout";
            }
            if (entry.Role == RoleCupboard || !string.IsNullOrEmpty(entry.On))
            {
                if (rest == null)
                    return string.IsNullOrEmpty(entry.On)
                        ? "no placed foundation under the cupboard position"
                        : $"{entry.On}, which it stands on, is not in place";
                if (entry.Role == RoleCupboard)
                    return null;
                Vector3 local = Quaternion.Inverse(rest.Rotation) * (position - rest.Position);
                LayoutEntry under = EntryById(entry.On);
                if (under != null && !IsBuildingBlock(under) && IsBlockRole(under.Role))
                {
                    float off = Mathf.Max(Mathf.Abs(local.x), Mathf.Abs(local.z));
                    if (off > FoundationHalfWidth)
                        return $"its spot lies {off:0.00} m from the centre of {entry.On}, past its " +
                               $"{FoundationHalfWidth:0.0} m half width";
                }
                else if (!OnFootprint(local, IsTrianglePrefab(rest.Prefab)))
                    return $"its spot lies {new Vector2(local.x, local.z).magnitude:0.00} m from the pivot of " +
                           $"{entry.On}, outside its top";
                if (local.y < -OnTolerance || local.y > OnMaxRise)
                    return $"it would stand {local.y:0.00} m from the top of {entry.On}; Y counts " +
                           (building != null ? $"from the base height of {building}" : "from that top");
                return null;
            }
            switch (entry.Role)
            {
                case RoleFoundation:
                case RolePlatform:
                {
                    float lowest;
                    TerrainSpan(position, rotation, out lowest);
                    if (position.y - lowest > FoundationMaxRise)
                        return $"terrain falls {position.y - lowest:0.00} m under the foundation, more than " +
                               $"{FoundationMaxRise} m";
                    string buried = BuriedUnder(position, rotation, false, aside);
                    return buried == null
                        ? null
                        : $"it would bury {buried}; farm.remove confirm and farm.build apply a layout that puts a " +
                          "foundation under standing structures";
                }
                case RoleFence:
                case RoleGate:
                    return null;
                case RolePump:
                {
                    Vector3 ground = FarmPoint(entry.X, entry.Z);
                    float water = TerrainMeta.WaterMap.GetHeight(ground);
                    return water > ground.y + entry.Y
                        ? null
                        : $"no water over the pump spot (water {water:0.00} m, ground {ground.y + entry.Y:0.00} m)";
                }
                default:
                    return _config.GroundCheckRoles.Contains(entry.Role)
                        ? CheckFootprint(entry.Prefab, position, rotation)
                        : null;
            }
        }

        // Role setup before Spawn, after the occupant check: the building the entity joins (a block's standing
        // neighbour, a new building of its own for a foundation without a Building, an insert's or a cupboard's
        // block, an On entry's block, or a building entry's parent or nearest block within 1.5 m), and a bed's
        // respawn owner.
        private string Prepare(LayoutEntry entry, BaseEntity entity, ulong owner, Rest rest)
        {
            var decay = entity as DecayEntity;
            if (IsBlockRole(entry.Role))
            {
                var block = entity as BuildingBlock;
                if (block == null)
                    return "prefab is not a BuildingBlock";
                block.AttachToBuilding(IsBuildingBlock(entry) ? StandingBlockOf(entry.Building) : null);
                return null;
            }
            if (entry.Role == RoleDoor || entry.Role == RoleInsert)
            {
                if (decay == null)
                    return "prefab is not a door or building insert";
                decay.AttachToBuilding(((DecayEntity)rest.Entity).buildingID);
                return null;
            }
            if (entry.Role == RoleCupboard)
            {
                if (!(entity is BuildingPrivlidge))
                    return "prefab is not a BuildingPrivlidge";
                decay.AttachToBuilding(((DecayEntity)rest.Entity).buildingID);
                return null;
            }
            string why = PrepareSpawn(entry, entity, owner);
            if (why != null || decay == null)
                return why;
            string building = BuildingOf(entry);
            if (!string.IsNullOrEmpty(entry.On))
            {
                LayoutEntry under = EntryById(entry.On);
                if (building != null || (under != null && IsBuildingBlock(under)))
                    decay.AttachToBuilding(((DecayEntity)rest.Entity).buildingID);
            }
            else if (building != null)
                decay.AttachToBuilding(rest?.Entity as DecayEntity);
            return null;
        }
        // World rotation = Euler(Pitch, farm Yaw + Yaw, Roll).
        private Quaternion EntryRotation(LayoutEntry entry) =>
            Quaternion.Euler(entry.Pitch, _data.Yaw + entry.Yaw, entry.Roll);

        // The first standing block of the building in layer order; BuildingBlock.AttachToBuilding(null) starts a new
        // building id, and a later block joining any piece of a split building merges the parts it links to.
        private BuildingBlock StandingBlockOf(string building)
        {
            BuildingBlock best = null;
            int bestLayer = 0;
            foreach (LayoutEntry entry in _config.Layout.Entries)
            {
                if (entry.Building != building || !IsBuildingBlock(entry))
                    continue;
                int layer = LayerOf(entry);
                if (best != null && layer >= bestLayer)
                    continue;
                StructureRecord rec = RecordById(entry.Id);
                if (rec == null || rec.NetId == 0 || rec.Missing)
                    continue;
                var block = ResolveStructure(rec) as BuildingBlock;
                if (block == null)
                    continue;
                best = block;
                bestLayer = layer;
            }
            return best;
        }

        // The first record with the Id, the one RecordsById keeps.
        private StructureRecord RecordById(string id) =>
            id == null ? null : _data.Structures.FirstOrDefault(r => r.Id == id);

        private DecayEntity ParentEntity(LayoutEntry entry)
        {
            if (string.IsNullOrEmpty(entry.Parent))
                return null;
            StructureRecord rec = _data.Structures.FirstOrDefault(r => r.Id == entry.Parent);
            return rec == null || rec.NetId == 0 || rec.Missing ? null : ResolveStructure(rec) as DecayEntity;
        }

        // Role setup before Spawn: a bed's respawn owner (SleepingBag.deployerUserID), which the planner's OnDeployed
        // sets and a plugin spawn skips.
        private static string PrepareSpawn(LayoutEntry entry, BaseEntity entity, ulong owner)
        {
            if (entry.Role != RoleBed)
                return null;
            var bag = entity as SleepingBag;
            if (bag == null)
                return "a bed entry's prefab is not a SleepingBag";
            bag.deployerUserID = owner;
            return null;
        }

        // Role setup after Spawn: a block's grade, skin, and full health (blocks spawn at 0 health), stone for a
        // foundation without a Building, the cupboard's owner, doors closed, Mount entries made children of their
        // parent (the planner does so for wall signs and picture frames), and signs painted.
        private void AfterSpawn(LayoutEntry entry, StructureRecord rec, BaseEntity entity, ulong owner)
        {
            BuildingBlock block = IsBlockRole(entry.Role) ? entity as BuildingBlock : null;
            BuildingGrade.Enum grade;
            ulong skin;
            if (block != null && TargetGrade(entry, out grade, out skin))
                ApplyGrade(block, rec, grade, skin, true);
            else if (block != null)
            {
                block.ChangeGrade(BuildingGrade.Enum.Stone);
                block.SetHealthToMax();
                block.SendNetworkUpdate();
            }
            Authorize(entity as BuildingPrivlidge, owner);
            (entity as Door)?.SetOpen(false);
            if (entry.Mount)
            {
                DecayEntity parent = ParentEntity(entry);
                if (parent != null)
                    entity.SetParent(parent, true);
                else
                    Failure($"mount:{entry.Id}", $"{entry.Id} could not be mounted: its parent {entry.Parent} is gone");
            }
            var sign = entity as Signage;
            if (sign != null && !string.IsNullOrEmpty(entry.Image))
                Guard("signs", entry.Id, () => PaintFarmSign(entry, sign, owner));
        }
        // Deployables and building blocks whose pivot lies inside the footprint below the top, such as a fridge a farm
        // placed on terrain before its layout gained a platform; ones standing on the top (the cupboard) do not count,
        // nor the parts in aside (what a farm.upgrade confirm lifts, moves, or retires) and what is parented to them.
        private string BuriedUnder(Vector3 top, Quaternion rotation, bool triangle, ICollection<ulong> aside)
        {
            float centreZ = triangle ? TriangleDepth / 2f : 0f;
            float depth = triangle ? TriangleDepth : 2f * FoundationHalfWidth;
            var box = new OBB(top, rotation, new Bounds(new Vector3(0f, -1.5f, centreZ),
                new Vector3(2f * FoundationHalfWidth, 2.8f, depth)));
            var found = new List<BaseEntity>();
            Vis.Entities(box, found, Rust.Layers.Mask.Deployed | Rust.Layers.Mask.Construction);
            var buried = new List<string>();
            foreach (BaseEntity entity in found)
            {
                // Item bags and loose items (farm.remove leaves them where containers stood) are not structures.
                if (entity == null || entity.IsDestroyed || !(entity is DecayEntity) || aside.Contains(IdOf(entity)))
                    continue;
                BaseEntity parent = entity.GetParentEntity();
                if (parent != null && aside.Contains(IdOf(parent)))
                    continue;
                Vector3 local = Quaternion.Inverse(rotation) * (entity.transform.position - top);
                if (OnFootprint(local, triangle) && local.y < -0.1f)
                    buried.Add(NameOf(entity));
            }
            return buried.Count > 0 ? string.Join(", ", buried) : null;
        }
        // Whether a block-local point lies over a square foundation or floor (pivot at the centre) or a triangle one
        // (pivot at the base edge's midpoint, apex along +Z).
        private static bool OnFootprint(Vector3 local, bool triangle)
        {
            const float slack = 0.01f;
            if (!triangle)
                return Mathf.Abs(local.x) <= FoundationHalfWidth + slack &&
                       Mathf.Abs(local.z) <= FoundationHalfWidth + slack;
            return local.z >= -slack && local.z <= TriangleDepth + slack &&
                   Mathf.Abs(local.x) <= FoundationHalfWidth * (1f - local.z / TriangleDepth) + slack;
        }

        // The standing farm foundation or floor whose footprint holds the point: the one nearest across, or with
        // byHeight the one whose top is nearest the point's height (a loft floor over a foundation).
        private BuildingBlock BlockUnder(Vector3 point, bool byHeight) => BlockUnder(point, byHeight, Surfaces());

        private static BuildingBlock BlockUnder(Vector3 point, bool byHeight, List<Surface> surfaces)
        {
            BuildingBlock best = null;
            float bestDistance = float.MaxValue;
            foreach (Surface surface in surfaces)
            {
                BuildingBlock block = surface.Block;
                Vector3 local = Quaternion.Inverse(block.transform.rotation) * (point - block.transform.position);
                float distance = byHeight ? Mathf.Abs(local.y) : new Vector2(local.x, local.z).magnitude;
                if (OnFootprint(local, surface.Triangle) && distance < bestDistance)
                {
                    bestDistance = distance;
                    best = block;
                }
            }
            return best;
        }

        // A standing farm foundation or floor, resolved once for any number of BlockUnder lookups.
        private class Surface
        {
            public BuildingBlock Block;
            public bool Triangle;
        }

        // In record order: BlockUnder keeps the first of two blocks at the same distance.
        private List<Surface> Surfaces()
        {
            var surfaces = new List<Surface>();
            foreach (StructureRecord rec in _data.Structures)
            {
                if (rec.NetId == 0 || rec.Missing || !IsSurfacePrefab(rec.Prefab))
                    continue;
                var block = ResolveStructure(rec) as BuildingBlock;
                if (block != null)
                    surfaces.Add(new Surface { Block = block, Triangle = IsTrianglePrefab(rec.Prefab) });
            }
            return surfaces;
        }

        private BuildingBlock StandingBlock(string id)
        {
            StructureRecord rec = _data.Structures.FirstOrDefault(r => r.Id == id && r.NetId != 0 && !r.Missing);
            return rec == null ? null : ResolveStructure(rec) as BuildingBlock;
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
            rec.Pending = false;
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
        // within > 0 asks for that distance in 3D instead: blocks and inserts stand socket-exact.
        private BaseEntity Adopt(StructureRecord rec, Vector3 spot, ulong owner, float within = 0f)
        {
            float bestDistance;
            BaseEntity best = FindAdoptable(rec.Prefab, spot, within, out bestDistance);
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

        private BaseEntity FindAdoptable(string prefab, Vector3 spot, float within, out float bestDistance)
        {
            bestDistance = within > 0f ? within : AdoptDistance;
            if (prefab == null)
                return null;
            var found = new List<BaseEntity>();
            Vis.Entities(spot, AdoptSearchRadius, found);
            BaseEntity best = null;
            foreach (BaseEntity entity in found)
            {
                if (entity == null || entity.IsDestroyed || !SamePrefab(entity, prefab) ||
                    _structureIds.Contains(IdOf(entity)) || (entity.OwnerID != 0 && !OwnerOk(entity.OwnerID)))
                    continue;
                Vector3 offset = entity.transform.position - spot;
                float distance = within > 0f ? offset.magnitude : new Vector2(offset.x, offset.z).magnitude;
                if (distance <= bestDistance && (within > 0f || Mathf.Abs(offset.y) <= AdoptHeight))
                {
                    best = entity;
                    bestDistance = distance;
                }
            }
            return best;
        }

        // Kills trees, bushes, and resource piles (dead logs, ore) whose colliders reach into the entity's base plus
        // 0.5 m; trees elsewhere in the pen stay.
        private void ClearVegetation(BaseEntity entity)
        {
            OBB box = Footprint(entity);
            var found = new List<BaseEntity>();
            Vis.Entities(box, found, Rust.Layers.Mask.Tree | Rust.Layers.Mask.Bush | Rust.Layers.Mask.World |
                                     Rust.Layers.Mask.Default | Rust.Layers.Mask.Harvestable);
            foreach (BaseEntity other in found)
            {
                if (other == null || other.IsDestroyed || other == entity || IsProtected(other) ||
                    !(other is TreeEntity || other is BushEntity || other is ResourceEntity))
                    continue;
                _clearedVegetation.Add(other.ShortPrefabName);
                other.Kill();
            }
        }

        // The entity's base (ClearingAreas, or its own bounds) plus 0.5 m, from just under the ground to 3 m up.
        private static OBB Footprint(BaseEntity entity) =>
            FootprintAt(entity.ShortPrefabName, entity.bounds, entity.transform.position, entity.transform.rotation);

        // Footprint for a prefab at a pose before it spawns, the box farm.upgrade's plan checks for occupants.
        private static OBB FootprintAt(string prefab, Vector3 position, Quaternion rotation)
        {
            GameObject go = string.IsNullOrEmpty(prefab) ? null : GameManager.server.FindPrefab(prefab);
            BaseEntity entity = go == null ? null : go.GetComponent<BaseEntity>();
            Bounds bounds = entity != null && entity.bounds.size.sqrMagnitude > 0f
                ? entity.bounds
                : new Bounds(new Vector3(0f, 0.5f, 0f), Vector3.one);
            return FootprintAt(PrefabName(prefab), bounds, position, rotation);
        }

        private static OBB FootprintAt(string shortName, Bounds bounds, Vector3 position, Quaternion rotation)
        {
            Vector4 area;
            Bounds local;
            if (ClearingAreas.TryGetValue(shortName, out area))
                local = new Bounds(new Vector3(area.x, 1.25f, area.y),
                    new Vector3(2f * (area.z + VegetationMargin), 3.5f, 2f * (area.w + VegetationMargin)));
            else
                local = new Bounds(bounds.center, bounds.size + Vector3.one * (2f * VegetationMargin));
            return new OBB(position, rotation, local);
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
        // With planters (farm.upgrade's new ones), only their slots count and clones already stored do not: the
        // planters the farm had keep their own stock.
        private void StockStarterClones(UpkeepPass pass, ICollection<string> planters = null)
        {
            List<ItemContainer> boxes = pass.Containers(BoxClones);
            foreach (KeyValuePair<string, int> crop in PlannedSlots(planters))
            {
                int want = 2 * crop.Value;
                int have = planters == null ? CountItems(boxes, crop.Key) : 0;
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

        private Dictionary<string, int> PlannedSlots(ICollection<string> planters = null)
        {
            var slots = new Dictionary<string, int>();
            foreach (List<string> plan in _config.SlotPlan.Where(p => p.Value != null &&
                                                                     !IsManualPlanter(p.Key) &&
                                                                     (planters == null || planters.Contains(p.Key)))
                         .Select(p => p.Value))
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
                    .OrderByDescending(r => LayerOfRecord(r))
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

        #region Buildings

        private const string GradeNames = "twigs, wood, stone, metal, toptier";

        private static bool TryParseGrade(string name, out BuildingGrade.Enum grade)
        {
            switch (name?.ToLowerInvariant())
            {
                case "twigs":
                    grade = BuildingGrade.Enum.Twigs;
                    return true;
                case "wood":
                    grade = BuildingGrade.Enum.Wood;
                    return true;
                case "stone":
                    grade = BuildingGrade.Enum.Stone;
                    return true;
                case "metal":
                    grade = BuildingGrade.Enum.Metal;
                    return true;
                case "toptier":
                    grade = BuildingGrade.Enum.TopTier;
                    return true;
                default:
                    grade = BuildingGrade.Enum.None;
                    return false;
            }
        }

        // Lower case, as ShortPrefabName is, so the prefab predicates and ClearingAreas match a path in any casing.
        private static string PrefabName(string prefab) =>
            string.IsNullOrEmpty(prefab) ? string.Empty : Path.GetFileNameWithoutExtension(prefab).ToLowerInvariant();

        private static bool IsFoundationPrefab(string prefab)
        {
            string name = PrefabName(prefab);
            return name == "foundation" || name == "foundation.triangle";
        }

        // Blocks another entry may stand On: foundations and floors, frames included.
        private static bool IsSurfacePrefab(string prefab)
        {
            string name = PrefabName(prefab);
            return IsFoundationPrefab(prefab) || name == "floor" || name == "floor.triangle" || name == "floor.frame" ||
                   name == "floor.triangle.frame";
        }

        private static bool IsTrianglePrefab(string prefab) => PrefabName(prefab).Contains("triangle");

        private static bool IsBlockRole(string role) =>
            role == RoleBlock || role == RoleFoundation || role == RolePlatform;

        // A block placed at its building's base height; a foundation or platform without a Building is not one.
        private static bool IsBuildingBlock(LayoutEntry entry) =>
            entry != null && IsBlockRole(entry.Role) && !string.IsNullOrEmpty(entry.Building);

        // Where an insert sits in its parent: doors, the garage door, and the hatch at the opening's pivot, shutters,
        // bars, and glass 1 m up the window wall; false for a prefab with no recipe.
        private static bool InsertOffset(string prefab, out Vector3 offset)
        {
            float turnStep;
            string opening = InsertOpening(prefab, out turnStep);
            offset = opening == "wall.window" ? new Vector3(0f, WindowInsertHeight, 0f) : Vector3.zero;
            return opening != null;
        }

        // The block an insert with a recipe fits and the turns it takes there: a door a wall.doorway, a double or
        // garage door a wall.frame, shutters, bars, and glass a wall.window, each at 0 or 180 degrees; the square hatch
        // a floor.frame at any multiple of 90. Null for a prefab without a recipe, whose turn PlaceInsert snaps to 90.
        private static string InsertOpening(string prefab, out float turnStep)
        {
            string name = PrefabName(prefab);
            turnStep = 180f;
            if (name.StartsWith("door.double.") || name == "wall.frame.garagedoor")
                return "wall.frame";
            if (name.StartsWith("door."))
                return "wall.doorway";
            if (name.StartsWith("shutter.") || name.StartsWith("wall.window."))
                return "wall.window";
            turnStep = 90f;
            return name.StartsWith("floor.ladder.hatch") ? "floor.frame" : null;
        }

        // Farm-local layout point, Y as written (above T within one building).
        private static Vector3 LayoutPoint(LayoutEntry entry) => new Vector3(entry.X, entry.Y, entry.Z);

        private static Dictionary<string, LayoutEntry> EntriesById(List<LayoutEntry> entries)
        {
            var byId = new Dictionary<string, LayoutEntry>();
            foreach (LayoutEntry entry in entries)
                if (entry != null && !string.IsNullOrEmpty(entry.Id) && !byId.ContainsKey(entry.Id))
                    byId[entry.Id] = entry;
            return byId;
        }

        private Dictionary<string, LayoutEntry> EntriesById() => EntriesById(_config.Layout.Entries);

        // An entry's Building, or its parent's when it has none (a door in a doorway is in the doorway's building).
        private static string BuildingOf(LayoutEntry entry, Dictionary<string, LayoutEntry> byId)
        {
            for (int depth = 0; entry != null && depth < 16; depth++)
            {
                if (!string.IsNullOrEmpty(entry.Building))
                    return entry.Building;
                LayoutEntry parent;
                if (string.IsNullOrEmpty(entry.Parent) || !byId.TryGetValue(entry.Parent, out parent))
                    return null;
                entry = parent;
            }
            return null;
        }

        private string BuildingOf(LayoutEntry entry) =>
            string.IsNullOrEmpty(entry.Building) && string.IsNullOrEmpty(entry.Parent)
                ? null
                : BuildingOf(entry, EntriesById());

        // T of a building, stored in the record when a piece of it is placed, and kept while one stands.
        private bool BuildingBase(string building, out float t, out string problem)
        {
            string how;
            if (!WorkOutBuildingBase(building, out t, out how))
            {
                problem = $"base height of building {building}: {how}";
                return false;
            }
            problem = null;
            float stored;
            if (!_data.Buildings.TryGetValue(building, out stored) || stored != t)
            {
                _data.Buildings[building] = t;
                Puts($"build: building {building} has base height {t:0.00} m ({how})");
            }
            return true;
        }

        private class BaseHeight
        {
            public bool Found;
            public float T;
            public string How;
        }

        // WorkOutBuildingBase, once per building while PlanUpgrade runs (_planBases).
        private bool PreviewBuildingBase(string building, out float t, out string how)
        {
            BaseHeight known;
            if (_planBases != null && _planBases.TryGetValue(building, out known))
            {
                t = known.T;
                how = known.How;
                return known.Found;
            }
            bool found = WorkOutBuildingBase(building, out t, out how);
            if (_planBases != null)
                _planBases[building] = new BaseHeight { Found = found, T = t, How = how };
            return found;
        }

        // A building's base height T: the stored value while a block of the building stands (with none, as
        // after a first foundation that failed or a building moved in the layout, T is worked out again); else the
        // pivot of a standing foundation of the building less its Y (one the record holds, or an untracked one at its
        // spot that farm.build would adopt); else the highest ground under any of its foundations less that
        // foundation's Y, refused when a foundation would stand 2.6 m or more over the lowest ground at its corners, as
        // the planner refuses it.
        private bool WorkOutBuildingBase(string building, out float t, out string how)
        {
            if (_data.Buildings.TryGetValue(building, out t) && StandingBlockOf(building) != null)
            {
                how = "stored";
                return true;
            }
            List<LayoutEntry> foundations = _config.Layout.Entries
                .Where(e => e.Building == building && IsFoundationPrefab(e.Prefab))
                .ToList();
            if (foundations.Count == 0)
            {
                how = "the layout has no foundation entry for it";
                return false;
            }
            foreach (LayoutEntry f in foundations)
            {
                BuildingBlock standing = StandingBlock(f.Id);
                if (standing == null)
                    continue;
                t = standing.transform.position.y - f.Y;
                how = $"{f.Id} stands";
                return true;
            }
            float highest = foundations.Max(f => GroundUnder(f, true) - f.Y);
            foreach (LayoutEntry f in foundations)
            {
                float distance;
                BaseEntity found = FindAdoptable(f.Prefab, FarmPoint(f.X, f.Z).WithY(highest + f.Y), 0f, out distance);
                // Place adopts a block only within AdoptBlockDistance; one farther off would not be adopted.
                if (found == null || distance > AdoptBlockDistance)
                    continue;
                t = found.transform.position.y - f.Y;
                how = $"an untracked {found.ShortPrefabName} stands at {f.Id}'s spot";
                return true;
            }
            t = highest;
            foreach (LayoutEntry f in foundations)
            {
                float rise = highest + f.Y - GroundUnder(f, false);
                if (rise < FoundationMaxRise)
                    continue;
                how = $"{f.Id} would stand {rise:0.00} m over the lowest ground at its corners, " +
                      $"{FoundationMaxRise} m or more";
                return false;
            }
            how = "terrain";
            return true;
        }

        // The highest ground under a foundation entry (5 x 5 over a square, the corners and centroid of a triangle),
        // or with highest false the lowest ground at its corners.
        private float GroundUnder(LayoutEntry foundation, bool highest)
        {
            Vector3 centre = FarmPoint(foundation.X, foundation.Z);
            Quaternion rotation = Quaternion.Euler(0f, _data.Yaw + foundation.Yaw, 0f);
            const float h = FoundationHalfWidth;
            Vector2[] points = IsTrianglePrefab(foundation.Prefab)
                ? highest
                    ? new[] { new Vector2(-h, 0f), new Vector2(h, 0f), new Vector2(0f, TriangleDepth),
                        new Vector2(0f, TriangleDepth / 3f) }
                    : new[] { new Vector2(-h, 0f), new Vector2(h, 0f), new Vector2(0f, TriangleDepth) }
                : highest
                    ? Grid(-h, h, -h, h, 5, 5)
                    : new[] { new Vector2(-h, -h), new Vector2(-h, h), new Vector2(h, -h), new Vector2(h, h) };
            float result = highest ? float.MinValue : float.MaxValue;
            foreach (Vector2 p in points)
            {
                float ground = TerrainMeta.HeightMap.GetHeight(centre + rotation * new Vector3(p.x, 0f, p.y));
                result = highest ? Mathf.Max(result, ground) : Mathf.Min(result, ground);
            }
            return result;
        }

        // The grade and skin the farm keeps a block at: the entry's Grade and Skin, else the config's Building grade
        // and, with that grade, its Building skin. Role block and every entry with a Building are graded; a
        // foundation or platform without a Building only when it sets Grade or Skin, and stays stone otherwise.
        private bool TargetGrade(LayoutEntry entry, out BuildingGrade.Enum grade, out ulong skin)
        {
            grade = BuildingGrade.Enum.Stone;
            skin = 0;
            if (entry.Role != RoleBlock && string.IsNullOrEmpty(entry.Building) && entry.Grade == null &&
                !entry.Skin.HasValue)
                return false;
            BuildingGrade.Enum fallback;
            if (!TryParseGrade(_config.BuildingGrade, out fallback))
                fallback = BuildingGrade.Enum.Wood;
            if (entry.Grade == null || !TryParseGrade(entry.Grade, out grade))
                grade = fallback;
            skin = entry.Skin ?? (grade == fallback ? _config.BuildingSkin : 0UL);
            return true;
        }

        // ChangeGradeAndSkin after Spawn, then SetHealthToMax after a spawn (blocks start at 0 health) or a grade
        // change. A grade the prefab lacks in that skin would fall back to twigs, so it gets
        // skin 0 instead.
        private void ApplyGrade(BuildingBlock block, StructureRecord rec, BuildingGrade.Enum grade, ulong skin,
            bool fresh)
        {
            if (skin != 0 && !HasGradeSkin(block, grade, skin))
            {
                if (_plainSkins.Count == 0)
                    NextTick(LogPlainSkins);
                string lacks = $"plain {grade}: {block.ShortPrefabName} has no {grade} grade in skin {skin}";
                List<string> ids;
                if (!_plainSkins.TryGetValue(lacks, out ids))
                    _plainSkins[lacks] = ids = new List<string>();
                if (!ids.Contains(rec.Id))
                    ids.Add(rec.Id);
                skin = 0;
            }
            rec.Grade = grade.ToString();
            rec.Skin = skin;
            bool regrade = block.grade != grade;
            if (regrade || block.skinID != skin)
                block.ChangeGradeAndSkin(grade, skin);
            if (fresh || regrade)
                block.SetHealthToMax();
            block.SendNetworkUpdate();
        }

        private static bool HasGradeSkin(BuildingBlock block, BuildingGrade.Enum grade, ulong skin) =>
            block.blockDefinition?.grades != null &&
            block.blockDefinition.grades.Any(g => g.gradeBase.type == grade && g.gradeBase.skin == skin);

        // One line per missing skin after the call that graded the blocks, naming each; the same blocks again within
        // the failure repeat interval log nothing.
        private void LogPlainSkins()
        {
            foreach (KeyValuePair<string, List<string>> pair in _plainSkins)
            {
                string ids = string.Join(", ", pair.Value);
                Failure($"skin:{ids}:{pair.Key}", $"{ids} got {pair.Key}");
            }
            _plainSkins.Clear();
        }

        // Puts back the grade and skin the farm gave a block, which BuildingSkins and admin commands change with no
        // hook to veto. Returns what it was, or null when nothing changed. A block whose layout entry is gone or no
        // longer grades it is open to players (IsGuarded), so the farm forgets its grade instead of reverting a paid
        // upgrade.
        private string RestoreGrade(BuildingBlock block, StructureRecord rec, LayoutEntry entry)
        {
            BuildingGrade.Enum grade;
            ulong skin;
            if (rec == null || rec.Grade == null)
                return null;
            if (entry == null || !TargetGrade(entry, out grade, out skin))
            {
                rec.Grade = null;
                rec.Skin = null;
                return null;
            }
            if (!Enum.TryParse(rec.Grade, out grade))
                return null;
            skin = rec.Skin ?? 0UL;
            if (block.grade == grade && block.skinID == skin)
                return null;
            string was = $"{block.grade} {block.skinID}";
            bool regrade = block.grade != grade;
            block.ChangeGradeAndSkin(grade, skin);
            if (regrade)
                block.SetHealthToMax();
            block.SendNetworkUpdate();
            return was;
        }

        private void LogRestores(Dictionary<string, List<string>> restored)
        {
            foreach (KeyValuePair<string, List<string>> pair in restored)
                Failure($"regrade:{pair.Key}", $"restored the grade and skin of {pair.Value.Count} blocks of " +
                                               $"{pair.Key}: {string.Join(", ", pair.Value)}; later restores of " +
                                               "this building log at most hourly", "repair", 60f);
        }

        // An entry with a Parent goes with it: a door the stability guard keeps, or a panel the ground veto
        // keeps, in place of a lost parent is removed, and auto-repair brings it back after the parent. A container
        // with items stays, so nothing is destroyed.
        private void RemoveOrphans(string parentId)
        {
            StructureRecord parent = _data.Structures.FirstOrDefault(r => r.Id == parentId);
            if (parent != null && parent.NetId != 0 && !parent.Missing)
                return;
            foreach (LayoutEntry child in _config.Layout.Entries.Where(e => e.Parent == parentId))
            {
                StructureRecord rec = _data.Structures.FirstOrDefault(r => r.Id == child.Id && r.NetId != 0 &&
                                                                           !r.Missing);
                BaseEntity entity = rec == null ? null : ResolveStructure(rec);
                if (entity == null || entity.IsDestroyed)
                    continue;
                ItemContainer inventory = InventoryOf(entity);
                int plants = PlantCount(entity);
                int animals = (entity as ChickenCoop)?.Animals.Count ?? 0;
                string holds = inventory != null && inventory.itemList.Count > 0 ? Contents(inventory)
                    : plants > 0 ? $"{plants} plants"
                    : animals > 0 ? $"{animals} chickens or hatching eggs"
                    : null;
                if (holds != null)
                {
                    Failure($"orphan:{child.Id}", $"{child.Id} stays although its parent {parentId} is gone: it " +
                                                  $"holds {holds}");
                    continue;
                }
                // Missing and untracked before the kill, so OnEntityKill counts no loss and auto-repair no churn; its
                // own children go first, while they still stand.
                rec.Missing = true;
                _structureIds.Remove(rec.NetId);
                RemoveOrphans(child.Id);
                try
                {
                    entity.Kill();
                }
                catch (Exception)
                {
                    if (!entity.IsDestroyed)
                    {
                        rec.Missing = false;
                        _structureIds.Add(rec.NetId);
                    }
                    throw;
                }
                Puts($"structures: removed {child.Id} with its parent {parentId}; it comes back after its parent");
            }
        }

        private void PaintFarmSign(LayoutEntry entry, Signage sign, ulong owner)
        {
            byte[] png = SignPng(entry.Image);
            string problem = png == null ? $"the plugin carries no image {entry.Image}" : PaintSign(sign, png, owner);
            if (problem != null)
                Failure($"sign:{entry.Id}", $"{entry.Id} ({PrefabName(entry.Prefab)}) was not painted with " +
                                            $"{entry.Image}: {problem}");
        }

        private LayoutEntry EntryById(string id) =>
            id == null ? null : _config.Layout.Entries.FirstOrDefault(e => e.Id == id);

        // Where Place would put the entry now, without spawning or adopting, for farm.upgrade's plan and moves:
        // Locate and PlaceCheck as Place runs them, with a support that does not stand yet taken at its own layout
        // pose and the parts in aside (lifted, moved, or retired by the confirm) left out of the burial check. With
        // adopt, an untracked match standing at the pose passes, since Place adopts it; a moving part does not.
        private bool EntryPose(LayoutEntry entry, ICollection<ulong> aside, bool adopt, out Vector3 position,
            out Quaternion rotation, out string problem)
        {
            Rest rest;
            if (!Locate(entry, false, 0, out position, out rotation, out rest, out problem))
                return false;
            problem = PrefabProblem(entry);
            if (problem != null)
                return false;
            float distance;
            if (adopt && FindAdoptable(entry.Prefab, position, AdoptWithin(entry), out distance) != null)
                return true;
            problem = PlaceCheck(entry, false, position, rotation, rest, aside);
            return problem == null;
        }

        // What SpawnStructure and Prepare would refuse about the entry's prefab (none such, or the wrong type for its
        // role), read from the prefab so the plan refuses it before confirm places anything.
        private static string PrefabProblem(LayoutEntry entry)
        {
            GameObject go = string.IsNullOrEmpty(entry.Prefab) ? null : GameManager.server.FindPrefab(entry.Prefab);
            BaseEntity prefab = go == null ? null : go.GetComponent<BaseEntity>();
            if (prefab == null)
                return $"prefab {entry.Prefab} did not create an entity";
            if (IsBlockRole(entry.Role) && !(prefab is BuildingBlock))
                return "prefab is not a BuildingBlock";
            if ((entry.Role == RoleDoor || entry.Role == RoleInsert) && !(prefab is DecayEntity))
                return "prefab is not a door or building insert";
            if (entry.Role == RoleCupboard && !(prefab is BuildingPrivlidge))
                return "prefab is not a BuildingPrivlidge";
            if (entry.Role == RoleBed && !(prefab is SleepingBag))
                return "a bed entry's prefab is not a SleepingBag";
            return null;
        }

        // The box an entry's prefab fills at a pose: its BaseEntity bounds (a foundation's reach 2.8 m down from its
        // top), or a 1 m cube on the pivot for a prefab without them.
        private static OBB EntryBox(LayoutEntry entry, Vector3 position, Quaternion rotation)
        {
            GameObject prefab = string.IsNullOrEmpty(entry.Prefab) ? null : GameManager.server.FindPrefab(entry.Prefab);
            BaseEntity entity = prefab == null ? null : prefab.GetComponent<BaseEntity>();
            Bounds bounds = entity != null && entity.bounds.size.sqrMagnitude > 0f
                ? entity.bounds
                : new Bounds(new Vector3(0f, 0.5f, 0f), Vector3.one);
            return new OBB(position, rotation, bounds);
        }

        // Moves a standing farm entity to a pose with what is parented to it (coop chickens, planted crops, a trough's
        // water end), joins a deployable to the building of the block within 1.5 m of its new pivot (none: id 0) as
        // DecayEntity.AttachToBuilding(null) does, queues navmesh rebuilds where it stood and where it stands, and
        // sends network updates. The caller runs Physics.SyncTransforms() once per batch of moves.
        private static void MoveEntity(BaseEntity entity, Vector3 position, Quaternion rotation)
        {
            Bounds before = entity.WorldSpaceBounds().ToBounds();
            entity.transform.SetPositionAndRotation(position, rotation);
            var decay = entity as DecayEntity;
            if (decay != null && !(decay is BuildingBlock))
            {
                BuildingBlock block = decay.GetNearbyBuildingBlock();
                decay.AttachToBuilding(block != null ? block.buildingID : 0u);
            }
            if (decay != null && !ConVar.AI.useUnityNavmesh)
            {
                Rust.Ai.Gen2.Nav.RustNavigation.Instance.RebuildTilesInBounds(before);
                Rust.Ai.Gen2.Nav.RustNavigation.Instance.RebuildTilesInBounds(entity.WorldSpaceBounds().ToBounds());
            }
            SendMoved(entity);
        }

        private static void SendMoved(BaseEntity entity)
        {
            entity.UpdateNetworkGroup();
            entity.SendNetworkUpdateImmediate();
            foreach (BaseEntity child in entity.children.ToList())
                if (child != null && !child.IsDestroyed)
                    SendMoved(child);
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

        // Convex hull of the recorded fence and gate rail ends in world x/z, over the rails that bound the fence face
        // around the herd point. A garden or chicken run built against the pen shares rails and joints with it but
        // not that face, so it stays out. When the rails close no face around the herd point (a gap), the hull falls
        // back to the smallest fence line, joined at its rail ends, whose hull holds the herd point.
        private List<Vector2> PenHull()
        {
            if (_penHull != null)
                return _penHull;
            var rails = new List<Vector2[]>();
            foreach (StructureRecord rec in _data.Structures)
            {
                if (rec.Role != RoleFence && rec.Role != RoleGate)
                    continue;
                // A rail never placed counts at its layout pose, so its gap does not split the line into smaller pens.
                LayoutEntry planned = rec.NetId == 0 ? EntryById(rec.Id) : null;
                if (rec.NetId == 0 && planned == null)
                    continue;
                float yaw = (planned != null ? _data.Yaw + planned.Yaw : rec.Yaw) * Mathf.Deg2Rad;
                Vector2 along = new Vector2(Mathf.Sin(yaw), Mathf.Cos(yaw)) * RailHalfLength;
                Vector3 at = planned != null ? FarmPoint(planned.X, planned.Z) : new Vector3(rec.X, 0f, rec.Z);
                var centre = new Vector2(at.x, at.z);
                rails.Add(new[] { centre + along, centre - along });
            }
            Vector3 herd = FarmPoint(_config.HerdPoint.X, _config.HerdPoint.Z);
            List<int> face = FaceAround(rails, new Vector2(herd.x, herd.z));
            if (face != null)
                return _penHull = ConvexHull(face.SelectMany(i => rails[i]).ToList());
            var lines = new List<List<Vector2>>();
            foreach (Vector2[] ends in rails)
            {
                var rail = new List<Vector2>(ends);
                List<List<Vector2>> joined = lines.Where(line => line.Any(p => rail.Any(q =>
                    (p - q).sqrMagnitude < RailJoin * RailJoin))).ToList();
                foreach (List<Vector2> line in joined)
                {
                    rail.AddRange(line);
                    lines.Remove(line);
                }
                lines.Add(rail);
            }
            List<List<Vector2>> hulls = lines.Select(ConvexHull).ToList();
            List<Vector2> pen = hulls.Where(h => InsidePen(herd, h)).OrderBy(HullArea).FirstOrDefault();
            _penHull = pen ?? ConvexHull(lines.SelectMany(line => line).ToList());
            return _penHull;
        }

        // The rails bounding the fence face that holds the point, or null when they close none around it. Rail ends
        // within RailJoin meet at one joint. The first rail a ray from the point towards +x crosses bounds the face;
        // walking it with the point on the left and turning as far left as possible at every joint goes once around
        // the face, past rails that branch off outwards (a garden's fence starting at a pen joint).
        private static List<int> FaceAround(List<Vector2[]> rails, Vector2 point)
        {
            int count = rails.Count * 2;
            var joint = new int[count];
            for (int i = 0; i < count; i++)
                joint[i] = i;
            Func<int, int> find = i =>
            {
                while (joint[i] != i)
                    i = joint[i] = joint[joint[i]];
                return i;
            };
            for (int i = 0; i < count; i++)
                for (int j = i + 1; j < count; j++)
                    if ((rails[i / 2][i % 2] - rails[j / 2][j % 2]).sqrMagnitude < RailJoin * RailJoin)
                        joint[find(i)] = find(j);
            var sum = new Dictionary<int, Vector2>();
            var ends = new Dictionary<int, int>();
            for (int i = 0; i < count; i++)
            {
                int at = find(i);
                Vector2 total;
                int n;
                sum.TryGetValue(at, out total);
                ends.TryGetValue(at, out n);
                sum[at] = total + rails[i / 2][i % 2];
                ends[at] = n + 1;
            }
            var pos = sum.ToDictionary(p => p.Key, p => p.Value / ends[p.Key]);
            var edges = new Dictionary<int, KeyValuePair<int, int>>();
            var around = new Dictionary<int, List<int>>();
            for (int k = 0; k < rails.Count; k++)
            {
                int a = find(2 * k), b = find(2 * k + 1);
                if (a == b)
                    continue;
                edges[k] = new KeyValuePair<int, int>(a, b);
                foreach (int end in new[] { a, b })
                {
                    List<int> list;
                    if (!around.TryGetValue(end, out list))
                        around[end] = list = new List<int>();
                    list.Add(k);
                }
            }
            int start = -1;
            float nearest = float.MaxValue;
            foreach (KeyValuePair<int, KeyValuePair<int, int>> edge in edges)
            {
                Vector2 pa = pos[edge.Value.Key], pb = pos[edge.Value.Value];
                if ((pa.y > point.y) == (pb.y > point.y))
                    continue;
                float x = pa.x + (point.y - pa.y) / (pb.y - pa.y) * (pb.x - pa.x);
                if (x > point.x && x < nearest)
                {
                    nearest = x;
                    start = edge.Key;
                }
            }
            if (start < 0)
                return null;
            int from = edges[start].Key, to = edges[start].Value;
            if (Cross(pos[from], pos[to], point) < 0f)
            {
                from = edges[start].Value;
                to = edges[start].Key;
            }
            var face = new List<int> { start };
            var corners = new List<Vector2> { pos[from] };
            int rail = start, came = from, here = to;
            for (int step = 0; ; step++)
            {
                if (step > 4 * edges.Count)
                    return null;
                Vector2 heading = pos[here] - pos[came];
                int next = -1, then = -1;
                float best = float.MinValue;
                foreach (int k in around[here])
                {
                    if (k == rail && around[here].Count > 1)
                        continue;
                    int other = edges[k].Key == here ? edges[k].Value : edges[k].Key;
                    Vector2 leg = pos[other] - pos[here];
                    float turn = Mathf.Atan2(heading.x * leg.y - heading.y * leg.x, Vector2.Dot(heading, leg));
                    if (turn > best)
                    {
                        best = turn;
                        next = k;
                        then = other;
                    }
                }
                corners.Add(pos[here]);
                if (next == start && here == from)
                    break;
                face.Add(next);
                rail = next;
                came = here;
                here = then;
            }
            corners.RemoveAt(corners.Count - 1);
            float area = 0f;
            int winding = 0;
            for (int i = 0; i < corners.Count; i++)
            {
                Vector2 a = corners[i], b = corners[(i + 1) % corners.Count];
                area += Cross(Vector2.zero, a, b);
                if (a.y <= point.y && b.y > point.y && Cross(a, b, point) > 0f)
                    winding++;
                else if (b.y <= point.y && a.y > point.y && Cross(a, b, point) < 0f)
                    winding--;
            }
            return area > 0f && winding != 0 ? face.Distinct().ToList() : null;
        }

        private static float HullArea(List<Vector2> hull)
        {
            float area = 0f;
            for (int i = 0; i < hull.Count; i++)
                area += Cross(Vector2.zero, hull[i], hull[(i + 1) % hull.Count]);
            return Mathf.Abs(area) / 2f;
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
            public bool FarmOccupied;
            public bool AbsenceFallback;
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
            // Net ids of records farm.upgrade retired but could not empty; they take no new items (TendCoop still feeds
            // the chickens a retired coop holds).
            public readonly HashSet<ulong> Retired = new HashSet<ulong>();

            public List<BaseEntity> Role(string role)
            {
                List<BaseEntity> list;
                return Roles.TryGetValue(role, out list) ? list : NoEntities;
            }

            public List<ItemContainer> Containers(string role) =>
                Role(role).Where(e => !Retired.Contains(IdOf(e))).Select(InventoryOf).Where(c => c != null).ToList();

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

        // record false (farm.status, the farm.upgrade dry run) only reads: a vanished structure or animal is left out
        // of the pass, and the next upkeep pass records and logs it.
        private UpkeepPass BeginPass(bool record = true)
        {
            var pass = new UpkeepPass { Owner = FarmOwner() };
            pass.FarmOccupied = FarmHasPlayers();
            pass.AbsenceFallback = AbsenceDue(Epoch.Current, _data.LastFarmVisit, _config.AbsenceMinutes,
                pass.FarmOccupied);
            float now = Time.realtimeSinceStartup;
            pass.Elapsed = _lastUpkeepAt < 0f ? 0f : now - _lastUpkeepAt;
            foreach (StructureRecord rec in _data.Structures)
            {
                if (rec.NetId == 0 || rec.Missing)
                    continue;
                BaseEntity entity = ResolveStructure(rec);
                if (entity == null && !record)
                    continue;
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
                if (rec.Retired)
                    pass.Retired.Add(rec.NetId);
            }
            // Several containers of a role fill in layout order, the first with room first; records the layout no
            // longer names come after.
            var index = new Dictionary<string, int>();
            for (int i = 0; i < _config.Layout.Entries.Count; i++)
                if (_config.Layout.Entries[i].Id != null && !index.ContainsKey(_config.Layout.Entries[i].Id))
                    index[_config.Layout.Entries[i].Id] = i;
            foreach (string role in pass.Roles.Keys.ToList())
                pass.Roles[role] = pass.Roles[role]
                    .OrderBy(e =>
                    {
                        int at;
                        return index.TryGetValue(pass.Name(e) ?? string.Empty, out at) ? at : int.MaxValue;
                    })
                    .ToList();
            pass.Cupboard = pass.Role(RoleCupboard).OfType<BuildingPrivlidge>().FirstOrDefault();
            var lost = new List<ulong>();
            foreach (KeyValuePair<ulong, string> pair in _animals.ToList())
            {
                LivestockAnimal animal = ResolveAnimal(pair.Key, pair.Value);
                if (animal == null && record)
                {
                    _animals.Remove(pair.Key);
                    lost.Add(pair.Key);
                }
                if (animal != null)
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
            Dictionary<string, LayoutEntry> entries = EntriesById();
            var restored = new Dictionary<string, List<string>>();
            var orphaned = new HashSet<string>();
            foreach (StructureRecord rec in _data.Structures)
            {
                BaseEntity entity = rec.NetId == 0 || rec.Missing ? null : pass.ById(rec.Id);
                if (entity == null || IdOf(entity) != rec.NetId)
                    continue;
                LayoutEntry entry;
                entries.TryGetValue(rec.Id, out entry);
                var block = entity as BuildingBlock;
                if (block != null && rec.Grade != null)
                    Guard("structures", rec.Id, () =>
                    {
                        string was = RestoreGrade(block, rec, entry);
                        if (was == null)
                            return;
                        string building = entry?.Building ?? rec.Id;
                        List<string> list;
                        if (!restored.TryGetValue(building, out list))
                            restored[building] = list = new List<string>();
                        list.Add($"{rec.Id} (was {was})");
                    });
                var sign = entity as Signage;
                if (sign != null && entry?.Image != null && SignCheckDue(rec.NetId))
                    Guard("signs", rec.Id, () => KeepSignPainted(entry, sign, pass.Owner));
                var bag = entity as SleepingBag;
                if (bag != null && rec.Role == RoleBed && pass.Owner != 0 &&
                    (bag.deployerUserID != pass.Owner || bag.IsPublic()))
                    Guard("structures", rec.Id, () => KeepBedOwned(rec.Id, bag, pass.Owner));
                if (!string.IsNullOrEmpty(entry?.Parent) && pass.ById(entry.Parent) == null)
                    orphaned.Add(entry.Parent);
            }
            LogRestores(restored);
            foreach (string parent in orphaned)
                Guard("structures", $"children of {parent}", () => RemoveOrphans(parent));
        }

        // The farm bed respawns the owner: one made public, claimed, or handed to a friend is made private and assigned
        // back.
        private void KeepBedOwned(string id, SleepingBag bag, ulong owner)
        {
            var was = new List<string>();
            if (bag.IsPublic())
            {
                was.Add("public");
                bag.SetPublic(false);
            }
            if (bag.deployerUserID != owner)
            {
                was.Add(bag.deployerUserID == 0 ? "unassigned" : $"assigned to {OwnerLabel(bag.deployerUserID)}");
                bag.AssignToUser(owner);
            }
            Puts($"bed: gave {id} back to owner {OwnerLabel(owner)}; it was {string.Join(" and ", was)}");
        }

        private bool SignCheckDue(ulong id)
        {
            float now = Time.realtimeSinceStartup;
            float last;
            if (_signCheckedAt.TryGetValue(id, out last) && now - last < SignCheckSeconds)
                return false;
            _signCheckedAt[id] = now;
            return true;
        }

        // A farm sign whose image row is gone (lost from FileStorage, or cleared by a CRC clash at load, which also
        // clears the lock) is painted and locked again; a lost lock alone is set again.
        private void KeepSignPainted(LayoutEntry entry, Signage sign, ulong owner)
        {
            byte[] png = SignPng(entry.Image);
            if (png == null || owner == 0)
                return;
            if (!SignPainted(sign, png))
            {
                string problem = PaintSign(sign, png, owner);
                if (problem == null)
                    Event("repair", $"repainted {entry.Id} with {entry.Image}: its image was gone");
                else
                    Failure($"sign:{entry.Id}", $"{entry.Id} could not be repainted with {entry.Image}: {problem}");
                return;
            }
            if (SignLocked(sign, owner))
                return;
            LockSign(sign, owner);
            Puts($"signs: locked {entry.Id} to the owner again");
        }

        private void CupboardMissing() =>
            Failure("cupboard", "cupboard missing, run farm.repair: animals fall back to the herd point as their " +
                                "home, and reseeding and the starter herd wait until it is back");

        // Builds every layout connection that is not in place, and re-makes one a player or decay removed.
        // A link naming an entry the farm never placed (a layout switched in before farm.upgrade confirm or
        // farm.repair places it) waits without a log line; a placed end that is lost or failed still logs.
        private void WiringStep(UpkeepPass pass)
        {
            Dictionary<string, StructureRecord> records = RecordsById();
            foreach (LayoutConnection link in _config.Layout.Connections)
            {
                StructureRecord end;
                if (!records.TryGetValue(link.From ?? string.Empty, out end) || NotYetPlaced(end) ||
                    !records.TryGetValue(link.To ?? string.Empty, out end) || NotYetPlaced(end))
                    continue;
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
        }

        // Null when the link is already in place, empty when this call made it, otherwise why it cannot be made.
        private string WireProblem(LayoutConnection link, UpkeepPass pass)
        {
            IOEntity from = IoEnd(pass.ById(link.From));
            IOEntity to = IoEnd(pass.ById(link.To));
            if (from == null || to == null)
                return $"{(from == null ? link.From : link.To)} is missing or not an IO entity";
            if (link.FromSlot < 0 || link.FromSlot >= from.outputs.Length || link.ToSlot < 0 ||
                link.ToSlot >= to.inputs.Length)
                return "no such IO slot";
            IOEntity.IOSlot output = from.outputs[link.FromSlot];
            IOEntity.IOSlot input = to.inputs[link.ToSlot];
            bool connected = output.connectedTo.Get() == to && output.connectedToSlot == link.ToSlot;
            List<Vector3> points;
            string routeProblem = CachedWirePoints(link, from, to, pass, connected, out points);
            if (connected)
            {
                if (routeProblem != null)
                    Failure($"wire-route:{link}", $"wiring {link}: {routeProblem}; its drawing is kept");
                else if (link.Route != null && !SameWirePoints(output, points))
                {
                    output.linePoints = points.ToArray();
                    output.slackLevels = new float[points.Count];
                    output.lineAnchors = new IOEntity.LineAnchor[0];
                    output.originPosition = from.transform.position;
                    output.originRotation = from.transform.rotation.eulerAngles;
                    output.worldSpaceLineEndRotation = to.transform.TransformDirection(input.handleDirection);
                    from.SendNetworkUpdate();
                    from.NotifyClientsLineChanged();
                    Puts($"wiring: routed {link} along {points.Count} points");
                }
                return null;
            }
            if (output.connectedTo.Get() != null || input.connectedTo.Get() != null)
                return "a slot is already connected elsewhere";
            if (output.type != input.type)
                return $"slot types differ ({output.type} to {input.type})";
            var pump = from as WaterPump;
            if (pump != null &&
                WaterResource.SV_GetAtPoint(pump.WaterResourceLocation.position) != WaterTypes.WaterItemDef)
                return "the pump would deliver salt or radioactive water, which empties or kills the planters; " +
                       "left unwired";
            if (!Connect(from, link.FromSlot, to, link.ToSlot, points))
                return "ConnectTo refused";
            if (routeProblem != null)
                Failure($"wire-route:{link}", $"wiring {link}: {routeProblem}; wired straight until it is back");
            return string.Empty;
        }

        // Each link's last computed drawing with the poses it came from. A connected link reuses it while its ends
        // and anchors are the same entities at the same poses; colliders placed under a ground run since are not
        // noticed until then.
        private readonly Dictionary<LayoutConnection, WireDrawing> _wireDrawings =
            new Dictionary<LayoutConnection, WireDrawing>();

        private class WireDrawing
        {
            public List<Vector3> Points;
            public List<BaseEntity> Entities;
            public List<Vector3> Positions;
            public List<Quaternion> Rotations;

            public bool Matches(List<BaseEntity> entities)
            {
                if (entities.Count != Entities.Count)
                    return false;
                for (int i = 0; i < entities.Count; i++)
                    if (entities[i] == null || entities[i].IsDestroyed || entities[i] != Entities[i] ||
                        entities[i].transform.position != Positions[i] ||
                        entities[i].transform.rotation != Rotations[i])
                        return false;
                return true;
            }
        }

        private string CachedWirePoints(LayoutConnection link, IOEntity from, IOEntity to, UpkeepPass pass,
            bool reuse, out List<Vector3> points)
        {
            var entities = new List<BaseEntity> { from, to };
            foreach (WirePoint point in link.Route ?? new List<WirePoint>())
                if (point.Anchor != null)
                    entities.Add(pass.ById(point.Anchor));
            WireDrawing drawing;
            if (reuse && _wireDrawings.TryGetValue(link, out drawing) && drawing.Matches(entities))
            {
                points = drawing.Points;
                return null;
            }
            string problem = WirePoints(link, from, to, pass, out points);
            if (problem != null)
            {
                _wireDrawings.Remove(link);
                return problem;
            }
            _wireDrawings[link] = new WireDrawing
            {
                Points = points, Entities = entities,
                Positions = entities.Select(e => e.transform.position).ToList(),
                Rotations = entities.Select(e => e.transform.rotation).ToList(),
            };
            return null;
        }

        // Rust stores drawing points in output-local space, starting at the input. Its LineAnchor type is only
        // for moving door bones; fixed surfaces use ordinary points resolved from their supporting entities.
        // On a missing anchor the points are the straight drawing of a link without a route.
        private string WirePoints(LayoutConnection link, IOEntity from, IOEntity to, UpkeepPass pass,
            out List<Vector3> points)
        {
            points = new List<Vector3>();
            Vector3 start = from.transform.TransformPoint(from.outputs[link.FromSlot].handlePosition);
            Vector3 end = to.transform.TransformPoint(to.inputs[link.ToSlot].handlePosition);
            var world = new List<Vector3> { start };
            bool ground = false;
            float lift = 0f;
            foreach (WirePoint point in link.Route ?? new List<WirePoint>())
            {
                Vector3 next;
                if (point.Anchor == null)
                {
                    next = FarmPoint(point.X, point.Z);
                    next.y = WireRest(next, next.y + WireRestRise, from, to) + point.Y;
                    if (world.Count == 1)
                    {
                        world.Add(start.WithY(WireRest(start, start.y, from, to) + point.Y));
                        ground = true;
                    }
                    if (ground)
                        AddGroundSamples(world, next, point.Y, from, to);
                    ground = true;
                    lift = point.Y;
                }
                else
                {
                    BaseEntity anchor = pass.ById(point.Anchor);
                    if (anchor == null || anchor.IsDestroyed)
                    {
                        points.Add(from.transform.InverseTransformPoint(end));
                        points.Add(from.transform.InverseTransformPoint(start));
                        return $"route anchor {point.Anchor} is missing";
                    }
                    next = anchor.transform.TransformPoint(new Vector3(point.X, point.Y, point.Z));
                    ground = false;
                }
                world.Add(next);
            }
            if (ground)
            {
                Vector3 foot = end.WithY(WireRest(end, end.y, from, to) + lift);
                AddGroundSamples(world, foot, lift, from, to);
                world.Add(foot);
            }
            world.Add(end);
            for (int i = world.Count - 1; i >= 0; i--)
                points.Add(from.transform.InverseTransformPoint(world[i]));
            return null;
        }

        // Wires and hoses lie on blocks (decks, foundations), deployables (planters), rocks, and terrain.
        private const int WireRestMask = Rust.Layers.Mask.Construction | Rust.Layers.Mask.Deployed |
                                         Rust.Layers.Mask.Terrain | Rust.Layers.Mask.World;
        // How far above the higher end of a ground segment, or above the terrain at a waypoint, the ray starts, so
        // that it lands on a planter or deck the run crosses instead of starting inside it.
        private const float WireRestRise = 1f;

        // Points every at most 2 m from the last point to next, each on what lies under it.
        private static void AddGroundSamples(List<Vector3> world, Vector3 next, float lift, IOEntity from,
            IOEntity to)
        {
            Vector3 previous = world[world.Count - 1];
            float top = Mathf.Max(previous.y, next.y) + WireRestRise;
            int steps = Mathf.CeilToInt(Vector3.Distance(previous, next) / 2f);
            for (int i = 1; i < steps; i++)
            {
                Vector3 sample = Vector3.Lerp(previous, next, (float)i / steps);
                world.Add(sample.WithY(WireRest(sample, top, from, to) + lift));
            }
        }

        // The highest surface under point from height top down, never below the terrain and never the two entities
        // the link joins (a trough counts as its water end). A heater's own parent, the deck, is a surface.
        private static float WireRest(Vector3 point, float top, IOEntity from, IOEntity to)
        {
            float rest = TerrainMeta.HeightMap.GetHeight(point);
            if (top <= rest)
                return rest;
            foreach (RaycastHit hit in Physics.RaycastAll(point.WithY(top), Vector3.down, top - rest + 0.1f,
                         WireRestMask, QueryTriggerInteraction.Ignore))
            {
                IOEntity end = IoEnd(hit.GetEntity());
                if (end != null && (end == from || end == to))
                    continue;
                rest = Mathf.Max(rest, hit.point.y);
            }
            return rest;
        }

        private static bool SameWirePoints(IOEntity.IOSlot output, List<Vector3> points) =>
            output.linePoints != null && output.linePoints.Length == points.Count &&
            output.slackLevels != null && output.slackLevels.Length == points.Count &&
            (output.lineAnchors == null || output.lineAnchors.Length == 0) &&
            points.Select((point, i) => (point - output.linePoints[i]).sqrMagnitude < 0.0001f &&
                                       output.slackLevels[i] == 0f).All(same => same);

        private string WireStatus(LayoutConnection link, UpkeepPass pass)
        {
            if (!IsWired(link, pass))
                return "NOT connected";
            if (link.Route == null)
                return "connected";
            IOEntity from = IoEnd(pass.ById(link.From));
            IOEntity to = IoEnd(pass.ById(link.To));
            List<Vector3> points;
            string problem = CachedWirePoints(link, from, to, pass, true, out points);
            if (problem != null)
                return $"connected; {problem}";
            IOEntity.IOSlot output = from.outputs[link.FromSlot];
            if (!SameWirePoints(output, points))
                return "connected; route needs updating";
            float length = 0f;
            for (int i = 1; i < points.Count; i++)
                length += Vector3.Distance(points[i - 1], points[i]);
            return $"connected; routed {points.Count} points, {length:0.0} m";
        }

        private static bool Connect(IOEntity from, int output, IOEntity to, int input, List<Vector3> points)
        {
            if (from.outputs[output].connectedTo.Get() != null || to.inputs[input].connectedTo.Get() != null)
                return false;
            from.ConnectTo(to, output, input, points, Enumerable.Repeat(0f, points.Count).ToList(),
                new IOEntity.LineAnchor[0],
                WireTool.WireColour.Gray);
            return true;
        }

        private const string LanternPrefab = "assets/prefabs/deployable/lantern/lantern.deployed.prefab";
        private const string LightSwitchPrefab = "assets/prefabs/deployable/playerioents/simpleswitch/switch.prefab";

        private static void ValidateLightingConfig(Configuration config, List<string> problems)
        {
            CheckRange(problems, "Farm lights sunrise hour", config.LightSunrise, 0f, 24f);
            CheckRange(problems, "Farm lights sunset hour", config.LightSunset, 0f, 24f);
            if (config.LightSunrise == 24f || config.LightSunset == 24f)
                problems.Add("Farm light hours must be less than 24");
            if (config.LightSunrise >= config.LightSunset)
                problems.Add("Farm lights sunrise hour must be earlier than sunset hour");
            if (string.IsNullOrWhiteSpace(config.LightSwitchId))
                problems.Add("Farm lighting switch layout ID must not be empty");
            if (config.ScheduledLightIds == null || config.ScheduledLightIds.Any(string.IsNullOrWhiteSpace) ||
                config.ScheduledLightIds.Distinct().Count() != config.ScheduledLightIds.Count)
            {
                problems.Add("Scheduled decorative light layout IDs must be a non-null list of unique non-empty IDs");
                return;
            }
            // ConfigProblems reports a missing Layout.
            if (config.Layout?.Entries == null)
                return;
            var entries = new Dictionary<string, LayoutEntry>();
            foreach (LayoutEntry entry in config.Layout.Entries)
                if (!string.IsNullOrEmpty(entry?.Id) && !entries.ContainsKey(entry.Id))
                    entries[entry.Id] = entry;
            LayoutEntry lightSwitch;
            if (!string.IsNullOrWhiteSpace(config.LightSwitchId))
            {
                if (!entries.TryGetValue(config.LightSwitchId, out lightSwitch))
                    problems.Add($"Farm lighting switch layout ID {config.LightSwitchId} names no layout entry");
                else if (!string.Equals(lightSwitch.Prefab, LightSwitchPrefab, StringComparison.OrdinalIgnoreCase))
                    problems.Add($"Farm lighting switch {config.LightSwitchId} must be a {LightSwitchPrefab} entry");
            }
            foreach (string id in config.ScheduledLightIds)
            {
                LayoutEntry light;
                if (!entries.TryGetValue(id, out light))
                    problems.Add($"Scheduled decorative light {id} names no layout entry");
                else if (!string.Equals(light.Prefab, LanternPrefab, StringComparison.OrdinalIgnoreCase))
                    problems.Add($"Scheduled decorative light {id} must be a {LanternPrefab} entry");
            }
            var scheduled = new HashSet<string>(config.ScheduledLightIds);
            foreach (LayoutEntry entry in entries.Values)
                if (string.Equals(entry.Prefab, LanternPrefab, StringComparison.OrdinalIgnoreCase) &&
                    !scheduled.Contains(entry.Id))
                    problems.Add($"lantern {entry.Id} is missing from Scheduled decorative light layout IDs");
        }

        private bool FarmLightsWanted()
        {
            float hour = TOD_Sky.Instance.Cycle.Hour;
            return LightsAtHour(hour, _config.LightSunrise, _config.LightSunset);
        }

        private static bool LightsAtHour(float hour, float sunrise, float sunset) =>
            hour >= sunset || hour < sunrise;

        // Net ids of the scheduled lights' live records, rebuilt at most every ScheduledLightsRefresh seconds: Night
        // Lantern asks once per light, several a second at dusk and dawn. A killed light leaves _structureIds at once.
        private readonly HashSet<ulong> _scheduledLights = new HashSet<ulong>();
        private float _scheduledLightsAt = float.NegativeInfinity;
        private const float ScheduledLightsRefresh = 5f;

        private bool IsScheduledFarmLight(BaseEntity entity)
        {
            if (!_config.ScheduleLighting || entity == null || _data == null || !_data.Built || _disabled != null)
                return false;
            ulong id = IdOf(entity);
            if (!_structureIds.Contains(id))
                return false;
            float now = Time.realtimeSinceStartup;
            if (now - _scheduledLightsAt >= ScheduledLightsRefresh)
            {
                var ids = new HashSet<string>(_config.ScheduledLightIds);
                _scheduledLights.Clear();
                foreach (StructureRecord rec in _data.Structures)
                    if (!rec.Retired && !rec.Missing && rec.NetId != 0 && rec.Id != null && ids.Contains(rec.Id))
                        _scheduledLights.Add(rec.NetId);
                _scheduledLightsAt = now;
            }
            return _scheduledLights.Contains(id);
        }

        private object OnNightLanternToggle(BaseEntity entity, bool enabled) =>
            IsScheduledFarmLight(entity) ? False : null;

        private void LightingStep(UpkeepPass pass)
        {
            if (!_config.ScheduleLighting)
            {
                UnlockScheduledLights();
                return;
            }
            if (TOD_Sky.Instance == null)
                return;
            bool wanted = FarmLightsWanted();
            ElectricSwitch lightSwitch = pass.ById(_config.LightSwitchId) as ElectricSwitch;
            if (lightSwitch != null && lightSwitch.IsOn() != wanted)
                lightSwitch.SetSwitch(wanted);
            else if (lightSwitch == null)
                Failure("lighting:switch", "scheduled barn lighting awaits the lighting switch layout upgrade");
            foreach (string id in _config.ScheduledLightIds)
            {
                BaseEntity entity = pass.ById(id);
                if (entity == null || pass.Retired.Contains(IdOf(entity)))
                    continue;
                Guard("lighting", id, () =>
                {
                    BaseFuelLightSource lantern = entity as BaseFuelLightSource;
                    if (lantern == null)
                    {
                        Failure("lighting:" + id, $"{id} is not a controllable lantern; layout upgrade required");
                        return;
                    }
                    if (lantern.inventory == null)
                    {
                        Failure("lighting:" + id, $"{id} has no fuel inventory");
                        return;
                    }
                    // Locked, the fuel the farm creates can only burn: players can neither take nor add any.
                    if (!lantern.inventory.IsLocked())
                        lantern.inventory.SetLocked(true);
                    if (!wanted && lantern.IsOn())
                        lantern.StopCooking();
                    else if (wanted)
                    {
                        EnsureLightFuel(lantern.inventory, "lowgradefuel", id);
                        if (!lantern.IsOn())
                            lantern.StartCooking();
                    }
                });
            }
        }

        // Players fuel the lanterns themselves while the schedule is off or the plugin is unloaded.
        private void UnlockScheduledLights()
        {
            if (_data == null || _config?.ScheduledLightIds == null)
                return;
            var ids = new HashSet<string>(_config.ScheduledLightIds);
            foreach (StructureRecord rec in _data.Structures)
            {
                var lantern = rec.Id != null && ids.Contains(rec.Id)
                    ? FindEntity(rec.NetId) as BaseFuelLightSource
                    : null;
                if (lantern?.inventory != null && lantern.inventory.IsLocked())
                    lantern.inventory.SetLocked(false);
            }
        }

        private void EnsureLightFuel(ItemContainer inventory, string shortname, string id)
        {
            if (inventory.itemList.Any(i => i.info.shortname == shortname && !i.isBroken && i.amount > 0))
                return;
            Item fuel = ItemManager.CreateByName(shortname, 1);
            if (fuel == null)
            {
                Failure("lighting:" + id, $"{id} cannot create {shortname}");
                return;
            }
            if (!fuel.MoveToContainer(inventory))
            {
                fuel.Remove();
                Failure("lighting:" + id, $"{id} fuel inventory cannot accept {shortname}");
                return;
            }
            Event("topup", $"{id}: {shortname} 0 below 1, created {shortname} x1");
        }

        private string FarmLightingSummary(UpkeepPass pass)
        {
            if (!_config.ScheduleLighting)
                return "manual (schedule disabled)";
            if (TOD_Sky.Instance == null)
                return "waiting for world time";
            List<BaseFuelLightSource> lanterns = _config.ScheduledLightIds.Select(id => pass.ById(id))
                .OfType<BaseFuelLightSource>().Where(e => !pass.Retired.Contains(IdOf(e))).ToList();
            List<CeilingLight> barnLights = pass.Role(RoleDecor).OfType<CeilingLight>()
                .Where(e => !pass.Retired.Contains(IdOf(e))).ToList();
            string pending = lanterns.Count != _config.ScheduledLightIds.Count ||
                             !(pass.ById(_config.LightSwitchId) is ElectricSwitch)
                ? "; lighting upgrade incomplete"
                : string.Empty;
            return $"{(FarmLightsWanted() ? "night" : "day")}; yard lanterns {lanterns.Count(e => e.IsOn())}/" +
                   $"{_config.ScheduledLightIds.Count} on; barn lights {barnLights.Count(e => e.IsOn())}/" +
                   $"{barnLights.Count} on{pending}";
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

        private static void ValidateAssistedConfig(Configuration config, List<string> problems)
        {
            CheckRange(problems, "Assisted farming absence fallback after minutes", config.AbsenceMinutes, 0f);
            if (config.ManualPlanterIds == null || config.ManualPlanterIds.Any(string.IsNullOrWhiteSpace) ||
                config.ManualPlanterIds.Distinct().Count() != config.ManualPlanterIds.Count)
            {
                problems.Add("Player-managed planter layout IDs must be a non-null list of unique non-empty IDs");
                return;
            }
            foreach (string id in config.ManualPlanterIds)
                if (config.Layout?.Entries == null ||
                    !config.Layout.Entries.Any(e => e != null && e.Id == id && e.Role == RolePlanter))
                    problems.Add($"player-managed planter {id} must name a planter layout entry");
        }

        private bool IsManualPlanter(string id) => _config.ManualPlanterIds.Contains(id);

        private static string PlayerBedName(string id) =>
            id == "planter-07" ? "07 (east of scarecrow)" :
            id == "planter-08" ? "08 (south of scarecrow)" : id;

        private static bool AbsenceDue(long now, long lastVisit, float minutes, bool occupied) =>
            !occupied && minutes > 0f && lastVisit > 0 && now >= lastVisit && now - lastVisit >= minutes * 60d;

        private bool FarmHasPlayers()
        {
            Vector3 centre = FarmCentre();
            float radiusSquared = _config.FarmRadius * _config.FarmRadius;
            return BasePlayer.activePlayerList.Any(player => player != null && player.IsConnected &&
                !player.IsNpc && !player.IsDead() && !player.IsSleeping() &&
                (player.transform.position - centre).sqrMagnitude <= radiusSquared);
        }

        private bool _absenceFallbackActive;

        private void AttendanceStep(UpkeepPass pass)
        {
            long now = Epoch.Current;
            if (pass.FarmOccupied || _data.LastFarmVisit <= 0 || _data.LastFarmVisit > now)
                _data.LastFarmVisit = now;
            pass.AbsenceFallback = AbsenceDue(now, _data.LastFarmVisit, _config.AbsenceMinutes, pass.FarmOccupied);
            bool active = AbsenceAutomationActive(pass);
            if (active == _absenceFallbackActive)
                return;
            _absenceFallbackActive = active;
            Event("care", active
                ? "absence care active: milking and shearing resume; ripe player crops go to the feed store"
                : "absence care ended: configured player jobs are reserved again");
        }

        private bool AbsenceAutomationActive(UpkeepPass pass) =>
            (!_config.AutoMilk || !_config.AutoShear || _config.ManualPlanterIds.Count > 0) && pass.AbsenceFallback;

        private bool AutoMilkNow(UpkeepPass pass) => _config.AutoMilk || AbsenceAutomationActive(pass);

        private bool AutoShearNow(UpkeepPass pass) => _config.AutoShear || AbsenceAutomationActive(pass);

        private string AssistedCareSummary(UpkeepPass pass)
        {
            string grace = _config.AbsenceMinutes <= 0f ? "disabled" : $"{_config.AbsenceMinutes:0.##} min";
            return $"milk {(_config.AutoMilk ? "automatic" : "manual")}, " +
                   $"shear {(_config.AutoShear ? "automatic" : "manual")}, " +
                   $"culling {(_config.AutoCull ? "enabled" : "disabled")}; " +
                   $"player beds {string.Join(", ", _config.ManualPlanterIds)}; " +
                   $"absence care {(AbsenceAutomationActive(pass) ? "active" : "inactive")}, grace {grace}, " +
                   $"farm occupied {pass.FarmOccupied}, last visit {_data.LastFarmVisit}";
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
                    if (cow != null && AutoMilkNow(pass))
                        Milk(cow, pass);
                    var sheep = animal as Sheep;
                    if (sheep != null && AutoShearNow(pass))
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

        private static bool CowReadyToMilk(Cow cow) =>
            cow != null && !cow.IsDestroyed && !cow.IsDead() && !cow.IsInfant() && !cow.IsMale &&
            cow.HasFlag(Cow.MilkReady) && cow.MilkDef != null;

        private static bool SheepReadyToShear(Sheep sheep) =>
            sheep != null && !sheep.IsDestroyed && !sheep.IsDead() && !sheep.IsInfant() &&
            sheep.ShornQuadrants != LivestockFleece.AllQuadrants && sheep.ShearItems != null &&
            sheep.ShearItems.Any(item => item?.itemDef != null && sheep.UnshornAmount(item) > 0);

        // Cow.TryMilk without the player: milk goes to storage, or stays on the cow when there is no room.
        private void Milk(Cow cow, UpkeepPass pass)
        {
            if (!AutoMilkNow(pass) || cow == null || cow.IsDestroyed || cow.IsDead() || cow.IsInfant() ||
                cow.IsMale || !cow.HasFlag(Cow.MilkReady))
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
            if (!AutoShearNow(pass) || !SheepReadyToShear(sheep))
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
            if (!_config.AutoCull)
            {
                _cullBlocked.Clear();
                _cullWaiting.Clear();
                return;
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
            if (!_config.AutoCull)
            {
                _cullBlocked.Remove(species);
                _cullWaiting.Remove(species);
                return;
            }
            List<LivestockAnimal> herd = Herd(pass, species);
            int cap = CapOf(species);
            int count = CapCount(herd);
            if (count <= cap)
            {
                _cullBlocked.Remove(species);
                _cullWaiting.Remove(species);
                return;
            }
            string reason;
            bool due;
            LivestockAnimal victim = PickCull(herd, cap, a => !_data.CapExempt.Contains(IdOf(a)), out reason, out due);
            if (victim == null && !due)
            {
                _cullBlocked.Remove(species);
                if (_cullWaiting.Add(species))
                    Event("cull", $"{species} at {count}/{cap}: {reason}");
                return;
            }
            _cullWaiting.Remove(species);
            if (victim == null)
            {
                Failure($"cull:{species}", $"{species} at {count}/{cap}, but {reason}", "cull");
                _cullBlocked[species] = reason;
                HoldBreeding(herd);
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
                _cullBlocked[species] = $"no room in storage for the yield of {Describe(victim)}";
                HoldBreeding(herd);
                return;
            }
            _cullBlocked.Remove(species);
            Cull(victim, reason, species, count, cap);
        }

        // Without culling, mating stops at the cap including expected young. With culling, it stops at the cap plus
        // BirthOvershoot, and while a cull that is due cannot run (no room for the yield, or no adult it may cull). A
        // pregnancy begun one head short can still bring twins, and several can begin within one pass, so births can
        // pass the cap by a little more than BirthOvershoot. Null when mating may go on.
        private string BreedingHold(string species, List<LivestockAnimal> herd)
        {
            int heads = CapCount(herd);
            List<LivestockAnimal> pregnant = herd.Where(a => a.IsAdult() && a.IsFemale && a.IsPregnant()).ToList();
            int young = pregnant.Sum(MaxLitter);
            int cap = CapOf(species);
            if (!_config.AutoCull)
                return heads + young >= cap
                    ? $"automatic culling is disabled; {heads} heads and {pregnant.Count} pregnancies of up to " +
                      $"{young} young reach the cap of {cap}"
                    : null;
            string blocked;
            if (_cullBlocked.TryGetValue(species, out blocked))
                return $"a cull is due but cannot run: {blocked}";
            return heads + young >= cap + BirthOvershoot
                ? $"{heads} heads and {pregnant.Count} pregnancies of up to {young} young reach the cap of {cap} " +
                  $"plus {BirthOvershoot}"
                : null;
        }

        // LivestockAnimal.LitterSize rolls 1 plus the Fertility gene's excess over 1, rounded up or down at random,
        // capped at livestock.maxlittersize, and rolls again for every young, so this is the most one birth brings.
        private static int MaxLitter(LivestockAnimal mother) =>
            Mathf.Clamp(1 + Mathf.CeilToInt(Mathf.Max(0f, mother.GeneScale(LivestockGene.Fertility) - 1f)), 1,
                Mathf.Max(1, ConVar.Livestock.maxLitterSize));

        // Open adult females get a short breeding cooldown, renewed every pass while the hold lasts (CanBePregnant
        // needs none running); it lapses within HoldPasses upkeep intervals after, a longer cooldown of the animal's
        // own is left alone, and pregnancies already begun go on.
        private void HoldBreeding(List<LivestockAnimal> herd)
        {
            float hold = HoldPasses * Mathf.Max(5f, _config.UpkeepSeconds);
            foreach (LivestockAnimal female in herd.Where(a => a.IsAdult() && a.IsFemale && !a.IsPregnant()))
                if (!female.breedCooldown.IsRunning || female.breedCooldown.Remaining < hold)
                    female.breedCooldown.Start(hold);
        }

        // Who goes when a species is over its cap. Juveniles never go: calf, calfmale, and lamb corpses yield nothing.
        // While the heads exceed the cap, an adult male beyond one goes, oldest first; while the adults alone exceed
        // it, the oldest open adult female goes while another adult female stands. A herd over its cap only by its
        // juveniles waits for them to grow up; due says a cull is called for but cannot run, which holds mating. Never
        // a pregnant or calving female, never the last adult of a sex, never a cap-exempt reseeded animal (eligible).
        private static LivestockAnimal PickCull(List<LivestockAnimal> herd, int cap,
            Func<LivestockAnimal, bool> eligible, out string reason, out bool due)
        {
            List<LivestockAnimal> counted = herd.Where(eligible).ToList();
            List<LivestockAnimal> adultMales = counted.Where(a => a.IsAdult() && a.IsMale).ToList();
            List<LivestockAnimal> adultFemales = counted.Where(a => a.IsAdult() && a.IsFemale).ToList();
            int adults = adultMales.Count + adultFemales.Count;
            due = false;
            if (counted.Count > cap && adultMales.Count > 1)
            {
                reason = $"oldest of {adultMales.Count} adult males, one of which is kept";
                return adultMales.OrderBy(LifeLeft).First();
            }
            if (adults > cap)
            {
                LivestockAnimal female = adultFemales.Count > 1
                    ? adultFemales.Where(f => !f.IsPregnant() && !f.IsCarrying).OrderBy(LifeLeft).FirstOrDefault()
                    : null;
                if (female != null)
                {
                    reason = $"oldest open adult female, with {adults} adults over the cap of {cap}";
                    return female;
                }
                due = true;
                reason = adultFemales.Count <= 1
                    ? $"its {adults} adults exceed the cap of {cap}, but {adultMales.Count} adult male and " +
                      $"{adultFemales.Count} adult female are never culled"
                    : $"its {adults} adults exceed the cap of {cap}, but every other adult female is pregnant or " +
                      "calving";
                return null;
            }
            reason = $"its {counted.Count - adults} juveniles put it over the cap, so it waits for them to grow up " +
                     "(a juvenile's corpse yields nothing)";
            return null;
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

        private void Cull(LivestockAnimal victim, string reason, string species, int count, int cap)
        {
            ulong id = IdOf(victim);
            string description = Describe(victim);
            _pendingCulls[id] = new PendingCull { Description = description, Since = Time.realtimeSinceStartup };
            string age = victim.IsAdult()
                ? $"{Duration(LifeLeft(victim))} of life left"
                : $"{Duration(LifeLeft(victim))} from growing up";
            Event("cull", $"{description}, {age}: {reason}; {species} {count}/{cap}");
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
                Guard("breeding", species, () =>
                {
                    List<LivestockAnimal> herd = Herd(pass, species);
                    string hold = BreedingHold(species, herd);
                    string was;
                    _breedHeld.TryGetValue(species, out was);
                    if (hold != null)
                    {
                        HoldBreeding(herd);
                        if (was == null)
                            Event("breeding", $"{species}: mating held: {hold}");
                        _breedHeld[species] = hold;
                    }
                    else if (was != null)
                    {
                        _breedHeld.Remove(species);
                        Event("breeding", $"{species}: mating no longer held");
                    }
                    Breed(species, pass, now);
                });
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
                Guard("crops", pass.Name(entity), () =>
                {
                    if (pass.Retired.Contains(IdOf(entity)))
                        HarvestOnly(entity, pass);
                    else
                        TendPlanter(entity, pass, planned);
                });
            foreach (BaseEntity entity in pass.Role(RolePlanterStatic))
                Guard("crops", pass.Name(entity), () => HarvestOnly(entity, pass));
        }

        // A respawning static planter (PlanterBoxStatic.CreateStaticPlants, every respawnCheckTimer seconds) fills its
        // own soil water and plants a fertilized, god-quality crop with favourable genes in every free slot, so the
        // farm only harvests it: ripe plants go to the feed box the way a planter's do, and a dying plant is cleared,
        // which frees its slot for the next vanilla planting. A retired planter is emptied the same way.
        private void HarvestOnly(BaseEntity entity, UpkeepPass pass)
        {
            string id = pass.Name(entity);
            var planter = entity as PlanterBox;
            if (planter == null)
            {
                Failure($"type:{id}", $"{id} is not a planter");
                return;
            }
            List<GrowableEntity> plants = planter.children.OfType<GrowableEntity>()
                .Where(p => p != null && !p.IsDestroyed && !p.IsDead())
                .OrderBy(p => p.transform.localPosition.x)
                .ThenBy(p => p.transform.localPosition.z)
                .ToList();
            for (int i = 0; i < plants.Count; i++)
            {
                if (plants[i].State == PlantProperties.State.Ripe)
                    HarvestPlant(plants[i], id, i, pass);
                else if (plants[i].State == PlantProperties.State.Dying)
                    ClearDyingPlant(plants[i], id, i, pass);
            }
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
            TendSoil(planter, id, pass);
            Fertilize(planter, id, pass);
            GrowableEntity[] plants = PlantsBySlot(planter);
            if (IsManualPlanter(id))
            {
                if (AbsenceAutomationActive(pass))
                    for (int i = 0; i < plants.Length; i++)
                        if (plants[i] != null && plants[i].State == PlantProperties.State.Ripe)
                            HarvestPlant(plants[i], id, i, pass, preserveLifecycle: true);
                return;
            }
            List<string> plan;
            if (!_config.SlotPlan.TryGetValue(id, out plan) || plan == null)
            {
                Failure($"plan:{id}", $"{id} has no entry in the slot plan, so nothing is planted in it");
                return;
            }
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
            IOEntity from = IoEnd(pass.ById(link.From));
            IOEntity to = IoEnd(pass.ById(link.To));
            return from != null && to != null && link.FromSlot >= 0 && link.FromSlot < from.outputs.Length &&
                   from.outputs[link.FromSlot].connectedTo.Get() == to;
        }

        // The IO entity a connection names: any entry's entity that is one, whatever its role, or a trough's water
        // end, a LiquidContainer child where water connections land.
        private static IOEntity IoEnd(BaseEntity entity) =>
            entity as IOEntity ?? (entity as HitchTrough)?.WaterStorage;

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
        private void HarvestPlant(GrowableEntity plant, string id, int slot, UpkeepPass pass,
            bool preserveLifecycle = false)
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
            if (!preserveLifecycle && !plant.Properties.disappearAfterHarvest && plant.Properties.removeDyingItem != null)
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
            if (preserveLifecycle)
            {
                // PickFruit's counters and state transitions, after the produce has safely reached storage.
                plant.harvests++;
                plant.ResetSeason();
                if (plant.harvests < plant.Properties.maxHarvests)
                    plant.ChangeState(PlantProperties.State.Mature, resetAge: true);
                else if (!plant.Properties.disappearAfterHarvest)
                    plant.ChangeState(PlantProperties.State.Dying, resetAge: true);
                else
                {
                    plant.TellPlanter();
                    plant.Die();
                }
                return;
            }
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
            if (pass.Retired.Contains(IdOf(composter)))
                return;
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

        #region Bees, chickens, troughs, spoiled produce

        private void BeeStep(UpkeepPass pass)
        {
            foreach (StructureRecord rec in _data.Structures.Where(r => !r.Retired && !r.Missing))
            {
                Beehive hive = pass.ById(rec.Id) as Beehive;
                if (hive == null || rec.NetId == 0 || rec.BeeColonySeededNetId == rec.NetId)
                    continue;
                Guard("bees", rec.Id, () =>
                {
                    Item colony = hive.inventory.GetSlot(0);
                    if (colony != null)
                    {
                        if (colony.info.GetComponent<ItemModBeehiveNucleus>() != null)
                            rec.BeeColonySeededNetId = rec.NetId;
                        else
                            Failure("bees:" + rec.Id, $"{rec.Id} cannot start a colony: its nucleus slot is occupied");
                        return;
                    }
                    Item nucleus = ItemManager.Create(hive.BeeNucleusDefinition, 1);
                    if (nucleus == null)
                    {
                        Failure("bees:" + rec.Id, $"{rec.Id} could not create a starter nucleus");
                        return;
                    }
                    if (!nucleus.MoveToContainer(hive.inventory, 0, false))
                    {
                        nucleus.Remove();
                        Failure("bees:" + rec.Id, $"{rec.Id} could not accept a starter nucleus");
                        return;
                    }
                    rec.BeeColonySeededNetId = rec.NetId;
                    Event("bees", $"{rec.Id}: started a colony; honeycomb grows through the native hive cycle");
                });
            }
        }

        private List<Beehive> FarmHives(UpkeepPass pass) =>
            _data.Structures.Where(r => !r.Retired && !r.Missing)
                .Select(r => pass.ById(r.Id)).OfType<Beehive>().ToList();

        private static string BeeState(Beehive hive)
        {
            Item colony = hive.inventory.GetSlot(0);
            if (colony == null || colony.info.GetComponent<ItemModBeehiveNucleus>() == null)
                return "no colony; place a bee nucleus in the first slot";
            if (!hive.IsOutsideAccurate())
                return "indoors; production requires outdoor placement";
            if (hive.inventory.IsFull(true))
                return "full; collect honeycomb to resume production";
            return hive.IsOn() ? $"colony active; next comb {Mathf.Clamp01(hive.createNewCombAccumulator):P0}; " +
                                 "cold and rain slow production"
                : "colony stopped; inspect the hive";
        }

        private string BeeSummary(UpkeepPass pass)
        {
            List<Beehive> hives = FarmHives(pass);
            if (hives.Count == 0)
                return "Bees: no farm hive present.";
            int comb = hives.Sum(h => h.inventory.GetAmount(h.HoneyCombDefinition.itemid));
            return $"Bees: {comb} honeycomb ready in the hive. " +
                   string.Join("; ", hives.Select(BeeState)) +
                   ". Leave the nucleus in place; collect honeycomb and use Extract for honey.";
        }

        private void AppendBeeStatus(StringBuilder sb, UpkeepPass pass)
        {
            sb.AppendLine("bees:");
            foreach (Beehive hive in FarmHives(pass))
            {
                Item nucleus = hive.inventory.GetSlot(0);
                string grade = nucleus?.info.GetComponent<ItemModBeehiveNucleus>() == null ? "none"
                    : NucleusGrading.GetGradeAsString(NucleusGrading.XpToGrade(nucleus.instanceData?.dataInt ?? 0));
                sb.AppendLine($"  {pass.Name(hive)} [{IdOf(hive)}]: {BeeState(hive)}; nucleus grade {grade}, " +
                              $"honeycomb {hive.inventory.GetAmount(hive.HoneyCombDefinition.itemid)}, " +
                              $"temperature {hive.CalculateTemperature():0.0} C, rain {hive.CalculateRain():0.00}, " +
                              $"native growth rate {hive.growthRate:0.000}");
            }
        }

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
            bool retired = pass.Retired.Contains(IdOf(coop));
            if (!retired || coop.Animals.Count > 0)
            {
                StockFood(coop.inventory, ChickenCoop.FoodSlot, IsCoopFood, _config.CoopFeedTarget,
                    _config.CoopFeedMinimum, pass, $"{label} food slot", true);
                StockWater(coop.inventory, ChickenCoop.WaterSlot, _config.CoopWaterTarget, _config.CoopWaterMinimum,
                    pass, $"{label} water slot");
            }
            foreach (ChickenCoop.AnimalStatus status in coop.Animals)
            {
                FarmableAnimal chicken = status.SpawnedAnimal.Get(true);
                if (chicken != null && !chicken.IsDead())
                    TendChicken(chicken, label, pass);
            }
            if (!retired)
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
                    if (pass.Retired.Contains(IdOf(trough)))
                        return;
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
            List<ChickenCoop> coops = pass.Role(RoleCoop).OfType<ChickenCoop>()
                .Where(c => !pass.Retired.Contains(IdOf(c))).ToList();
            List<Composter> composters = pass.Role(RoleComposter).OfType<Composter>()
                .Where(c => !pass.Retired.Contains(IdOf(c))).ToList();
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
                if (role.Key == RoleCoop || role.Key == RoleComposter || role.Key == RolePlanter ||
                    role.Key == RolePlanterStatic)
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
            // A reserved slot (ChickenCoop's laying slot) holds only what the entity puts there; a hen's next egg is
            // destroyed when that slot is full.
            if (container.slotIsReserved != null && container.slotIsReserved(item, slot))
                return 0;
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
            _signCheckedAt.Clear();
            _cullBlocked.Clear();
            _cullWaiting.Clear();
            _breedHeld.Clear();
            _penHull = null;
            _relinkWanted = false;
            _recordChecked = false;
            // The first pass of a new record counts no time from the last pass of the old one.
            _lastUpkeepAt = -1f;
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

        #region Upgrade

        private const string MilkShortname = "milk";
        // A kept entry standing farther than this across from its layout spot is reported as kept where it stands.
        private const float KeptDistance = 1f;
        // How far over a block's top (pivot plane) the pivot of something standing on it sits.
        private const float StandsOnHeight = 0.3f;
        // Old parts in a new entry's way rise this far while the new entries are placed, all in one call.
        private const float StashHeight = 300f;
        // A kept part is in a new entry's footprint only when it reaches this far into the entry's box, so parts that
        // merely touch it (fence rails end to end) are not.
        private const float KeptSlack = 0.2f;
        // ChickenCoop.ServerInit's trace for IsOnTerrain: construction and terrain.
        private const int CoopGroundMask = Rust.Layers.Mask.Construction | Rust.Layers.Mask.Terrain;

        private static readonly HashSet<ulong> NoIds = new HashSet<ulong>();

        // Corpse yield shortnames of the herd prefabs, read once from their corpse dispensers.
        private HashSet<string> _cullYield;

        // What farm.upgrade would do or did. A Live plan (confirm, after the placement and the moves) counts only
        // standing targets.
        private class UpgradePlan
        {
            public UpkeepPass Pass;
            public bool Live;
            public readonly List<LayoutEntry> Place = new List<LayoutEntry>();
            // Entries step 1 places whose pose checks pass; moves may count on their room.
            public readonly HashSet<string> Placing = new HashSet<string>();
            public readonly List<string> PlaceLines = new List<string>();
            // The box each entry step 1 places, or a part moves to, fills at its pose.
            public readonly List<KeyValuePair<LayoutEntry, OBB>> Boxes = new List<KeyValuePair<LayoutEntry, OBB>>();
            public readonly List<UpgradeShift> Shifts = new List<UpgradeShift>();
            // Net ids of the old parts that move to the entries replacing them.
            public readonly Dictionary<ulong, LayoutEntry> Shifting = new Dictionary<ulong, LayoutEntry>();
            public readonly List<UpgradeStash> Stash = new List<UpgradeStash>();
            public readonly List<UpgradeRetire> Retire = new List<UpgradeRetire>();
            public readonly HashSet<ulong> Retiring = new HashSet<ulong>();
            // Net ids the confirm lifts, moves, or retires: BuriedUnder leaves them out, as the confirm does.
            public readonly HashSet<ulong> Aside = new HashSet<ulong>();
            // The occupant box SpawnStructure checks, at each pose step 1 places or a part moves to.
            public readonly List<KeyValuePair<LayoutEntry, OBB>> Footprints =
                new List<KeyValuePair<LayoutEntry, OBB>>();
            public readonly List<StructureRecord> Gone = new List<StructureRecord>();
            // Retired records whose Id the layout names again.
            public readonly List<StructureRecord> Unretire = new List<StructureRecord>();
            public readonly List<string> Regrade = new List<string>();
            public readonly List<LayoutConnection> Wire = new List<LayoutConnection>();
            public readonly List<string> Drop = new List<string>();
            public readonly List<string> Kept = new List<string>();
            public readonly List<string> Problems = new List<string>();
            public readonly Dictionary<string, List<UpgradeTarget>> Targets =
                new Dictionary<string, List<UpgradeTarget>>();
            public readonly Dictionary<string, UpgradeTarget> TargetsById = new Dictionary<string, UpgradeTarget>();
            public List<CoopRoom> Coops;
            public bool Blocked;
            public long Items;
            public long ItemsShort;
            public long Water;
            public long WaterShort;

            public bool Empty => Place.Count == 0 && Shifts.Count == 0 && Retire.Count == 0 && Gone.Count == 0 &&
                                 Unretire.Count == 0 && Regrade.Count == 0 && Wire.Count == 0 && Drop.Count == 0;

            public void Problem(string text, bool blocking)
            {
                Problems.Add(blocking ? $"{text} (blocks farm.upgrade confirm)" : text);
                Blocked |= blocking;
            }
        }

        // A standing record whose Id left the layout. Carry is the entry replacing it with another prefab, which takes
        // its items first; Stays says why it stays standing, retired; NoRoom holds what found no room.
        private class UpgradeRetire
        {
            public StructureRecord Rec;
            public BaseEntity Entity;
            public LayoutEntry Carry;
            public string Stays;
            public bool Gone;
            public readonly List<string> Chickens = new List<string>();
            public readonly List<UpgradeMove> Moves = new List<UpgradeMove>();
            public readonly List<UpgradeMove> NoRoom = new List<UpgradeMove>();
            // Why each shortname in NoRoom found no room, for one problem line per record.
            public readonly HashSet<string> NoRoomWhy = new HashSet<string>();
            public readonly List<string> Links = new List<string>();
            // A battery's charge handed to the battery replacing it, or lost.
            public readonly List<string> Notes = new List<string>();
            public string Threw;

            public bool Bare => Stays == null && !Gone && Carry == null && Chickens.Count == 0 && Moves.Count == 0 &&
                                NoRoom.Count == 0 && Notes.Count == 0;
        }

        // An old part moving to the entry that replaces it with the same prefab. Its record takes the entry's Id and
        // keeps the net id, so its items, crops, chickens, eggs, and charge stay with it.
        private class UpgradeShift
        {
            public StructureRecord Rec;
            public BaseEntity Entity;
            public LayoutEntry Entry;
            public string OldId;
            public Vector3 From;
            public Quaternion FromRotation;
            public Vector3 To;
            public bool Posed;
            public bool Done;
        }

        // An old part lifted out of the new entries' way while they are placed; InWay when it stands in a new entry's
        // footprint (so it cannot go back there). Settled once it stands at its new pose or back where it stood, or
        // is gone.
        private class UpgradeStash
        {
            public StructureRecord Rec;
            public BaseEntity Entity;
            public string Id;
            public string Why;
            public bool InWay;
            public Vector3 Position;
            public Quaternion Rotation;
            public bool Up;
            public bool Settled;
        }

        // A standing part PlanOverlaps tests against every box, with its own box worked out once. Kept: the layout
        // keeps its Id where it stands. Target: the entry it moves to, whose box it may fill.
        private class OldPart
        {
            public string Id;
            public BaseEntity Entity;
            public OBB Bounds;
            public bool Kept;
            public StructureRecord Rec;
            public LayoutEntry Target;
        }

        private class UpgradeMove
        {
            public string To;
            public string Shortname;
            public long Amount;
            public bool Water;

            public override string ToString() =>
                (Water ? $"{Shortname} {Amount} ml" : $"{Shortname} x{Amount}") +
                (To != null ? $" to {To}" : string.Empty);
        }

        // A layout entry that takes moved items: its live inventory, or for an entry step 1 places the empty
        // inventory its prefab starts with. One SimContainer per target serves every source of the plan.
        private class UpgradeTarget
        {
            public string Id;
            public ItemContainer Container;
            public SimContainer Room;
        }

        // Free chicken places of a coop; Coop is null for a coop step 1 places.
        private class CoopRoom
        {
            public string Id;
            public ChickenCoop Coop;
            public int Free;
            public bool Hatching;
        }

        private class CoopPick
        {
            public CoopRoom Room;
            public int Chickens;
            public int Eggs;
        }

        [ConsoleCommand("farm.upgrade")]
        private void UpgradeCommand(ConsoleSystem.Arg arg)
        {
            if (!Authorized(arg) || Refused(arg))
                return;
            if (!_data.Built)
            {
                arg.ReplyWith("no farm built");
                return;
            }
            int count = arg.Args?.Length ?? 0;
            bool confirm = count == 1 && arg.GetString(0) == "confirm";
            if (count > 0 && !confirm)
            {
                arg.ReplyWith("usage: farm.upgrade [confirm]; without confirm it only prints the plan");
                return;
            }
            ulong owner = FarmOwner();
            if (owner == 0)
            {
                arg.ReplyWith(NoOwner);
                return;
            }
            if (!confirm)
            {
                arg.ReplyWith(UpgradeReport(PlanUpgrade(BeginPass(false))));
                return;
            }
            string reply = Upgrade(owner);
            SaveWorld("farm.upgrade confirm");
            arg.ReplyWith(reply);
        }

        // One farm.status line: what farm.upgrade confirm would do now.
        private string UpgradeSummary(UpkeepPass pass)
        {
            UpgradePlan plan = PlanUpgrade(pass);
            string problems = plan.Problems.Count == 0 ? "none" : $"{plan.Problems.Count} (farm.upgrade lists them)";
            if (plan.Empty)
                return "farm.upgrade: nothing to do" +
                       (plan.Problems.Count > 0 ? $"; problems: {problems}" : string.Empty);
            string moving = plan.Items + plan.Water == 0
                ? "nothing to move"
                : $"{Quantity(plan.Items, plan.Water)} to move, " + (plan.ItemsShort + plan.WaterShort == 0
                    ? "all with room"
                    : $"{Quantity(plan.ItemsShort, plan.WaterShort)} without room");
            return $"farm.upgrade would place {plan.Place.Count} entries, move {plan.Shifts.Count} records, retire " +
                   $"{plan.Retire.Count + plan.Gone.Count} records ({moving}), regrade {plan.Regrade.Count} blocks, " +
                   $"rewire {plan.Wire.Count + plan.Drop.Count} links; problems: {problems}";
        }

        private static string Quantity(long items, long water) =>
            water == 0
                ? $"{items} items"
                : items == 0
                    ? $"{water} ml of water"
                    : $"{items} items and {water} ml of water";

        // Plans the change from the standing farm to the layout without changing anything (BeginPass may still mark
        // a lost record missing). An old record whose Id leaves the layout moves to the entry that Replaces it with
        // the same prefab, hands its items to one with another prefab, or retires. Every simulated item move shares
        // the targets' room, so two sources never count the same space.
        private UpgradePlan PlanUpgrade(UpkeepPass pass)
        {
            _planBases = new Dictionary<string, BaseHeight>();
            try
            {
                return PlanChange(pass);
            }
            finally
            {
                _planBases = null;
            }
        }

        private UpgradePlan PlanChange(UpkeepPass pass)
        {
            var plan = new UpgradePlan { Pass = pass };
            Dictionary<string, LayoutEntry> byId = EntriesById();
            var replacing = new Dictionary<string, LayoutEntry>();
            foreach (LayoutEntry entry in _config.Layout.Entries.Where(e => !string.IsNullOrEmpty(e.Replaces)))
                if (!replacing.ContainsKey(entry.Replaces))
                    replacing[entry.Replaces] = entry;
            List<LayoutEntry> missing = MissingEntries(true);
            var missingIds = new HashSet<string>(missing.Select(e => e.Id));
            var retiring = new List<UpgradeRetire>();
            foreach (StructureRecord rec in _data.Structures)
            {
                if (rec.Id != null && byId.ContainsKey(rec.Id))
                {
                    if (rec.Retired)
                        plan.Unretire.Add(rec);
                    continue;
                }
                BaseEntity entity = rec.NetId == 0 || rec.Missing ? null : ResolveStructure(rec);
                LayoutEntry by;
                replacing.TryGetValue(rec.Id ?? string.Empty, out by);
                // A mounted part (a sign on a wall) stays parented to its old block, so its replacement is placed new;
                // so is a building block, which a move would leave in its old building with stale socket links.
                if (entity == null)
                    plan.Gone.Add(rec);
                else if (by != null && missingIds.Contains(by.Id) && SamePrefab(entity, by.Prefab) &&
                         entity.GetParentEntity() == null && !(entity is BuildingBlock))
                    plan.Shifts.Add(new UpgradeShift
                    {
                        Rec = rec,
                        Entity = entity,
                        Entry = by,
                        OldId = rec.Id,
                        From = entity.transform.position,
                        FromRotation = entity.transform.rotation,
                    });
                else
                    retiring.Add(new UpgradeRetire { Rec = rec, Entity = entity, Carry = by });
            }
            plan.Shifts.Sort((a, b) => missing.IndexOf(a.Entry).CompareTo(missing.IndexOf(b.Entry)));
            foreach (UpgradeShift shift in plan.Shifts)
                plan.Shifting[IdOf(shift.Entity)] = shift.Entry;
            plan.Place.AddRange(missing.Where(e => plan.Shifts.All(s => s.Entry != e)));
            // A retiring record has no layout entry, so a block among them has the default layer of everything else:
            // within a layer, blocks go last and later records (placed later, on top) first. Static planters, which
            // stand on terrain, go after everything: their harvest takes only the feed-box room the others leave.
            plan.Retire.AddRange(retiring
                .Select((r, index) => new { r, index })
                .OrderBy(p => p.r.Entity is PlanterBoxStatic ? 1 : 0)
                .ThenByDescending(p => LayerOfRecord(p.r.Rec))
                .ThenBy(p => p.r.Entity is BuildingBlock ? 1 : 0)
                .ThenByDescending(p => p.index)
                .Select(p => p.r));
            foreach (UpgradeRetire r in plan.Retire)
                plan.Retiring.Add(IdOf(r.Entity));
            plan.Aside.UnionWith(plan.Retiring);
            plan.Aside.UnionWith(plan.Shifting.Keys);
            PlanPlacement(plan, byId);
            PlanShifts(plan);
            PlanOverlaps(plan, byId);
            PlanSupport(plan);
            foreach (UpgradeRetire r in plan.Retire)
            {
                bool planned = false;
                Guard("upgrade", $"plan {r.Rec.Id}", () =>
                {
                    PlanRetire(plan, r);
                    planned = true;
                });
                if (!planned)
                    plan.Problem($"planning the retirement of {r.Rec.Id} threw (the log has it)", true);
            }
            foreach (LayoutEntry entry in _config.Layout.Entries)
            {
                string line = RegradeStep(entry, pass, false);
                if (line != null)
                    plan.Regrade.Add(line);
            }
            plan.Wire.AddRange(_config.Layout.Connections.Where(link => !IsWired(link, pass)));
            PlanKept(plan);
            return plan;
        }

        // Step 1: the missing entries no old part moves into, grouped by building with the base height T each would
        // use, then the rest by role. An entry whose pose fails (a refused base height, a spot off its block, no
        // water, a ground check) would fail to place, so no move counts on it.
        private void PlanPlacement(UpgradePlan plan, Dictionary<string, LayoutEntry> byId)
        {
            var refused = new HashSet<string>();
            foreach (IGrouping<string, LayoutEntry> group in plan.Place
                         .Where(e => BuildingOf(e, byId) != null)
                         .GroupBy(e => BuildingOf(e, byId)))
            {
                float t;
                string how;
                string head = $"{group.Key}: {group.Count()} entries ({RoleCounts(group)})";
                string ids = IdRanges(group.Select(e => e.Id));
                if (PreviewBuildingBase(group.Key, out t, out how))
                {
                    plan.PlaceLines.Add($"{head}, base {t:0.00} m ({how}): {ids}");
                    continue;
                }
                refused.Add(group.Key);
                plan.PlaceLines.Add($"{head}, base refused ({how}): {ids}");
                plan.Problem($"building {group.Key}: {how}; its {group.Count()} entries would fail to place", true);
            }
            foreach (IGrouping<string, LayoutEntry> group in plan.Place
                         .Where(e => BuildingOf(e, byId) == null)
                         .GroupBy(e => e.Role))
                plan.PlaceLines.Add($"{group.Key} x{group.Count()}: {IdRanges(group.Select(e => e.Id))}");
            foreach (LayoutEntry entry in plan.Place)
            {
                LayoutEntry on;
                string building = BuildingOf(entry, byId);
                if (building == null && entry.On != null && byId.TryGetValue(entry.On, out on))
                    building = BuildingOf(on, byId);
                if (building != null && refused.Contains(building))
                    continue;
                Vector3 position;
                Quaternion rotation;
                string problem;
                if (!EntryPose(entry, plan.Aside, true, out position, out rotation, out problem))
                {
                    plan.Problem($"{entry.Id} would fail to place: {problem}", true);
                    continue;
                }
                plan.Placing.Add(entry.Id);
                plan.Boxes.Add(new KeyValuePair<LayoutEntry, OBB>(entry, EntryBox(entry, position, rotation)));
                plan.Footprints.Add(new KeyValuePair<LayoutEntry, OBB>(entry,
                    FootprintAt(entry.Prefab, position, rotation)));
            }
        }

        private static string RoleCounts(IEnumerable<LayoutEntry> entries) =>
            string.Join(", ", entries.GroupBy(e => e.Role).Select(g => $"{g.Key} x{g.Count()}"));

        // Each moving part's new pose, checked as Place checks the entry; a failed check refuses confirm. Its wires and
        // hoses go (their points would be stale) and WiringStep makes the layout's again.
        private void PlanShifts(UpgradePlan plan)
        {
            foreach (UpgradeShift shift in plan.Shifts)
            {
                foreach (string link in IoLinks(shift.Entity).Where(l => !plan.Drop.Contains(l)))
                    plan.Drop.Add(link);
                Vector3 position;
                Quaternion rotation;
                string problem;
                if (!EntryPose(shift.Entry, plan.Aside, false, out position, out rotation, out problem))
                {
                    plan.Problem($"{shift.OldId} cannot move to {shift.Entry.Id}: {problem}", true);
                    continue;
                }
                shift.To = position;
                shift.Posed = true;
                plan.Boxes.Add(new KeyValuePair<LayoutEntry, OBB>(shift.Entry,
                    EntryBox(shift.Entry, position, rotation)));
                plan.Footprints.Add(new KeyValuePair<LayoutEntry, OBB>(shift.Entry,
                    FootprintAt(shift.Entry.Prefab, position, rotation)));
            }
        }

        // Every replaced part, and every other old part in the way of an entry step 1 places or a part moves to (its
        // box at its pose), rises out of the way for the placement; a part parented to an old part rides along with
        // it. A kept part in the way, or a player or animal in a footprint, refuses confirm.
        private void PlanOverlaps(UpgradePlan plan, Dictionary<string, LayoutEntry> byId)
        {
            var occupied = new Dictionary<string, List<string>>();
            foreach (UpgradeShift shift in plan.Shifts)
                AddStash(plan, shift.Rec, shift.Entity, $"{shift.Entry.Id} replaces it", false);
            // A part parented to an old part rides along with it; lifting it too would raise it twice.
            foreach (UpgradeRetire r in plan.Retire.Where(r => r.Carry != null && !ParentedToRecord(r.Entity)))
                AddStash(plan, r.Rec, r.Entity, $"{r.Carry.Id} replaces it", false);
            foreach (KeyValuePair<LayoutEntry, OBB> footprint in plan.Footprints)
            {
                string occupant = Occupant(footprint.Value);
                if (occupant != null)
                    AddTo(occupied, occupant, footprint.Key.Id);
            }
            if (plan.Boxes.Count == 0)
            {
                ReportOccupied(plan, occupied);
                return;
            }
            Dictionary<string, StructureRecord> records = RecordsById();
            var parts = new List<OldPart>();
            foreach (KeyValuePair<string, BaseEntity> old in plan.Pass.Ids)
            {
                BaseEntity entity = old.Value;
                BaseEntity parent = entity.GetParentEntity();
                if (parent != null && _structureIds.Contains(IdOf(parent)))
                    continue;
                StructureRecord rec;
                LayoutEntry target;
                records.TryGetValue(old.Key, out rec);
                plan.Shifting.TryGetValue(IdOf(entity), out target);
                parts.Add(new OldPart
                {
                    Id = old.Key,
                    Entity = entity,
                    Bounds = entity.WorldSpaceBounds(),
                    Kept = byId.ContainsKey(old.Key),
                    Rec = rec,
                    Target = target,
                });
            }
            List<Surface> surfaces = Surfaces();
            var blocking = new Dictionary<string, List<string>>();
            foreach (KeyValuePair<LayoutEntry, OBB> box in plan.Boxes)
            {
                OBB inner = Resize(box.Value, -KeptSlack);
                foreach (OldPart part in parts)
                {
                    if (part.Target == box.Key)
                        continue;
                    if (part.Kept)
                    {
                        // The box test goes first: MayTouch can look for the block under the part.
                        if (inner.Intersects(part.Bounds) && !MayTouch(box.Key, part.Id, part.Entity, byId, surfaces))
                            AddTo(blocking, part.Id, box.Key.Id);
                    }
                    else if (part.Rec != null && box.Value.Intersects(part.Bounds))
                    {
                        AddStash(plan, part.Rec, part.Entity, $"it stands in the footprint of {box.Key.Id}", true);
                    }
                }
            }
            ReportOccupied(plan, occupied);
            foreach (KeyValuePair<string, List<string>> pair in blocking)
                plan.Problem($"{pair.Key}, which the layout keeps where it stands, is in the footprint of " +
                             IdRanges(pair.Value), true);
        }

        private static void ReportOccupied(UpgradePlan plan, Dictionary<string, List<string>> occupied)
        {
            foreach (KeyValuePair<string, List<string>> pair in occupied)
                plan.Problem($"{pair.Key} stands in the footprint of {IdRanges(pair.Value)}", true);
        }

        private bool ParentedToRecord(BaseEntity entity)
        {
            BaseEntity parent = entity == null ? null : entity.GetParentEntity();
            return parent != null && _structureIds.Contains(IdOf(parent));
        }

        private void AddStash(UpgradePlan plan, StructureRecord rec, BaseEntity entity, string why, bool inWay)
        {
            if (entity == null)
                return;
            UpgradeStash stash = StashOf(plan, entity);
            if (stash == null)
            {
                plan.Stash.Add(new UpgradeStash
                {
                    Rec = rec,
                    Entity = entity,
                    Id = rec?.Id ?? NameOf(entity),
                    Why = why,
                    InWay = inWay,
                });
                return;
            }
            if (inWay && !stash.InWay)
                stash.Why += $"; {why}";
            stash.InWay |= inWay;
        }

        private static void AddTo(Dictionary<string, List<string>> lists, string key, string value)
        {
            List<string> list;
            if (!lists.TryGetValue(key, out list))
                lists[key] = list = new List<string>();
            if (!list.Contains(value))
                list.Add(value);
        }

        // The box grown (or shrunk, by a negative amount) on every side.
        private static OBB Resize(OBB box, float by) =>
            new OBB(box.position, 2f * new Vector3(Mathf.Max(0.01f, box.extents.x + by),
                Mathf.Max(0.01f, box.extents.y + by), Mathf.Max(0.01f, box.extents.z + by)), box.rotation);

        // A kept part a new entry rests against by design: its support (On, Parent, up the parent chain), a block of
        // its own building, anything standing on a block of that building (the herd's cupboard under the shelter
        // roof), or, for a fence or gate, another fence or gate (rails meet end to end and at corners).
        private bool MayTouch(LayoutEntry entry, string keptId, BaseEntity keptEntity,
            Dictionary<string, LayoutEntry> byId, List<Surface> surfaces)
        {
            LayoutEntry e = entry;
            for (int depth = 0; e != null && depth < 16; depth++)
            {
                if (e.On == keptId || e.Parent == keptId)
                    return true;
                LayoutEntry parent;
                e = e.Parent != null && byId.TryGetValue(e.Parent, out parent) ? parent : null;
            }
            LayoutEntry kept;
            if (!byId.TryGetValue(keptId, out kept))
                return false;
            if (IsPenLine(entry.Role) && IsPenLine(kept.Role))
                return true;
            string building = BuildingOf(entry, byId);
            if (building == null)
                return false;
            if (BuildingOf(kept, byId) == building)
                return true;
            BuildingBlock under = BlockUnder(keptEntity.transform.position, true, surfaces);
            LayoutEntry underEntry;
            return under != null && byId.TryGetValue(NameOf(under), out underEntry) &&
                   BuildingOf(underEntry, byId) == building;
        }

        // The herd's cupboard must not retire, and the block it stands on (ReattachCupboard's BlockUnder) must not
        // retire or move away. Any other kept part standing on a foundation or floor that retires or moves away is
        // reported: it would stand on nothing, and a block standing there may collapse.
        private void PlanSupport(UpgradePlan plan)
        {
            foreach (UpgradeRetire r in plan.Retire.Where(r => r.Rec.Role == RoleCupboard))
                plan.Problem($"the plan retires the herd's cupboard {r.Rec.Id}; the layout must keep its entry", true);
            PlanDeployableSupport(plan);
            List<BuildingBlock> surfaces = plan.Retire.Select(r => r.Entity)
                .Concat(plan.Shifts.Select(s => s.Entity))
                .OfType<BuildingBlock>()
                .Where(b => IsSurfacePrefab(b.PrefabName))
                .ToList();
            if (surfaces.Count == 0)
                return;
            BuildingPrivlidge cupboard = plan.Pass.Cupboard;
            BuildingBlock home = cupboard == null || plan.Shifting.ContainsKey(IdOf(cupboard))
                ? null
                : BlockUnder(cupboard.transform.position, true);
            if (home != null && (plan.Retiring.Contains(IdOf(home)) || plan.Shifting.ContainsKey(IdOf(home))))
                plan.Problem($"the plan retires or moves {NameOf(home)}, which the herd's cupboard " +
                             $"{NameOf(cupboard)} stands on; the layout must keep its entry", true);
            foreach (KeyValuePair<string, BaseEntity> pair in plan.Pass.Ids)
            {
                BaseEntity entity = pair.Value;
                ulong id = IdOf(entity);
                if (entity == cupboard || plan.Retiring.Contains(id) || plan.Shifting.ContainsKey(id))
                    continue;
                foreach (BuildingBlock block in surfaces)
                {
                    Vector3 local = block.transform.InverseTransformPoint(entity.transform.position);
                    if (local.y < -OnTolerance || local.y > StandsOnHeight ||
                        !OnFootprint(local, IsTrianglePrefab(block.PrefabName)))
                        continue;
                    plan.Problem($"{pair.Key} stands on {NameOf(block)}, which retires or moves away; {pair.Key} " +
                                 "stays where it is, " +
                                 (entity is StabilityEntity ? "and may collapse" : "on nothing"), false);
                    break;
                }
            }
            // A part that is not the farm's dies with its ground (GroundWatch) and drops what it holds.
            foreach (BuildingBlock block in surfaces)
            {
                var found = new List<BaseEntity>();
                Vis.Entities(block.WorldSpaceBounds(), found,
                    Rust.Layers.Mask.Deployed | Rust.Layers.Mask.Construction);
                foreach (BaseEntity entity in found)
                {
                    BaseEntity parent = entity == null ? null : entity.GetParentEntity();
                    if (entity == null || entity.IsDestroyed || entity == block || !(entity is DecayEntity) ||
                        _structureIds.Contains(IdOf(entity)) ||
                        (parent != null && _structureIds.Contains(IdOf(parent))))
                        continue;
                    Vector3 local = block.transform.InverseTransformPoint(entity.transform.position);
                    if (local.y < -OnTolerance || local.y > StandsOnHeight ||
                        !OnFootprint(local, IsTrianglePrefab(block.PrefabName)))
                        continue;
                    plan.Problem($"{NameOf(entity)}, which is not the farm's, stands on {NameOf(block)}, which " +
                                 "retires or moves away; move it first, or it falls with what it holds", true);
                }
            }
        }

        // The same for a deployable set unparented on top of a farm deployable that retires or moves (a lantern on a
        // box or the table): a kill or move leaves it on nothing.
        private void PlanDeployableSupport(UpgradePlan plan)
        {
            foreach (BaseEntity support in plan.Retire.Select(r => r.Entity).Concat(plan.Shifts.Select(s => s.Entity)))
            {
                if (support == null || support.IsDestroyed || support is BuildingBlock)
                    continue;
                Bounds bounds = support.bounds;
                var reach = new Bounds(bounds.center + Vector3.up * (StandsOnHeight / 2f),
                    bounds.size + Vector3.up * StandsOnHeight);
                var found = new List<BaseEntity>();
                Vis.Entities(new OBB(support.transform.position, support.transform.rotation, reach), found,
                    Rust.Layers.Mask.Deployed | Rust.Layers.Mask.Construction);
                foreach (BaseEntity entity in found)
                {
                    if (entity == null || entity.IsDestroyed || entity == support || !(entity is DecayEntity) ||
                        entity.GetParentEntity() != null || _structureIds.Contains(IdOf(entity)))
                        continue;
                    Vector3 local = support.transform.InverseTransformPoint(entity.transform.position);
                    if (local.y < bounds.max.y - OnTolerance || local.y > bounds.max.y + StandsOnHeight ||
                        Mathf.Abs(local.x - bounds.center.x) > bounds.extents.x ||
                        Mathf.Abs(local.z - bounds.center.z) > bounds.extents.z)
                        continue;
                    plan.Problem($"{NameOf(entity)}, which is not the farm's, stands on {NameOf(support)}, which " +
                                 "retires or moves away; move it first, or it falls with what it holds", true);
                }
            }
        }

        // One retiring record: a planter with plants, or anything with a part attached that a kill would take along,
        // stays; a coop first finds places for all of its chickens and hatching eggs (in its replacement first) or
        // keeps them, and its items with them; everything else moves into its replacement, then by the item and
        // water rules. One that stays but stands in a new entry's footprint refuses confirm: it could not go back.
        private void PlanRetire(UpgradePlan plan, UpgradeRetire r)
        {
            BaseEntity entity = r.Entity;
            r.Links.AddRange(IoLinks(entity));
            foreach (string link in r.Links.Where(l => !plan.Drop.Contains(l)))
                plan.Drop.Add(link);
            // A static planter replants itself on a timer: its ripe crops go to the feed box and the rest go with it.
            int plants = entity is PlanterBoxStatic ? 0 : PlantCount(entity);
            string attached = plants > 0 ? null : Attached(entity, plan.Retiring);
            var coop = entity as ChickenCoop;
            if (entity is PlanterBoxStatic)
                r.Notes.Add("its ripe crops go to the feed box first, and its other plants go with it");
            if (plants > 0)
            {
                r.Stays = PlantsText(plants);
            }
            else if (attached != null)
            {
                r.Stays = $"{attached} is attached to it, and a kill would take it along";
                plan.Problem($"{r.Rec.Id} stays standing, retired, with its items: {r.Stays}", false);
            }
            else if (coop != null && !ReserveCoops(plan, r, coop, false))
            {
                plan.Problem($"{r.Rec.Id}: {r.Stays}", false);
            }
            UpgradeStash stash = StashOf(plan, entity);
            if (r.Stays != null)
            {
                if (stash != null && stash.InWay)
                    plan.Problem($"{r.Rec.Id} cannot retire ({r.Stays}) and stands in a new entry's footprint, " +
                                 "where it cannot go back", true);
                return;
            }
            foreach (Item item in StoredItems(entity))
                PlanMove(plan, r, item, false);
            foreach (Item item in StoredWater(entity))
                PlanMove(plan, r, item, true);
            if (r.NoRoom.Count > 0)
                plan.Problem($"no room for {string.Join(", ", r.NoRoom)} from {r.Rec.Id} ({r.Rec.Role}): " +
                             string.Join("; ", r.NoRoomWhy), true);
            var battery = entity as ElectricBattery;
            if (battery == null || battery.rustWattSeconds < 1f)
                return;
            GameObject carry = r.Carry == null ? null : PrefabObject(r.Carry.Prefab);
            ElectricBattery to = carry == null ? null : carry.GetComponent<ElectricBattery>();
            if (to == null)
            {
                r.Notes.Add($"its charge of {battery.rustWattSeconds:0} rW s goes with it (no battery replaces it)");
                return;
            }
            float moved = Mathf.Min(battery.rustWattSeconds, to.maxCapactiySeconds);
            r.Notes.Add($"charge {moved:0} rW s to {r.Carry.Id}" + (moved < battery.rustWattSeconds
                ? $"; {battery.rustWattSeconds - moved:0} rW s more than {r.Carry.Id} holds goes with it"
                : string.Empty));
        }

        // A retiring battery's charge goes to the battery replacing it (another prefab), up to that one's capacity.
        private void CarryCharge(UpgradePlan live, UpgradeRetire done, BaseEntity entity)
        {
            var from = entity as ElectricBattery;
            var to = done.Carry == null ? null : live.Pass.ById(done.Carry.Id) as ElectricBattery;
            if (from == null || from.rustWattSeconds < 1f)
                return;
            if (to == null)
            {
                done.Notes.Add($"its charge of {from.rustWattSeconds:0} rW s went with it (no battery replaces it)");
                return;
            }
            float moved = Mathf.Min(from.rustWattSeconds, Mathf.Max(0f, to.maxCapactiySeconds - to.rustWattSeconds));
            to.rustWattSeconds += moved;
            from.rustWattSeconds -= moved;
            to.SendNetworkUpdate();
            done.Notes.Add($"charge {moved:0} rW s to {done.Carry.Id}" + (from.rustWattSeconds >= 1f
                ? $", {from.rustWattSeconds:0} over its capacity lost"
                : string.Empty));
        }

        private static string PlantsText(int plants) =>
            $"holds {plants} plants, which the farm harvests until they are gone";

        private void PlanMove(UpgradePlan plan, UpgradeRetire r, Item item, bool water)
        {
            List<string> roles = water ? WaterRoles(r.Rec.Role) : ItemRoles(r.Rec.Role, item.info);
            List<UpgradeTarget> targets = MoveTargets(plan, r, roles, water);
            string shortname = item.info.shortname;
            long left = item.amount;
            foreach (UpgradeTarget target in targets)
            {
                if (left <= 0)
                    break;
                long taken = target.Room.Take(item, left);
                if (taken > 0)
                    AddMove(r.Moves, target.Id, shortname, taken, water);
                left -= taken;
            }
            if (water)
            {
                plan.Water += item.amount;
                plan.WaterShort += left;
            }
            else
            {
                plan.Items += item.amount;
                plan.ItemsShort += left;
            }
            if (left <= 0)
                return;
            AddMove(r.NoRoom, null, shortname, left, water);
            r.NoRoomWhy.Add(targets.Count == 0
                ? $"nothing of role {string.Join(", ", roles)} stands or is placed to take {shortname}"
                : $"{string.Join(", ", targets.Select(t => t.Id))} have no room left for {shortname}");
        }

        private static void AddMove(List<UpgradeMove> moves, string to, string shortname, long amount, bool water)
        {
            UpgradeMove move = moves.FirstOrDefault(m => m.To == to && m.Shortname == shortname && m.Water == water);
            if (move == null)
                moves.Add(new UpgradeMove { To = to, Shortname = shortname, Amount = amount, Water = water });
            else
                move.Amount += amount;
        }

        // Layout entries whose standing record has another prefab or role than the entry, or stands more than 1 m
        // across from the entry's spot. An Id that stays keeps its record where it stands; moving a part takes a new
        // Id that Replaces the old one.
        private void PlanKept(UpgradePlan plan)
        {
            Dictionary<string, StructureRecord> records = RecordsById();
            foreach (LayoutEntry entry in _config.Layout.Entries)
            {
                BaseEntity entity = plan.Pass.ById(entry.Id);
                StructureRecord rec;
                if (entity == null || !records.TryGetValue(entry.Id, out rec))
                    continue;
                var why = new List<string>();
                if (!string.Equals(rec.Prefab, entry.Prefab, StringComparison.OrdinalIgnoreCase))
                    why.Add($"it is a {entity.ShortPrefabName}, the layout names " +
                            Path.GetFileNameWithoutExtension(entry.Prefab ?? string.Empty));
                Vector3 spot = FarmPoint(entry.X, entry.Z);
                Vector3 at = entity.transform.position;
                float across = new Vector2(at.x - spot.x, at.z - spot.z).magnitude;
                if (across > KeptDistance)
                    why.Add($"it stands {across:0.0} m from its layout spot");
                if (why.Count > 0)
                    plan.Kept.Add($"{entry.Id}: {string.Join("; ", why)}");
            }
        }

        // farm.upgrade confirm: plans again and applies the plan in this one call, or changes nothing while a problem
        // blocks it. Order: lift what is in the way, place, move the replaced parts (then place what waited for
        // them), carry over and retire, put back what could not retire, regrade, wire. A throw anywhere still puts
        // every lifted part back.
        private string Upgrade(ulong owner)
        {
            UpgradePlan plan = PlanUpgrade(BeginPass());
            if (plan.Blocked)
                return "farm.upgrade confirm refused; nothing changed\n" + UpgradeReport(plan);
            if (plan.Empty)
                return UpgradeReport(plan);
            var lines = new List<string> { "farm.upgrade done:" };
            var placed = new List<string>();
            var moved = new List<string>();
            var retired = new List<UpgradeRetire>();
            var threw = new List<string>();
            var dropped = new List<string>();
            var errors = new List<string>();
            var inPlace = new HashSet<string>();
            bool trouble = false;
            // Guard for this one call: every throw reaches the reply and is logged even when it repeats.
            Action<string, Action> step = (what, action) =>
            {
                try
                {
                    action();
                }
                catch (Exception e)
                {
                    errors.Add($"{what}: {e.GetType().Name}: {e.Message}");
                    Failure($"error:upgrade:{what}", $"upgrade: {what} threw {e}", "failure", 0f);
                }
            };
            foreach (StructureRecord rec in plan.Unretire)
                rec.Retired = false;
            try
            {
                foreach (UpgradeStash stash in plan.Stash)
                    step($"unwire {stash.Id}", () => dropped.AddRange(Unwire(stash.Entity)));
                foreach (UpgradeShift shift in plan.Shifts.Where(sh => StashOf(plan, sh.Entity) == null))
                    step($"unwire {shift.OldId}", () => dropped.AddRange(Unwire(shift.Entity)));
                foreach (UpgradeStash stash in plan.Stash)
                    step($"lift {stash.Id}", () => Lift(stash));
                Physics.SyncTransforms();
                PlaceTally first = null;
                if (plan.Place.Count > 0)
                {
                    first = PlaceUpgrade(plan.Place, owner, plan.Retiring.Concat(plan.Shifting.Keys));
                    inPlace.UnionWith(first.InPlaceIds);
                    placed.Add(PlaceText(first));
                    trouble |= first.Failed + first.DeferredIds.Count > 0;
                }
                // A placement the plan passed failed after all: nothing moves or retires, and the finally puts every
                // lifted part back, retired, where it stood.
                bool stop = first != null && first.Failed > 0;
                if (stop)
                    lines.Add($"stopped after placing, so nothing moved or retired: {first.Failed} entries failed " +
                              $"({string.Join(", ", first.FailedIds)}; farm.status lists why)");
                foreach (UpgradeShift shift in stop ? new List<UpgradeShift>() : plan.Shifts)
                    step($"move {shift.OldId}", () =>
                    {
                        moved.Add(ShiftNow(plan, shift));
                        Physics.SyncTransforms();
                    });
                foreach (UpgradeShift shift in plan.Shifts.Where(s => s.Done))
                    step($"settle {shift.Entry.Id}", () => Settle(shift.Entity));
                trouble |= plan.Shifts.Any(s => !s.Done);
                if (first != null && first.DeferredIds.Count > 0 && plan.Shifts.Any(s => s.Done))
                {
                    List<LayoutEntry> again = MissingEntries(true).Where(e => first.DeferredIds.Contains(e.Id))
                        .ToList();
                    if (again.Count > 0)
                    {
                        PlaceTally later = PlaceUpgrade(again, owner, plan.Retiring);
                        inPlace.UnionWith(later.InPlaceIds);
                        placed.Add("after the moves, " + PlaceText(later));
                    }
                }
                var live = new UpgradePlan { Pass = BeginPass(), Live = true };
                live.Retiring.UnionWith(plan.Retiring);
                // A static planter's harvest goes through the pass's role containers, which skip retired records only.
                live.Pass.Retired.UnionWith(plan.Retiring);
                foreach (UpgradeRetire r in stop ? new List<UpgradeRetire>() : plan.Retire)
                {
                    UpgradeRetire done = null;
                    step($"retire {r.Rec.Id}", () => done = RetireNow(live, r, StashOf(plan, r.Entity)));
                    if (done?.Threw != null)
                        errors.Add($"retire {r.Rec.Id}: {done.Threw}");
                    if (done != null)
                        retired.Add(done);
                    else
                        threw.Add(r.Rec.Id);
                }
            }
            catch (Exception e)
            {
                Failure("upgrade", "farm.upgrade confirm threw midway; every lifted part goes back where it stood: " +
                                   e, "failure", 0f);
                lines.Add($"threw midway ({e.GetType().Name}: {e.Message}); the log has the trace");
                trouble = true;
            }
            finally
            {
                foreach (UpgradeStash stash in plan.Stash.Where(s => s.Up && !s.Settled))
                    step($"put back {stash.Id}", () =>
                    {
                        if (stash.Rec != null)
                            stash.Rec.Retired = true;
                        PutBack(stash);
                        threw.Add(stash.InWay ? $"{stash.Id} (back where it stood, {stash.Why})" : stash.Id);
                    });
                Physics.SyncTransforms();
            }
            foreach (StructureRecord rec in plan.Gone)
            {
                _data.Structures.Remove(rec);
                _repairs.Remove(rec.Id);
            }
            _penHull = null;
            if (placed.Count > 0)
                lines.Add($"placed: {string.Join("; ", placed)}");
            if (moved.Count > 0)
            {
                lines.Add($"moved {plan.Shifts.Count(s => s.Done)} of {plan.Shifts.Count} records:");
                lines.AddRange(moved.Select(m => "  " + m));
            }
            AppendRetire(lines, retired, plan.Gone, plan.Unretire, true);
            if (threw.Count > 0)
                lines.Add("  threw, so these stay standing, retired, where they stood: " +
                          string.Join(", ", threw.Distinct()));
            UpkeepPass after = BeginPass();
            var regraded = new List<string>();
            foreach (LayoutEntry entry in _config.Layout.Entries)
                step($"regrade {entry.Id}", () =>
                {
                    string line = RegradeStep(entry, after, true);
                    if (line != null)
                        regraded.Add(line);
                });
            if (regraded.Count > 0)
                lines.Add($"regraded: {string.Join("; ", regraded)}");
            step("wiring", () => WiringStep(BeginPass()));
            UpkeepPass wired = BeginPass();
            List<LayoutConnection> unwired = plan.Wire.Where(link => !IsWired(link, wired)).ToList();
            int wiredCount = plan.Wire.Count - unwired.Count;
            if (wiredCount > 0)
                lines.Add($"wired: {string.Join("; ", plan.Wire.Except(unwired))}");
            if (unwired.Count > 0)
                lines.Add($"not wired: {string.Join("; ", unwired.Select(link => UnwiredText(link, wired)))}");
            dropped = dropped.Concat(retired.SelectMany(r => r.Links)).Distinct().ToList();
            if (dropped.Count > 0)
                lines.Add($"links dropped: {string.Join("; ", dropped)}");
            step("cupboard", ReattachCupboard);
            var planters = new HashSet<string>(plan.Place.Where(e => e.Role == RolePlanter && inPlace.Contains(e.Id))
                .Select(e => e.Id));
            if (planters.Count > 0)
                step("starter clones", () => StockStarterClones(BeginPass(), planters));
            if (plan.Kept.Count > 0)
            {
                lines.Add("kept where it stands (an Id that stays never moves; a new Id that Replaces it does):");
                lines.AddRange(plan.Kept.Select(k => "  " + k));
            }
            if (plan.Problems.Count > 0)
            {
                lines.Add($"problems the plan reported ({plan.Problems.Count}):");
                lines.AddRange(plan.Problems.Select(p => "  " + p));
            }
            if (errors.Count > 0)
            {
                lines.Add($"threw ({errors.Count}; the log has each trace):");
                lines.AddRange(errors.Select(e => "  " + e));
            }
            int stayed = retired.Count(r => r.Stays != null) + threw.Distinct().Count();
            trouble |= stayed > 0 || unwired.Count > 0 || errors.Count > 0;
            Event("upgrade", (placed.Count > 0 ? $"placed {string.Join("; ", placed)}; " : string.Empty) +
                             $"moved {plan.Shifts.Count(s => s.Done)} of {plan.Shifts.Count} records; retired " +
                             $"{retired.Count + threw.Distinct().Count() + plan.Gone.Count} records ({stayed} stay " +
                             $"standing, retired); regraded {regraded.Count} blocks; wired {wiredCount} of " +
                             $"{plan.Wire.Count} links; dropped {dropped.Count} links", trouble);
            foreach (string line in lines.Skip(1))
                Puts($"upgrade: {line.Trim()}");
            return string.Join("\n", lines);
        }

        // Why a link of the plan is not wired after confirm: an end that is not placed yet, or one that is missing or
        // no IO entity; anything else is in the wiring line of the log.
        private string UnwiredText(LayoutConnection link, UpkeepPass pass)
        {
            Dictionary<string, StructureRecord> records = RecordsById();
            StructureRecord rec;
            string waiting = new[] { link.From, link.To }.FirstOrDefault(id =>
                !records.TryGetValue(id ?? string.Empty, out rec) || NotYetPlaced(rec));
            if (waiting != null)
                return $"{link} (waits for {waiting}, which is not placed yet)";
            string missing = new[] { link.From, link.To }.FirstOrDefault(id => IoEnd(pass.ById(id)) == null);
            return missing != null
                ? $"{link} ({missing} is missing or not an IO entity)"
                : $"{link} (the wiring line in the log says why)";
        }

        // Places entries the way farm.repair does, with BuriedUnder ignoring the old parts this upgrade lifts,
        // moves, or retires.
        private PlaceTally PlaceUpgrade(List<LayoutEntry> entries, ulong owner, IEnumerable<ulong> old)
        {
            PlaceTally tally;
            _retiring.UnionWith(old);
            try
            {
                tally = PlaceMissing(entries, owner, "upgrade");
            }
            finally
            {
                _retiring.Clear();
            }
            float now = Time.realtimeSinceStartup;
            foreach (string id in tally.InPlaceIds)
                _repairs.Remove(id);
            foreach (string id in tally.FailedIds)
                BackOff(id, now);
            foreach (string id in tally.DeferredIds)
            {
                RepairState state = RepairOf(id);
                state.Waiting = true;
                state.WaitingFor = tally.WaitReason(id);
            }
            Physics.SyncTransforms();
            return tally;
        }

        private static string PlaceText(PlaceTally tally) =>
            $"{tally.Placed} placed, {tally.Adopted} adopted, {tally.Failed} failed" +
            (tally.FailedIds.Count > 0 ? $" ({string.Join(", ", tally.FailedIds)})" : string.Empty) +
            (tally.DeferredIds.Count > 0
                ? $", {tally.DeferredIds.Count} {tally.WaitingText()} (farm.upgrade confirm or farm.repair " +
                  "places them later)"
                : string.Empty);

        private static UpgradeStash StashOf(UpgradePlan plan, BaseEntity entity) =>
            entity == null ? null : plan.Stash.FirstOrDefault(s => s.Entity == entity);

        // Raises the part with what is parented to it, with no network update: clients only ever see where it ends.
        private static void Lift(UpgradeStash stash)
        {
            Transform transform = stash.Entity.transform;
            stash.Position = transform.position;
            stash.Rotation = transform.rotation;
            transform.position = stash.Position + Vector3.up * StashHeight;
            stash.Up = true;
        }

        // Sets a lifted part down silently where it stood, so the navmesh rebuild of a following MoveEntity or kill
        // covers that spot rather than the lifted one.
        private static void Unlift(UpgradeStash stash)
        {
            if (stash.Up && stash.Entity != null && !stash.Entity.IsDestroyed)
                stash.Entity.transform.SetPositionAndRotation(stash.Position, stash.Rotation);
            stash.Up = false;
        }

        private static void PutBack(UpgradeStash stash)
        {
            bool up = stash.Up;
            Unlift(stash);
            stash.Settled = true;
            if (up && stash.Entity != null && !stash.Entity.IsDestroyed)
                MoveEntity(stash.Entity, stash.Position, stash.Rotation);
        }

        // Moves a replaced part to its entry's pose and re-keys its record, or puts it back where it stood, retired,
        // when its pose fails now or what it rests on did not get placed.
        private string ShiftNow(UpgradePlan plan, UpgradeShift shift)
        {
            UpgradeStash stash = StashOf(plan, shift.Entity);
            LayoutEntry entry = shift.Entry;
            if (shift.Entity == null || shift.Entity.IsDestroyed)
            {
                if (stash != null)
                    stash.Settled = true;
                _data.Structures.Remove(shift.Rec);
                _repairs.Remove(shift.OldId);
                return $"{shift.OldId} was gone before it could move to {entry.Id}; dropped from the record, and " +
                       $"farm.repair places {entry.Id}";
            }
            Vector3 position = Vector3.zero;
            Quaternion rotation = Quaternion.identity;
            string problem = null;
            string support = new[] { entry.On, entry.Parent }
                .FirstOrDefault(id => !string.IsNullOrEmpty(id) && StandingBlock(id) == null);
            if (support != null)
                problem = $"{support}, which it rests on, is not in place";
            else if (!EntryPose(entry, plan.Aside, false, out position, out rotation, out problem))
                problem = problem ?? "its pose check failed";
            if (problem != null)
            {
                shift.Rec.Retired = true;
                if (stash != null)
                    PutBack(stash);
                IoEnd(shift.Entity)?.DisconnectAll();
                return $"{shift.OldId} stays standing, retired, where it stood instead of moving to {entry.Id}: " +
                       problem + (stash != null && stash.InWay ? $"; back where it stood, {stash.Why}" : string.Empty);
            }
            try
            {
                if (stash != null)
                    Unlift(stash);
                MoveEntity(shift.Entity, position, rotation);
                Rekey(shift.Rec, entry, shift.Entity);
                DecayEntity mount = entry.Mount ? ParentEntity(entry) : null;
                if (mount != null)
                    shift.Entity.SetParent(mount, true);
            }
            catch
            {
                // Retired where it stood, as for a failed pose; the caller reports the throw.
                shift.Rec.Retired = true;
                if (shift.Rec.Id != shift.OldId)
                    shift.Rec.Id = shift.OldId;
                if (!shift.Entity.IsDestroyed)
                    MoveEntity(shift.Entity, stash?.Position ?? shift.From, stash?.Rotation ?? shift.FromRotation);
                if (stash != null)
                    stash.Settled = true;
                throw;
            }
            if (stash != null)
                stash.Settled = true;
            shift.Done = true;
            return $"{shift.OldId} to {entry.Id}: {Where(shift.From)} to {Where(position)}";
        }

        // The moved part's record becomes the entry's, as Place would have recorded it; the net id stays, and so
        // does the repair backoff, under the new Id.
        private void Rekey(StructureRecord rec, LayoutEntry entry, BaseEntity entity)
        {
            string old = rec.Id;
            _data.Structures.RemoveAll(r => r != rec && r.Id == entry.Id);
            Vector3 at = entity.transform.position;
            rec.Id = entry.Id;
            rec.Role = entry.Role;
            rec.Prefab = entry.Prefab;
            rec.X = at.x;
            rec.Y = at.y;
            rec.Z = at.z;
            rec.Yaw = _data.Yaw + entry.Yaw;
            rec.Failure = null;
            rec.Missing = false;
            rec.Retired = false;
            rec.Waiting = null;
            RepairState state;
            bool had = _repairs.TryGetValue(old, out state);
            _repairs.Remove(old);
            _repairs.Remove(entry.Id);
            if (had)
                _repairs[entry.Id] = state;
        }

        // What a transform write leaves stale after a move (its wires and hoses went before it, in Unwire, and
        // WiringStep makes the layout's again): the entity query and sprinkler splash grids, and a coop's terrain
        // test, which ChickenCoop.ServerInit runs once and its chickens snap to.
        private void Settle(BaseEntity entity)
        {
            Regrid(entity);
            var coop = entity as ChickenCoop;
            RaycastHit hit;
            if (coop != null && GamePhysics.Trace(new Ray(coop.transform.position + coop.transform.up,
                    -coop.transform.up), 0f, out hit, 1.1f, CoopGroundMask))
            {
                coop.IsOnTerrain = hit.IsOnLayer(Rust.Layer.Terrain);
                coop.UpdateMovementPlane();
            }
        }

        // Disconnects every wire and hose of a part (a trough's on its water end) and returns them by their names now.
        private List<string> Unwire(BaseEntity entity)
        {
            List<string> links = IoLinks(entity);
            IoEnd(entity)?.DisconnectAll();
            return links;
        }

        private static void Regrid(BaseEntity entity)
        {
            BaseEntity.Query.Server?.Move(entity);
            if (Sprinkler.SplashableGrid.Grid.Contains(entity))
                Sprinkler.SplashableGrid.RegisterEntity(entity);
            foreach (BaseEntity child in entity.children.ToList())
                if (child != null && !child.IsDestroyed)
                    Regrid(child);
        }

        // Farm-local x and z, as the layout writes them, and the height.
        private string Where(Vector3 world)
        {
            Vector2 local = LocalOf(world);
            return $"({local.x:0.0}, {local.y:0.0}) at {world.y:0.00} m";
        }

        // Retires one record for real: its chickens first, then its items and water into its replacement and the
        // live targets in the planned order. Emptied, it leaves the record before the kill, so OnEntityKill counts
        // no loss; otherwise it stays standing, retired, unwired, and a lifted one goes back where it stood.
        private UpgradeRetire RetireNow(UpgradePlan live, UpgradeRetire planned, UpgradeStash stash)
        {
            StructureRecord rec = planned.Rec;
            var done = new UpgradeRetire { Rec = rec, Carry = planned.Carry };
            BaseEntity entity = ResolveStructure(rec);
            if (entity == null)
            {
                _data.Structures.Remove(rec);
                _repairs.Remove(rec.Id);
                done.Gone = true;
                if (stash != null)
                    stash.Settled = true;
                return done;
            }
            rec.Retired = true;
            done.Entity = entity;
            done.Links.AddRange(IoLinks(entity));
            if (entity is PlanterBoxStatic)
                HarvestOnly(entity, live.Pass);
            int plants = entity is PlanterBoxStatic ? 0 : PlantCount(entity);
            string attached = Attached(entity, NoIds);
            var coop = entity as ChickenCoop;
            if (plants > 0)
            {
                done.Stays = PlantsText(plants);
            }
            else if (attached != null)
            {
                done.Stays = $"{attached} is attached to it, and a kill would take it along";
            }
            else if (coop == null || ReserveCoops(live, done, coop, true))
            {
                foreach (Item item in StoredItems(entity))
                    MoveNow(live, done, item, false);
                foreach (Item item in StoredWater(entity))
                    MoveNow(live, done, item, true);
                List<Item> left = StoredItems(entity).Concat(StoredWater(entity)).ToList();
                if (left.Count > 0)
                    done.Stays = $"no room for {Summarize(left)}";
                else
                    CarryCharge(live, done, entity);
            }
            if (done.Stays != null)
            {
                if (stash != null)
                    PutBack(stash);
                if (stash != null && stash.InWay)
                    done.Notes.Add($"back where it stood, {stash.Why}");
                IoEnd(entity)?.DisconnectAll();
                return done;
            }
            _data.Structures.Remove(rec);
            _structureIds.Remove(rec.NetId);
            _repairs.Remove(rec.Id);
            if (stash != null)
                Unlift(stash);
            try
            {
                entity.Kill();
            }
            catch (Exception e)
            {
                PrintError(e.ToString());
                done.Threw = $"its kill threw {e.GetType().Name}: {e.Message}";
                if (!entity.IsDestroyed)
                {
                    _data.Structures.Add(rec);
                    _structureIds.Add(rec.NetId);
                    done.Stays = done.Threw;
                }
                else
                    done.Notes.Add(done.Threw);
            }
            if (stash != null)
                stash.Settled = true;
            return done;
        }

        // MoveToContainers' walk, one target at a time so each target's share is reported.
        private void MoveNow(UpgradePlan live, UpgradeRetire done, Item item, bool water)
        {
            string shortname = item.info.shortname;
            ItemContainer origin = item.parent;
            List<string> roles = water ? WaterRoles(done.Rec.Role) : ItemRoles(done.Rec.Role, item.info);
            foreach (UpgradeTarget target in MoveTargets(live, done, roles, water))
            {
                if (item.amount <= 0 || item.parent != origin)
                    break;
                int moved = MoveToContainers(item, new List<ItemContainer> { target.Container });
                if (moved > 0)
                    AddMove(done.Moves, target.Id, shortname, moved, water);
            }
        }

        // "foundation-01 Stone skin 0 to Wood skin 10232" for a standing block whose grade or skin is not its entry's
        // TargetGrade (a skin its prefab lacks counts as met, as ApplyGrade falls back to plain); with apply it also
        // regrades the block.
        private string RegradeStep(LayoutEntry entry, UpkeepPass pass, bool apply)
        {
            var block = pass.ById(entry.Id) as BuildingBlock;
            BuildingGrade.Enum grade;
            ulong skin;
            if (block == null || !TargetGrade(entry, out grade, out skin))
                return null;
            bool skinMet = block.skinID == skin || !HasGradeSkin(block, grade, skin);
            if (block.grade == grade && skinMet)
                return null;
            ulong effective = skin != 0 && !HasGradeSkin(block, grade, skin) ? 0UL : skin;
            string line = $"{entry.Id} {block.grade} skin {block.skinID} to {grade} skin {effective}";
            if (!apply)
                return line;
            StructureRecord rec = _data.Structures.FirstOrDefault(r => r.Id == entry.Id && r.NetId == IdOf(block));
            if (rec == null)
                return null;
            ApplyGrade(block, rec, grade, skin, false);
            return line;
        }

        // Fridges first for "Fridge items", as Targets does, then the source's own role, then the item's home box.
        private List<string> ItemRoles(string role, ItemDefinition def)
        {
            var roles = new List<string>();
            if (_config.FridgeItems.Contains(def.shortname))
                roles.Add(RoleFridge);
            roles.Add(role);
            string home = HomeBox(def);
            if (home != null)
                roles.Add(home);
            return roles.Distinct().ToList();
        }

        // Water goes to the source's own role first, then to the liquid containers of the other water source roles
        // but pumps, whose store the sprinklers drain.
        private List<string> WaterRoles(string role) =>
            new[] { role }.Concat(_config.WaterSourceRoles.Where(r => r != RolePump)).Distinct().ToList();

        // The box role upkeep stores an item in, or null for an item no step stores.
        private string HomeBox(ItemDefinition def)
        {
            string name = def.shortname;
            string role;
            if (_config.SweptItems.TryGetValue(name, out role))
                return role;
            if (name == MilkShortname)
                return BoxMilk;
            if (name == EggShortname)
                return BoxEggs;
            if (name == WoolShortname)
                return BoxWool;
            if (name == DungShortname || name == FertilizerShortname)
                return BoxDung;
            if (name.StartsWith("clone.", StringComparison.Ordinal))
                return BoxClones;
            if (CullYield().Contains(name))
                return BoxMeat;
            return def.category == ItemCategory.Food ? BoxFeed : null;
        }

        // What CorpseTargets sends to the meat box: each herd prefab's corpse yield but wool.
        private HashSet<string> CullYield()
        {
            if (_cullYield != null)
                return _cullYield;
            var names = new HashSet<string>();
            IEnumerable<string> prefabs = _config.StarterHerd.Select(e => e.Prefab)
                .Concat(new[] { CowPrefab, BullPrefab, CalfPrefab, CalfMalePrefab, SheepPrefab, LambPrefab });
            foreach (string prefab in prefabs.Where(p => !string.IsNullOrEmpty(p)).Distinct())
            {
                GameObject animal = PrefabObject(prefab);
                LivestockAnimalFSM fsm = animal == null ? null : animal.GetComponent<LivestockAnimalFSM>();
                GameObject body = PrefabObject(fsm?.dead?.Corpse?.resourcePath);
                ResourceDispenser dispenser = body == null ? null : body.GetComponent<ResourceDispenser>();
                if (dispenser == null)
                    continue;
                foreach (ItemAmount amount in (dispenser.containedItems ?? new List<ItemAmount>())
                         .Concat(dispenser.finishBonus ?? new List<ItemAmount>()))
                    if (amount?.itemDef != null && amount.itemDef.shortname != WoolShortname)
                        names.Add(amount.itemDef.shortname);
            }
            return _cullYield = names;
        }

        // The entry replacing the source with another prefab first, then the role rules' targets.
        private List<UpgradeTarget> MoveTargets(UpgradePlan plan, UpgradeRetire r, List<string> roles, bool water)
        {
            var targets = new List<UpgradeTarget>();
            UpgradeTarget carry = r.Carry == null ? null : CarryTarget(plan, r.Carry, water);
            if (carry != null)
                targets.Add(carry);
            foreach (UpgradeTarget target in roles.SelectMany(role => RoleTargets(plan, role, water)))
                if (!targets.Contains(target))
                    targets.Add(target);
            return targets;
        }

        private UpgradeTarget CarryTarget(UpgradePlan plan, LayoutEntry entry, bool water)
        {
            BaseEntity standing = plan.Pass.ById(entry.Id);
            if (standing != null)
                return TargetFor(plan, entry.Id, standing, null, water);
            return !plan.Live && plan.Placing.Contains(entry.Id) ? TargetFor(plan, entry.Id, null, entry, water) : null;
        }

        // The containers of a role in layout order, as BeginPass gives them to upkeep and to the confirm: the standing
        // records, then (planning only) the parts that move into an entry of that role and the entries of that role
        // step 1 places, as the empty inventory their prefab starts with. Never a retiring record, nor (on confirm)
        // one an earlier upgrade retired.
        private List<UpgradeTarget> RoleTargets(UpgradePlan plan, string role, bool water)
        {
            string key = (water ? "water:" : "items:") + role;
            List<UpgradeTarget> targets;
            if (plan.Targets.TryGetValue(key, out targets))
                return targets;
            plan.Targets[key] = targets = new List<UpgradeTarget>();
            var found = new List<KeyValuePair<string, Func<UpgradeTarget>>>();
            foreach (BaseEntity entity in plan.Pass.Role(role))
            {
                ulong id = IdOf(entity);
                if (entity == null || entity.IsDestroyed || plan.Retiring.Contains(id) ||
                    plan.Shifting.ContainsKey(id) || (plan.Live && plan.Pass.Retired.Contains(id)))
                    continue;
                string name = plan.Pass.Name(entity);
                BaseEntity standing = entity;
                found.Add(new KeyValuePair<string, Func<UpgradeTarget>>(name,
                    () => TargetFor(plan, name, standing, null, water)));
            }
            if (!plan.Live)
            {
                foreach (UpgradeShift shift in plan.Shifts.Where(s => s.Entry.Role == role))
                    found.Add(new KeyValuePair<string, Func<UpgradeTarget>>(shift.Entry.Id,
                        () => TargetFor(plan, shift.Entry.Id, shift.Entity, null, water)));
                foreach (LayoutEntry entry in plan.Place.Where(e => e.Role == role && plan.Placing.Contains(e.Id)))
                    found.Add(new KeyValuePair<string, Func<UpgradeTarget>>(entry.Id,
                        () => TargetFor(plan, entry.Id, null, entry, water)));
            }
            Dictionary<string, int> order = LayoutIndex();
            foreach (KeyValuePair<string, Func<UpgradeTarget>> target in found
                         .Select((pair, index) => new { pair, index })
                         .OrderBy(t => IndexIn(order, t.pair.Key))
                         .ThenBy(t => t.index)
                         .Select(t => t.pair))
                AddTarget(targets, target.Value());
            return targets;
        }

        private static int IndexIn(Dictionary<string, int> order, string id)
        {
            int index;
            return id != null && order.TryGetValue(id, out index) ? index : int.MaxValue;
        }

        private Dictionary<string, int> LayoutIndex()
        {
            var order = new Dictionary<string, int>();
            for (int i = 0; i < _config.Layout.Entries.Count; i++)
                if (_config.Layout.Entries[i].Id != null && !order.ContainsKey(_config.Layout.Entries[i].Id))
                    order[_config.Layout.Entries[i].Id] = i;
            return order;
        }

        private static void AddTarget(List<UpgradeTarget> targets, UpgradeTarget target)
        {
            if (target != null && !targets.Contains(target))
                targets.Add(target);
        }

        // One target per entry and kind (items or water), so its SimContainer serves every source of the plan.
        private static UpgradeTarget TargetFor(UpgradePlan plan, string id, BaseEntity entity, LayoutEntry planned,
            bool water)
        {
            string key = (water ? "water:" : "items:") + id;
            UpgradeTarget target;
            if (plan.TargetsById.TryGetValue(key, out target))
                return target;
            ItemContainer container = entity != null
                ? water ? WaterInventory(entity) : entity is LiquidContainer ? null : InventoryOf(entity)
                : planned == null
                    ? null
                    : water
                        ? VirtualWater(planned.Prefab)
                        : VirtualItems(planned.Prefab);
            target = container == null
                ? null
                : new UpgradeTarget { Id = id, Container = container, Room = new SimContainer(container) };
            plan.TargetsById[key] = target;
            return target;
        }

        private static GameObject PrefabObject(string path) =>
            string.IsNullOrEmpty(path) ? null : GameManager.server.FindPrefab(path);

        private static ItemContainer VirtualItems(string prefab)
        {
            GameObject go = PrefabObject(prefab);
            return go == null || go.GetComponent<LiquidContainer>() != null ? null : VirtualInventory(go);
        }

        // A trough keeps its water in a child LiquidContainer (HitchTrough.waterStoragePrefab), a coop in slot 2.
        private static ItemContainer VirtualWater(string prefab)
        {
            GameObject go = PrefabObject(prefab);
            if (go == null)
                return null;
            var trough = go.GetComponent<HitchTrough>();
            if (trough != null)
                return trough.waterStoragePrefab != null && trough.waterStoragePrefab.isValid
                    ? VirtualInventory(PrefabObject(trough.waterStoragePrefab.resourcePath))
                    : null;
            return go.GetComponent<LiquidContainer>() != null || go.GetComponent<ChickenCoop>() != null
                ? VirtualInventory(go)
                : null;
        }

        // The empty inventory a spawned prefab starts with: StorageContainer.CreateInventory, or
        // ContainerIOEntity.CreateInventory for a fridge or liquid container, then what each ServerInit changes
        // (PlanterBox takes only fertilizer, ChickenCoop reserves its egg output slot, BaseOven sizes it). It is
        // never spawned or filled, only measured through a SimContainer.
        private static ItemContainer VirtualInventory(GameObject go)
        {
            if (go == null)
                return null;
            var container = new ItemContainer();
            var storage = go.GetComponent<StorageContainer>();
            if (storage != null)
            {
                var oven = storage as BaseOven;
                container.allowedContents = storage.allowedContents == 0
                    ? ItemContainer.ContentsType.Generic
                    : storage.allowedContents;
                container.SetOnlyAllowedItems(storage.allowedItems ?? new ItemDefinition[0], storage.allowedItem,
                    storage.allowedItem2);
                container.SetBlacklist(storage.blockedItems);
                container.maxStackSize = storage.maxStackSize;
                container.ServerInitialize(null,
                    oven != null ? oven.fuelSlots + oven.inputSlots + oven.outputSlots : storage.inventorySlots);
                container.canAcceptItem = oven != null ? OvenFilter(oven) : storage.ItemFilter;
                var planter = storage as PlanterBox;
                if (planter != null)
                {
                    container.SetOnlyAllowedItem(planter.allowedItem);
                    container.canAcceptItem = planter.InventoryItemFilter;
                }
                var coop = storage as ChickenCoop;
                if (coop != null)
                    container.slotIsReserved = coop.SlotIsReserved;
                return container;
            }
            var io = go.GetComponent<ContainerIOEntity>();
            if (io == null)
                return null;
            container.allowedContents =
                io.allowedContents == 0 ? ItemContainer.ContentsType.Generic : io.allowedContents;
            container.SetOnlyAllowedItem(io.onlyAllowedItem);
            container.maxStackSize = io.maxStackSize;
            container.ServerInitialize(null, io.numSlots);
            var fridge = io as Fridge;
            var liquid = io as LiquidContainer;
            if (fridge != null)
                container.canAcceptItem = fridge.CanAcceptItem;
            else if (liquid != null)
                container.canAcceptItem = liquid.CanAcceptItem;
            return container;
        }

        // BaseOven.ItemFilter with the slot ranges a spawned oven has: the prefab's own filter reads ranges that
        // PreInitShared sets only on a spawned oven.
        private static Func<BasePlayer, Item, int, bool> OvenFilter(BaseOven oven)
        {
            int input = oven.fuelSlots;
            int output = input + oven.inputSlots;
            return (player, item, slot) =>
            {
                if (oven.onlyAcceptCategory != ItemCategory.All && item.info.category != oven.onlyAcceptCategory)
                    return false;
                if (oven.IsBurnableItem(item))
                    return slot >= 0 && slot < input;
                if (oven.IsOutputItem(item))
                    return slot >= output && slot < output + oven.outputSlots;
                return oven.IsMaterialInput(item) && slot >= input && slot < output;
            };
        }

        // Every standing coop that is not retiring, then (planning only) every coop a part moves into and every coop
        // step 1 places, with its free places; a plan reserves places in them as it goes.
        private List<CoopRoom> CoopRooms(UpgradePlan plan)
        {
            if (plan.Coops != null)
                return plan.Coops;
            plan.Coops = new List<CoopRoom>();
            // The confirm builds these lazily, so a retiring coop an earlier retirement killed (net id 0) is skipped
            // by IsDestroyed, not by its id.
            foreach (ChickenCoop coop in plan.Pass.Role(RoleCoop).OfType<ChickenCoop>())
            {
                ulong id = IdOf(coop);
                if (coop != null && !coop.IsDestroyed && !plan.Retiring.Contains(id) &&
                    !plan.Shifting.ContainsKey(id) && !(plan.Live && plan.Pass.Retired.Contains(id)))
                    plan.Coops.Add(Room(plan.Pass.Name(coop), coop));
            }
            if (plan.Live)
                return plan.Coops;
            foreach (UpgradeShift shift in plan.Shifts.Where(s => s.Entry.Role == RoleCoop && s.Entity is ChickenCoop))
                plan.Coops.Add(Room(shift.Entry.Id, (ChickenCoop)shift.Entity));
            foreach (LayoutEntry entry in plan.Place.Where(e => e.Role == RoleCoop && plan.Placing.Contains(e.Id)))
            {
                GameObject go = PrefabObject(entry.Prefab);
                ChickenCoop prefab = go == null ? null : go.GetComponent<ChickenCoop>();
                if (prefab != null)
                    plan.Coops.Add(new CoopRoom { Id = entry.Id, Free = prefab.MaxChickens });
            }
            return plan.Coops;
        }

        private static CoopRoom Room(string id, ChickenCoop coop) => new CoopRoom
        {
            Id = id,
            Coop = coop,
            Free = coop.MaxChickens - coop.Animals.Count,
            Hatching = coop.HasFlag(ChickenCoop.Hatching) || coop.Animals.Any(s => !s.SpawnedAnimal.IsSet),
        };

        // Places for every chicken and hatching egg of a retiring coop, its replacement first, reserved in the plan's
        // coops; with apply the animals move. False, with r.Stays, when they do not all fit.
        private bool ReserveCoops(UpgradePlan plan, UpgradeRetire r, ChickenCoop coop, bool apply)
        {
            int chickens = coop.Animals.Count(s => s.SpawnedAnimal.IsSet && s.SpawnedAnimal.Get(true) != null);
            int eggs = coop.Animals.Count(s => !s.SpawnedAnimal.IsSet);
            if (chickens + eggs == 0)
                return true;
            string first = r.Carry?.Id;
            List<CoopRoom> rooms = CoopRooms(plan);
            rooms = rooms.Where(c => c.Id == first).Concat(rooms.Where(c => c.Id != first)).ToList();
            List<CoopPick> picks = PickCoops(rooms, chickens, eggs);
            if (picks == null)
            {
                r.Stays = $"its {AnimalsText(chickens, eggs)} do not fit in the other coops " +
                          $"({rooms.Sum(c => Math.Max(0, c.Free))} free places), so it keeps them and its items";
                return false;
            }
            if (apply)
                HandOverChickens(coop, picks);
            foreach (CoopPick pick in picks)
            {
                pick.Room.Free -= pick.Chickens + pick.Eggs;
                pick.Room.Hatching |= pick.Eggs > 0;
                r.Chickens.Add($"{AnimalsText(pick.Chickens, pick.Eggs)} to {pick.Room.Id}");
            }
            return true;
        }

        // All or nothing, coops in the order given. At most one hatching egg per coop, and none into a coop already
        // hatching: CheckEggHatchState stops its timer once the first egg hatches, so a second would never hatch.
        private static List<CoopPick> PickCoops(List<CoopRoom> rooms, int chickens, int eggs)
        {
            var picks = new List<CoopPick>();
            foreach (CoopRoom room in rooms)
            {
                if (chickens == 0 && eggs == 0)
                    break;
                int free = Math.Max(0, room.Free);
                int egg = eggs > 0 && !room.Hatching && free > 0 ? 1 : 0;
                int hens = Math.Min(free - egg, chickens);
                if (egg + hens == 0)
                    continue;
                picks.Add(new CoopPick { Room = room, Chickens = hens, Eggs = egg });
                chickens -= hens;
                eggs -= egg;
            }
            return chickens == 0 && eggs == 0 ? picks : null;
        }

        // FarmableAnimal finds its coop through its parent, so a chicken moves by reparenting it onto a move point of
        // the new coop, with its AnimalStatus entry; a hatching egg is only its entry. Both coops then get the flags
        // and hatch timer SubmitEggForHatching and OnAnimalDied leave.
        private static void HandOverChickens(ChickenCoop from, List<CoopPick> picks)
        {
            List<ChickenCoop.AnimalStatus> eggs = from.Animals.Where(s => !s.SpawnedAnimal.IsSet).ToList();
            List<ChickenCoop.AnimalStatus> hens = from.Animals
                .Where(s => s.SpawnedAnimal.IsSet && s.SpawnedAnimal.Get(true) != null)
                .ToList();
            int nextEgg = 0;
            int nextHen = 0;
            foreach (CoopPick pick in picks)
            {
                ChickenCoop to = pick.Room.Coop;
                var moving = new List<ChickenCoop.AnimalStatus>();
                for (int i = 0; i < pick.Eggs; i++)
                    moving.Add(eggs[nextEgg++]);
                for (int i = 0; i < pick.Chickens; i++)
                    moving.Add(hens[nextHen++]);
                foreach (ChickenCoop.AnimalStatus status in moving)
                {
                    FarmableAnimal chicken = status.SpawnedAnimal.Get(true);
                    if (chicken != null)
                    {
                        Vector3 point = to.GetRandomMovePoint();
                        chicken.SetParent(to);
                        chicken.transform.localPosition = chicken.SnapMovementPos(point);
                        chicken.SendNetworkUpdate();
                    }
                    to.Animals.Add(status);
                    int index = chicken != null
                        ? from.Animals.FindIndex(s => s.SpawnedAnimal.uid == chicken.net.ID)
                        : from.Animals.FindIndex(s => !s.SpawnedAnimal.IsSet);
                    if (index >= 0)
                        from.Animals.RemoveAt(index);
                }
                SetCoopState(to);
            }
            SetCoopState(from);
        }

        private static void SetCoopState(ChickenCoop coop)
        {
            bool hatching = coop.Animals.Any(s => !s.SpawnedAnimal.IsSet);
            using (BaseEntity.FlagsUpdateScope flags = coop.StartSetFlags(BaseEntity.FlagsUpdateMode.SendNetworkUpdate))
            {
                flags.Set(ChickenCoop.Hatching, hatching);
                flags.Set(ChickenCoop.Full, coop.Animals.Count >= coop.MaxChickens);
            }
            if (hatching && !coop.IsInvoking(coop.CheckEggHatchState))
                coop.InvokeRepeating(coop.CheckEggHatchState, 10f, 10f);
            else if (!hatching)
                coop.CancelInvoke(coop.CheckEggHatchState);
            coop.SendNetworkUpdate();
        }

        private static string AnimalsText(int chickens, int eggs)
        {
            var parts = new List<string>();
            if (chickens > 0)
                parts.Add(chickens == 1 ? "1 chicken" : $"{chickens} chickens");
            if (eggs > 0)
                parts.Add(eggs == 1 ? "1 hatching egg" : $"{eggs} hatching eggs");
            return string.Join(" and ", parts);
        }

        // Everything a retiring entity stores but its water: a coop's water slot and a liquid container's contents
        // move by the water rule.
        private static List<Item> StoredItems(BaseEntity entity)
        {
            ItemContainer inventory = entity is LiquidContainer ? null : InventoryOf(entity);
            if (inventory == null)
                return new List<Item>();
            Item water = entity is ChickenCoop ? inventory.GetSlot(ChickenCoop.WaterSlot) : null;
            return inventory.itemList.Where(i => i != water && i.amount > 0).ToList();
        }

        private static List<Item> StoredWater(BaseEntity entity)
        {
            ItemContainer water = WaterInventory(entity);
            if (water == null)
                return new List<Item>();
            if (entity is ChickenCoop)
            {
                Item slot = water.GetSlot(ChickenCoop.WaterSlot);
                return slot != null && slot.amount > 0 ? new List<Item> { slot } : new List<Item>();
            }
            return water.itemList.Where(i => i.amount > 0).ToList();
        }

        // A liquid container's inventory, a trough's water end, or a coop's inventory (water lives in slot 2).
        private static ItemContainer WaterInventory(BaseEntity entity) =>
            entity is LiquidContainer || entity is ChickenCoop
                ? InventoryOf(entity)
                : (entity as HitchTrough)?.WaterStorage?.inventory;

        private static int PlantCount(BaseEntity entity) =>
            entity is PlanterBox ? entity.children.Count(c => c is GrowableEntity && !c.IsDestroyed) : 0;

        // Children a kill would take along: anything but a trough's water end, a coop's chickens, a planter's plants,
        // and parts this upgrade retires first.
        private string Attached(BaseEntity entity, HashSet<ulong> retiring)
        {
            BaseEntity waterEnd = (entity as HitchTrough)?.WaterStorage;
            List<string> names = entity.children
                .Where(c => c != null && !c.IsDestroyed && c != waterEnd &&
                            !(c is FarmableAnimal && entity is ChickenCoop) &&
                            !(c is GrowableEntity && entity is PlanterBox) && !retiring.Contains(IdOf(c)))
                .Select(NameOf)
                .ToList();
            return names.Count > 0 ? string.Join(", ", names) : null;
        }

        // "solar-02 out 0 to battery-01 in 0" for every wire or hose on the entity.
        private List<string> IoLinks(BaseEntity entity)
        {
            var links = new List<string>();
            IOEntity io = IoEnd(entity);
            if (io == null)
                return links;
            for (int i = 0; i < io.outputs.Length; i++)
            {
                IOEntity other = io.outputs[i].connectedTo.Get();
                if (other != null)
                    links.Add($"{NameOf(io)} out {i} to {NameOf(other)} in {io.outputs[i].connectedToSlot}");
            }
            for (int i = 0; i < io.inputs.Length; i++)
            {
                IOEntity other = io.inputs[i].connectedTo.Get();
                if (other != null)
                    links.Add($"{NameOf(other)} out {io.inputs[i].connectedToSlot} to {NameOf(io)} in {i}");
            }
            return links;
        }

        private string UpgradeReport(UpgradePlan plan)
        {
            var lines = new List<string>
            {
                plan.Empty
                    ? "farm.upgrade: nothing to do"
                    : plan.Blocked
                        ? "farm.upgrade plan (farm.upgrade confirm refuses it while a problem below blocks it):"
                        : "farm.upgrade plan (farm.upgrade confirm applies it):",
            };
            if (plan.Place.Count > 0)
            {
                lines.Add($"place {plan.Place.Count} entries:");
                lines.AddRange(plan.PlaceLines.Select(l => "  " + l));
                var planters = new HashSet<string>(plan.Place
                    .Where(e => e.Role == RolePlanter && plan.Placing.Contains(e.Id)).Select(e => e.Id));
                Dictionary<string, int> clones = PlannedSlots(planters);
                if (clones.Count > 0)
                    lines.Add($"  starter clones for the new planters in {BoxClones} ({_config.CloneGenes}): " +
                              string.Join(", ", clones.Select(c => $"{c.Key} x{2 * c.Value}")));
            }
            if (plan.Shifts.Count > 0)
            {
                lines.Add($"move {plan.Shifts.Count} records (each keeps its items, crops, chickens, charge, and net " +
                          "id):");
                lines.AddRange(plan.Shifts.Select(s => $"  {s.OldId} to {s.Entry.Id}: " + (s.Posed
                    ? $"{Where(s.From)} to {Where(s.To)}"
                    : "its new pose fails (problems say why)")));
            }
            if (plan.Stash.Count > 0)
            {
                lines.Add($"lift {plan.Stash.Count} old parts {StashHeight:0} m up while the new entries are placed, " +
                          "then move, retire, or put them back:");
                lines.AddRange(plan.Stash.Select(s => $"  {s.Id}: {s.Why}"));
            }
            AppendRetire(lines, plan.Retire, plan.Gone, plan.Unretire, false);
            if (plan.Regrade.Count > 0)
            {
                lines.Add($"regrade {plan.Regrade.Count} blocks:");
                lines.AddRange(plan.Regrade.Select(l => "  " + l));
            }
            if (plan.Wire.Count + plan.Drop.Count > 0)
            {
                lines.Add($"rewire {plan.Wire.Count + plan.Drop.Count} links:");
                lines.AddRange(plan.Wire.Select(l => $"  wire {l}"));
                lines.AddRange(plan.Drop.Select(l => $"  drop {l}"));
            }
            if (plan.Kept.Count > 0)
            {
                lines.Add("kept where it stands (an Id that stays never moves; a new Id that Replaces it does):");
                lines.AddRange(plan.Kept.Select(k => "  " + k));
            }
            lines.Add(plan.Problems.Count == 0 ? "problems: none" : $"problems ({plan.Problems.Count}):");
            lines.AddRange(plan.Problems.Select(p => "  " + p));
            return string.Join("\n", lines);
        }

        private static void AppendRetire(List<string> lines, List<UpgradeRetire> retire, List<StructureRecord> gone,
            List<StructureRecord> back, bool done)
        {
            List<string> vanished = retire.Where(r => r.Gone).Select(r => $"{r.Rec.Id} ({r.Rec.Role})")
                .Concat(gone.Select(g => $"{g.Id} ({g.Role})"))
                .ToList();
            int count = retire.Count(r => !r.Gone) + vanished.Count;
            if (count > 0)
            {
                lines.Add(done ? $"retired {count} records:" : $"retire {count} records:");
                foreach (UpgradeRetire r in retire.Where(r => !r.Gone && !r.Bare))
                    lines.Add("  " + RetireLine(r, done));
                List<string> bare = retire.Where(r => r.Bare).Select(r => r.Rec.Id).ToList();
                if (bare.Count > 0)
                    lines.Add($"  {(done ? "removed" : "remove")}, nothing to move: {IdRanges(bare)}");
                if (vanished.Count > 0)
                    lines.Add($"  already gone, {(done ? "dropped" : "drop")} from the record: " +
                              string.Join(", ", vanished));
            }
            if (back.Count > 0)
                lines.Add($"{(done ? "tracked again" : "track again")} as entries (retired before, back in the " +
                          $"layout): {string.Join(", ", back.Select(b => b.Id))}");
        }

        // "box-feed (box_feed), replaced by barn-barrel-feed: potato x40 to barn-barrel-feed; then removed"
        private static string RetireLine(UpgradeRetire r, bool done)
        {
            List<string> parts = r.Chickens.Concat(r.Moves.Select(m => m.ToString())).Concat(r.Notes).ToList();
            string line = $"{r.Rec.Id} ({r.Rec.Role})" +
                          (r.Carry != null ? $", replaced by {r.Carry.Id}" : string.Empty) +
                          (parts.Count > 0 ? ": " + string.Join(", ", parts) : string.Empty);
            if (r.NoRoom.Count > 0)
                return line + $"; no room for {string.Join(", ", r.NoRoom)}, so it would stay standing, retired";
            if (r.Stays != null)
                return line + $"; {(done ? "stays" : "would stay")} standing, retired: {r.Stays}";
            return line + (done ? "; removed" : "; then removed");
        }

        // "fence-01 to 63, gate-01": consecutive numbers of one prefix and width collapse into a range.
        private static string IdRanges(IEnumerable<string> ids)
        {
            var runs = new List<List<string>>();
            foreach (string id in ids)
            {
                List<string> run = runs.Count > 0 ? runs[runs.Count - 1] : null;
                if (run != null && Follows(run[run.Count - 1], id))
                    run.Add(id);
                else
                    runs.Add(new List<string> { id });
            }
            return string.Join(", ", runs.Select(r =>
            {
                string last = r[r.Count - 1];
                return r.Count == 1 ? r[0] : $"{r[0]} to {last.Substring(last.Length - TrailingDigits(last))}";
            }));
        }

        private static bool Follows(string previous, string next)
        {
            int digits = TrailingDigits(previous);
            int a;
            int b;
            return digits > 0 && digits < 10 && previous.Length == next.Length && TrailingDigits(next) == digits &&
                   string.CompareOrdinal(previous, 0, next, 0, previous.Length - digits) == 0 &&
                   int.TryParse(previous.Substring(previous.Length - digits), out a) &&
                   int.TryParse(next.Substring(next.Length - digits), out b) && b == a + 1;
        }

        private static int TrailingDigits(string id)
        {
            int digits = 0;
            while (digits < id.Length && char.IsDigit(id[id.Length - 1 - digits]))
                digits++;
            return digits;
        }

        #endregion

        #region Signs

        // Generated by research/farm/v2/plugin/tools/sign_images.py from research/farm/v2/construction/assets/.
        private static readonly Dictionary<string, string> SignImages = new Dictionary<string, string>
        {
            ["pandora-farm-sign.huge.wood-1024x256"] =
                "iVBORw0KGgoAAAANSUhEUgAABAAAAAEACAIAAACiR5NzAABHb0lEQVR42uzdtZr1uhUG4H0VaVOFGapcQq4kZaowcxNswszMzMzM" +
                "acL3ETg841n/yFvWXkt6n+etzr/PkG3p+wzy6S/fftP5/vC1NxD49RdfM97PPvOqg/zoEy8rJPhFBgg2EJBnDGSiEZiGyYhlo2lQ" +
                "AA4n9HdXfQoxXAKKB6I212MVfUP46Yjv5By/Wceo2mWnumnvYsqdk/pDNOQiLhMVgKIkfgxzcyd1HJgjAWL0337w9kUoAKJ/mYkf" +
                "Ob4VRgYQhaeN0SgAQpvB9Dizpvz8jx6uQG24zpCF0MzE/v3zD2RwGhaJMKWJ++WTvSivOSgMhlP5GKG8vpNVfdbJ65TI9GI6msZS" +
                "FUKIHw84lU5vRmrSpvkM2R2UigWvSCgPwFkF4CkPfVA5AABAVACCf6v4qwIAAMFZ/tkKAAAAENwrqAAAAEAiCgAAAKAAAAAACgAA" +
                "ABAUgOZlQK00f4SLvBeW1r8kdjz7Kg4f9r3pwoF8aI7yLtfW0F61AGTbqGaFM0lFEoZ3YDmyIJsdg1XFA0oQr6+9AAR/oPhqQl0S" +
                "mJBRmiDOyB7ikIdzCoAI7iXWY16G3fwisGB33Pxam59EfKdJ6cj+vQ+/iLQmbhfGn0CJ82jVTxTectQNdlTpXHDvRQHoT3Afzw5T" +
                "Pal/8/3PvQjWqRmGROePL94EdlwBCDaQjI4CIOIvEdCvDKlpb81KkuPF9AVpGtXrhMIwpbAAbIsLgCifgmcAOhaAJz/2kfd308cw" +
                "JeQP9EfneNkd5qgTzgf911JXAIIR/vqOceafyEUArkTrYx8CjgtAnP51AEPtrIFejgddYqknKJyoCtJ/sKtc2Y67f3jYjNa9Vu5p" +
                "XgUo/hGf9fSn/Vfwg15hALJ00n/lD/SiPOgSvVj6qeY9w2H6b6gBHaywcCJBtO5y287OAhCk/7gDkE22k/QCvQdMaWXfduxYZ/ZM" +
                "4RS2Hf3vuOmDZ4JjcMdo3aEAnH87kQIg048P9HLPmktPslT3wDF+qX4SH3q333D7ShHcMVpf/182Q3vwLfoUgOA6xX+d84pf5rjr" +
                "RqY3WzOTdfqGFqFy7N7HjMm7ecFiEK1zFYCY9P9fCZettMqNe4Jd9y9B2XChQ2EoupV3D/UoALEOBaDLMwAx8b3QCvTzzYsJgrvw" +
                "jWaibKAAuJDrpFWKAtDtIeD/umz690bYQPUzXpbjEBNNTlyneCSnCQj3yw6AvZ8D3tBnFaDg32Z9D0DROSnDGOd+d4kKc63WkZBW" +
                "k/a3MERYeXa3OFqf+x6A4I/YejtRfHkioTzDIsav3USBWYkLaCP5bR6wDiIvYO0luK3m3AIQ7HmbX8vRLs1I8LpodxZmSUKXOBMK" +
                "mOR9kSBOuHLPhnoFQHx3F40oL4tjaVqDDyOjs0Qulx9tK6Y3KFAAxHdPrwr0InsXX33XM/PQKEa2C0VCBkVM5/a3AM1ZAIRy8b36" +
                "OktrhvIFC4DKcYU6oTMg1tNq/mcASD4fxP+9YqYfk+MBXUKjgI6rwBMs3XnIKkAxcXwwC6peIc0DpR+iMAkGFA8Y/R4Ad5JMPwCN" +
                "yfQCvaCDJ7lxw2pL9wA6nLXfWQCQ6Z2kdxoyJ3QPcj4dByQqAPHzBApAfjJ9OW47PgeKh6ahVCgk0PTgbocCcOizGuR/NNZUXWUS" +
                "cgtcFRpIq7kva2gUqDpkvAKQP+i7gT5m5cqcA2vOCA7LNg0PeFyhLWgIeAZgWou/UkqC30dYpzR9o9xtVCCIJ1dgFaDgx71UAVj2" +
                "kvcFzxi5i0Zwh6KUDXVF2pa/vc0tuG1nxHsAjKSLXAXOObZK8HOnKOy6DhadRMg2+Hhf8l++/abrNkP75ifvkvRNwE5j5EzqQnxd" +
                "AsdIrmhjcMDhVjdeJ5e3ALi26BJn34EPofw45Ew5gNPb/O0Hb7+lzdAefL65AGSOsIJ75hCP1L7J8TsxXUKeQxYX2TvIUABM5yVm" +
                "erNplTQvmo+HRhFQFRJCNCdXAcBSkn0npNJp3on27hGTnM0h27bWJRQJaR5anwE4cBUgcXzivJ4/5XdP80BrAchZEd3mpFSgfigA" +
                "R70IbMFH742qAv05ZCDszJj1FA8Y9B6AfV9rnVu9jTgyfVHTRwc0kBjOr80EUhQAqpPpPQSiCTtgz6Fs5KFaqCXQetvOqdftROfM" +
                "bThPby5MnsjdtGrEUzNQLdJSh2g9a68AJGK5m0tJOGJmCNYjkzc6yUxXM7QORUJbYMJnABZkvfnSLj665Tk1vi/Eo0XEHzNcL/tQ" +
                "uJqBEF/1IeDgtz2oAHgOL/OAXnEx+PzJPlUBiLmsxwULgMctrD1Vi1gvsvdSYBlQY6K3AgWE+4QkLTMW9uqJoyrGvYNM/h6A1jcB" +
                "Zw7QovkAtcJ9TBZJwpxt/sY6tvk5MSGjZ/C3H7x902Zo3/zkXU5PfeKDb7L5tYLPDwYAALSGdgUgBAAACgAAAKAApAcAAArAv3/+" +
                "gZtsfq3g84MBAACtof301Xc98yabX+ub73/uEequS2OtAGsRFJV6iQ9vpE4wEC21Lsc06/ZYUGURg8f/hCPzNKms1590M7RfCcz3" +
                "D96nIJQHX6uL43YFwV3ut0iZtyllVuUg6rVo3QCDj5S5A3287fJTDwa03C4D7+DUlyGQHDevbYb2YOX6U5DFg691S86499pRkPJL" +
                "sJh6k1o753yZb9heN35r1tp2akC2Xy1BSUh3/iX/jLYZ2oPedQqiefC1epEvFYAuFIBluUYkC06w5yy+HZ3gL/rXS3DH0U4zxcW4" +
                "AAS/yKn1akKJu2UEd5qsUxXM2RLDgrFP0Jfs55N2KySvBLHSraC5ALReTZgpwHm/elo6gzS5g8gu6CfM+nYP1/1uUmXwSXihoFAm" +
                "uWwBCD5fvwCI6YTmbgsLJleEQkHfpkx4sm+C2jCy0SWoBDm6gQIgvqMw1CwJGsI2pjnRK+sP3nZupq14GVkl6J4lpi0AErxFypWE" +
                "fVxJIIPJbucQ9MdstZVPz2WbEVSC0nvCogVAapf1dYPjSoKewKx3bMv6h261tLO8mWWaetB9b5xpW6crAPHvKb4L9wZoTyYM6Ays" +
                "vRiroN9hw3WM9YJB9UXuVYJESWP8MqCyeyriLxY7mrVLeNPCsHU2Bf0S4V5sGDxf7zsGVYLB+W1oARDTB5DdSc4717xArVbQF/Td" +
                "luOG3sBB479KcNR2bA/tdxeA4Pttfi2JvBc3oI9nSQp7hRAv6Cd3gaDfnmxYM4pMUwyOmPcvvgu1nrVPXQAk+xXCGaqFXZG5n/0Q" +
                "9Jk+0lQvBk3jVa5K0B7aFYA9esV6+cl51r60helJ+bK+lN/q0D+CkpClGOSvBANaQXtoP33vwy+6yebX2vhkI8m+lZecF+UqxGqN" +
                "Aks5XWrJHaulZaAedO8GCeJHcAgnaAWpCoBb7XdbNhlQsR6s0CJyFqopD+T5zuuPDwfZTrE7oXD9j6MY/Ie9q+Cx3oiB/QUVQ1lU" +
                "0QlPdIIyMzMzMzMzMzNXUBYU/1pHsmSd7Bd/m83OOg+kaT/K5dlrmtnkJWpzrjzIlwSE5KELgM1TL9kNrsOcXkYCMQdLNhcK0tkn" +
                "ddy2qsrllTE8Nr/s7L+6ihsmav9hmsVxl5KmE1aAkRjC7kiY7cUEQt/LfIPeKAEQZMiMvgOQ0j6yhEGitM1iLRUltywSZa2+hTxb" +
                "U+umY3kJEyzMYTzT037OLzSYw836cdDnf7dGOnueXpUr8I2IUWvOpv7/ffOQh/59oRKQv6eqiyb5w9/mYwkD+lOAlu6um+ZVlN/m" +
                "OFyqm1NMOxskUrWa2twuP7aBNs/bkvlXzYTKzUsnHNMFOXsHvZuGT/xirobD0a98xMnfYIYSUFGYQZeYIao1gJ6hAbmfgDjH4g/9" +
                "6/P7GnxucbbXcZKZXPkfKwCCSZT2JuBEjuJLaKxC7d8OmosB776CN2zGigHf5qgw5sVlPx+2l84vOzfKYP4BhROupLSD0muQt3yM" +
                "jR1PAPiS6QDvVJ9b9k3cTQamI96yLSzwYIDyUCFUCEayEFRuiV9t2bww8lHwqTXUloc+4o8P7xrCWGNiOVGR8H20wXRS1/VNwMv7" +
                "sA45yWzbdHnXq3Dc+O5rkgH1S30Z27jZ0fGDcCFDKsgiOma1WVVy5WT6fSAx8xji+hWjwi9y7KaBSYwUwjEldgwBYFaGcYO7d9zW" +
                "rylh/mP11aogFdMxRInKb7BhDNByO4HCuJgJMsMQCMrVjvpFdS3m5YagBwLAloM788/v3FoBY0aFqTaXCJyzpyRYSNqDhrZXxdWE" +
                "JX8Mn53QPinT4RvExC2QuCGK11qHzWEKL6i6oLuRo2MtNObFAlItZGAso82SpsHO6/SbRmLFZTJZ0vjH124Evn7hmgByDA6Ox1j5" +
                "daG4vVRoEmr4YtcaCoDd1H8KWSxfuiBw5Fdo2cZl0nIicvuewrN/dZOKIL6FQ6RBLDhB9LUfTHz5fTDHPfge2eh4AaBRsI26F9Rg" +
                "sdZKygb3UnLlQbUAGAJbABAIPYH9IxuEEHz61BUMfPjoxQF2H6m8xHSKITFQ8cYT0xClCMVIEsRHOGV6hHHBwLQMMZJnpyy+aQ3B" +
                "jNGVFPPgHSN/1CQJk6RHf+xuoHE2NtxR3uO2uslhCcHbD5z37M2n3nvpUdectnPJ8dvnHL518s7BJ24fZIC/xD/hAByGgx+79gT8" +
                "oJZekKglGkDslNwQw9IDJ4iHn3jXVgOQFiSoFykZU8Kdnwzje5fY3LBZNYQsGrJCVywWAL4BygwN4tLKZR0iamr5sLNDJBV+3AuK" +
                "x73de5L1T/HOj0612cB3bLEWvbcbYjYiIL3XrKFUSBMAq/G0E60f6VzICRACeJcCw0huPfdQMJJX7jpLMxWIW4MiboVDzAlVIe5T" +
                "fQTHWsiwPbydfYzE+mPZzYzxMsA0X2H/4JHwsblJcBlpIDkA2zpnqaHL8BHJabhysFZWA0xj/35jVUUR7EHJoHZ8CLb23TuG8Rdu" +
                "SvVprpoJEbhmBAB+BPWLE+K0qbBqBwIJSWUcNMxPvWvL/vGhuiCkpIWzSFGvAQDqc2wCZtawd/EWDUbCVCuZCi5+io86Q9lDxGsA" +
                "QcD+CUOENe61r5px72e96YTiWpZHaCyiIQNrDfHQiZaS7WbQi83NHzHE1gYNBECDcy3hS0y1ecmOFLJBuEI6TJqiQQjf8kogvuUu" +
                "oNRahMqf0IDEfapf8EW4rLgQdwrfLMBaEo009W8GvNAahnk4p5BRgL0CDbnyxB1lZdKG/ZuUQF1gfOoIaesmTouT4yMM4TAsObgC" +
                "gOORS7Fhue0FYxtJpVvmDWWAWQrdaulWxXDKR42HYHeAMV9IfcZcBCi8iVSKEXOqT2QxCst3kQhDhNhR4R06qox7c71OnRWIa/PJ" +
                "MRgPs31cAjGGCkWDTbEZH4pMkBVeuLYWfGGg+J+9a8fRndmNZynehNfhvTly4tSrcOLEmVMHjpx4HyZQQEEo6uPRfGKJao0A4mLu" +
                "/HPUzX6wq7r5eCwBgHrjspaB3jJsXBKIg5DSgFKgsrzBxTfD7lB9t40gWEwbT0X6SdvtHvAwwbC/xTEjxhcXvT5ThYddwxHrwspn" +
                "aAD+poD+4EK877dqh1MZKBmq1RyAqBeQN8bE0EPL9IUdyDTg+MR9IkIhmD4+lF1jaqCOgwMcTwG0EAGAncECkJ1bH6AEc5fNLOyM" +
                "TG5x32whAP7jHma/OOvHVZOe83GmJgDoMA/Nqd7CpP8URPUSAwc38McArF9w23cx4MYicU7HWSIPhRo5dAD9b6//8XR4mY3I2Drk" +
                "r0TlyvvvGA1YB+lnwU8CbVgJAFwtxwlAIVuaunv5uhsfXLj9ZKjBV2Pw1ZArtQMNiA7UEBk/A/XGXxoIgNe8xPYUGqAT98OUTdvp" +
                "i49fZmowWcUj3hE5f8e0FgEo/KaKAxQG8MpD5BN0y0m3DIfIgDndfQ1ouyAzPM7IjpM1Q0seOq5BAAx5+RzGZ2EC4HiTLZ4FlyMA" +
                "EPiphwXkU0D2CKohtVgNt02UzodFi57fnADAlhVu7ujexQQg+gMCsMS5VXjOFGiy8PnBIAdCxa3/oGq8KVeltPMeAuA3L4TOuzxH" +
                "2E793jtyEQ5FYqnggBc40gj0511MDZSpHC6Nb74efeIQGbhFmhC8zslxD5Up86qlx5ni2ZzTIY7HqxMA86OByhnQ/ncC0FAIzIXg" +
                "G2TpiwECkdg8uxzgiDUU/58r/fDCnGVgbbDdFkegnJGTBNJKABAEEgKLucq5hQHk6B1PMPLpjU6jLKYhclaKggjg5QgAhPH64vJU" +
                "Txwlg+CRIz+maesFJIjWl656XQJQxNfWB2hsc/MhojO760oa8tfnbt6hLCT5uNfknqOqyaGZA4ogec3gIfdZBGD+3eDSQmA3Ka/9" +
                "03GftwsGo1ATALn+D2HOREWu/o13cwIAQaIA6epLAL54SCkCA0IENmX0j+0ZC/5uuy9nTpRb0vivRH7rcoDv8mnKPMqRP3IfyTly" +
                "x+Q9gwDUjvVyi3TxISKWeYAATB/3IVAKUzCuWhGiTZH8DTDpgwSgjiQZLwv9hRwH7TSDf4rP7X7Lhd39Qugm9mvdiwHECdWpgSi7" +
                "iqsf3lyIbYjhlabZYUk4wEsADt6X1wUWBDKGNFwu+lWDxzyVytdd6xIAmpfCj+uTfHITx5E/hWhlyXVh/YcRgDqb++45cp9DBPJI" +
                "AkBOvg3/oyCb/rhq6GQsBpmXhsSD/hihg0nJbyJfg/ZMA/4Uzex+a0Th3nKYMeWrEwCiK/GoOUgACqsxm2eTsu1w7WY6ErUsg/kS" +
                "gNodqOYAChn96N/EAUImc/MZzMsXuXSKZ5zrCXx2ZDoC918CUJwj9zxEinfvZxAAlqmRsoz4QWdh2guI2Dokux7Uj4GzSQK8l9d+" +
                "+emtfScBwEzfX2LKzSZs/i5kyxQL223N3VaEAWw7jL6Np3CuHYFeAnBGPnGAkMKx2Kivwe01B+ctTQBqp97j5Vc5myOmZkvPJP7E" +
                "JDn+4RkEoMgiPX+IsLfzfqT2Gwc6AkG8BKCBm6lJbPD/8WcJW8Cl5Rxop75/CqC8+61p+P4SAI2IOp4XedR3s4i/0e3XQAD8mYsE" +
                "rb4EoNbiE5UqjmocEjcXKXFNMSzaGeZWPwLUBEAZ8kRxK7IXPwHQAOho9HkEIIO5WCTjh0jtsLQ+AdALqZoAjHsBfYpXRldnDzIh" +
                "ALJy2mNc3e4wJ2/tfyMB8McADBturuaaAOBYmnKcKAiAcIBZLCUID+dfJgBvGtDjMcH1cwpQhWk83feRUM2fHnE+Ocxx/59BUxNQ" +
                "Y5uvqZEA/DUKYusJhmXwPAIQf3+rQyRkIN/3xJs/OUBNAKZ7qLEK8UtD4VFX0ttCxgNfz4D2lwCMxwYNVEefDsTRDH0ZVeQNNnuI" +
                "Fs8sFBKA5xQC8x/bxUj6PdPsz0SQZxAAqKbVwQ6UiQ3ZAq8RUwPbmJM1/ZQAnKg0vzYBKBLocave7RCZ8CMd8LERAjCIYYoqOhKo" +
                "IHlHliYAWcbT4XwH2n8FAdBhGnQNHCjAMZ+ItyjnXhOAIp5y8MIpI1cQAEf3YKri+5N3PIYHYhlJ/Ox1/vHnzNFTWQ3L2l5ANQGg" +
                "ZAIQ4zBoaqLzu3HA57E+5EAIxNoEgOrkxAy8zb3zqTcwEX7F72xqYOTjwMqdlFNslgBIlUCIv4yVlxX8LgJwhioBefCFOs456ti+" +
                "2kRMdPY4ARDfzdnDJu/A3GekEcQcUUYQXkaugDhxUYoitZCuBUOnT5h42wiwURXfjbJ4iFpjCp16afSbXHc9YMpCi0wAakBMAgB6" +
                "PGVqYBuPd76Ww6e+ZrO9+HyBfG2TPyVGFJf60UNEO2w4RAwTYailAykxzMDUsGoBBeg/xn/wdVdOVawcIbr3r2x7Mgh4AQKAjrol" +
                "R8TG6oxlgZqjvR7k8Vl8OX4GUnQn68i2e/DRo0CBPyItsCAhmKORrIjb2Ca5uuYSOmPjACVDQWi6fe1lEyHtqxQtUtBErKv2lw20" +
                "lT3mTdf/qJwdQxpvOKFOSPwQ/xdszXQwUzvrlMGwcL7i5/gNbmQdYQCfymntJsAJuc/bHbyApP9dWD9Ldpe/4HwRqWvnFXi6Ts0c" +
                "klIzz7/KFo4cmbfEP8FcLLofcdwLAZADCKqNewHhEYDoPwQsZaRXqEiDCaLzmHDdQm5LCSA3DQK+Z4HlojAWiuNi0/Y+h0GwRWMD" +
                "x+/deNpSvN3gVX8whIvTpHM058IOkb6dHFvMJnF/dit3hFKFUc7Pyo47G1muFJP3PyuR4TTabkOcke168RHgsinb8sMQZosyhdIW" +
                "XjS7IbAIAJg1NTmGwXpOF+cLNlR0yXS+UOBp3RXtTb0kqc7sISJ1Kj4RgHz/ZdqPAnkx3Yb9SOYjFyguDNO1LAfD2LBaOEG5uOfX" +
                "9XfvRgnsBGC58si1jbbaBewEpcICqvwluMcTgBbl3Hf7nGsX1HM04uotHIAmuIUAkGZ8aqX99MrIlaNNyOIIAoE4PAqgF6tlU8Q/" +
                "xxTcIuKeMrbS3hAWZEEAihpYDAC4YYiU03NXra7sJuv5Qgh4kgBAl7pOHw6RWZRZzGzNAS64Q9EZ93tR6vk47QVE9F/CnnkCEOIA" +
                "nJfxgZoAFGZqvhJwdGVEPuFLNwEAB+h9PN16pwipNRRvN5Ywy7ylJgCzKQ5ggk0EAB8sUuU40GQ+URxpsjD77kQW8mJDkZly6CVn" +
                "s3/KjPPFXDrHXwC2RQCik3cwNUWi5C652HYVuJMEoDEvSsbQ2sTozN6EAGRCzlxJvvs+SK3a1EFJDhA/8MJllgBgUoQAQC6GrA4+" +
                "4K8EPA/rvY8ADgKQo2EAwd2VEfMhlOzRfDoz2YoigwSgvtsQW+8gAByWPA4ONEkoeUHtxhwy26iRYJoct20qhSFnM8U0ZWxIVMN8" +
                "DRIAgq1RN3GdFLkqPnlI1Qdf8YDpfgjiK9B3BECQdFHbASRzfGaLMICaA/gIeYg8+9DOuAlAiNjS2cRoMSaU2QUDAsCbuwJ1iEzf" +
                "ZStbGCYABt1m0P8sAej1dmArAgg+oeeTUaqmkoHoqoUAGExbTGK+73EQABkKNwEQvmGo5KqzjznFjmhPAVnzKOAkX6NSb6SdAAhh" +
                "U738BKC+/o9/NW9q9H7kGw7w05O+dr904043AYgmXDNrqFR9TSLs4kUOhNxHAOrTYfZ9Bo8ACH3UY3GUANByFhxgCWJwlgA0fGsF" +
                "oK/yUAIgYFqvPM+5PqMh2P2u7CKFT15BAMSrctARSB7fQU6eQQCs/r6cfZNTHCGyYr6UEqSXdRBI5T1oIgA6X5cQgC0H+JQA9OSy" +
                "if5jH3VNyqcyt+4D6JqdK7gTC/t8vtRMALjA+P3xmf1rpWqtCeAnAJTfTADoBQT/H5iClQiAP4nlSwAEqbtlgAAgHD67AJkIQOHw" +
                "cBJpYRchhVGXi3aMdr0n8fM0ASg6r2lYfAQgfh4kAKacOTJo7QRAkIFcBDqITSjyJAKAmcr1dIsKuOjGmQSgSN+EXJamEtRuAnDl" +
                "xXMGndywBwXnlBCAwv/nVjNbBHi8BOAmXkBAHbclAFlM3MBADwZiAJaRej64bdwEwJ0QNz6VF3d+iDyJtAAFwnbHXnK75bkIgMG6" +
                "cWZ7CYCAFUkrvjQBkKisEEyolQAIJvMRANyXX0kAQqwEgKn0P1UC/uQlEpcFZ0wNU7X2pknIKQfM3ro6LFYCAEH21RjDgwKCxCkW" +
                "9Hz/mT1IALBNVncBAiF3EQCDF5D6/0wTAA7deRksfvVwAoBxcYsbXIIAhGmQhLiOIGBB0pSchfoMFAjbDS+gRmf6mpffkwAA6uVs" +
                "xy0EQMAKxe1JzOayr3w7AcDWUD48SwAMrkdoyEQAcrBBe4YWnOJFgkhI71U3GmVNva7FtksAzHk8dGSuIQCA6bD5InJhAdvL+a0J" +
                "gGFmLWleQ4q0H9cHATcSAAzgKgQAR+S4/w+JYiYAMoCQdbmBnQCgT2tJPbWCkgGR2wkAOQB+ZsVT21W6XkKczz4Ev+34Qhebhy3j" +
                "tnS9APhvOCTb8boEAD4AEGsQMJdTfFn58MouQNAr1kAm4ZchP/XQsLlZS5UoDuz5zFFotLGIEivdZgLQBfSnCECRwBcTQeEv5Yyo" +
                "KyJJ/2ENbj+zAwQgpwHlgdW+H90EoBH/RG+/QzsAnO1553KiFBUPH7B6Fk1VAvaLfyawZ4QA+JJhISK+PVH3EQJwMhUXdAlb1tV5" +
                "gN1PD3O3zQIkdl/mt50AyEWyM4m1ToG1chzpE7eD6SZekI2fAGj6Uf+Noyai5Xj6b1gV/ZPtnPH6De1QtB8faSyflBVpxPoSGiE3" +
                "6G4CIHfPEIGkgvgpdVS0ULvzMxt9bp/ZQQJQFwJD3u32/egjAIjTaLzjw4E41QdipHytNoxCDfRgkgCg6wuJXJv5CEB8cwsQwwJy" +
                "f/qiaalaSwJQbEjoAkLfazfrnWmAv82vnNv5LQb5JQCiKXCeHpDdd+RyTaJpapyVp5QArFZ5lN7zn1znJZvC+WoY3FMwlaGpw89N" +
                "YOIZoF8I/thHAGQZZDaYCUC2sXU4BCeX1O78zLIslGeJ6sxmJuPbjyHbzXjBfmwnAJimliMesCHkO1SDPpgIgPFi2i9doP1P0cbu" +
                "t77r7ksAIEDPYkAd+TQp2bvpPFyDS19vhj4lAM5AbdNN9rIEQG+VQtCcmwDQQxQ7wkIA/GlAi8wkFEfc9m56sV42xfw5uwk062z3" +
                "J2ONsKF4V9KiS3wtw8QWoD9OAGBCC8EMFpN4MItR18zG/7bPrGQxorgJAEzozH40EACcQY2zE/LFeMa4mQhAfvWirEsJIAsTACzf" +
                "AWkKky3WMdzgAqbHz7qvDBW1BMBBOyYAPe+w0RjChc9G3woCYEnVanAEwsGGwVkrBkALyUH8BCC+CQIQKpsIQH1VltOZ93qbmAgA" +
                "pgwBORRYGDTRbltqAiCjGn9/PtaIMLE34gi6KAHogfsDBIDLAJY5Cy8mviuDIATgfGgHwn58MyuJjIpcRtb9CDHtx5gFNwGIqQFv" +
                "GXxXR65YUznwIr52HUrQANr/FLB491t+OD6A/n0EAItPxFFuoyAA9P/Blj4ZvAXBpxqpuZQxdhOA3lmQ55FFXwC4hNwEQIhTvuX1" +
                "+TXpQvIQAN42URysddc+O2o1ZMBRoH9QnfOmBjCRYUuNIIAw0fEI4A8CVjmY5h+vUjKJx9E/qd3X04GuMieeb2bJ7vwvAPP70UcA" +
                "YnZgG0cIAK5NHS8APAvcBACtLEsAXgLgF1MiHSEALZ7BDMuLthicZ3rBcBEAg49jTm/3JAKArYHF4yMAOSl4r1L1MEYfrDmnIf7I" +
                "dVeli0+vc4L+ZXtiC5yME9USQoZHIRIAkxhw59nk8YJ+avSfU7u2zCxzYTuimFQ7YxDwwH6kQfuUKkNOh4F83/bcQQYXIP/dv8Ep" +
                "pgG0/+EjdZbdb+U/G5ffTABgUosKAFQNQVcn3bVJAPC13hgGxRm2GAAch3iAboezDyMAGj3iMfT6cadSopeVAFBOMPDxWtcFM9ft" +
                "eX5U+aRJAgBT40jQJDDxPNAvYPRsjna9Iz9MAOTmpWVmmTG5fWbrZKCZzKy4H2MWCgKAaWohADE7I15ABBs+AiCmzFR7ygr9XwLw" +
                "EgA9pOsSYLhIOxm85UhkJFrU1xuNBq7dQZNFLl8CEPISAKn/7ScAliJ3OX40i2TpbTE1rJrCcTPFUAImmoTA4p4EAHKw5yvObMj8" +
                "RFgcfXUztp+PgOBhHkecGhoJAOR8GtB59O8gADHEn+STN+Ej5Z/+8R8gUHwVAsCeQ7JSlJNK4Wv6QY8i7jmCOoa5hiL45qmP6JzW" +
                "I2BaSP5NQU1NH4cK/mGsm17esNS7cn1Ts/L5YlD8PUSW3I+G8zFk5AVg/Y3vlybQvioBeAkA5sJvESoD1KiO6uInAA4tNOvZSwBe" +
                "ArC+YckD+FRT8xKA9xBZdz/6z8eXALwE4CUAi1wJ9IAP3ZP6zaUJwMAlx0sAXgKwGtTI+/GppuYlAO8hsu5+XP18ZNMvAXgJwEsA" +
                "2h4EyeO7bLdpfw4YuIEZfwnASwAWQxs6gA81NS8BeA+Rpffj+gRAtX4JgJ0A/N9//dsn2f2W/M3q8r//+a8h//Mf//Lf//7PSOYQ" +
                "MSII4rxzMBBSWERvkaY6Oh8qQBeoE4Kk1BH5FH+McP6WCOBtdo7eCucRoAOlVKPuOULeIYZNp+TW84HdMWXbcYCYVinyF8VoxFLh" +
                "ckJbXELRnClVP3ICOj4uSskwUi9TheNoWvJL3NmwYLgQwr5NlBk9z/sRlvORpuap5wutSj4vim6/h8gS+9F0PiLgWEu/+Vcp220s" +
                "047UGjEsOA5k/S8rDaD9DxdQlt1vyd8sLUQDqOESOydsR6y8G9qF6E9sBhTNZQI7IQAZ4lCjMLKOEie9r7dh6chqCgMXincZuLpK" +
                "6yABiC7R3LMU0bakaPxBLwGI0eC6yonz4r/6anWZ0uNAKaR62B1G6OUjAGga0j5lprUHOxMrUGqocSluhzEkJ7nHeN7Z1KAcFVQT" +
                "Q+qQ7SgNFgKLs0OsSqHyA2aWmkoxYCnJvNB+PJLktEU1FhdittZ2I1knBGfatNC93SZv77kAM54ku/sr/xlh1dgLwK1oADhAAS7H" +
                "TQA2JGpY7EL/rBEpTewlA4O3bP4wXtyfbgIAfEMOgKTUdyMAckj7CIBgZQia8xEApMsMEV16WY0odQEBCHUuIABWIIWXxlwz6xMB" +
                "QApCXBYuUTyRqn1HAGB7a9niaROXRiJdbCLcqQO3iQA77qL/I31efWYh1zOx9rS8Uuksp211EID4wV0QgBYbPAeCKo2OV1nBTpfK" +
                "SwBeF6BCtnc2fAQkjimgPzUSAhAWgQUX7yx8pJNXTvE0aCcAMbDMck0zN04AxFTlU9lBAAQMYTuwud7DUko+hcpWAoCRpIAY+whA" +
                "qGMlAIL84odYz9FoY0FlzA4vHTIByHfboaDbla5RtWxUFdl3yHagHASAtY1zHRhKLv1LZQ+SlrVmNjCrslYzAcB+jCFCJfjYj3GU" +
                "xGY37Udo5yYA4I2QUAr0zw0AUHgE0kgA8HG6h70EgLIsATBwACDm2D+Gi7oqGgNYP4xFbNpoOvYeK/DrTUYJ/YUAUB0HuoI4EBu3" +
                "aHZ/bycAPCO1NMw0ASAR4jntc1TDyItxFOBiIgAoeWa6YVJWcyEBCHWsBADawURIHcNY2PGfHEwm09GMqKDgEqaGMDFj4ka5wHkv" +
                "lzfCfOVCp/Xdv/R5xZkFECdrtRKA+vaaTizt+xHEVbQDB2gnAADiuIy3noxSpTvERwBeF6Ct/OFbwO8kAJBtwFMvAYBdIJkmuxWR" +
                "qsZYpmqyS+hPEW+Z+PgqhY3hwRnqQ3HZovFDF/yFrZEjExxA/Q5HCYDwPaVAHgLAlba9CDQRABTvBGy9gABQIzcBCHUAQULomOsg" +
                "AHLj2+7MBjglryiZAEBBBO0tZGoIE/0EwPgCIOVveXbkQr8yfbvKrj6zxJFYtH4CoExMJIausaItqptnL6B2AsAgYIg7TwYHkEAI" +
                "93HWGIAMMB4gu6C9+Ps6CPglAD0oJFvnuv48DZYu02PoP4RYOTYVdVnCgzN2vrzT8WRqJwCclC1Kw230LAEAC9p9BvERALkasWYB" +
                "kpCvQGN+AuAKAq4jESFYWibDQmlfw9yS9QsA3cQXMjV43CBMXJ0AiJNPfaBQPvVWAgCGnEgbrsmvJwAArxRwADzC9wY5fIoDbiQA" +
                "1MKdC4izdg0BMBD+ZQkAsYUjC9DrAkQUkm8jjgeZ7c5RHc/A3G0k7ksIfHMzTScYbSQAtKEQw8FgSQMav7wsDWiIjwAAKMdom04X" +
                "bL1d5OomAKGOEACuK9/NAgSILbAIxrPXd7YgAAbm7JWY9+wFtCIBKFze67Om7i3t4dIzyzG5kgBsX+RC6EPvCF8xqKZJMqAI3cD8" +
                "/j9KAKxhP3eWSwlAlok0oAO0gaDZRACUdx6eYJmOYqBy/h+f67bbgzO2PYbL5wFPAvDJgBKPjhMATr1zlQ4QAMw1H82RZNAXUlLE" +
                "UjvaJf64jACQwbIt6GWID9H9WOeJWsJZXBbJogRAXUZLqcN/h9NlGvxkriEAOQib7uyN+zGnAodIpqneGDlrIDhH72ICELIiyp8k" +
                "AMXfZ8A6LjXub88wU6CQIw9PJwdT3MTDECzkuynQPAOORg94tMJ3BrTCEwI1m3Dp9csJgNUFCBd1JACijgW5CiAzEACeNH4CoFe/" +
                "jYnABU6RRxUEYDDX8HmUk+3zcgRAVCjkeNJSviYtOrPkRSHZT8YajAFxpJfAHVlObibPce0EACsh1PTl/yH0J2uaJwB+NP9YAgC5" +
                "OWEQzwo3AZBV0jg+1AKGADbuvO8m572W9hBYNwGIkQlDWUBDHg/zLkATBEAQHk8Uj9On5mC1pjZyEwA6l9M553oCwCeyFo2ioexJ" +
                "tbqpAezIzuItp3vhUTNIAAoVitSucVey7sxKaAT3iJMA6MaHfevdj5hxR5nIggBQC7//j4kA6GZfAtN7swA1fmtQzqJ/Hmb+DOtf" +
                "EIBtb6X/FIHIsAInY49gCHJxGUr8Ph5be6FbADJQpmsIAL+fBxCOQOMEwB8EPJAGNCd9EwTZy71FKZMLUB1fjuPTRAByW9j7XYBD" +
                "LJhcFYe+LaaG1axEYGdihaCVdseD0M53LygeNeMuQDR0IvIKyt62zGzMXczgrlhnVjjABQRgm4aV7wC9BICJubbT7XgBkHgGkxcQ" +
                "MiWOEAAHyl8xDahK/a0nSX7LdhMAWXBn6I0o0p7hRDIbiHejCbrBNxcjdiUByGNIR6ApAhA92U2E6iMAWKXuirmcZYfHp/iubHPY" +
                "7zrLtjcqF/PWOgD0N7ASgJip2ue40dTA2ojg9+3ZomAHimrHXejfjTuFw3COKN/1FrsSM3vSI/9TFmz8HrkyrTMrGwSqmSZiZD96" +
                "sgApAQAoN9X/ut4FqIb+C1xkn87c86flW7XcGf0T81kJQF1y8ut1sKsIrQB8N8/vGVqBnHAwpB26wfowQjfnb2mMAVArkIaRlvR6" +
                "AqD8x/9OJRhimxmp18Nbl5bnfRnZeATb8ZjkHkGL7cnm8qWjrw6AFBxwAI4txBS9ukwN1IFGIg5MwKSKsM+ORwB8J0Nqq+cJpqZO" +
                "A1p3ldPakp4r+AMIgBS9EQ6AhtpnNm8QqOafCDsB4HSbKgHHRwSUO4K1SDZmCcBN4PsAAZC42DPfWkKEAMhbtuOlfvvwtMsBziwg" +
                "0WWblht3Ki3ezPkeRdAGzE3jlUD0f/dCmkG6rVmA9nE28qgig9tsJeDCy9OdUqZeS71ZJtpbYc5s6OWu+cASYGxuSzbcBGALbnoB" +
                "B3KqFMMIx4CugBCsBzQnF5B9oE2JB000EXMv+hfc6asELAWAIQUNKLp6vvwTq30LARAOAAKAp4bemUVz1sDcggCwxSv3I1RrqgSs" +
                "oJxLwur/M08A5mH9BAFoqQS8EA0QtCcEwBerR1v8BQHgIIsu7QCRXtrZzUCsmwQbmC6kt+dTbx0A8csSTkiYOEIAsounD03KO48J" +
                "shC1oDk25Cg4ytNl91JZMYffL99DABQuOwgAgVQexvOXgiBOQgByQ716iSsaZ+2If/BxdxoaZBOHUS9N3cIkokoDCvTf2FVCyb++" +
                "AIQWjbuDrkcyMjYyKftR+YZ1P7IhqtZIAERA1UJZk/8PVHDHAEiEYSfcfwmARQxMQwjANckB8hnDJfhFGQRRRN4xoumum4ZdTBNi" +
                "uruVQv3tWIo30JwO4QDCpo44wvryIEH8nsTqgCFlLO15cgxMEnY/1pIcZvAm5wA2Hmail9ynugsPmQgA6rV9ur4FIOhqog43wgWk" +
                "KRi9JR9o4f1PWNO4DEBx8+xAHTHdVC3nPhY/pa6ZxfCCALCTFCEAATrdviUQph5+0n6kXhjM8wRA3m0oJPym/D9WAiBvfd9dwr4E" +
                "YG3JuBleJYhGCquHzdN1DwH8ATPHHQV0G00LAThfxQxbFIi8pefoPJHN9pDAiKG5dowIA8RNC9TLOeoycNBLOIA8AtAR6HoCQOHD" +
                "znYEuhrFXMeASH6nOEGD+bTX/xLXfCIPRwm26Hx8MMYqZjzUCYkf4gCLX7brBdYq93/cI+4pw05pvylnfp6QLSbACsEiOW9qwoDg" +
                "+9BImAYWJNdkO22Lz2IAY76KdG1fZ//EjECLmLXG2wQOHScIwoWBwSzrBCtRQT+7ZpYpgGT9cFqZCAgEoHdmsUdCpFHTRODLsh8J" +
                "mq37MX7gYEZPoNqZMwiztt2PbAjHfXv9L6IXzFHvmRsDQiTG2qwtjwDzcQIvATiJm3FfDkwA6YUglGgCEs3FNg6buyUAZxSJpRz7" +
                "JxAGtWjsOTpM3AancM+IadPRSuxbXkdtB7NLNQTd1nWO9HLaTwB4YDPyIYyXf5WqOLQTRFI7d7WIVS9JPY49wuhYLlq3YcEVlxCA" +
                "3oaA53A8gxn2mhrZ8rzyx5p3mxqYuJwb4EcpJXafEN3LQEQWBh6mcp7QTFSY0AkD7pjZODiAwFD6wzyz2m6sqyv3oxQ69O1H3MGZ" +
                "zsfoOaas924eNlNSQeCUd88OiEd2BBoTfxrQjwTgJQBh+2I1jGR7xCPAeQIAOx77J2/LdrccjZ70DxRtt6NFxhz/NRoYjkAjBIB1" +
                "CTAC6wq9/6ldrtA5UH7Bdv2vi9Y8tgQcvQQgOwdjjtxbPqTwQDOZuOJq8IjI9QFMx9TOhVKorFLUP5KMro+cWQBlvbr22zpPhIM+" +
                "ZYc4MvRLoHzvA6N434EAWA2muDfLTjdwgDXrACxHAF4CgMPGTQC4XUkAVrfdNHChVD71c2RF9AGn40sAzqTlgWpFGctrkIGp8igl" +
                "JvF5BACAAwTAveVHCIBk4D2TXZoEgMtgRGoCwN1HAuCe2SkCEOv2YgIQ28RNAGIM/QRAUzaz+E9XeOT1BIA7XS7+XgJwyJ3oMQQg" +
                "5p4EYLDga3TmfABAbMtrCABebx9PADIHUGzqJwDAECsTAEXJkoQhoxA+tqyibHSVmfJnCUB0gy4HqxMAugJbd1yRHZgRQQ8gAHQB" +
                "qt85n00ATCi5DnZi/M/qBCDn/jofZoDPDhIA8QK6RpYnAAfl/kHAjACO/TlLAPgIcIbGXEMAQp5NAGSHiCMQvJ8vIwAQuyOBfxKB" +
                "kuvUq9ugSX/UdWfwX07LPU4AlnYBAlwbIQDYegILHvMCIPtOth4IwAPfdkBcLycAMe9PIgAwceeLqEiScQpiCy8mALopLpHFCYBB" +
                "rr/+HycAYRFOEAChMRcRgDAEv4wA6CMAjJTzjFQCgHbXJQAM66RSgq4+FeRG9OcSTk04S3JSo+sJAHbomaDDlwDky/LjZ3ku2n2r" +
                "F4BdjX4JATiZ1+glAFIfBkagy//n8QTgUS5Ad5CT1/80fIMEgLizyARaaPESAB8ByAXjjhRZewlAgf6L7Mu7RTkMoSb9M5XrfHPK" +
                "XgJwcsvPE4CvkIFE1t4tBuAxLwAvAYBeoMqXEQAIHTXPWM6nEoDnxwCsLEoAYjVPEwDFnTX6Vy1eAmAjAOQAcqWnedCcBGDdGAB4" +
                "/kS3a/RPyVHXXGmroH/ILAEIeQYBCEWuJwDMUSsc4LjkIgA3JwBS9PAlAP4YgCcQAF7QnPH/wWcXIQB+uP8SgJcAQF4CME4AKLuO" +
                "QDC4bxBwvcIl6aeg/zzIMs739AWKzsT6x6FI1aRsMwnA+llHxgjASKgoHboka/5xkcz6dyAARfC9UG4QgOe97YAAxP9eTwC4H1cn" +
                "AFhC572AcFUhSZOXIwC/OAvQ+uifsbPvC8AbBHyEAMhQ463cZNNBAORGeRUCABVQ544QOadVqTdm5gCobTmuPlTj9ZUUVxL8N0gA" +
                "HvACAEWWIwBkgLJ6xwmAKJXpCv06HhndMUQAsB8fRQBCtl5AKMl/PmnyeBagkMXu/l8C8LoA9cYAvAQgw9NPwQAmRyCmh1qRALBy" +
                "ZIxM4fZzhJwLNImvAQuidOiUdlQtw6mMqNZ3ARpLA8rHxlBk3AUopEb8IuIGdgcCEMNIvppfqxh183gCEK2vT8iHCQDkay8gRtpk" +
                "RrEaAVBZkgC8BGA2C5DiziEC8NYBkHRMIZ+eXLY+HjC7DgLAc5rNoVrFzaF/9vjfvfg//rQlTwFhuHHnFHJxMANQaUxN7SNOirhi" +
                "EPBLALB6z/j/ZBeg2xAAJTa5qy8B8BPy5xCA2guoyP/Dr1GmCICmxnqDgF8CcHEdgOMEoICkIACPrAQMNIBDdDAfK0ZbOEB7RiDY" +
                "dC6MOxMA9BbgGOcQsw0WF/9/XdvFUwBIFxZDHEt8DXBrB48UQMPoQ1YwZ4Dho81bB2ChcCPWAcj0NeRrApBhzTgBCJHHise/ACBX" +
                "5iP34wUEYGv6uGBgjWsvoAx76D85kzf5fQGgvAQAdQDGKwGfJABgMnHSgAD49gxe7kYIQLRuJQCwAjVO3U0KFMa3cdhh08Mabg3T" +
                "VL3q3DdKoATg/sCCNOgZGR9H/zUHyDQgGo2mYxPF4KNLvWrGZ+PjseoK6C86SlqV6wlALEU66fkAB9AbYOLDHhsBC77OAZrdwMbT" +
                "gILBxjBOE4D58G48Iz9yP7pfyLd+ccIBinwYBYrQr+lOGdjpA0D/JQBTIs4zyGgbho8SmjqE3w/rAJyHxfeVC5ASgFjQuBal9HY4" +
                "0Mw2XVf8Jn7vFrQbLQbmcLSIiQjrU1//FxwgJrFxNBBjGv0h/uAqjVMEq9Qv2qWQaDqYUhxjYe5jRnBBm1GF+PyEfFe741NUQL5e" +
                "BROISeSbAOVrVkPcj1ui7BFeZzIN2ZJD6ZXJsIT66DPf6ECNGoWx3TCYJr1QOIKPjdEiFLEKTY1w73zkH6EBEsEC26WN+oWOeRLY" +
                "UEQsxOS6Z5ZvOzGz7q3BdnmL5J8I3Y/ksVDWsR9NqkERyUoH2aL2yhromOhjAr925U7B44wmxlWIv6T8FLT/XgIg+UZw2JCvOwQf" +
                "h4AEb6//T74AQAvcjFKO9Oe4YN9CpCGrMPtY+5eJ8H5Ui02GHVCPwOukwNTSKgkHONPEyRWLvknwFo14AY5/Wr+v3q1CA0K2EWlI" +
                "uBGHfRy3gXvivImTaZebgdLEH8SfxR/HPyGryff9BSKUfuaF0WJSgLmzyN+An0OkUfz9SeH4hOCbDgFsOqJIu30GJshzfVwyB6BZ" +
                "RuevlzKsWUl1dJX9xFPPETneGa4fmDKn4rqidN2aJe9H0+RaB5P7gsZwlwMcHFWm2Mpfw6fcO0V2ek31r5FeZPsSgG84gOAJk/C1" +
                "q4iPhHx9P8q9ZBLd/35BWz7JfurHHVTySX9e8gn9CfWOiHSVvS3MaO9uzTRAxoRlZUpKppSGrEa1+6DaGauCTjpEm7AKxtkgB5tr" +
                "H7fCge08B5AZyVo7B7OOWVcO4Oth7yq6//IemXHT4pG3o3wFE9LyNcfg582ed/o8lB8hAAXQ+SUEgKc15RNSbJTznhL1/aintyoH" +
                "/skCcjJK9buRL9bYtmOQfFQ3ync0TGZZOtxrHDO6yivwkzrbODNKrWNBaQpF6v3YNRHzgpG8vXyhV/3Uc5IDhFw/5p+uFYq4Bd8V" +
                "w1+1e/4aNjNz3z6S46m4f6klQ/9MQSnte7zYDn5Y/xQCEL9fWvJRrajCL2jua+hf04BJ8cfQWPtzZAoaF0/Nl6ST8g/Hpeit9cG0" +
                "HpMvCJLolTWqD4MThqVnQkUR/LyQHHz0c78WKus7vZhrpuqWQi8RQz+XX4Gv5D1Vv0tD6u8Un+pde8cPL8d79UsAFpDiinHK/aud" +
                "1Sz1pOWfCIOyNS05yQ97v2mSgk1dwwFO4ulaqYblcYK4OuarGJ9XegntDfesamTuasszskOOQ8lX6rmDtOyy6zfCONp5CcC8jE/Y" +
                "DTV6xlBbCYD7gcL4cYNcydN8cFmV6mCJ/nGw8IpXTm4H77AbKPry68TAeF8pFhX/9//bOwNNy34zjt43KQBFUX2KPkkBBVoKg6KP" +
                "USgGigIUhUHRh+plqHF8N7c580uy8p3FAnWd/3Qn2fmt7CTfLMx5CpbjFQAA2yKyEJQAEOwkE38jc1LQGIMzTeChMZiNBYD03wRs" +
                "37jdCpThey/RbzPFKwAFogaMBz9GAwIZkcx6EwhzdeMCnrAIUQMk9W48kOwlWwhMAWiPe65uTNtCDq+3+x7BwUTM4vwPVn5kvoVk" +
                "JWAFIIIpX+wS8CwLmDhBUqoPYDG7i0Hft9CUAAz+floATDyviQle7u1ybb4hqARmZXkpDPrv3Dtm6QIQPE9gwLoF07y04Ui6BX52" +
                "v0sJlAERs/5lg0sBIO+lNu6Y6UUe2BZzsfeu9lYCrUDM+l3DfYMWbCUA3nMiZnpRD5jpdsN7WCXQDcSsH+zSJvtTtwDtFgCFwVgv" +
                "ohjAJ5i7lEA3EIO+1Qb98BK/Baj4LT46g5le7For6GQFBDc4lVF0A3GbfqeuxW8LYiXgwG9FUQCCGOjN7kJuL0DGBYgB6VJXTUD4" +
                "l2wa8Wd5kcXl5ZWAGwcFo48Y5QVtBYCAu3/KzCYhI740K5v14hHfJeN9AmAYMr6LdXZpwEdo/QsYKzj7Af1s1zXlk7E4rgv5hnuW" +
                "AIgY6437asNxNi8NHklLB7u3JgDPjqb8Ni1lvlcAREz2qgKR7koQiBeBgGVp0hxGeRfyUe3Y8A2sAIiY7EVD6G8FdRbRDRB6IJ67" +
                "/c8//vIRgZQP2JHvPJW/BnSDAIgY7kVVuKgJlqWW/LYiJUHY7ZWP+GOgQV8a1AEoj76JmN3F1BWcIG8xgd73hIxRACSf4xMsGlO+" +
                "3uGUoX3w9x9WAh4IwIprNMTI7hqtqd35YGfvzT7MFTGIudHcLGKaDwb6f//9zz/y/X9ZnfshFu2J+SBJARh/TYDEKYuMmhdF7j1q" +
                "2fLGccIiKP98qmAD308m+Cz53N+3BaUM7YO/f4t8TXCl1nTeG3GR3nI58UuEVsSsqDYEMHYf5Ln1eAjx7grrDIEW5EMXgN/++hcf" +
                "Uf5W8ZcrERERERGRYGhXAIaIiIiIiCgAIiIiIiKiANAREREREVEALARGR0REREQkGNoD14DCecFLqRrc9iC0uwt8mHa2l+qNNln2" +
                "QhsIgOdGR759/XIpZWgf/P0zlYCxnNWG26O/wiCikDODu7f1e7vimLtcS3+QzQJQ/hsAAoABogpOh8ygphqZ47H4MC2RsbqTwHO/" +
                "X9X8YCJlaC//UgF4npbVjphzmIhiTNaATvE9OzW4/dVdYZqDKrKzWlwZ2gf/lxWAMG3CMWRmEjHfE+I+4U3inAJ5FWMDrlbwNAIM" +
                "9JOsFwDGootWcIBT61UiAg/6zgLHIbyTyQFXMeDAh3nSPS8Ap24B0h9UghKVQIQZ9M3393KjGHjHlGIgGwSgHtLlb5HzZYPpRyXQ" +
                "CsS4n2XPKO6agPnwxYBoBR4zEAWgGJkxAQjg1KUSaAXrkP196eqsb6bvRODBAsTAq4fUA3lSAAb9oPytBpfHgSc8lUAryCMH+8ml" +
                "Wb/flhUg3nbKt4K8GCgJogAwVeFGMVAJFAOBdIDrsv69Eb/jkes8/a3A2uqqgigAkDlVJVgxdekGAmxTsz6huqIl9l7BE/gfCtQD" +
                "LQIsAPUdRwABQJJ9daoEdCsA6IHAG8isf7Y24otEDaAqaAXqwRXoJzOVgBWAeTpZQZu8C5+TBPKc+XGfn/UxRz9dLywAeKnbhzob" +
                "gsCrFmQEgHYLUA8xUAmYuVNh8MyG2ws3FCox2XMAeKxWoCrguLs22bwABK4BBXKdFagEioHc9nIw6xeY79vQwA20Ap2hWQXivABc" +
                "Vwn4akkIKIEHCXQDUz4y6/Pj/vGgb455wVzIfwNrBcKP+HkBKIfBWADicxIwcNCsQCXQEMz3Zv1ZTmV9FyMX0c8NAC9eYAkzMd/n" +
                "BaC4BnQweZS/1a/MO9MNVAJVgR/rzfpjzPqQWI99S7yaJzQQA63AcM9kqg5AXgD4gEoVqgS8lOkV+Ab9Nkv7nFyyoh2lmSHEWx9g" +
                "BYqBmT5GoA5ANdYAAkBi5w3EKoFKIKi13ktv2T++rs9I9n52K7hdD/xcoBuwc3wg2QeZuLtfAQBclBGGdrxYK5AD6tu6otaprE8u" +
                "QSXbhqdiUEIuhGe+38bxzj8QgHJQKAD8WzVUApgVCMBmF2V94z6r2Nk8t08W/NDJF4P++4i8e9e7dytm7+5/5hags/S3AqoS8J+t" +
                "YlBDPeYeBPAOIcb9sykf9bZXAMbcLwYeMCggHCuXbVNnQAAIhcBaqkJICTxIEJhZrVh343UrgJcAOu4fCfqp97NBvIcedBIDfnUz" +
                "76HaD2ACzQnA7G9RggIvHACsQCUIzPTuujnLpbafyfrzYIO+gb5GMcinRsWgoN9aletieQF44jwBB/o+AcClHx4kKDn1VhXMNz3+" +
                "0n6eG1+SN9a1sJZIPysgZJK7hoBRPkBSAIqmeZu9UejU2OMscTVcX1QJAoFDT3DnXkGPrE8ebpdml9fUBoIVZG9ugaxXvpInS2Db" +
                "Tl4AfvOrX/4IxtQDIzM6R6oEBR7jeweY472khR/3+UEfeCOK65FaQaEEOTcA1Chs4gnyEK0HQ2MgAOUYCQhAkf4LB2BxPNeqBPwP" +
                "BW5KziZ7D+UDTJsV9L3ocJbNtvCaVrD6xndgwROsNkgZrcf79gehPSUAdfr/4+9/987AAfhg9sCoBAXWlDDHm/WnJviWFdP7lRyC" +
                "CAMxXwKsYFtTkm9S5tPv6u3JaF2H9kEHDgjAwz8x4ABU1lmBSsC3ArEqn1l/g0EBEjwd/oI03goCvQ7UTIBDq8b9+GwyG63fO1gZ" +
                "2suu+O3rl3cUgJNiQMi1tGVR9nFtMesHQGf99UH/bLKXg9di9raCfLuAt4dJuFrCfLR+7EIfCMD3rF8yfw1orSn1d4rvvGaliafH" +
                "m0pw4z1OAthq4jYe0HHne/O9HHUDraCGVsTKKL/6SPqn0fphOf+d5QJQnlEYY1G6n7QC8j3cV1StIu+Al2X3ix+gQdafepLIiC+P" +
                "seCIHoDFoOj5DXR3dVaRzaeMPo3WD2n+GQEYdPr1ApDkXkNQCeIJjFPcVI6XFw3CP9FOyPrMBWw+m61gzNKHDxADaBWzNkODn+Dj" +
                "xMdmSgAG/8W3wegqf6scMIN/4u0VrVUCmhLwr8Az2X9KZNh6QjeQ9dlBfxxZJGgFszDFoGttY116P3v1u+b/Sf+fngEYtOzboMcP" +
                "BeCRXPoPsEcP4EowCCIqAbYOCzrE9y/hlAkZR1qKmPXdtABYsMxHlrQewMVgftD1FwOmTqD23UX4aCPQR//16ToAKQF4B5H+AWuQ" +
                "KkFJt0Oc86rgLcv8Kqou7aOmfNK4DsC0hc1BJ9V5Iu2LsgL+rVkSyfSzlOl//urOYsgEBAAPfwyrBDUNlo29E82sH4j74Kx/Ktw7" +
                "xGqObknfkI0uPWCwYbVRQ7Cyx/jqTgWAtRcQoATkUmU1ffOogT7wYAFZnxj3j0xIxvpTbNCDnWLg54LELmUloX/NvoEAlN1pWgCW" +
                "lZHTClSCmnvvjRHrQmTj/sS0jUz5VxQl1RCSx0XmrUAx2GZ98S34BvqzX0RnQ3vgGtCz8MUAbgX5XUN8K8CLgax4CQCK6IHi/qL5" +
                "if9u9/vAkWXmnWcoQ1YQ6OScrnvdJelt7jXaOCGuFwD+jgi+J6gE6+hxDaURn1/Qjbxxnx/0TfaKQd4K+GKAcIMawHl0Ovj0OxaA" +
                "osXfnttOFMdLWhJKYFGCGuAdsob7eGk24z4n6BvudYOsGNRKALCCuBgA3cCZDrWinVq1nxYAPiIiIiIiMpDwt6kbhfiIiIiIiEgR" +
                "77sKgIiIiIiIDLa3dRMAEREREREZnJnpJgAiIiIiIvK/uP+vv/3pgW6HgEVERERE5DH3KwAiIiIiIi8qAP0uHibUs1h6fSy/LgGh" +
                "JAetqpTcUiwvVT+SNtjzb9f8kJdAX41wsHYSJDYcLVYQINDcgRaHsngMBrpEHdnT8AUgA6EDqQSQWn0HKlIZ7itCIzeW9Q/oKKD6" +
                "j0E/j4bAEQOtICAJNQ6WollhET8jAMz6uEA9gLwHj7cLKklknu36h8zI7gFOzb5jzmX9mvhzNutfXUBUPfjJRswnAX5tY5Qz8Amk" +
                "eX64ryn/nQpAzAQIExtwX0H2oa2efvLzzbAVsOwcR2dTSzD3z7KiFTBZ08QfgB/9Uxxs+vGogZsAIEoi4ed1QEvVAiBMBxhzank7" +
                "9VQvXbKKTD8cTqUWbOagtSDVqWoM/UGAGnBkRZnfW1LPHxhe5Z9//cMKkqF/ZtwpACwNOPgShDzVu5aveiQbSKTYEyOCDcHP9/2G" +
                "hkA0AL4F5Wgfyxt4wAQAwbdENib+R2oBEMArIABWDyIPEDJRoZLTqV7HzwQrEsDxS3XWtQ45+gvkbbBt08XS1wKN1GPvHvpN/IEh" +
                "8Lb0PSXwjRCn9sR7xGoA57QTn20CHGd9GghAcFenjxgADaDtU8ded5N9+JpA/8RfdeyeAqADLJrOcZMxwAQ8NbVh5l7fjWvuivsN" +
                "7hGXrvchXnHgEi+9gCZQCQBb+be9vf8LDoBj+Wq8WqIAAAAASUVORK5CYII=",
            ["pandora-farm-sign.post.town-512x256"] =
                "iVBORw0KGgoAAAANSUhEUgAAAgAAAAEACAIAAABK8lkwAAAq/klEQVR42uzdgaZUURTH4fMUAQSEEBLoEXqSAAECQSAIAhcCCAJB" +
                "IBgUBEFw0as0ii3Nmm12p5l91pqPDzQalzvn/7vTnX1avn18PQT4+uEVXMCX9y/Pavn+6U0N2OU9UAIBQN5gdUgEAFA4Kk1/s9zu" +
                "blx7fYB0VVv/FoDmMAYAglFs+pul/voDAuBffvwOAPATfe1Pdg5ZrvN1gNky2WVU+ZTOBL2DYI/v3QEgNQEoBEAAABAAAAQAAAEA" +
                "oPPhJQEAOI0AtEfxuWncmL6e1N+m6QFAPAABAPEAAdjccXYkBASgs7Dhc/16CEHSEhAAhAR1QQAePbj/p/VTiFSAGP81rVsMQPvi" +
                "sjTA+gO51r/ZVgDal/X82dO9czeAf3tBd/6WomDxN6gzrYMBiA9zrAlAvP4XaAArX9lWAFufbv1PacDAvYAEoOoLPfxOawAmvlgA" +
                "Di/zWQEI3qc0Zx1chs7rawD2PctdN+JpvUwATj8I1r6aY7yOp6z/7t2L3zQA457x9krxov7fAHS+QwKQ5RIKd78JS6ABmPX5W586" +
                "AI31n3ulxesf8T4Am57o/qn99Z8fgMb6CwCmnMu8D9jizeCsf9IAyIAFJ/FJYLeDFoBmOADWv8BwgwAIQKcBjgXUXnAQAA0IMuA0" +
                "gJVnAgFwsnf2GWDngSvFALrX7Pho+/8AKjTpHK8kdIJMV5YAiIrR1w+u5RIQACUAEIAxSqBDEBOA293NXvhc+z8vr14SvJWBgACE" +
                "C5ggABqDloAACAmKAgJALwmJ46E3IAB4zwHaLABoBsiJAKAWwPhn9wUAqYAcBABEAgQAFAIEAOQBhgPw4/PbY8Lnao+WscHdQR5A" +
                "AEQFhQABkBCkAgQA8UA2WH8LZwEIKAdc+ZKmUy4AyAYgAFht/LSO3wGYaUwwCIDlxUaDk8DYa2A4AE8e3j0mfK72KAAbMj7aAgCQ" +
                "jgAAIAAA/GTvCjStCqLo+4I+oC+ICIIoSESEJzxJlEiUwhMeKahQJCgUKFEoKAQCQai/6EdaLJax5pztnrtnrnm5LOTec/Zes2f2" +
                "WnOmc2uxAfz9/WEOk7H07VAoX93ZwH/iPPfTQb1EtIh2Q+Y1qzk+m8kLLB/pKBDJBRO9cMaBug5/vj1bEZNsjWFisQ1Qw5FaZlwk" +
                "RHvnx/uDSfz89GgyFj4fEKL9/e0+8PX13d5gIsFKZ/j15bFhkrnQhJWRiVO3zWupO2RM8EzAZ9lrGyIx759f3iaWVr4moOxCMMw8" +
                "4nK5jYXmYQap4SRhfGo33Twy768vfXd/J5itw2IAtaawW4iPz2/G4GXrwfowkL+6CWvy1u0ZVoEHmARY3mQp4iJYRiYdFrXUxjpr" +
                "uiadWt16tWLfPblOvHl4xaCvuHRFz7jFBqDsvTFnA5MGEPzLPGYDZgBNzMkMICHH7dHxCSAQ1sNoAGgJts2rg70esD6Uf1gfxh4w" +
                "KYitmJObbEBMAi3GKDSuRFmY1+0nzkiskYvmrYopVB7xFE8+YM2ov9vAnOtL9DGQp3cu3r969tbuqcvnTuyePlae7fIP+BBfXbtw" +
                "8t7eGVyMW0jSy+4664qppJ1QbrDETcTW23HzGhkA4iAmB5IBIiAOopkBuP4Oi60BSFmw8tA8bJi2sA5EFjQqO9BkIvaAgDlaOsmc" +
                "JKEOiBYbgAQIzPMVY17wlwcEBpDPiCmg6CtO71ku7W1Oau10fsYA3PWp+8iCdOVYjh89EkCXkSoq/2L/Ehmu4gFIzcUG9GgTq+GD" +
                "G+eNnnlAqf7x7/NLA0AoRGauJEAS0cSHZMaX/q0BeDuhUbHm2D9doYKo/bgtlUb48/gKzBGqCTcsaPCJHwJK48lXTHmhlShCXQGz" +
                "OlhUJiPuxQChm73n2mwVcsNZrj1g7lDblNfqgFHIwzIDIUNUA/Wn9xuxEtz++xT0ryF1FkNGAVEBO3iJN921AeBejKLh0gUrxPwf" +
                "DGBrAD7BnVEubj0N1OIbH8Lg3vxSFh9IFWI6B0+dNwDPi1DQZZfIngawyVmm32O3jmWmKVZtAwMw9ce9IC/pb8sQtaX9i5v9HUBj" +
                "A0gwBBM3AKl/aAAcCBYSCphnpRUlPlsD2BrAYlAdoLzBmWwJOweArDQ0AJ4CIXL/JwBP7QdBAxtAfqI1xsAApLw6AcM+HREQp992" +
                "BMsJif6xdwWc9j3Jcb8/QEAgEFggnyAQBAILEgRBYIF8hTRFOWru6989XdPn9Xt3KPz+u+/O6TNzpmqmu6dH9An/2C8AhoXvawAE" +
                "4Or/iW7c+93GaP7cTUAxCyhJWXvZlv5Zisc0YIIAcC8JahAGXPNwWj1XwS/rKpXj0iYA6giaLwCODbGEj06Gv4XjuwqAsH8QX3TR" +
                "M+ZFV181oEsADAupATntrv6f6Pb4+d4pE8ZwEEWNfGruxz0BQMTFEoABUL9qkwD4DCj8KwIQXOaTwkqR0XJuQIcAwBEE+REvEP5z" +
                "mgD4Li9ogGwCZPkP9o9lZj/t6kCIDA8RAFgYchi9h67TTUDq/4nODMrea0z0FcZR1OgZfM+NYMk5w5dtJX/vwBAAdWh8uwBw4omP" +
                "eNUtBgDWzaxvA50AuQEUgI6nr5sA8KAvAEgJNSzf6Q5aNYC4ut0NL5/rDMQo+EPQsVqK3ls3AUn+D9Q0frXa70fOMI5iTAA2DEel" +
                "HPR6AM8QgKdhrGfbJ15w+heBOLWZC8O+mf8yHZBc0NFjzETCqr9DAFy/WcNCexVajnJ8D8Ymz/VuYykAjBIAfKjxnUhGkHAu2Z9p" +
                "rFDTDks4X+RQ2PybLUwBULxsa06pFspAy3q2n39FtDrWVmClfGXaxAXsAQbDMUDTBcB42XivNeKK5aqf4+v7JK/HFecIAMxbfS+B" +
                "9fzX9aNtmjKcs3pObSR80v7BAgD00lk//yJu0bE2hKNcXBPSaa3uYOypuQmwl5/at40CYLzsNeZPh/t+17+RFLAlDv+A74Xg8p/b" +
                "1uenjEHNnfAFwN1NDFA/8bT6J2vklR0vUO4apnMAT+mIsIGSaIB0Gg+FOj2WgP7xqwCESfLEwuE71BvYPtaAQ7IMezDG40T4eYTK" +
                "fE18DDyv3tR19v5JP1Sif8qou0yNmYYjAMBLDcDx+lqWWPwEJ9cD8Z2hEZ9/pUKAKFbTWiwWyMm8ghnXHsNCtSMZkRrAuDeeuK5D" +
                "c4aNQWFVBrTjWA6RvpYkiv+ESX7om2/qfEI4XUjzkEJqbgIAlFGKxp1pEiYB0Y5pm/heAkkqbeuUweeanO04AjAL4tPgxKuFieIn" +
                "PNBrCkn8kMcdvxKAeER8/d1uqPdT1MPsPgc0NYCOCCHuvD85Oa8Ma451jC8IkUXZoscw4qanxUzwxffD+iJk7aBd2FZTO7bGanr+" +
                "NAHCVNjmOd+Vdtcdc/eU4Z71p2jAEQClM/gZnC+bn3W5SAM2tokAwM4bBOG5ocShKZ2GHuME7nAEgRYBRwDEoUTLHQGgST5rowiH" +
                "z9dIo0JTNMw56ogP8qoovgBIWc3oN1BzLQc/95dybYf52B3L+RQByNr6sQJgkgInHiYJ6cD3wKzLmfeNNHchf9wE9AkAzKDfhnTm" +
                "CACc7B0CAGXySRaL4vhPfzNB0H1fFhVUaiJ8AeDuxHlfDAQEIEmZ4z61e8rEU44A9GYB9QlAYK8AIIfPFgD9oG/lw4D+UECm5l31" +
                "BcB3BF35glx2VwD0YMEmAUCzfpIMSBYxamcngZddBQCU7XiBooU+AdAB9QVAD/rkQ6Nn9DBlCtL7gACMOAeAdyN8AXgeTTsAwheA" +
                "5BSuEkSan4DZZUwtTQbtFwB1BF2VdaMACIv5AkDDnMAyklXMcn52wEO/opkCIJwrAnCt9fItU2bmmQBfANpLQTzG/ntjAJwkvgtI" +
                "q3GVvJlwTQpBmz5NRwDMqCYAvvBdQODEITsA8A5yY2CVIwByDZkpAOKh6hcANwi8Tuq7Uybezpkysmb6fQJwrxgcBGAs1FfoCQAB" +
                "AfCdiQCY625pMIQEuaL0zyT7kumUSgUcASD8sfYFYO1wCIDZiL6sLQD0b4yKAcAqFQBvUvtThrfWNB0J9iv/mMXgBlUD3V0UyBUA" +
                "+EkBbuTNQyVErW4dUuADBSlCMujLutCmZBb6hLn8UFZTANCTcwRABgvj2ysARhy4KwvIWCrlKUB3pwxkGFPGrwnRwN0DBMC5EGZa" +
                "XeiAfw5ADgfFV15ObFiLQtdKQIMOyCm2JdAAWzINRxAq+WCNPEQAAqYAyNozQDb0BcD3dylxP38OwDszzwoQt5xdLBnr1ISQy+KP" +
                "AHTBZ38yrPNl4x7wQHzQTjaI+JeFszBbbrmVa+c28fP1mnhfMh1HUMFJogfBxgsA2MoXADvgoWzrC4B/Ejg/BmxWgMAsDgGgm9E8" +
                "xk8BGK4Bd2MAhgDMgy8AgLyyeSmKCIBmc9+cbHgp/4KwAHqsJgCwyslxjMkcrDFHANigKQBoFm/nl8fxU57y88DmNBH0VazKa+rl" +
                "jlystMoZHFKnGhhbGvrTBQATRg4ZPQ9QdtiwkkuA3tJbkxYCUPuasSZd/VGOAOAefCdVJl5qqgBAnkcIgIyUmayFL9MXAB8whkn3" +
                "uQAgABB/fOuDhwBgIMxqvr8jDTTsF/weARAPuy8A/k2BPMdPZoFtd68AQ1ARAoB7RcqCRGO2CIDpIovHfXsMAG46NLVXAAJHAN6s" +
                "E5X7f1g05daU0eN4fmSiRwO+4yTwEYD+z5rLfxWAm7MOxEcBQGjRP1jkCwDT7GBPbfUXrzZNANDgKAG4fuF7BcAP/JTNYE7wWnnN" +
                "PKXICDAFIL4cP42btk3G3dz94wJqiXAK+4sA3LrJhGkzZlYS1kS+AKz7EtOJjH9/40GwNdPmcwTA3wE4QSBOk+QEQOEaUTgYrwKA" +
                "0XQueAhLTA2AqnXjtgCY5wB+ZRDYcWiu7E8UEkA5YykATu374OvtAuBoEuAIwJZSENogBKAnBnAEAK+GTOuXAhAA+5dvzcM7on3M" +
                "GjNERPOoAd0gwebwD28ZAjAJvgD4+9mgDPH7C63AMAYACmmpQE0A0E4SB64JAOaYRiY6BcB3ix8B8AXAXyrFc6OLRAPkRmWOL6bM" +
                "rXfEEMBL6WSEr06qybjrthkmAAMOghWuJwxGvn7KK8OKMoHB/Xw7P7U8eswRAMbZZJF1BADU0x8EdguCPp8tLTIQxrxzQsWfMluS" +
                "ua/m1TiqG79QAN7fLpHO/JpZK3g1K0KXQRDRvtxQKAv/l74pBAC+8Z76VQDILDUB0PsdOwUgnjVfAPyDYPMFQM5L8obhcp3wrzyT" +
                "zh04Plg5Y53ak2AIgB9P8DGtFlB80My6ofOdwJoXUyjn/VUAnNumfGDy06cJOAIgZcXgCPq5OwAjCNxSCkJocWMpCApAjKB5nXI0" +
                "xeuUNbXBPjKJlRykdMKBHoyFoQEjdwATBcDQgF3F4MDvAIkekC91pf612hqtIlX5KNeng/2+AKz3C4JNnhcAPwjsp4H+oGJwXMRs" +
                "EYCAEwpCU68PzXjXau6tYT5JAwa4gObvAPw6t8L1QvdEXs6IJvknjHy8TKRhjzkCAA2o1d4ach8AMKccdHYjmCFOTQJQdgOyVpWs" +
                "tMwS0z7YY0lyx+cKgI++5b9fHuCr8U4G/p3Q9Hq5mA+/RB1sMwWAGuA7gh6vBqoku+tCmPiHGX6US+Fhm9lsqwBgIMzb6q97SnrS" +
                "OuATwo8XAP5L8HFZQMZ451bRJN5n5IejAScLEJyyVwCAG5xiXJMrlfX8sfYvNlkr9zm+CwoAzTO99nKokL77jQLAitBVtdOXRWDp" +
                "26cMkkGvnPBjXUBKVs+dBH6S/UkK3QKQmCTe/4JJmK5IZxawjFqNAtbtNszzBQBTt2nVxmjhuij24z0ASMdxZCGtxSncBHCpfjUv" +
                "uUatcKhwgADoy8pAQO0KU0ZhlHbnBsUQgG4yNA6CkdpW5AIw8wywXwuIpFDWgKtVNEnWlbXpSnCv7fg0xX2PHqulgYoANAXurl4R" +
                "c1EsYy1wBEwKN5XjwNer00w/u4iTfEiQkyECECD731Ri3X7Ja/pTJhGA4QfBEt/7bQGQvzHRxP4gBV8AXiZ6AgWTkM1Wna4qANwa" +
                "O6znrLYoAOvO3eMpY51o3P5GCmM2ixMAcNbXYpugLE6sKtgkAGjQFACBN2UU5pQRATCpf0otIJDUS7xsS5JhJkDyxkhA/pctMoAH" +
                "5QJ2tYom1RZHOCcpxxH4nz5J8R1rbBVPB5uwlwg0m1voZ7AAymK2SyTMNpfYbNAcpjCDrZH9HUWJJTBbY4PmNBE5cQTA309w2/QS" +
                "kBMzDCAC0MrmswWgH3/k/ZX9Qf30kptHHOFwB+msW4FckESNCvagtBwtoVeBPs2ym4ItS5u3LETdU3TRqgEd9SE4vUkQ/ljjXhoi" +
                "DA4y8kOs/p3S1ACMFBDtg8J8/w/6zZ8mnCOOAGCtw3YChWHFFkdM4kcSCNv8zZMUaHqe/TffBwBSe4mXbclJqG+HZA3D84BT6c5p" +
                "yStwdck1cPqVRK2Vn7EQrtlzNQDlhrBeM98OYLM8wFm2UHxBgesWXvnUFgDhVn+sBU6Kre0WV/AD8F8T276OrsOHZCY+oR3wrD9l" +
                "MGe5r/WnDJuNF4x5nQhAd8X/AQLQBpP9sdaGyPedBqQG5Fb54VAxIFmvOc3qssiuLgDo6dAeAYDlEwCGhRAaYdvG00yy/PeFed36" +
                "mGrHIWYj/nBk7kHjBmO5IcCg/iMANkC1fcdG+H1L/Z9Azv5gwMkCEK1tFICXGsDQwi8WALgIRAVbqiQZtMWtiVyS5eMIANh/vgCs" +
                "DqUfLwDX1MPHBEA0QNj/YwWAlHeVZHO9OV8AYJjUj5K8WD8cYo4yv95dAnAEQK8wszXg+SDwbxMAJYV+AchN+kgB0CR9dgKWwGMF" +
                "wHT9y+5w3Q46B7j8rYlsSpwCy0cA1ouCGzBVAI4AJF6gswOgOooGcB6OFgDjShOh/pf5YFYwwGB/2ZfsFYAjAKM04O5J4D1poN+u" +
                "AWCZCTGAIwBfybM/NBQAir3JYn7v4cXzuoHX0vY+HxXYf50vEADfjCMAgcLVVX14UAAGwGcZRwASq65LrQ/JAmLPfCWHtiNIz4v6" +
                "jmzzSts368PIVoD90CQDzMuEeWvVP9S7PgLgCIBxHvgIwG6QYkgK3ZdCfEsaKM4B+AKw6oovAC+vzWOHGH7wQQJwvQIaZqwL/5UO" +
                "qAFSqRBD6chAfhhKzJNTk0cAfooAdJeDbqsF1A8l3LYLSfAxUQDyw8Ad9xnBAB4E6xAA8wCUXJydnIs2S9lgFADHj1E4WAR3Galf" +
                "3jchAtEAOSToywDsRP+EhfmH2icAZr0dtPPjBOD5otBHADLCjU+QM8rHehL4VimIAC9I8QzQY40b3+u6FC0Ak3a9ZYysJ4vfWofA" +
                "WhayBnC9mmH5u1f/h8FfOQBz9ieSEoHYDaAExfVd3lQmbkpwzuulhYRcerz3Q0JsxvnaobLR1RunTGDvlAlxOgIwC6ufgSVuTKAd" +
                "2e8DBZOAmg1SAMcHmzXbxBxL/OBJh9x6CkyVLCMYD+zqEIBPXHN/84ug8418Ur6QxeOCsIIHQw+C2aFGqzjFHyB+g6g4jczZX+qT" +
                "b+y3axln/4M0G+mbMmMvBz4CoNHXjUhWVQl8kyaDy/BkRZyzXgFJHnAfvqpW8ubloGtXJL0BAs1VbRUnDkdi5B+fe/D+Zz/3Ysgj" +
                "AB2kUGB/WvUkVZkvWEbCiQnrmc/ym3KeW7vW4v2P9q4GC/snAlCeKQe5vv5QAWjMAnpYAIA/rhC3UIBv1Xz4tJj3CbC3RGD3CxJ9" +
                "H60p3rmdTl8dGCuAwQIAr+JLJCGyg4ODg4MRMEh7vgAcHBwcHBwBODg4ODg4AnBwcHBw4ArA//7HP3+Fl20lf9+Kg//5938C/vvf" +
                "/vGK//zXf2iFPI5mvG2k4snnNvSAQM0DVvOSntlrIR/kd5E/TP04sEj7L8k3/bIt/F8u+KH0465hfQTq98Zq4VeXS+QmFW6rkNd5" +
                "x8Itl12sBuQDJ09sRdpFCu38fXauNsh3ldvgQ15cBODvf/trE/7vv/7lHeCP+Y9uNCz7buAlaSd/bwiAweaDeN+gfl/t7n4KPtP5" +
                "RJPIgNi2pQQun548mt3C5/LRfciFKtcn+a1tg5ohGrAa0Pp59GsAqH8EJgmJsQPwxaRv6+fyfj+c3nj/UxCGfZM4duUXozUh4pyF" +
                "fQPWa1dzASg81zSMWDUyYX+1sG2ArugzQHrA0IDpYjBfTl6SdkIsf7m7m3jK2X0Dk/nd//RXAYhpdi2vtgt/PHEaT7xSzB9ZOP6e" +
                "v3VKTehzUwGIv4zOeaxExFoTWGQAgG3d5q0dBci6IUzdbkA0iKdf5acgAEcPTOQCsOKbg8ATWN7geo/lDfaPSoeV9ACjOiaKcXKS" +
                "r0S8sj+M9DPK8Nxce/hodk4rWCsU5dhQHlLuCFx9/a3m4RKYrzTgyv5RQSjs7+iTUAJ+HhQAf8ochTAFIPl7QwAGrOXnc70PdiPn" +
                "cEzy77pePGiO/CJhRmGZ4IL4+123rgfDgltzAYDwPHbvrpSqDiXAMnz1itDXHx3YZ16YEeR+7ShAhBnV9jt6I56uAuDNmrkicQTA" +
                "j80ern8z8iMcZ5CIhVwAyDLwckS5Sr390bty8itHEB/tM6wvBvFoyAB3A1dAF2Fekw1RTRoKtG5BaACEuUkA8Oh+ATg60SMAZhDY" +
                "J+sRwdh+3NpCyeJ6iABwkq/L/+AgvRnKvt0JPpZVA7jC3c+wxn28QYVSHBv/bjUPCgT5kRwhdI4jzIYAYHJ9LkYLQEKa5jmAw/W7" +
                "QiOrd+W7BCAeHQYIBQOtLBz+5fACgUypAat/Yz/DGlckyi1pvIqr1Txcz0k3FED2D3vg/5m5Azh4XgD8cwCjM3AmE/0Np9lUAQiI" +
                "eSCaIAJ/mbneOx+8JhoAXFe4wbCbLwo2di28O3C9jb3vuUHxMUxSphgKhM7pe3RoDz8PakDrrDwYJgAG0c/neoPlLUwTgGQNDqIJ" +
                "P3iY18Fr0AAYQAwUACzGww8GDQCeEYBonw4oAvITIhpWtQoARmc9DXADRySeOgfwtACYLA/MZPlWDIkBIM+P/PvVGjz+JjIdtwtA" +
                "vHKQqWwCyP7UnjkCAJt5mzRS77sFAMITRA+xJHAJMP0/3QIgTkJzyh+F6BAAwBCAhOUP0XtYg8ABI9GlyRGvC3BQMDiuybu9pttf" +
                "z7WOEgDYjKQgolsAuFvizqN/86ECIE7CAlGMFokjAM6wzSd62P8w3skCmiAA9C2s/MswI/JMmnIcJcNyVSAnytphc2yGwqR+FlYv" +
                "EHce3Hy0+n8oAIw/UwMI321wROIJAcizgMqd/rHLeR+rAESAkcdry9NVcEsAXiaZoPYDEkCbwqp5GAAOqI7OMb1AJOIQg0gQonnd" +
                "XiCi1f+Dd4nPYxUAaECOIxLfKQBJf71s67D8w9QPSKAVzoSY5LUjwfET1DCIFgLxj2DtYA1fAEBwMKkjCo0wwFeJQNI5oQTlzomf" +
                "B50FonNQvMHZNjEXCBog5vV5ga6uJ2yMmtxc0VH8NhwB6BWJD9CJJ2oBzc+n/MlcryD7XwWAVIJwa82dEjMWAB8VBAD2SAAg/qw5" +
                "yV0FAJBQRDkWjZRTYc/atgY2o9MCMAzg2HXHn4E+/w8W/lA4n/3ni4SpE8B0AcjbMin+sHzhWgISnC8ALK4QBBEtAG8KwHUBHlgj" +
                "wNEO7GktdZCnojIZCU6PmgCIAx2+e8MzrkmZNK9PLCFj3f4fLAss9p8vEoZO+OgVgI1tNRQH/TiiF9InhN0CvgCA+gFEbmtZQLWD" +
                "pjWzw7/EVNRVAAIdAoDgLV1bvgCIea1eoACM7xOA1S9XZtj5IuFLRatatAvAofg60Ru3M75MuPQFgC4CCMCtg2Bq0p0yDMhUCe64" +
                "a7yIEFea6iWDGhkCcGV/aK0vAFIUqFsAYizi0dze9ScHv64R0lcubL5ONEkFsOdGsLti8hnkrti+nCcMAbDc3Gt+Oug4QRBKUGEi" +
                "AGsFiDylB5RaqwlBM5oEgDsAXwCiWwwBcL1ADGD0Hw/UKoE+jlQkcK5xrAvAJ7O8SfQ+5NDTXjc3coGiQSD+fQViBnIM+CoAtypA" +
                "wJsP48u1buKhtARoEgAIZEEAwMKPC4CmM8HyVgGQ2xosAThSYajFS9JOlON2NVBzPA7RO2iNc/Lixq8uPhT2dypAIHcQglHwbCTJ" +
                "oAENSBjbIz+NEh4SXwAw+wonwkJow4Y3C1k/LACJI4H/8PEJPiigUL6hvRz0IfoaxIwmL4dogJA+eX89eQsz1ips77NGGADftLOm" +
                "Fg2gSb4AXIUQQfKw2YlaUwAK4o23rvXVm6cNcKejIwAYiEwA+iOi3QQyXzBeknby95trAY1i+flEX8iWu667fY5LrjtP2P9lQAJh" +
                "5FvxyRCAQl0EGB8WPiMAyKKpNQVPF00lYN5dASj7oN4ckaDy8hZHYgAGLRjyMFItfEwUgIEUP5/ofcefCADud7UPuyp4cjVd+2sC" +
                "aB5pXCvk4FkFUsPPGQagbbYAaOdAongS2M+Rd+5miabi72FGU7QAYmwIgJUD2i8PH6EW7QJwWL6V63P4OwAiyfZ5ebRndbWXK0Dw" +
                "HHIwLOz3a0JQAHo6h41YQmUKQHRagaMbGjcEwMNkhVjxPElWYgBJt24UAH/j4xP9fK5vEgAvdV0DrdSAcgUIRIDjJ+UMRdaESOLS" +
                "Tuf4YHG0r0onFQQAe5GO4Y44QTQ+UACmbSPma0ZO2huCwBazGyz/YVyveHn3Vr8AaKol4VSAQARYaxCVfOszBQAS9bJ4tSMA8EcZ" +
                "XqCsM6Px4QJwRCJHbdX+QgAquwmD332Wl/kPDOB6F0kWEJi3TwCSur5mNiojwBCA+EfwjpNdQyOBbxcA5FPGo+Gk2isA6K4bL2W0" +
                "bB4EewJHJPyTwIY7SRiq/Shsjvlc72ukw7ymAIgltQoQ9IyzDh3jwLX46ioADdujSuo9KnFSAPwYAGl69QL5/p9o1hQAvGBNAI5I" +
                "AIMEoJBRtOWIEzGf632Wr8nnMwJAbl0rO66ZSKgAcSvhJH7CHYBTtHn1sPudY5bfCfZHBR4k1NJC0zz6zQwvUBqQbxAAO1J4dKK/" +
                "FtDdlNJmcld0Z9YPIPqxAqBe3a/iEDjQe/cuGqIsAKgJsSZZmp3jx37jjVCKmebJKTBHAGrO+qRZmPqwAPg4UlEL3KoAJDSaC4BJ" +
                "7v2LesVMlndcQB1poDgRKmG9vOw+GNx5tHPniTKsWylPLayJExIrcRswLDTNowDYXiANpUSDaHZjENhit6MW/cXgiNsC0M/1FtED" +
                "o4jeeKnGUhDxw+vJL4C1gPIE0OvFZLcqQHRcECaHlvEPRwAQpWBpPF7hW7srkZsAwhSAzAtkXAxnZgGh858/CHYEwy0GlzC1IQDt" +
                "K3piAsv7r/a8AICb6KcGpPxDfgIABrAChAWDv8CwAZNhV1oEcC0wotzlG+Ft8zR3Nren1qYpAOIFEjQ6SY5aOAfBHAFozrNUTGF5" +
                "4wV97BUAKfpGT8U71d/AYmateT+DJdgnZ1jzujQg/pdgutomYLcAqBfI9/+YAkBHnKwYhl6rMgA/WACeIcHxLK/oLgVO8xoE4DX7" +
                "A0n1N+OixG0IzkqW2Bsr5fFNzUh1oGYeo7WA7wVaNzqmACRuQ8H8axonY7MA+EFgfzjnOOUtuu+HnwWUCICwPyDsLwKgRZIHhAFy" +
                "AXBuBCu0hj0Ky0GbWUDILuX5CdMLJIqyVwD6T2IekXDr9xQFoG9gmvImJ9C9Dw3AqhuhVwCE/fn0aMTI/1GYYQAYRpgCQOcYpI7O" +
                "LufEslMOGgLAHUlJkNSwIO4BAmDMyiMSfhaQUVp6BC3uxeS38y+FTwQAeNP/Eygsill0LOhVEP8jHCw1IusWAGhA+f6yXQKA08WE" +
                "E36HcF7dXNF4kwuob7bOFwngCMAgiid+kIx95YI3BYDh03UTQPbPAwDwQtx1Okt81blxhQS01wVEtxKA1hwBUBeQIQD+NcXcT/h3" +
                "XuYCMHLiK0Zd9f6JAjBgmOeKWcK/mLSmAKyJQG/m/xQSQOOP6XQm79ChwXqi5oId5vkxgI0CEG/n65MIAOQfXqmy/6dVAN6kxR9B" +
                "IE3JHT5+hgBM2PfNZ/kcIOVVAMKpYgrAqgEUgK/uW68RYngYQjNEAIxLdzXThm+xbo/8ILApAKtDqSwANIxeoHLgpEkA5CzYriTI" +
                "D3QntKaBGgLQ36fzub6D5XP2v7qPwZhwnftXQvIYMKhq9eRK9Tc8/Zb2gArDYDyRGoAExAAuJCkX4Ix3wStATpDYDgtr/Air2GZB" +
                "AICrbSDumnk4UgCTzORU+n/YiNP/YRhekEWh/XJAvlT4+KGUdTsNNHlz4xzAfvxoovchhSRZn8DJA1mBm8eD4r8SgHg6luqFR/Mp" +
                "LJcPFxZexH8XNBvm+Z2zmur4W9AOTgUHauaxqetWgFaV/T9Y+PvdxRJSKAgBASgXOp5z5+D85EPTbWOcA9iNQ/T5sygAMc2wW28q" +
                "Z49NwFcCUHbWr1cCrC4sv9n1RKt/2DjsJNU6HYtXNs0Lqr2u36HH5fwfucvTLyLLL4c7gFF3KA6s8lvA+sOfJwC/yUHfXhHPuICl" +
                "4LKQ6r5HAHwBYAgksEsA6FCCF6imH00CgAneLwCzpAL4Fip7s4b/owJwiB7we+lKwUcAjgCg+gXAE2p3/T/47V4BiLeTMMCEu3PH" +
                "OKAMwZjvAvpYlve5Po/7r0UgHhYAiQBHtPAIwAQBYEIRSPxW/g/14xkB8DXgqIXg2wRgPsuDsIZzfWGwo8HHBADT+HoO4AiA+eJe" +
                "DEAFQM4ooBsLvz0CMFwtiI5qoCoAAw+C+SzvwyF682YMQwA27wA+0wWENoP9RwkA6u8TVOW7ZTOQ0TvcBXTgnwRO9GP6QTCl4/lc" +
                "7x/+9gVgfAwAuZUdAhAc1CEA0SejBEDumEx4XPw/ZH9WTJqTBeTjoP0ksEHuFsXPd9b7dD/DBaRpoMR2AQh8oABg7BwBgEJfEUOW" +
                "HnvWq7uIDgF4mQL097/9NXAI+lsEIPl7IwjsUfx8rvfp3gcFgBSM2d59DmC92w/jGE8Pht0iADjHJLTlN4tC+dMEAB2LWIIvANyi" +
                "XY8HoixE7v/hMW8A35Kvl3xEvB39P2B/UP9NHJ0YLADmcVYTn0P66/KffRi7+Fg4x2znAU4faC0QLUf7wv5XBULYEH9cQ/w8fBGs" +
                "h/P+D/9oP6qNmp0jbUKrePtNrVkcscamR8zLn74CCg0eJ7gsSPo8BAwOegLfEk4mm8AjoqN4DFgEwMSPiNn+RAG4fSWkQd8W2km/" +
                "n/oLj16PAnC2YwXtQ6qzBSPI8l9EiE8nCs+Sqm27gOTI7T1DU/12YJ7TiLA/IeOyjuyV/a9ny2GSCfQ8lv+5APgYRfpDUBeAhHML" +
                "AnB1L84n/cnINYDRvxx0+KbQIRP2FwGgAQKa1IcDGSZAZl/yq+SaTxNs+Vb+jy8ARwzcctDJoL5sy/g+3oLP+POp398HBPq2WdJ7" +
                "hadj0BtwIOyvGnDrV0DHlyMpDx+Ytv/s6TDjUvi7Ltct7sK9ODg4ODgokPb/t3fHNlIsURSGNx4MLILCxNxUMMkDizRIBix0tRS0" +
                "tlXT596eT/q856CGd/5Z6Kq5OAAACAAAAgAQIAAbXykFwEEw0gLn9QJu/7aPp8raplc3818JGddvgpUDxUUAxENLQP8EQEskkJJG" +
                "2HB9gwDcbUklQQAIEACGrqcGPIy5RwCwoZh7BAAlwNYjACiBoQcBQAwMPQgASmDlYfs5gHUA6nd9CIAYmHjmcqDs4CeAZQCWBOA0" +
                "JTDxELkz6lwA1gSgGyUw9Fj56uoA1B8R+sPubwH5oW8VgEoPBEAJuI38yk8JgB4Yd0kYjfzQzw2AHpj1QbSBwNALgB5Yf3kgIjL0" +
                "AiAGGiAJBOwfeieB1wEQAwHQANLyQ+8qiGtKoAGYfgavvLuAZMD0n2Lx7b6VdxmcjcbigwBoAOYeDm8DfeOfbwEJwNASYO7xIpPL" +
                "4JQAo89zHUa77WVwSoDdx8oPOAj26eOH6vZbLwbWHy5Y+TfT2i0Adf01QAwEAFu/7WjCclrzAViu/+uXz7/VX6jjXWIwrgHQ5O6g" +
                "/0xrPgDL9V80oOtRLzEQAGh7cdDhtL43AH//P5IKgB6IgeknIL/1uwNQl3052n/+62MDUH9OqVwKJAZGn4jRl8QdTmv5898jAEsu" +
                "ibsgBrYehg59/RRfrRf1mQOgByb+vbD4nVe+Oh2A8FtAgfUv9MDKY/EHDv2BxTugRf9zAHX9A64ZXDGw+zzUeuXnD/3ocwALB+uf" +
                "FtxZMRidAQj8OR9wEnisViM7ugRigKF/ui+F3/oVYHn9d3Z+BiQBWz8/ANvXc2IPvKgjDPjbGwHYP6CVGCAP2PoHBuDnj6/VcQAa" +
                "vI2jB0iFrfe7vz8A1YkABHoQDYMYyAa2PmXDZXAHAZgMgLr40wMAgAAAIAAAbLsNdHQAANh/HTQAAgDAmAAszgFc/AZ9/TeGE278" +
                "hQHX/4K9Te9V+gfxiKY85JcmJ630oAM9MPEneFZzH+nLuIO4+wKgBwccu7VcPtrnn+GUAEy/BzSykjPX38rnefh9AhD6IswNXh40" +
                "FhNTEdzHbEV8kKTVKl3fgxGPKx+AEXMW/eCP0TfoGzzz4/r+7fVdBgdg7t+ftJ0/W8/phRKAfdud1y4AqV279/pjx7vNVrcGhB5O" +
                "XqMAeKkLW3y9+PR4UJ1/C34BP7yblfEbb7kAAAAASUVORK5CYII=",
        };

        // FileStorage keys a row by the image's CRC alone (INSERT OR REPLACE), so signs given identical bytes share one
        // row, and after a restart Signage.Load clears every one of them but the row's last writer. SignTag puts a
        // tEXt chunk naming the sign's net id and texture index after IHDR, so each sign stores distinct bytes;
        // ImageProcessing.IsValidPNG reads only the length and the first 29 bytes.
        private const string SignTagPrefix = "Comment\0farm sign ";
        private const int PngIhdrEnd = 33;

        private static readonly byte[] PngSignature = { 137, 80, 78, 71, 13, 10, 26, 10 };

        private static readonly uint[] PngCrcTable = MakePngCrcTable();

        // Decoded on first use and shared, so callers must not modify the arrays.
        private static readonly Dictionary<string, byte[]> SignPngs = new Dictionary<string, byte[]>();

        private static readonly Dictionary<string, string> ExperienceSignImages =
            new Dictionary<string, string>
            {
                ["farm-feed"] =
                    "iVBORw0KGgoAAAANSUhEUgAAAQAAAACACAIAAABr1yBdAAAJRElEQVR42u3de1BTVx4H8N9NAuEVEVQeSwkIgiDyElFERLDo" +
                    "OlRdVChad9fW3eljWt3tutvpbmd327HOUmeYrXVmHZ2pnbZ2qq1Wd9txdX0AER8oQiCAvI1g5P2QEEJIQvaPi0wMEPPAXZr7" +
                    "/fwVTg733nNyv/ece5PcMPWSwwTAVTx0ASAAAAgAAAIAwCkCk793781Dp4ADO5T/LkYAAAQAYOIUaKqRAuBHbaq5PUYAwBQI" +
                    "AAEAQAAAEAAABAAAAQBAAAAQAAAEAAABAEAAABAAgB89AWdbrhoafvmtj6z6l/x9b4gDfGb+EhiG+Hw+n89zdRG6u7l4e4l8" +
                    "53mHBvtHR4b4+nhhp0cAHJzBQDqdXqfTazTa/keDirZuGd27VEREFBEWuHVDalz0AvQSpkBcVNvQuv/vXx08clqj0aI3EACO" +
                    "Ki6pev/A55oRZABTICNCodPxw39ymCVodfpBlbrlQYe0qumypEyt1hhXa7yn+OTod79/80WGYTACgANyEvC9PD1io0J35q77" +
                    "x4HfLIkJM6lwq6xWcqMSUyBwfB7uru/s3hYZLjYpP3m2UK8fRQDA8fH5vNd2bjSZ73R199fU3UcAgBMC/OdGLQw2KZRWNeIk" +
                    "GIiINBptzq4Ppno2c+3yV7avn/lLMC9yYVBVrdy4RN7ajhEAuGKut6dJiVI5hAAAV7i5Ck1KVEPDCABwxdCT7wYQkbubC84B" +
                    "gMhR3ggzr7O736RklsgdIwBwRfWTZ8BEFCz2QwCAE+63dtQ2tJgUxkaFIgDg+HQ6/dEvfjApnDd39qKFQTgHAAenHBz6+Mjp" +
                    "+qYHJuW5WWl8Pg8BAMc85A+q1C0POstlDZevlqsnXP9ZtiQidUUMx3sJAZhO5t/HJaItG1Zt37Lm/7sE1oL5AXte3cLxz0Ij" +
                    "AByVkhT9+s6NQmcndAUCwC0RYeLsTakcv/KDADg4hiEejycQ8Nm7QszxmuXr4xUS9JOYqBCfubPRPwgAEZG7m8u3x/6KJXAc" +
                    "3gcABAAAAQBAAAAQAAAEAAABAEAAABAAAAQAwNHM0I9CvLf/0/Fvb3y8/80A/7njT3116tLZc9eIyEnAP5S3Z473LLZ8154D" +
                    "ykG1yMP12CfvGFfbtjl968ZU4xIiys1Ky960enyZnx4/d/7KbfbxL15cu2l9srUbM76Wp1K0dZ/6V1FDs6Knb0AodPIUuYsD" +
                    "fFKTYxPjF5pZ5unvJSfOFBBRVubKHdkZJs0hIoZhRB6u4aGBm19ICQ99zmRRltSZtAnmu9HY2rSEV3+5wcI2YgQwp6Ozz/i7" +
                    "S0XXKyatptXpT31fZNsqLknKRkcN7GPNiNbMTZIt3BgLNcvb/vD+keKSqo6uPp1Or1INP2zvuXnnbrmswc5OMxgMA8qhUmnd" +
                    "n/92rFzWaHMd+z27NnJlBDDZHYtvyrZveX7Sb24UXJVmZab4zrP6d696egfKZQ0JseFEdK2kauLdcmzYGEuc/XexVqtjGPrt" +
                    "a9kJceEjIzpFW/eN29VCoe0fzWePzaqh4c9PXCgolo6OGo5/czH+yR9BsqSObev937SRWyMAu88JBHx2xOzqeXS3YZI7GPN4" +
                    "jH509Jt/Flq7fM9Z7kR0qegO++fFwjvjhTZvjIUetvcQkZuby4rERUJnJ5GHa0RY4CsvrX9p6/N2dpq7m8u2zWPfFGtRdI6M" +
                    "6GyrY79n10ZOBKC+6UF7Zy8RRUfOT1sZN7YXXp9kisJ+n/XqDZmirduqVaSnxBNRWWVDT9+AvKW98Z6CYSg9Jc6ejbGQl6cH" +
                    "EalUwx/mHy8oliraug2Gaes6AxmmpY6dnmkbHX8KdPXxlGN5QmTc4gVCZyfNiPZmac2vfp7pJOAb10yIDX/Y3lPf9ODk2YLf" +
                    "vZFj+SpCg/1Dgv2b5W2XJWX9jwaJKDoyxM/H256NsTR7q+KlVU1EVFnTXFnTTEQiD9eVyxbn/CxtlsjNnn5TDQ2fPFPAPhYH" +
                    "+Dg7C2yrY7kTZwpOPF4aEb339g72xyefXRsdfwTQ60ev3apmpzeJ8RHOzoLYxaHsK3enon5i/dzN6UR0s7RG3tph1YrWrl5K" +
                    "RJclZcU3ZewVDPs3xhLJiVG7f73Z+KRFOag+f+X2vvwv9aOjNu+IObs+ePmtjwqKpeylnh05GTbUmS7Poo1cGQHKZY3KwSEi" +
                    "iggTs0eL5Usib5XVEpHkekVSQqRJ/ZhFIYsWBtXU3T955opVK0pJWvzFyQu9fUp29p8YH1F4TWrnxlgoNTkmNTlG3tpRUyeX" +
                    "yhrZqzHylvaGJkVEWCARCfj88ctcxpe8xl4w/uQjD8MwHu6u4aHPZWWujAgT21zH/pNgS9qIADxl/lNTd9/k3h7lssZBldrD" +
                    "3XXCy7DmL3mflUrreTwrLs24CJ1XJcX8p7CUiNasip/05lA2bIzlggN9gwN9MzOWHzz6HTsK9T9Sjs2hZ4vYB11GN7Lt7Oob" +
                    "e9ZLZPmOaFWdaWemjZgCTUKt1tyW1k31rE6nv367emJ5ZLiYvcfB+HV9S2dBaQlExDCUsTphujbmqQ4eOf3Z1+erauU9vQM6" +
                    "nb5V0XXvfhv7lL/vnLFhLSqEvV1PSdndyupmjUZbWd18q7yWPYTP/Bs6WNJGjACTuFFao9XqiCh5WdTbr2ePl1dUN32Yf5y9" +
                    "/LIubekkh7ct6RXVTVYfn8R+Zr5Obu3GmJwRRoQF7vvjromL7e1XFpdUnbtYYlKeEBseFOjLPvbz8X5hXdIPF25oNNp9+V8a" +
                    "V9u0PtmGNz1sO6mdqglPrW9JGxEAc/Of1cmxJhN9by9Rb5+yrrG14/FMwNiC+QFL48JLpfUzYWPMy81Kv1VWW9vQ0ts3MDA4" +
                    "JODz/X29VyRGbfzpCuNqO3PXhQT5X5aUyVva1cMaVxdhsNgvI3VJSlI0zXgWtnGGYOolh43/3r03j31wKP9dAnAUU+3Y+DQo" +
                    "cBoCAAgAAAIAgAAAIAAACAAAAgCAAAAgAAAIAAACAIAAACAAAAgAAAIAgAAAIAAACAAAAgCAAAAgAAAIAAACAIAAACAAAAgA" +
                    "AAIAgAAAIAAACAAAAgCAAAAgAAAIAAACAIAAACAAAAgAAAIAgAAAIAAACAAAAgCAAAAgAAAIAAACAIAAACAAAAgAwGOCqZ7Y" +
                    "vTcPvQMYAQAQAAAHxdRLDqMXACMAAAIAgAAAIAAAnPBfYWaXZdUz1w8AAAAASUVORK5CYII=",
                ["farm-clones"] =
                    "iVBORw0KGgoAAAANSUhEUgAAAQAAAACACAIAAABr1yBdAAARTElEQVR42u2deVhTV/rHT0hICAlhF1mCCCQEUBYRUFQQRS24" +
                    "ojIq6lSsfVoddazOM3Wqvz5tta3OPNqqHZkuYjvjLhWlFlFQQBFBUPZ9kX0PkJCQkPX3x5U03rBEirHg+/kr99x7zz335Hzv" +
                    "ed+zEiruRyMAeFPRgywAQAAAAAIAABAAALxRkHDHu/YdgUwBJjCnju2HGgAAQAAAoGkCDVVTAMC4ZijbHmoAAEwgAAABAAAI" +
                    "AABAAAAAAgAAEAAAgAAAAAQAACAAAAABAAAIAABAAAAw7iFNgHcQiSW5BZWllfVVNU08vkAgFEkkMgMq2YRBZ9pYujgzvaY7" +
                    "29lYqt8i7BNv2XlUPYRC0T8X/dHoElDf2J5XVFVSXtfcxhUIRH19YgMDMp1OtTQ34bDsp7tNdWNPGeZ2zcQghMxNGaeO7NLX" +
                    "H+QP2rn/ZFt7t+rQZrL5iS92jhjh8Bw7tN3edtKgp0or6jOyi2tqm9s6uvtE/Qq5gkwm0WhUMxOjSZamdtYWDvaTWY52DCND" +
                    "EICu4fGFN249TL7/VCTqx5cqoVgoFDe1dGY+Kf3p8h3nqTZbN4axHG3HNgFllQ1X41MLims0y7SwT9zW3l1U+iw2Ps2BabV6" +
                    "WeBsXzftY+Z28xOSs1aGznmN2dvU0hl9Nr68qkHziyMSSzq5vIrqRlXgV4d34L4yIIBXS35x9anv43h8oTYXVz1rrqhuGEMB" +
                    "KJXo2s37l6+nKpXKES+ubWg7Hn01KN/z3T8vpZD1tXxEXEJ6SKAPjWbwWrK3vqn9k6M/9gpEWl6v0CIfwAcYM+4/Kvj8+Dkt" +
                    "S/+r4Mz5hEtxKcqX+dfTMvK//PqCXK7Q8nqhUByXkP5a3k6pVJ76Pk770g8+gE4pKqs9HXNDs+z5zeAEBXiyHG2N6IZSqayH" +
                    "L6yubc4vqn6UU9zfLx3DBCSlPrl9LxsXaGVpunrZPE93J2MGTdgnLqus/+X2I5z9UFxWG3Ph1rubl2r5oFt3s0JD/MxNGb8z" +
                    "wS/r4RSU1NTWt6qHODCt1iwPdHFmMhg0mVTO7xU+q28trazPyinp4PJAALpDLlf858d43HeUakDe8/7aGR6s316MRKRSKdZW" +
                    "ZnP9p23dGHrz9iOqAWVMEiDsE5+PTcYFunMc/rEnUmXeGDNo/j6ufjNcY84nJL4olaTUnEVBPg72k7V5lkQiu3I9dXvUCh1n" +
                    "Ms6rMaRSPvlwC83wuTFGpOhZUkwsLUz8ZnDeXre4pKLul8QMPQIBTCBdkJz2RL0NBCFEICBc6cdBNSBHrAxaMM97TBKQkJQl" +
                    "7BOrhxjRqft2RGga9wQC2rox1MnBBuc8XI1P0/5xqQ/zmlo6dZzJnV0vfNRtJluoSr8mbuwpH+7eMB494HEpgPuPCnAhs2a6" +
                    "DVP6x5zMnBJcSOhCfyP64I2ABAIhYmUQLjC3oFIikQ3zCFtri9+cS4VSs8J51SgUyhebgzq6eYIJ6QOMMwEIheKqZ024wMXz" +
                    "Z+osATy+sL6pXVOBw9ziNd3ZgEJWD5HK5GWV9cPc4jjFepaPq+owO7dcvcFRB1iYG6sfisSSA4d/+DUps6WtC5zg10l9Uzvu" +
                    "40QiEV2cmTpLQGNzB96/JOvb2VgMcwtRT28K0wrnDTc2d3i4Ow5zV+Sahdm55XLFc1fn3NWkz/ZHjTrZ/f3SiK2fDnU2bJF/" +
                    "1Ia31EO8pznfvP1IPaSDy/vx4u0fL942olOn2ls7Oli7ODM5LHs6jQoC0B28Xny7p4kxfdDu0ldEr6APF2LMoBFG8v9MTYxw" +
                    "IXyNeHBYTzYPnuednPYEOyytqH+SX+HjydbNa3q4O7qy7Usr6gfLAVFBSU1BSQ2mbXdXh7AQf50l7E03gfpe9D4xB1enCdDo" +
                    "csaZN4NiQNHXiEc84l1/WjVf3bG+EHtXqcPOpr3bIxyYVsNfI1coCoprjpy4+MVX54V9YhDAK8dQoy1CJJboNAFUfFuquH/k" +
                    "BIg1eiEMqSP375oa05cunqVu/qVl5OvsTU2M6Z8f2LY+PHgo//4Ft76w6nj0VTCBXjnGRjRcSA9PIJXKdGYFaZYGHl+oVCqH" +
                    "t4K6e3pxIQy6VkPHVobOSUrNUfXIXr6eOsd/+iiSPbqhfmQyac3ywJWhc/KKqgtLakor6uob21VuCY6C4pqCkhoPN0cQwCuE" +
                    "aWupp0dQ94NlMnl5VcM016m6SYBma3e/RNrY3Mm0tRzGTqhraBsxnqEqnNVL5/10+Q522MnlJd59rOsiQiLO9GLP9GIjhCQS" +
                    "WU1dMzY+FNdVjNUD404A48wEotOozlPxA9rupOborgpi0DSHDWv2DKiTV1iFM5P0SUQOy17LJy5Z6KfeKHnt5gOxbq0+XJ3A" +
                    "YdmHL537r0/eW7poFu5s5zgcEzH+OsICZ3tolr+nBZU6S4Bmq/+tu1lDjRtTKpWxGv2+3h4sMlnbulefRFy3Klh1KBCKXuMQ" +
                    "wBeUGYzvfiER9UAAr5yQIB+rSaYvFjL09X9icwurhrpFJJbExqfde5A7JgkIW+SPGxfQKxAdP31Fs3NXqUQxFxKrnjWrBxII" +
                    "KGJF0Es9MSjAw95uki4z+ebtR9/+dLO6tnmYa5pa8QM0zH73oD3wAUaGSNR7/+3lh4+fUx8PJxJLvvjqvGo0KMOIJpHKeHxB" +
                    "9bPmvKLqzJwScb9ky4YlY5IAmqHBxrUh3/33pnpgUVnt3o9Pr1kW6OHuaMKgD4wGzSirxM8mWTR/ppYj4dQ0Q4hcs/DIiYs6" +
                    "y+Q+cX9y2pPktCfmZgwfDzbLyW4K08rclEEzNJBIpF09vdm5ZdcTHuLu0pkn9kYLAMvoHVErvzkTh2sWf/y07PHTstHFOXxf" +
                    "KUJo9bJ5G1YvGCjEPnWNbbgR0W3t3adjbgz/FHeOw9bI0FEkz8eT7caeUlJRp4O3U4fbxb+TmqONl2VrbTHuPGA0fifEBAZ4" +
                    "fPTBJmMG7XUl4J2NYevCgwkvMwY4KMDzH3siiaM1lDdGhPxh/w4qlbJzWzgRfABd4jXN6dhn25ctmU2ljjzQ38nBhu00lkOG" +
                    "CAS0dnngZ/ujtPnsOTCt9m6P2LltlfbzITVhO9n5zeDoJm85zvYuzkwt5c12sju0P8p5qs14LEXje1K8MYP29rrFf1oRlFtY" +
                    "VVpRV1nT1MMXCIViiVRqQCGbGNNtrS1cnJneHqyhljz4vQWFxfy/v23+bVWI1k6BUNTX10+h6BvRDS3NjTks++nujsOvCqE9" +
                    "kWsW5uSV44YDvgo83B093B17BaKyyvrKmsaGpvb2zp7uHkG/RCKVyg0o+oZUA+vJ5o5TrP1mcNhOzPE5GQYhhAgV96PVj1V7" +
                    "icEmecBEYqiCDQtjAW80IAAABAAAIAAAAAEAAAgAAEAAAAACAAAQAACAAABgQkKaGK9xPjZZfXg6gUAwolPZTszwpXPZTna4" +
                    "a9aHB69ZHjhUVAc+P6Nahu3rz/+ivkqhKgZ9EvHUkd3mZs/nf2zd/c9egciITo05+Xfs91CRM20tjx/aMeipppbO2Pi0ypom" +
                    "bjefQtE3NqLZ204KDPD09XZRPUKbaKueNSckZZZW1vfwBPokorWVua+3S2iIv+binu2dPQlJWfnF1Z1cnhIpzUwZHGfmgnkz" +
                    "OCzmoNmlUChPfn/tYVYRQojDYh7Yu0mbJWFAAK8BpVLJ7+3LySt/WlCx/6+R3tOdtbyxrb1bfRHCtIz8yDULNS+TyuSxv6S9" +
                    "9/bysUpwTW3LwS9jpNLnc8pkMrlQKG5u5RoZGWIC0JL4xIxzV5NVywfJZPKaupaaupa7D3IP7t2kLuaMx8X/jrmuPoutpZXb" +
                    "0srt7uk9sHeTZswKhfLUQOlnO9l99MFEKP0TUADY50rYJ/7p0u2U9DyFQnnuSpL2AsCtvJueWbhh9cJBhzqmPMhbFTbXytIU" +
                    "Fx5z8u/YD3G/ZPP2L7Hq4sJ3B4d/7vVb6VKpjEBAe95b6+PFlkhkTS2dj7KLKQMramkT7dOCyv9dSUIIGVDI26NWzPRyEQhF" +
                    "l+LupaTndXJ5R09ePH5oB4lERAhVVDee/O6aXKEgENCqsLlLgn2N6IZtHd2llXUNje2DflC++SEuPasIIcRytD24d5OO1yMD" +
                    "H+DloBkarA9/Pr+pvql9+KWYNQVAIhGx724Hl1daOcgkLD09glyhuHIjdawS3NzKRQgZGhrM9nWjkPWN6FQOixkV+dag9c9Q" +
                    "/PzLfexHxMqgAD93MplkZmq0PWqF9WRzhFBLW9fDx0XYBRev3cOW91k0f2bkmoXmZgwymcS0tVw8f+Y7m8IGKf1nrj/ILEQI" +
                    "OU+1ObhvszYTMEAAr9sKQi89aL6iurG1vQshNN116vw5Xs8lkVGgeSW2MsWDR4VjtXK/qTEdISQUig8fO5eSntfU0vmyiyD2" +
                    "ifora56vmx0U4KnuDs0fOMwvqkYIicSSkvJaLGTFWwEjxnwzKRPLBEcH64P7NhtOoNI/YQUg7BNfjkvBftvbTtJyDZIHA/aP" +
                    "v4+r1zRnbPZWZk6JVCbHXenjyWY72SmVysvXU8YkwcEDm3cUlNScjrmx58C/3/nrP8+cS+D39mkZA7eLj5n+FLI+bqboJAsT" +
                    "7Ae27QW3i4dNqdHXJ02yMB05M4ViTKIf7/vzMNtkgAD+EFyKS4nY+umWnUdT0vOw75+WU2nlcsXDx8WYeePrzSGTSZ7TnDAt" +
                    "Pcmv0Lx+XXgwJo9ajVXfRkGAr/uubeHqHkWvQJR4L/vQsf8NtRThy1SGA7UBGv3ErW6eIPHeYzThmJg1AIFAMKIb+niyP9u/" +
                    "RcvNY3ILq7Clzzkse2zPZ/8ZrgNW0CBL0nq4Obq5TFEq0eW4e2OS5sAAj2+O7v7Xp+9HRb6l8tpr61srq5u0ud3cjIFN4e2X" +
                    "SHErZ3V09qiuQQiZmxnr6REQQlKprL2ze8SYOSx7op4e9nHRXAoFWoH+iK1Ao7hRZf+UlNfhVhDJLawSCEWaO0GsD1/w8ZGz" +
                    "OXkVWHkaExyYVg5Mq7AQ/xPfXUvPLEQI9fB6tbnRkEphOdpibbhpGfkq416pVKYOCBir06gGZDcXh6LSZwih+MSMEbes9Jrm" +
                    "FBbi//W3sdhmTUSi3vIls6EGmFCIRP3ZeeVDnZXJ5BnZxZrhrmx7T3cnpLGj1ig48e3PZy8mFpXVcrv4Mpm8oanjWV0Ldsra" +
                    "ylzLSFTKj41Py8gulkpl3T290WfjW1q5CKHJk8zm+E1TfSawj3pSas6Fn+9yu/lSqay5lZuc9uTM+VuaMc/2ddv17mpM5/+9" +
                    "fCchKQtqgPHtJ1yKS1Gr4pnBc72xTqgAP/cP3l+rOpVfXH342DmsLWjQncjWrw7OL67+/Unq6ulNzyrSLFg+nuwpI+1SoWKG" +
                    "B2tTRMj52GSRWPJVdCzOQPpw93qsEwAh5OLM/Mu2VdFn46VSWdyv6XG/pqt/7weNfK7/NIVC8c0P15VK5dmLiUSi3pIFviCA" +
                    "CYLK/lFvQMQMfTNTo67u3vKqhraOQcxl56m2M73YOXkVvzMB61YFP35aVlZZ39XN5wv6SESitZXZbF/3lzU2VobOcec4/JqU" +
                    "VVpRx+MLsXh8vTlhIf402gsNOPNmTWc72iUkPx8KgRAyM2O4ODEXBg65mWzgbA+FQoFtUX7mfIKent6i+T7j3l2EZVGANwFY" +
                    "FgUAwAkGABAAAIAAAAAEAIAAIAsAEAAAgAAAAAQAACAAAAABAAAIAABAAAAAAgAAEAAAgAAAAAQAACAAAAABAAAIAABAAAAA" +
                    "AgAAEAAAgAAAAAQAACAAAAABAAAIAABAAAAAAgAAEAAAgAAAAAQAACAAAAABAAAIAABAAAAAAgAAEAAAgAAAAAQAACAAAAAB" +
                    "AAAIAABAAAAAAgAAEAAAgAAAAAQAACAAAAABAMAwkIY6sWvfEcgdAGoAAAABAMAEhVBxPxpyAYAaAABAAAAAAgAAEAAAvBH8" +
                    "P1go5y2mJTtJAAAAAElFTkSuQmCC",
                ["farm-compost"] =
                    "iVBORw0KGgoAAAANSUhEUgAAAQAAAACACAIAAABr1yBdAAAPTElEQVR42u2deVRUV57H76M2ijWAskTABVkUZZFN2QSBKGrc" +
                    "beOSTExnkmMSk+lkptvpzrSnO+mOyUmcjkmOJ5loOhN3iTKmRSLIJqLsi8i+ibKoFAUFxVJV1Js/npSPV1BUlTl2gd/PX+/e" +
                    "t9xbt9733t/vvvt+j6rLOUwAeFoxQxMACAAACAAACACApwo+J733vQNoFDCN+eKzfRgBAIAAANA2gSYaKQCY0kxk22MEADCB" +
                    "AIAAAIAAAIAAAIAAAIAAAIAAAIAAAIAAAIAAAIAAAIAAAJjy8E22ZvKBoZff+lg7f95sl4/3vzbRWQqF6vX3DvbLBzn59nbW" +
                    "X3/2ru4S07NLvv7+J+38v/3lzVkuMwytqoOdzRcH9goE47TwW/sO3bsv1SSfdXb4/K9v6b6gSCQ4dvj345a+47UPlaoRds4P" +
                    "h//TXCSctCUZeGZm5uZCaysL12dnes93Cw/1dZzxjKF/ig4++2CP+yxHjAC/GE23O+oa7060Nzf/pvbdrydZ18oMyteNRCpL" +
                    "Sc83/fYcUavlA0Od97uLymqPJ6Xv3Xfof364qFSqYAKZLpeuFBixSzcd97prG+6Muys7r0Ktpo245vmUXLl8aGq1rVpNX84s" +
                    "+uTL0zQNAZgqN4qqemVy7fya+taW1s5ftvsnhEh7+ipuNRpjxcmHzqfkTsUWLrvZkJt/Ez6AiaJSjaRlF295Pprb/acb2f3T" +
                    "NJ2dV67jgMxrZQGL5xs1WOUnxoc62NmYVANqPAqapmV9A8Xldd+f+nlgcJh9TE5eedTSxfpfCiPAEyUtq2hEreb00/nF1cZd" +
                    "7WZVs6Rbxs5Z4OXOThaW1soHjDFmFArVmeQsk21GiqJsbSxXRAW+sjORs6uhuR0mkGlhZ2ul2e6W9hUU17D3Xs58JAnmf9X/" +
                    "ypm5pZyC9ry8jp2jVKqu5VcabVy1dXSZeNsG+Xtx7beBQfop8AOmkgB8F8x1cXbQJFNZ/i5jFLH+Tk8He32tjsHB4YLSMVqK" +
                    "XLrYxdnBy8N1rEjK9K8qe9pUraaPJ6WbeNtaWoi5dwZlRlEUBGBK4zUhq2JDNMmqututd+8z23mFt9hu8aq4UP0ve62gUqEY" +
                    "M+sXHe5PCFke7j/WJGjTvyOfN9tladACtgWlY+rWFOiXD3BynJ3s4QSbHLGRASfPZQwNK0ZdzILX/2UtZzRwcXbwW+hx4scr" +
                    "ets/Y7p2d1fHOW5OhJDwUN+jJy6NjKjZR+7aGq/nZXdsjissrdVYZcfOpv15326TbdiisrpJjaJxGR5Wbn3lTxPtXZ0Qtnv7" +
                    "KowAvxhisSg63E+TvHqjQj4w1NjSXt/UpslMXBGq/9Dd0Snh9M2ajt/KUrzEz3PMxMh1Ax4IuDg7xEYFapLVda3F5XWm1p40" +
                    "TXpl8oyrpX8/mTrGIrI0X7cqHCOAKZIYF3o5s0jT/WTmlrHn/s1FwpgIfwPc37HT/xRFRYY9mvuLXuZXWFqrSUp7+spvNQbq" +
                    "PR/6qw0xV69XDCuUTPJE0pUlfp6mYFjr7rYtLc1/t3e7QbMI8AGeHK7Pzly0YK4meTHtRl7Bo/mZmAh/sVikd/9H5+RVsHMW" +
                    "L5hrb2f9yAwI8La0MNcxX6QbO1urNc8t1SRb2+7rftrwT0ckFMRFLzn45zc4s8AYAUxuEKisbma2uyS97F0Gub/lt5okUtlY" +
                    "99ePnRTwectCFqZnlzyylUtr5fIhS0tzPYtYnxiRllXU1/9wedLp5KyIsMmfLj3mKEERI0+nCa1UqXg8A7rFqf4gbEoKIDjA" +
                    "e4aDLefWJ4T4LZyne9kmB+3lD19+m/zlt8k6TlGqRnLzb65cEaJnERZi0aY1Ud+fvqyRa6oeq5XMRUKKIuxZ+IlWp9E0zVkK" +
                    "amZGCYUC4xpWoVDl5FVUVjf/9f1XTe3pNUygMf/xythg7XyDuv+BweGCkhojSjd0cejKuNAZDraa5Ll/XB0aUkz6A8XmYww5" +
                    "tZqW9Q1oH9nT28+14C3Ek44fIpHg7NH9Z47s//Zv//7Wqxs493q3tO+TL04Zt/4PAnhCxEUHcZbaz5zxTHCAl/5XuJZfadyi" +
                    "34bm9rvtD/Q/XsDnbdsQq0n2ywfHXcnHwd2Vu4a+vmmcJwnajxdmu+q7+J6iiK2N5fJw/z/te5nj6jS1dKRmFEAApou1lTgi" +
                    "dNGYjjY22CDT2bhV/qOusGHnLg/3c3c17KUQX5+5nJyUtHzO0gSapi+m3dA6cY6hP8dppp32840zyVlGv1kBH+CJuMLxoZqb" +
                    "WCDgr4haov+57VrT/+sTI3Q85Pqvj76rqW/VJHOuV+zcEm9mpq/eKIrasTnuwOcnDRjiogKTL+ayF/xVVDV9+tXpjWuiZrs6" +
                    "0jS5ffde0oXs6rrWMX8nnxcbGWhEY66IWnIxLZ89sskHhs794+pL256DAEyUebNdzh7db2wXzp3N1P3gM8jfiy2Ant7+ssoG" +
                    "zmMy3QT5ey30ml1Vd1vP42fOeGZVXCingy8oqdHtt6yOD9N/ERTH69i5Je7jQ6fYmalXChLjw2ayHBhtdD9SIIRsWhu1fdMK" +
                    "mEAmBE3TOdfHTP9bWYq957vpOGWJv+djWkGEkJ16L6NgeHFbgkEaCw7w3rkl3uhmCQ7w5kz/K1Ujp85lwAeYbpRXNnZL+9g5" +
                    "AYvn67Zn3Gc5cjrC4rJaQ01kLw/X0CU++h/PMzPb9872HZvjJn20ZyEW7doa/9u92/S3ysZl19YETs7VGxVGv2QHAZgomVru" +
                    "rz4LvwLHdsZK1Uiu4W8I7NgcZ9A9SlHUxjWR3xx8d8/udcvD/d1nOdpYW/D5PAGfZ2tj6e7qGBMR8Oav139z8L31iRGPv8jC" +
                    "y8M1jLWIlRBC0+SHs2nT+Gag6nIOs9Oab4nhI3lgOjHRjY3AWOCpBgIAEAAAEAAAEAAAEAAAEAAAEAAAEAAAEAAA05IpvBz6" +
                    "eFJ6cso17fyEmKDXXlqrSTY0t6ek3aiub+3p7RfweS5ODiGB3onxYex3oDiX4vHMbKwtvT1cNz0fPdfdWXc17nf1pKTll99q" +
                    "7JL00oS2t7Pxme+2ImqJj6eb0XXYtiFmy7rlml1HjqWkZhQy2y/+KoGJ2MOpM0VR1lZiLw+3jWsiOUEd9SmdENLW0ZV0Ibu+" +
                    "qU0ilYlEAltrS/dZjtHh/iGB3oSQV97+RPNqvzZus2Ye/OANCMDkuJCad+xsuibIq0o10nS7o+l2x5Wrpe+/u2uiN+hHRtTS" +
                    "nr4bxdWlNxsOfviGjk8G5RXc+upoMjuyYkenpKNTIu3p+8O7u4yuQ3pOyaa10cyyuWGFkrN4e1yYQOdFZbUlFXX73tmhCV6k" +
                    "Z+lNLR3vf3RU846oSjUilw+1d0qsrS0YAWAEMF1e2Bi7WetbAYSQkor6H86kEULMRcI9u9cFB3j3ywdPnc/IzC3rkvR+fOjk" +
                    "wQ/e4PN52peSdMs+/ep0Q3P7sEJZUFKzlhXbh01d491D35wbUaspimxYHbkyNsTayuLeA2l1/e07o0FLjagDIUTSLSu9Wc+s" +
                    "Ub2WX8mJ3D/uz5cPDH1/6ufM3DK1mj52Jo0RgP6lJ1/KVSpVFEX+7fUtQQFeCoWqraPreuEtkehhgImjh37LbAwNK17c8xEh" +
                    "RMDnnfjmffgApsuPP+UwG1vXLw8P9RUK+fZ21nt2r2NCTHfc675WMP56Zgd7Gz9fD2ZbPfYrBGxOnstgXllMiAnesTnOwd5G" +
                    "KOS7zZr5XEzwr3etNroOTEi29NFg12lZxZpMHVhamL+w8eGLV61t95lBSf/S2zslhBALC/NlIQtFQoG1ldjH0233jlU7NsfB" +
                    "CZ6SDAwOawKGsuM8UxQVM5osrxz/w0cSqYzZRVHEf1QJHAaHFFW1Lcz2RGE0jasD81JvSUW9RCprae1saG6jKBIbGTC5FURo" +
                    "o0tnvr0glw99+NmxzNyyto6up+QbYdPBBDp1PvPU+UxN8g+/2RmweL6kW8YYviKhgNN9amz6ru5eHZdysLd5adtzs92cxhdJ" +
                    "dy8TOUcg4DvOsJvgGGPq4DHHZd4cl6aWjis5JUzYn8UL5jk7ThKsXD4wdHq05u6zHIVC/r02qf6lx0YFllU2EkIqqpoqqprI" +
                    "aNyNretjbKwtIIBphaZr0x1CkKZp+2ds/il1SFge/HXLT1dySgYHh5l5LR0faOLon6KoSV8+1i49PMRXpRw5839Z9x48/IBx" +
                    "X/9gakZhTcOdA3/8V56ZGQQwxZxgB3sbiqJomh5WKHtlcnYX+KCrR3OM9qXWJUZcziz8+8mfu6V9n351+osDb4vNheP5CbZm" +
                    "ZpRaTSuVqvtdUqeZduP6EkbUgRASuXTR/57+mXlx2dbGMiTQZ9IoRhRFWVmKvTxcN6yO8PF0N6L06HC/6HC/ljv3qmpbym42" +
                    "lN5sIIS0tHbWN7axp3ThA0wNLMQiz3mzmG12TGaaprNGk/6LxrHvBXzemoSlTADqXpn80gQfuxabCxd6z9FMNf6ydTAXCaOW" +
                    "PgzTuyIqUHe02hc2xp49uv/MkT8ePfQf+97Zztz9Rpc+x81pdXzY73+zM3L0E5E9vX1wgqckmmEh6UJ2XuEtpVIl7ek7/N2F" +
                    "jk4JIcTZ0Z4TW46Nxq9NzShkfySGc+cxtkFaVtGJH69IpDKlUtXeKUnPLj5y/NJj1iEhJojxwuOXBz2BFvj86x+/O5laWdMi" +
                    "6ZapVCN32h403+5gdrk4OcAHmJIs8fPctTX+eFL64JDivw8ncYyT3739gvYEvIaARfNdnB2YR1rXi6oiw8a5Tb3nu7356obD" +
                    "311QKlXnL+aev5jLOt3jMeswx93Z6LBfRrRAd09fbn5lShp3uAvy95poGgACmAKsT4zw9ZlzMS2/uu52r0zO5/FcnOxDAn1W" +
                    "x4fpjvFPUWR1fNiRYymEkJS0G+MKgBAStXSx1zzXlPSHSyEIIfb2Nt4ebnHRgY9fhyfZAts2xBaU1NTUt3ZLZbL+AeawZSG+" +
                    "z69cNr3vEIRFAU8FCIsCwFPmBAMAAQAAAQAAAQAAAQAAAQAAAQAAAQAIAAAIAAAIAAAIAAAIAAAIAAAIAAAIAAAIAAAIAAAI" +
                    "AAAIAAAIAAAIAAAIAAAIAAAIAAAIAAAIAAAIAAAIAAAIAAAIAAAIAAAIAAAIAAAIAAAIAAAIAAAIAAAIAAAIAAAIAAAIAAAI" +
                    "AAAIAAAIAAAIAAAIAAAIAAAIAAAIAADD4E+0Y+97B9A6ACMAABAAANMUqi7nMFoBYAQAAAIAAAIAAAIA4Kng/wE2Y+44Qpqg" +
                    "WgAAAABJRU5ErkJggg==",
                ["farm-wool"] =
                    "iVBORw0KGgoAAAANSUhEUgAAAQAAAACACAIAAABr1yBdAAAOnklEQVR42u2de1QU1x3H7755LQiIiAjyWlheAlWCoqIYTSNR" +
                    "E204SWOStqRtatLYvE7bpKk9qcmJbaNNmyae9lTT02hilBwNOUUTH4hRVCAKCrjAIu+Xsiy7y+6y7Kt/jI6zM/siWXIS9/v5" +
                    "a+bO3d/M/s793vu7d+7cy2s7vYsA4K/w4QIAAQAAAQAAAQDgVwhZ58+8sB1OAXcwb+/4LVoAACAAALghkKuWAoDvNK5ie7QA" +
                    "ACEQABAAABAAABAAABAAABAAABAAABAAABAAABAAABAAABAAAN95hHfwf7PbyRO/+rNu3EinxMbMfOv1p7k5T51tfGf3YWbK" +
                    "4vyM5zeXcnO+f+BYxdEaZsqObZvjY2c5fYCevusNTcqW1u6BYdX4uNFgmAgIEIeEBEZFzpDL4rMzEjNS503pH31Ng3rDxI9/" +
                    "+SdmikQi2rvrZQjgzoTHI3JZfN2lVjqlf3BEqzOESoNYOa+2dXNSepzabGl1yBkcHBA3x0npV7T3Hqw4dbn5GrcI6g0Tw9fV" +
                    "TVc7yyuqE+KiN64tWpyf4fG/+Nwg8IsQiFsjcss6IeRqO7u4j2nGh66PshJNJnNn9yAzJV0Wz+Oxm52PPz29dft73MLKpat3" +
                    "eOeug//492HTpNlNO+Zbg8CPBJCeNs9j1a7R6geHVE5UwcnZ2tFrtdkc7HMEtntf5f5DVXa73fuHrK5pfOOtD6xWm9OrPjcI" +
                    "/CUEIoQkxscEBoiNE5Nuo51uV9FO8dJch5yt7JxyWTzz9NipLz87WcfKEx0VvnHtspzM5LDQYL1hQtHe8+ln51qVvcw8zYqu" +
                    "PR8c+dlj97F+63ODwL9aAD6fl5oc5xgkDBmNJlc1fWCghBEXsYt7i6NUJGJRcsIcZji+r/w46yeZ8oQd2zavXJYXGREqFArC" +
                    "QoMLFqRve6ns3pX5nLJe39UzxIrvfWsQ+J0ACCEZjlGQzWZXOFaWzBaAWYyGr6vVmnH61GKxKjv7mT+UJc8VCG47sPLYBb1h" +
                    "gplBGhL4wlOlErGI2zsv27SGKR4q1j9YUc1M8blB4I8CSHfbDzYaTd19w/Rp8dK8iHCp05zKzoHJSYujZYf453x9C+tGa+4u" +
                    "kIYEuRih4pXev5yVeOlyO/MWPjcI/FEAKUmxIqHAlQAUyl6b7Wb/Miw0OCY6gikYZtDP7Sqky+Yxe9I9/ddZGRYtdDccmZud" +
                    "EiARM1PMFqvi1niUzw0CPxWASChISYplpig7B8xmC7dYU0WfOXLKHB5ldQAEfH5q8lz6tG/gBuu+ErFo7pyZbh5MwOfPi4tm" +
                    "JdJ2fG4Q+KkAuFGQxWJtv9bP7QFTIQ0zc0/fdSoKt9vtrGGWpIQYieR2LK4bN7BuGhYazGO9I+AQPkPKStHesuNzg8B/BZDB" +
                    "eRtAVedmi7WD0a+l6v65c6LoOJsu95097LEjlqgMjlcJIaxoxCkBEnZ31mCcmCaDwH8FkJYSx+c71J1UcN9+rc9ssVIpQYES" +
                    "Kn7g8Rx6t9TcBycdAMcecBBj/JRiwjTp8cEmTOz3tUGBAdNkEPivAAIk4sT4GGYK9U5XwYh/0lLi6ADDoR/c1k04U4CoWUbM" +
                    "FO7gjEar9/j6Vj2mY6WE3rLjc4PAfwVAOHMiqFk9zHqdGSYx+8EdXQOmSTNrLCVuzqyQ4EBmytw5Uaw7mibNfQMjbh7JarN1" +
                    "9w6zEmk7PjcI/FoA3FlxTVe7mP1aOWNMMyF+Nv1K2Gq1VZ9t1OocupJyx/iH6qFyJ0VzB/KZNFxRsqIakVBANyw+Nwj8WgBy" +
                    "zrTNz6vq6DlCIpEwJfH2a1Q+n5eWcnsCxeHKM+6HlSi4g/RHTlxgfo3AxG63l3Ne0+bNl4nFwukzCPxXANKQQFYwcEOloY9l" +
                    "SbFCx5dlzBaDmdNpD5iiZHVBcJBDj1M3btz57gHuu1i7nez54Kiyc4DVryhdv3xaDQL/FYCratvVJTeZo2bOiAwP5aYHBwVs" +
                    "enAVO9BSdD2/9d2qMw0qtdZqtWl1htqLiq3b9xw9UcvKuXrFwoT42dNqEHDxo/YxI3Xe51X1LgTArtFTEueIxUKnE2ncfHa4" +
                    "esWC7r5h1gTm4evqd/d84v7ZMuUJZY+s+QYMsjvWJnNp2atuMmxcu+yHG1eiBbgjBJDmvOCyIv6bFYNQIEua66VamDyxqeSh" +
                    "DcUeX9kyWV6Y89KzjzAnlk6rQeCnLUD4DGl0VPjwDTUrPTE+xulL1vTUec2KrimFUlTk/eC6oix54sFPTl1u8fARozef8Prc" +
                    "IPBTAVBllysAVzW601AnLDR4zuxIjzeSy+J+/+JjtxdxGBoZ1xsNBpNEIpKGBEVFhsll8dmZSd6vCuFzg+Bm/dJ2ehfznN5L" +
                    "DJvkgTsJVwUbYSLwayAAAAEAAAEAAAEAAAEAAAEAAAEAAAEAAAEAcEfy7Z0L1D84Ul5R3X6tX6XWSiSiMGlwfOysosKc/Lw0" +
                    "KsO+8uOHK88SQh7eUPyDdUVUom7cULblL4QQaUjgnr//mpmNxeoVC37++Fpmyu9e393W0Ucdv/X607Ext1ehYhkRCPih0uC0" +
                    "5Lkb1xUl3ppzz30eOkUkFLy9fUtkxM2vCMq2/Fk3bqSfkEI1qj1yoraxSTk8MmY2W8KkwTMjw7IzkooWZce4nn3k3kvUjVz9" +
                    "Ni42aue2p6hjZedA5bHzV9t7xjTjIqEgJjoyPy9tzSqHL3Km5ASP3oYA3HGta/CVN/bQ67dZLFa9fmJgSCWVBtEC8C3D19V0" +
                    "6SeEVNc0PvKDu11ltlpt6jHd+S+vXrqi3PnaU7NmznBv3Gyxln9a/eSP1rnKcL6+5R+7D5sYi5qo1FqVWtuq7LVYLNzPYnzr" +
                    "pYqjNXsPHqeXnLBYrNe6B691D5744tIrzz/KrAi+jhPQAkyBw0fOmM0WHo88++SDC3JTJyct/YMj5+qaJZyFn7yH2VBwOX3u" +
                    "MvP0zPkrP9x4N3cSPmVENap9852PlJ0Dpklz7UXF2nsWebx71RcND5QsjY4K515q6+h7658fU/tZ3Lsyv2RVQdTMGRqdfmBQ" +
                    "dba2KTBA8pW9RLcwE6bJxza/QbVFH/zrFaaRi5fb3z9wjBASIBFv/sn6hblp43rj/kMnq840jKg0f/r7hzu3PcX6XtQbJ7j3" +
                    "NgTggYEhFSEkKChgcX4Gj8eTiEVyWZxcFjd9d6QEIBQK8rJT6i613lBprrZ3u5pdHBkROj8zmfoG12bzvBELn8+z2mwHPjn1" +
                    "zE83cK/uP3SSKv0rl+U98WjJzVuEh0aGh2ZnJE63lz7+9DR1UHr/8sK7MgkhEWLp5p+sVyh7B4dUg8OjZ2ublhfmfH0noBM8" +
                    "BcLDQgghev3Eazv2Vp1p6B8cmcoWQVOmraOP2hEsOz1xxZLcm5Kouewqv0qtbWzqIITweCQnM9mj/aLF8wkhX5y70j/IXtjH" +
                    "ODFJf3az8b5l37CXDEYTvUwqs5TzeLwVt06pf/r1nYAWYAoUL8traOoghFxuuUZ9BiUNCVxyV1bp/Su4ezzuP1S1/1CVR5us" +
                    "bL97blNudgp1/MWt+KdgQXpuVopELDJNms/XtzzxaAlraXWmkciI0Mcfuoe7IDOXBTmpA0Oqto6+jw5XsXZfVY1qqPXZRULB" +
                    "rKgZ0+cl54V4VEuF/hKxKCw0mHmJjulHRjVuPOnKCW68DQF4pjA/02K2HvjkFP0Bl27cePRknULZu33rzwR8XzZcVqvtbG0z" +
                    "Fajk58nFYmFOVnLtRYXeMPFlY9uiBemufmi32yNmhHp5l4c2FG978/3z9S1dnMXb6FqXkJt9jj+++d8rLZ3UsZutfKfVS3Rb" +
                    "wiPuPkeekhMggClQVDi/qHB+V+9wS2tXwxXlpStKQkhXz1B7Rz8rzHU6DOp9J/jSFSW1FrlcFk9VnAXfS6+9qCCEnK5pZAng" +
                    "4Q3F69cs+byq7j8ffjaq1r35zkdvb98SGOB53eb5GUkZafNaWrs/OnTSMZIO4/N5NpvdbLbcUI1NdSzFey+56szweDy73W6a" +
                    "NGu0emYjcGNkjM7D9aRHJ3xXOsHf9hdhCXHRJasKXn5u09JF2VTKmEbn21vQ8U9La3dp2aulZa++/e9DtDbG9exxdJFQcN/q" +
                    "RVnpiYQQjVZ/5PgFr4ehVhJC6hvamDt/BQaIM9IS6OFI6mDri48f3PMH5mZN0+SloECJ7NbuIdU1jcx6/dSt05wsJ/H9V3YC" +
                    "BOAVf/vnx+99eLRJ0aUa1Vos1t7+G/QO1THRkT68kdFoqmtodXXVYrHW1DU7vbT+3kLq4OjJOi935E1Pjac6i/SmTHRlSYUr" +
                    "n1fV7Ss/cUOlsdpsunGDR7M+8RJdT5dXVNfUNZvNFvWYbtd7FdTeybNnRSy5K8vVb7+CExACecXomO7MhabKYxe4vUlvOp3e" +
                    "dILlsrhtL5Wdq2+hXiQV3pX53C8epK82Nne8tmMvNRZ0z4qFXGu5WSkxsyMHh1TqMd25+palBVleNQIbixub2YMqaSlxT//0" +
                    "gV3vVZjNlsOVZ7hLkU6rl743X/Zo6ap95ceNE5N/3VXOCpB+s+Vh1ksAL53g1NsQgLc89EBx7UWFor1nVK3VjhuEAkFMdMTi" +
                    "/Mx13188TfEPa6h7fkZSRLh0VK1rVfZyV1Khuqwlqwp2760khFQeO++lAFISYxfmptY3tLHSly3KTk2aW3n8QmNzx4hKYyf2" +
                    "8DBpVGRYTlZywYKM6fbS/WuWZMoT/nfswtW2bo1WT9nJz5OXrCoIDna3v8ZXc8K3CiyLAvwCLIsCwHdwFAgACAAACAAACAAA" +
                    "CAAACAAACAAACAAACAAACAAACAAACAAACAAACAAACAAACAAACAAACAAACAAACABAAABAAABAAABAAABAAABAAABAAABAAABA" +
                    "AABAAABAAABAAABAAABAAABAAABAAABAAABAAABAAABAAABAAABAAABAAABAAABAAABAAABAAABAAABAAABAAAB8cwhdXXjm" +
                    "he3wDkALAAAEAMAdCq/t9C54AaAFAAACAAACAAACAMAv+D+c0MHlDgzikgAAAABJRU5ErkJggg==",
                ["farm-cold"] =
                    "iVBORw0KGgoAAAANSUhEUgAAAQAAAACACAIAAABr1yBdAAAUcUlEQVR42u2dd0CT1/rH3zcJGWQBAREhiBAgygYViwoO1Iri" +
                    "5jqr1tthvd3tr/t2719rh731dmj1Xq2LVkstojjAjaAiM5Cwwh4BErLIIPePF2J4A4EwEtHn8xc5JCdPzjnfc85z3nOeg5Ze" +
                    "3I0AwIMKAYoAAAEAAAgAAEAAAPBAQcK9fualT6FQgPuYXV++BiMAAIAAAMB8CtTfSAEAY5r+5vYwAgAwBQIAEAAAgAAAAAQA" +
                    "ACAAAAABAAAIAABAAAAAAgAAEAAAgAAAAAQAAGMe0vCzUKk1t/OExUKxqLxWKpPLFSqNRkelkZ1YDO4Et0AeNzyE5zXBzXIm" +
                    "4pqm3AJRUUlVXaNELlcplWoqlcxg0Nw4Tnx/75Apk6YETLTwcYVSvfXpz0xTKBSHA7vfGIz95p9FEARFESKRSCQSaFQK3ZHq" +
                    "4sx0d3Px8/EImezrPs55BCuguFR8NbuwvLKusblNqers0neRySQ6nebixBzn5uzl4erjPd7f14vFdDR+5Lk3vqtrkAz5G2Om" +
                    "B72wfY1tasEUIoFApZKZDEevCW6BPG7M9KBxrk5Dy6pPvvzgKW/PcTYVgFSm+OPUlbMXb6lUnfgfoFArFOra+pbrN4v3HznD" +
                    "mzRh28YEf19P80wEwupjKRl5heXmRaBQqhub2gqKK5JTMn247quWxj40bYptOgaDAdHp9DqdvrNT2y6V19a35CMVZzMRBEH4" +
                    "/tzVS2PDQ3jD/Ira+pbdv6SUiKrNOxSVWtMikZaW1RgTv/pwx4CdyHCwQS3ou7qw3BqaWnNySw79fi4+LmrrukUODiTEfgz9" +
                    "u+8Ulu366bhUphjMm0UVdaVl1TgBGAzI7ycvHjmRYTAYBsyhsrpx5+5jcXfCHt+8hEJ2sGORCYTVH311cFZ08PatyyiUIVoi" +
                    "rm1697N9HXLVIN/fNYgiGrLU7VILXV2GMxdymlra33h+I4qONR/g4rW8j3YeGGTr7489B1MPH79gsKZqM6/e+eTrX/X6LrvP" +
                    "HS9nFbz7+f5OjXZIbc6w66fjg2/9o4p9ayE3X3Q5K3+MjQAFgsrv9/5hXmLTI/lxMWH+vp5MhqNWq2uXKcoq6+4UlF3LKezs" +
                    "xDeU9Iybp89n4xLd3ZxXLZ0dFuTHZtEVSrVAKP7z9DXcJKFQULn311OPP7Jk9ArF6D9odXq5QiWuacwtKDtnNtMTVdR+++Pv" +
                    "L//jb6iVPVheUXmluME0xYfrvjoxNpDHZbHoOq1e1qGoEDcUC8VZOUXNEinu4998/LR5nu1S+eMvfGmawmTQ9n77imVLRrsW" +
                    "jCVpMBhkHcqbd0r3Hz6t7F2MF6/emT0jZPCVYmcB6PVd/96XglM/jUp+fvuayFD/u/mSiDQaxcPdZVZ08LaNi0+evkajUkxn" +
                    "lgeTz+JyDuL7vP78BuPAymbRo6MmT4+cvPdgalrvSkrPyFkQF+XjPX60uwcHEtGZzXBmM8KC/FYvnb3rp+O38oSmb7hxS3Dx" +
                    "Wl5cTJh1Aug91XakUd59dSvdkdrtLFIIbhQnN1en6ZH8LWsXFpVW/Zl2lTAKswRb1gKKomwWfd7sCCKR8N3PJ3DTYzuOAFZP" +
                    "gc5m3mxsasMtmOBaPw4alZy0PG7e7AhjSmp6lkKpxnVXL+1IMp9WoiiybeNiP58JuGnrsZRMG5cUg0575Zl1kwO8celHTmRY" +
                    "Oxloae3VqU8Y72ps/eZMCZj46rPrR8MDtkstRIUFmOlQZRg1D2fkBXDxWh4uZcbUKRZaf59czynCpSyeH81kOPbXeSQtj8Ml" +
                    "3s4TajQ6GxcWkUh4cksiri9ubmkvKqmy1v/rvRzU3CaV277u7VILdEcavgmiBNR+XrB1AlAo1KKKWlziwjlTrcpEKlOIa5vM" +
                    "VWThI+EhPCqFbJqi1ekFQrHty8vTwzUo0AfvyRWIrMrElcM2falSa9788Oe/0q/XN7ba7IfYqxbkCiUuZby7y5hxgsW1Tbje" +
                    "i0QiBvK4VmVSU9eM92/IDl4TXC11vQTCRK47zg+rqWsODfK1fZFNDpxYIKjsvTjYYFUOEcG8k6ev9RpGJNJ9h07vO3SayaBN" +
                    "8vbw9fEI5HH5/t4MOm2UfoW9aiEnt3TASVGfdHZqk7a9199/ExZEP7r+4VEXgLQDv+7pxGZY+yCjQ47vA9gs+oCDoLMTE5ci" +
                    "M8vHNri6sPG/qMM6S0KDfCcHeBeXivsqHFVeUXleUTnW4IIm+yTERw+yidzLtWAwILIOBbYK1GtGRKcuezhmzIwAyt4+E+bg" +
                    "WvuVSrPHxriBtU+oZo+clCq1XYrMkUYxX06xNpMXn0r6aOeByupGSwtuXV15heV5heURIbznnlxtwVEeArapBcvdNp1OffWZ" +
                    "9WwWfcysAjma1YFKrRl+A1J3DpyJ2uxJgiONapciM286Q2iaTmzGR28+tm7l3P6czl6+Zr5o5+5joy1jW9YChewwPzZy5/s7" +
                    "zFfV7ukRgM3Ei7VdKtdqdVbNgsyrXCpTGAwGy+NvW3sHLoU1iKYzGjS1tOMtYQ6lDyOTSasTY5cvnplbUJZfVF5cWiWuadJ3" +
                    "9b2imldYnldUHjplxHwe+9aCATFodToi0Yr+9554EMb1dCMQUFM/WKfTl4iqgydPGnwm5kvanRptTV0L19PNwmSgymy2MKqb" +
                    "wyxQ2NsDRhBkOI/kSCTi1PCAqeEBCIJoNLryqjpsfyjuUTE2DoygAOxbCxqN7uLVvILiio/feozjzBozUyAGncabhN/ReSYj" +
                    "x7phhEU337ZqviZtSm6+CDdAO5CIfH87jJ5V1Y3mC39hQX4jkjmZTOL7e69cMuv/331yyYIZuP+2mO2JGA62qQUKxeHY3neO" +
                    "7nnn569ffvqxFbi23trW8fmuw7h1xXtaAAiCxD4Ual5quA0CA2K+3nzqXFZ/m8MMBkOy2RPHiFB/MtnW22h1Ov2P/zmJS3Rz" +
                    "dZoSOHHEv2vRXPzTFRJxhE8v2awWUBRhs+hxMWHvvbYV5y+VV9annb8xlgQQHxeFOxFiMCBf/zv5dn6/D4NUak1ySub5S7eN" +
                    "KQkLonEF0SFX7fz+qPljRYMB2ftrGm67CIoiScvibFxSHXLlJ9/8arpHH2PtijlEK5vmydPXfth/sqzS0h6Y2oYWXIrLSE8V" +
                    "bF8L7m7Om5LicYlHT2TIFXbbGGt1J0okErZvSfxw5wHTDTAqtebjrw4ad4OymHSNVieVycsq6nILyq7nFKk7NVvXLzJdNtm4" +
                    "Jh7XmxYIKl98+/vVS2NDg3ydWIyefYhXBUL8kZEFc6baYCcc1uXLFSpxTdPtfOG5S7fNz/1Mj+SbD4kDryOpO89m3jybeZPj" +
                    "wooKDfD385rIdec4s+iOVI1G29rekX1bcCL1Cu5TVjlag8EutTBvduRf6Vmmj+EUSvXvJy9tXrtwbAgAq4kdjy7/bs9x3Bam" +
                    "G7cEN24JBpnJgjlRVTWNuL24jU1t3+/9w/IHg/g+2zYsHjB/yyvQCIKsWjp7/ap5Q/ssBm+S57NPrBrOPhZJq+xMRs5gnChP" +
                    "D9cR9IBtVgt9TDkI6MY18z/79rBpYtq5G4vjo9047FGq0JGcAnV7AjGhb7ywaZiPMP6+MWHtyrlWNaC4mLDXn99AJNr/LP+s" +
                    "GSHvvrLFNmfTaDTK04+tHKVfbftamBoeiFv+1+r0h38/P2ZGAIzwYL8v33/qxKkr5/o6E4zDz2dCgB/X3DdakxgbzJ907I8M" +
                    "7OG/BWx8JtgCfH/vNctih7Pyw+d5B/K4pWU1g9kGHODn9cTmpRO57qP0c+xSC5uSFrz50R7TlEvX8xIXPWSbme3ICABbStuy" +
                    "duHflsXdzhcVl1YJy2vbZXKFQq3RaqkUshOb4enhGsjjRoT6Wziuz/fn/vPlR+7GI2hokStUSmUnheLAZDi6cdh8f++QIF/L" +
                    "8QhGo1kQCAQSiYhFheA4s9zHOftOnBAa5GshlsEgCQ3yDQ3y7ZCrBEKxsLymurapqaW9rV3eqdFotXoqxcGRRvUYz/Gd6DE9" +
                    "kh/gx7XBZmEb10KAn1d01OSsm8WmfvZ/j6X/86VHbCwAtPTibtPXxrvE4JI84H6iv4YNgbGABxoQAAACAAAQAACAAAAABAAA" +
                    "IAAAAAEAAAgAAEAAAHBfYs+7CQ4mnzXd9Y6iKJNBC/DjrlwyK8DPy/Sdooq61PTrxUJxu1TuQCJ6uHOmRQQujr97niM94ya2" +
                    "r33D6vkrl8zCEt/7fD8WwWr71sT5sZFY4ktv7xbXNBEI6L7vXqNRyTgbjCyYE/XE5qXmRhKJBBaTHujntSoxdlLPzi2rMiES" +
                    "CVQqmePE4vl6LpgTZX7E1EIRDdO8dSvnrk6MtdYeSavs1LkbdwpEjS3tWq2OzaS7ctghU3xjZ4R4jOcMxkgEQWrrW5JTMoXl" +
                    "tZI2GYXiwGbSvT3HxcaETYsIfHAFgAOLoJ2TW3Irr/S15zZE9FzBkpJ29cCxs8aNkzqdvryqvryq/tyl22+9uMnTwxVBkED/" +
                    "7q2mxgO7XV0GYU8UR4GwGhOAUtVZXduMIIi3l/sQIhohCKLXd7W1d1y/WXw7X7Tzwx1D2Bin13dh1+eIa5vOX7q9ImHmxjXx" +
                    "I1WGQzDPsj3Xc4q+23PCNLq9pE0maZOViKp1Ot0gLS+vrH/rk71arc5YgwqFuq5BwmQ6ggAQY8+kUKr3Hz594XJuV5fhwNF0" +
                    "TAC38oT/PZqOIAiVQn7q0WVTwwPlCtXh4+cvXM5tkUg/+/bQzg92kEhE7oRxdEeqQqkuKas2GBAURSrFDcZqKxF1q6JUVI0J" +
                    "iW8WztHYO1o2UtIq++JfR0QVdZ0a7Y1bgqULZ1ibyaqlse0y+Y2bxQeTz6rUmhOpV9gsBi6f/r59mOZZa09pWc3XP/yGHf17" +
                    "eN60hPhoN1cnaYeirl5y5UaBabx7y0aeOHVZq9WhKPL8k2uiwgM0Gl1tfcu17MIh369zf/oAdEfqupXdJ3rEtU3YydTf/ryI" +
                    "pSQtj4uZHkQmk1ycmU89ugwbfOsbW6/cKEAQBEURbNakUKhr65sRBBGIxAiChAf7EQhofWOrrEOJIIigJ66ltfFMjXBcWKE9" +
                    "JwG6uoZyRQqKIs5sxqJ5057ckoilJKdkml8gYjPzLNhz+Ph5rPXPmx3x900JHuM5JBKR48wKmTJp+9bEVUtnD9Iq7Eo/R0fq" +
                    "Q9OmUMgOTAaN7899dMPDG1bPBwH0ngUhvQ6IKFWdwvLuaYzpJRQois7peXmnoAz7wxifAzu9isVwDQ/heXu5G1+W9EyQAoca" +
                    "UkXSJsO+EUWHGw1lZnQwFiZaoVQXl1aNSAEOxzycPSq1xhgBadWS2cOxypnNwPqmD788cOFybm19i8FwrzS5e8gHUCjVR45f" +
                    "wP729hxHJpMaa9uwGQuF7IA7fmmc3RovmzB26gKhOD4uskRYjSBIIM+7obG1UtwgEIojw/wxr8DFmWl+/PTw8QuHe74dQZA3" +
                    "X9iIuwfS9A0cF9bmtQvNT2kNmAkOz/GuWLQf82hzo2HegJja48phYxF7HEjEcW6DcnX6M3Lu7IjcgjIEQYxxf5kM2szpwUnL" +
                    "55he//rgCgBXcCiKbkyKH2is6Hkz0n1ciufrSSQS9PougUjcIpFK2mQUssMk7/GNTd5p57NLRNUVVd1ewZDnP6b+uovTCAQp" +
                    "GaWbUYZsXt/2oCjSU8jvf/Gf/KIK7O/BxyqMmRak0+qP/pHR2Nx9t1CHXJV2Plsgqv707ceJBMKDLgBju2fQaQF+XisSZmLz" +
                    "GY4LC0VRg8HQqdFKZQrTQaC5p8vkuHTXNNbcRRV1jU1tWbeKjZLgB3gjCFJeWVdQ3F1zfJ730LzMZYtnnrmQve/Q6da2ji/+" +
                    "dWTXp8/ilpIGzKR3a7sb/GfA5ZoRMc8qezgubCwMplara5a0D2ZByYKRsTGhsTGhldWNRSWVufkiLIpUpbhBWFbL9+fasdXd" +
                    "Ez7AupVzj+195+iet/d++3+vPbfeOJt3pFGMVwtnXr1j2lFl9LwMC7470w3sadknz1w39vQcZ5Yrh63V6U9fyO5xAIZY4g4k" +
                    "4pIFM7D4PFKZ4tTZrOH86stZ+ZJWGeb9Tx6J2HLDNA9nD41KntJzF05K2lXsj7df3nxs7zsuzsyhWejDdU+Ij37jhY2zeq6F" +
                    "bJd2gBNsCWOPkpySeTW7UKvVtbV37P4lpb5BgiDI+HEuM6cHG99s7EuwiaxxqoMpCkukkB2GGXrAeKFD2vnsIdyVazAg7VJ5" +
                    "2vnsH/d3R6RasyxuBMOrWGueBXvWrZyLzU/OXMg+mHyuWSLVd3V1yJXW/upvfvjtl0NpBYJKSatMp9NX1zZXVNVj//Jw54AP" +
                    "YInIUP9NSfHYEvVXu5NxS36vPruORCLeHQFM1nZQFAnsCcTC53EvX8+/6yr0NenE+SF8f+4Hr2/r06TwYJ7HeE59g6StveNa" +
                    "TtGs6ODBZ4J7A4IgKxJmDbhabxUWzBvwV+PsCeRx//HYit2/pGi1uhOpl0+kXrbKlzP+/Nb2jstZBanp+BEpKixg9MK93CcC" +
                    "QBBk+eKZQXyfv9KzikurpDIFiUj0cHeZFsFPiI+m06m45bZxrk7Yioqnh5vxv6YRjIfvAaMokhAfvedAKoIgqenXLbewPsZc" +
                    "AkqjUXq2HkzlTZow0q6UdeZZtmf2jJAAX6/Us1l3CstaJFIDYnBmM9047LBgv+iowUYHWrti7o1bAoFQ3Nomk8mVWA0+NC0o" +
                    "cdFD9vc8ISwK8CAAYVEAYAw6wQAAAgAAEAAAgAAAAAQAACAAAAABAAAIAABAAAAAAgAAEAAAgAAAAAQAACAAAAABAAAIAABA" +
                    "AAAAAgAAEAAAgAAAEAAAgAAAAAQAACAAAAABAAAIAABAAAAAAgAAEAAAgAAAAAQAACAAAAABAAAIAABAAAAAAgAAEAAAgAAA" +
                    "AAQAACAAAAABAAAIAABAAAAAAgAAEAAAgAAAAAQAACAAAAABAAAIAABsB6m/fzzz0qdQOgCMAAAAAgCA+xS09OJuKAUARgAA" +
                    "AAEAAAgAAEAAAPBA8D94EzJ5xo8q/AAAAABJRU5ErkJggg==",
                ["farm-milk"] =
                    "iVBORw0KGgoAAAANSUhEUgAAAQAAAACACAIAAABr1yBdAAALlElEQVR42u3daVgU5wEH8HfZXZYbuQREVBAEhAAGEAVFUALi" +
                    "QSLRakyiQptEk+oTYw41iXglhSTaHE8eEx+jWLWallRrE7TBSkREDhEEOeQKoNy77MUCe0E/jGzGWRYW26Ys/H+fdt6dHead" +
                    "mf+87wxzsKqzjxCAicoIiwAQAAAEAAABAJhQOIzhrTtSsFBgHPvi0E60AAAIAIB2F0hXSwFg0HT17dECALpAAAgAAAIAgAAA" +
                    "IAAACAAAAgCAAAAgAAAIAAACAIAAABg8zgSvv6ynb9PvU7XL3ac7pya/rOtXCoXqlR2Hu2W9jHJbG8uvD70x/PR5PO7pI7tH" +
                    "O47+86/rt9Lunv2fnGpoaqMXWluZv7/jxemujmgB4BH1ja3VdQ90fZuTX6a99Y9lInF3cmoaY+u3sbbY986mibz1IwDDufSv" +
                    "gsf4agzqEkr3pKbdb+6kF9rZWu3flejibI9jABha3q0KsUSmXV5V08TYlY5lnXzRnpQTrW0CeuFk+0kHdiY6TbbFWkYAdFKp" +
                    "1JnXiobY/V8xmN1/W0fX+ykn2juF9EJnR9v9uxId7CdhFSMAI8j86Za6v59eIhRJ84sqDWLmm1v5e1LSBF0SeqGLs/2+nYl2" +
                    "NlZYuQjA0GysLei954KiKvq3P2b9EgkWi2VtZT42a9F4vz05NU0oktILp7s67t+ZSK8gIABMvj5uzk52msHLtONdRqcoKMDT" +
                    "znYs7krrG1r3fnSScQDjPsN579sbrSzNsIoRgOGwCFkaFaIZrKhubHrQQX3OLSynb1VLl8wdi4cuSvW+j08yztLOmjk1+a2N" +
                    "FuamWL8IwMiiFgSa8Ix/OeodbATorYGzk53/7JljcObV/f09vXJ6ic+sae/teNHMlIc1iwDoxdSUFxHmrxm8nlcq6+mra2ip" +
                    "qW/WFMYtnstiGUKPznvGu2+8YGpijNWKAIxCHK17I5crs3JK6Gc/TXjGkeEBBlGRTr5IMtR/MwABGM7UKQ5+Pm6awR8y83IL" +
                    "7moGI8MDTA2kR9HBF+1JTWvvEGKdIgCP3wjwBWKlSj3GD3914QvEe1KZ/wwGBGAEwYFe9nbW2uX+s93H8iU0PB53/bNLGIXU" +
                    "5UDNrXysVgRA70VjxIqNCtYuH/u7/1XLF2xYG8MopC4I1ZzSBQRgZEsigrjcR26ZcLCfFBw4a+zP+crY+Unr4xiFYols70dp" +
                    "BnQlHwLwf2ZpYRo+149eEhsVzDKI05+ExEXPfWnDcsbMSrt79358sq6hBSsXAdB3M9J85nI5ixc+aUAzHxMZvHlTPCOxMlnf" +
                    "/o//NMztPhMKB4tgeO7Tnf96PPlX/qNyuXJN0r5hRkhYsfC5hMX6TGrxwjkcNvvL4xf6+wc0hT298oOHTu3e/ry35zS0ADDO" +
                    "RYT5b3spgW30yLru7VMcPHy6vKoBAYDxLzzU7/Utq9lsI0Y78+GnZ0or6hEAGP/mBfm8+dpvOBw2vVChUKV8dra4rHbCLhZW" +
                    "dfYR+rDmXWJ4SR6MJ7o2bLQAgGMAAAQAAAEAQAAAEAAABAAAAQBAAAAQAIDxZrxdDn0m/cqFjBvUZzbbyMTE2G6SlYe7y1OR" +
                    "QR5uLtrjv/vBN5or4z/94DX6zb6aSa1bFfXsygjGxKnpW1mae82cmrAywm2ak1rd/87+o4332wkhG9fGrIidT42mVve/mfzV" +
                    "g5ZOQkjS+jj6DQZ0za389IvXauqbBUIJj8e1tjSf5jI5IiwgZI4XISRp20fSbp2v5HB1cTh84FXqc+3PLRmZeZU1TSJxN5fD" +
                    "dna0C5njFRcdam5mMuRS0q7IkONoPBUZ9PKGFWgBDIBa3S+T9TU1d1y9XrzrwLEz6VcYI7R3COn3hVzLvTPa6QtF0ryiyvc/" +
                    "PN7BF7HZRps3xRsZsQgh5y5kaR7L/PdLN6itf9bMqUuXhAw5qfqG1rf2fp2Tf7e9U6hSqWWyvpY2QV5RZXFZzahm6eLl3N0H" +
                    "j13PK+MLxCqVurdPUd/Y+u2Fn95M/mqYO+IZFUELMB6sWxWVsCJCJOkuKKo8k36lt09xIeOGtZXFiph5mnGyb5bSf5KTV/Zc" +
                    "whJ9bnik2gRBl+STL7+t/blFrlAW3K5aETPPw23KsujQ73/Mk8uV35zJeHvruvZO4XffZxNCOBy29s1ZGhcu5SiVKhaLvP7K" +
                    "6qDAWQqFqrmVf7OwnMfjUiMc//xt6kOfXPHilj8QQrgc9p+PvkefyO3SmlN/ySSEmPCMtyTGBwd6dct6z52/mpVTwheIUz8/" +
                    "e/jAq4yrQXVVRHscHAMYHhaL2FhbxC4OeWXjSqok/eI1uVzJCACHw6a6GZ0CcWVNo/7Tt7O18vd9+HjQ/sFnpq9LWDzZfhIh" +
                    "pLD4XmHxvWOnMxQKFSFk1fIFri4OuibV0iYghJiZmcwPmc0z5lpamHp7uiauX6r9gJNhfPePbOrDmqcXhc31NTbm2NpYbkmM" +
                    "px523dredYP2bK8RK4KD4PEjPNSPesKPrKevsvrhJl5d96Cto4sQ8oSPW2R44MNI5JbqP1mBUHLnbh2VtIDBDYhnzNXk7fOj" +
                    "fyspqyWETJ3ikLB84TCTop7ZL5P1HTx0OiunpLmVPzAwujr29Mo1jy5dFBZA2wuwIgcHqbnVsyLoAo0rLk72fIGYEKLp414f" +
                    "7P+EBvkE+nnwjLlyhTLvVsVvX1jGfbSfoO3c+axz57M0u88Na2Po71r093VfFBZwLfdOn1xBbYKbN63kDDvNqIVzSu7WEUJK" +
                    "K+qpW7SoB1KseTpSzyf6C7okAwMDVAIZr+2YPPg2JH6XeFQV0R6HEPLu9ucDn/BAC2BgBh7do6rV/TcKygkhRkaskDnexsac" +
                    "AL+ZVBNRdKd6tFO2ncR8Tcam52I1W2FsVLCXh+vwEwkL8d36u1WODjaaEml37+WrhQcOnVL/x30STc1ZhDXaiqAFGB9bP2lu" +
                    "49N3h8VltdLuHkKIt+c0ahcb+qRPwe0qQkh27p15QT4jHgTHx4X/mFWYdvafXULpJ19++0XKNvojyC3MTWfOmHK7tIYQouf+" +
                    "MiLMPyLMv+F+e8W9hpKyWuo2xYamtpq6Zm9PV30OSFgs1sDAgFyhFEtk9Eagc7DR036fzYgVwUHweJCTX0adlDQ3M/Hxmk7v" +
                    "/1Tca1yTtG9N0r4vjp2nSorLavV5CTaXw17+1DzqCdJiiezSlfz/yqzOcHVcFh26e/vzC+Y9QZWIxFJ9fmhmyvN0f/iPDvr5" +
                    "3IGBgZ8GB6lW7tepCAIwJnb8InH35auFR09+T5Wsjl/EM+b29soLS+7p+pVKpc4tLNfzT8QvDaM+XL5aqFY/fl/ls6+/O3H2" +
                    "8t2qBkGXRKVS32/u/LmxlfrK2dFOz4lo9tPpF6/lFpYrlSqhSHrkxEXqudBOk20ZT7n7X1QEXaCxgnHoRgh5ZtkC6gz3zVsV" +
                    "SqWKEBI213f75tWaEe6U1x08dJo6FxQTGazPXwn083B2smttEwhF0pu3KhaE+j3e3HaJpDn5dzMymXvfoIBZ2kelujzp7/nC" +
                    "mmjqnx5/PJLO6CC9s23dMAfiw1SEsSS9PV0P7EpCAAyhdTNimZryBi+FCPZwm8I4/0M/XUgI8Z/tbmtj2SWU3qu9z3i5tC4s" +
                    "FlkWHfrN6QxCSEZm3mMHYO0zUQW3q6pqmrqEEkl3D4fNdna0nR/iu3Lwego9PR0X7us944fM/MrqRrFERk0nZI73suhQc3OT" +
                    "X6EiBgePRYEJAY9FAZioZ4EAEAAABAAAAQBAAAAQAEAAsAgAAQBAAAAQAAAEAAABAEAAABAAAAQAAAEAQAAAEAAABAAAAQBA" +
                    "AAAQAAAEAAABAEAAABAAAAQAAAEAQAAAEAAABAAAAQBAAAAQAAAEAAABAEAAABAAAAQAAAEAQAAAEAAABAAAAQBAAAAQAAAE" +
                    "AAABAEAAABAAAL1wdH2xdUcKlg6gBQBAAADGKVZ19hEsBUALAIAAACAAAAgAwITwbwcUqAEm9wvLAAAAAElFTkSuQmCC",
                ["farm-eggs"] =
                    "iVBORw0KGgoAAAANSUhEUgAAAQAAAACACAIAAABr1yBdAAAMrklEQVR42u3da1QTVwIH8DtJyIOQAOEtBJBXQBBBUSwgPoq0" +
                    "4hOtq22t1q6tq9t2T9s9p92652y3urv2bLXduj3s2pc91WqrLR4fVAUfWBUVBEUBDRAkBUHkFSDkRZL9MCXGQSBBYNX8f58m" +
                    "N3cul8n8Z+5kJjOU/HQWAXBULCwCQAAAEAAABADAoXAYr197azMWCjzGtm15B3sAAAQAoO8QqL89BcAjrb+xPfYAgCEQAAIA" +
                    "gAAAIAAACAAAAgCAAAAgAAAIAAACAIAAACAAAI88jgP+z+pu7YuvfmDXLFs2rgv09x6ggqHHeOVa9Y0qpby6rrWts0ut6dZo" +
                    "nTgcPp8rcRP5+kiCpb5REUHhIf4cDnvQPzdcrVXIlecKyxQ3b92+09at0ZmMJi6XIxQKJG4iby/3AD/P4EDf8JAAscgZAYAh" +
                    "6tboDh0rOHaySNWhZryl0xt0eoOqQ12jbCwoLCeECIX8JxKi17yQwWaxRrS1+obmrK8O3Kj6hVGu0eo1Wn1zi0peXWcp/GjT" +
                    "+oAxXggA2K1cXrvts+zmFpWtOx+1Ni//0qrl6Wwed+RaU9Y3vffBjs4ujY3tmMxm7AHAbueLyrdm7TMP09ozXK2ZzeZtn2Xb" +
                    "vvbjGAAIj+e0M+tde7f9n2z/se/6GhEaMDMlPjJcKnEX87hO3Rpta1tnjbKhQq4sLLnR2dU90q2VlituKhutS4KlPkvmp8rC" +
                    "pGKxsMdg7OhU1ygbKyqVF4rK79i8t0EA4C6jyfSfHQcNPUbrQi6Xs3bl/NSkWOtCkYuzyMU5SOozIzlu7Spzcan84JECilAj" +
                    "11ppmcL6pbOA997bLwqd+fRLNo/lxXPz8nSbMjFy1bL0cnntwSPnWBSFAIAdjucXNzS2MApff3lx4qSoAeZisaiEOFlCnGxE" +
                    "W2tuvWejPsbX07L29zUuImhcRJAjf5Q4DzAUpwtKGSWJk6IGXl9HrTWTyXzv10F32lRd+MgQgGGjVmsrFXWMwqdnTX4YWiOE" +
                    "eHq4Wr/UaPUbNn1+OPd8w+1WfHYYAt2fTmdY+tJf+3s3Y3bi6meftrxU1jcxtrIcDlsWJh3anx7e1ggh8TFhh44WWJfcaVHt" +
                    "2H10x+6jIhfB2EC/kGA/WZg0MjzQRSjAR48A2E3VyTxF5SoWOjkxl+T+nDO79h2/bwuuYuHnH/9xJFojhMRGh0RFBFbIlX1r" +
                    "dnZpSssVpeUKQgibxYqOCs5IS5w0IQJDILBDd7eWUeIs4D0krdHeXLc0WOozcB2jyVRaptj8r91//2iXuk8fEADol3OfL1U0" +
                    "Wv1D0hrNzdXlbxvWLM+cKXIZ/CKfkqtVW7P2Ygjk0Ow6EeYqEjJK2lVdhh6jkw1XuY10axZcLmfJ/NSFc5IvX6u+Wq6okNcq" +
                    "65qMJtN9K5eWKUrLFbHjQhAAGJzU34vFoqyPXHt6jJXVdeNk93yhvigjZVFGCj399vvbFTcbRqE15qfLYSfERSTERRBC9Poe" +
                    "Re0t+vpQxqliej/gmAHAEMhuLkJB2Fh/RmHuqaKHobWB9wmR4YGZc1P++d7aubOnMt5tdtRrIhCAoUh9IpZRcvZi2eVr1Q9D" +
                    "a7Z4amYCc1/BZiEAYKu06ZN8vN2tS8xm89asvcWllf/31g4dLfjv14eqb94aoE59YzOjROIuxkEw2IrNZv1u1fxNW3cajXcP" +
                    "KzUa3T8+/jZ+fFhq0gRZaICr2IViUV1qTXXNLZVKPWqtdWt1efmX8vIveUjEk2IjwkMDgqQ+Hu5ioTNfrze0tncWllzfn3OW" +
                    "MVdM1FgEwHENfCaYELJ43rRnF89irDHrVy/89xfZjEuYS65WlVytsrcDw9saraW149ipomM2HE74+3k65hEwhkAPdiSQFPvu" +
                    "GytcxcKHsDXbCQS8V9dksh31GAB7gAcSFxO65f11+386e/x0sUajG7S+xF2Ukjh+1rT4kWstMixQFiaVV9fZ8uOyiNCAV1bO" +
                    "CxrstDECAP1yFQtXLUv/zYLpJVerKuS1lYr69o4utVqrNxj4PK5AwPPycB3j6xks9Y2JGiv19xrp1mKjQ2KjQzq7NNcrlZWK" +
                    "ul/qm5qa29vau3R6vcFg5POcnAV8P1+PkCC/KRMjI0KlDvxjGEcNgNCZv/fLvwz7QCJpSnTSlOiHpDWRi2ByvGxyvAxbKBwD" +
                    "ACAAAAgAAAIAgAAAIAAACAAgAAAIAAACAOBYHrFLIZqa23NyL1wpq25uUZmJWeIujgyTzpo2MTL87p2kqmpu5eSer6hUtqu6" +
                    "nDhsPx+PyfGyOWmJfW+RaUvNXfvyrK+epyhK5CKICJVmzk2JCA3or5+2zMWow2azxCKhLDRg8fzUsYG+D9JPNpvF53M93MRh" +
                    "If6zZ0yy/smlpdryzJlL5qfShT8cPL0n+yQhZFFG8vPPpNm4tBn9t5g9Y9IrK+chAMPv3MWyT7/cr9f3WEoaGlsaGlva2js3" +
                    "vLmCLjlw5NzOvXmWCyF7eoyK2gZFbcPxn0v+/OYKfz9Py7y217RmNps7OruLLt8oLpW/84fn4seH2dJzW+YyGk1t7Z3nL1WU" +
                    "XK3aumm9t6fbkPtpNJrUaq1arVXWN534uYSxWg/j0sYeYPTIq+s+2f6j0WSiKLIoI+WpmZNFLs6377RVVNb+UtdE1ykurfzm" +
                    "+1xCCJ/HXbd6QUKcrEut2ZN94uSZy80tqg8+2b1143r6oVq217SgN5nqbu3Xe46ePHPZZDLv/D530ADYMhddp6W148NPv6uq" +
                    "uaXTGy4WX5+XPnXI/Vw8L7W9o+vipYpd+/I0Wv3+nLOuYhe6wWFc2oz+4xhgZO3+8QR9W5vZMxKeW/Kkh0TM5XKk/l7pMxJ+" +
                    "uyLDsiunJ5YunJ40JZrL5UjcRetWL/Dz9SCENNxuPXvxmr01GYTO/OWZv/40TFnfZL2BHIAtc3lIxLHRofS0qfcGPkPrJ0UR" +
                    "d1eXp2ZNXrtqPl2y70C+TmcY3qWNg+DRo9Hqy2/cpKcXPJ103zrdGl2lop6enp40wXr8PaP35ZVr1XbVvP94hgzlKUaDztXS" +
                    "1kH/UYoiE6JDH7yfhJDkxBj6ZtHqbm2FvHYYlzaGQKOqpVVF3zrKyYnj7eneT50OeqDM4zoxflhoGU/TD4+wvWZf6m7td9kn" +
                    "6elAf28u16YFOPBce7JP7ul910MiXrksnf6J1oP008Lf15O+509Tc/swLu3++k8I2fDG83G2HRohAKPEsu1lPE3IrpqMj5mi" +
                    "qOeXptm7cgw6l9lslriJh/M/cuAnQD4+AfCQuNL3DzQYepqa23y83O87hqYoymw20w/Ttd5k3und+HlIxHbVvN/wmnIRCiJC" +
                    "AxZlJEeGB9rY/4HnWp45c8Gc5GMnC3fsPtra1vnhp99t2/y6gM99kH72rv13bwFE7zQ47F+PmK0fSWaZpt+1ZWk/NgfBj0YA" +
                    "BHzuOFnwtYoa+mvBl1+Y27eOs4AXHuJPP/85/9wVy+DVbDafOneFnp4QE2pXzQf/mG2cy4nDnjt7atFl+bWKGlWH+qe8C4vn" +
                    "TRtaP62duXC1pbWDPgqPkgURQtzdRIwIEUKa7rTRE+7uIhuXNg6CR9vyzJn089BzTxV9+8PxlrYOg6HnVmNLXv6lL3b9RNex" +
                    "rGr7DuSfKywzGHra2juzvjpAP4LO11uSPCXG3pqjybKKHzlRSN8ka2j9NJtJu6rryInC7V8fokueWTCdx3UihMRGh1AURQi5" +
                    "UFxRWqbQ6QylZYqLJdfp3dSE3q+hbFnaGAKNKlmY9PdrFmV9dcBg6Mk+fCb78BnLW3G9W8GJseErlqbRX35/lLWPMUB6+/Xl" +
                    "lq/Mba85muJiwvx8PeiTTQVF5SmJMUPoJ+OogxCyKCPFchLA11syN33qoaMFOp1h45ZvGPGzjHZsWdr9/cXIcOnGP72EAAy/" +
                    "aVPHR4QE5OT9enKeECKRiGWh0idT794YZ+Gc5OjI4MO5FyrktaoONYfN9vORTI6PzEhLFArvuRTC9pqjhqJIRlriFztzCCE5" +
                    "uedTEmOG1k8WixIIeL2XQiSEjR1j/e6qZekhQX7HTxffVDZqtDoBnxcc6JuWOjFl6nh7l/ZjgJKfzrJ+/dpbm+mJbVvewVcE" +
                    "8Njob8XG1aDg0BAAQAAAEAAABAAAAQBAAAAQAAAEAAABAEAAABAAAAQAAAEAQAAAEAAABAAAAQBAAAAQAAAEAAABAEAAABAA" +
                    "AAQAAAEAQAAAEAAABAAAAQBAAAAQAAAEAAABAEAAABAAAAQAAAEAQAAAEAAABAAAAQBAAAAQAAAEAAABAEAAABAAAAQAAAEA" +
                    "QAAAenH6e+O1tzZj6QD2AAAIAMBjipKfzsJSAOwBABAAAAQAAAEAcAj/A/YGUGDLSWJ9AAAAAElFTkSuQmCC",
                ["farm-meat"] =
                    "iVBORw0KGgoAAAANSUhEUgAAAQAAAACACAIAAABr1yBdAAAMkElEQVR42u2de1QTVx7H70AeBAhIoghFUHmqKKCAKCAFxRf1" +
                    "XT216ra1u61rn2t7drfbdtuzx3Zr91R3a9vDaU+L21Or1mp1bUVbUIQCCigg8pKXgEJQCYFAICEJs38MjENCYhiU+vh+/pq5" +
                    "d2Zy5zfzvff3uzP5DVWVlUwAeFixgwkABAAABAAABADAQ4XAZP3l13fAKOAB5pOdb2AEAAACAMDcBbI0UgBwX2PJt8cIAOAC" +
                    "AQABAAABAAABAAABAAABAAABAAABAAABAAABAAABAAABAHDfI3jYTljTrX3mpQ/Ny30nen747vOW9urtNWx5fVeXpsekXOYm" +
                    "/Xzna7Yc3wo7t2/18XK3skF6ZuHnX/9oXv6f91/08hw75C6vvvlpc4uSt5WiZwdv++NajAAPEXUNiqraa5Zqs/Mumd/9o8aZ" +
                    "nOJhlQMIgA8nTuXzqLrbKK63Xa65OmRVZm5JXx+NCwcB3BnOnS/vUGvMyyurG+sbW+617p8QomrvLCmrxYVDDHBnMBiMaZkX" +
                    "1i6PM+3+00fU/YvFwr3Jb/Lbl6bpzNyLVjbIyCkOm+FvXv7xP18yL2zv6Hpu205uidRZkrL7LxgBQD9pZ84b+/pMetm8CxW/" +
                    "VXsulV9Rtqm5JVMDfbirBUWXNd1aXDgIgD9urs7scpuqM/9CJbf2l4xbkqAoytXFaTTblpFdZNLUrc+s4Jbo9YacvFJcRAiA" +
                    "P8FTJ3t6yNnVk5x4l3GK2NXw0AC5zGXUGtbTo8svGqTG2DkzPD3kgX4TBoukGBcRAuAPRciShEh2tbyqofHaDWY5t6CMGxYv" +
                    "WTB7NBuWk1/a22vglsRFhxJCHo0O5RbWXGlqUrTiOiII5k9CbNj+H05rdb39Ue+p/C1PLzMZDTw95CHT/PYdPjXcg+t0+nXP" +
                    "/sNSbdLCqM1PLrHg/wzq2n0muE/yHk8IiZ4dnLLvhNHYx91y07pEXEeMADyRSMRx0SHs6q/nSjTd2tr65uq6JrZw6fzZFDV6" +
                    "TVK0KE0ezLEdv7OTZFZIALcq6yweCEAAI2Mpx73R6fQZ2cXc2U8HsSg+JnRUw9/B0/8URcVGzbjlC80N4daq2jsv4oEABDAS" +
                    "JjwybvrUyezq8bRzufm3ZlfiY0IlEvGoNYam6azcEm7JjKmTZW7SW+F4WJCTo8Ngf6kIFxExwEgHgdKKK8xyq7KDWzWS8JfH" +
                    "g7CLZXVKlXpw+DuoyxcK7OdGTkvPLGRLzhdd1mi0Tk4OuI4QAE8iwoLGyl1Nbn1CSMg0X0svXd4lzF9/+PTLo59+edTKLnqD" +
                    "MTvv0uL5kbiOcIH42sKOWpwQYV4+yrOf3T26/MLKOyIbAAEMjwVx4ULhoFFx3NgxEWGBo9mGnLxSvd7AY8eaK83Xmm/iIkIA" +
                    "/JE6S2JmT+eWLE6IoEZz+nNkHTmeCiMGGHEonDibvQWFQsH8ebNG89ebzab/Vy6NsfKQ6+8f7KmsbmRXs86WbFybaGdH4TpC" +
                    "ADzxnej5fcq7d/CA1p8EE0LWLJv35Jr5A1246WxmeKg1Byw8NJArgPaOruLSGpPHZAAu0P0BTdNZZwdN/zs7SYL8va3sMis0" +
                    "AF4QBPCAcLG0tk3VyS0Jm+Fv3Z/x8XIfJ3flllwovvwb/n0ZAgD8yTALf637PwwzBzs8eoMxG/8QsBmqKiuZu85+SwwfyQMP" +
                    "EpZubIwAAC4QABAAABAAABAAABAAABAAABAAABAAABAAAA8a9/Tr0N8eSj+amsOuUhQldZYE+nmvfiyWTQ9oso29vZ2L1CnI" +
                    "b8Ka5XGTfTy4R6u50pyadq6iurG9o0sosPccL4+cGbQ0MYqbXoF7NHt7OwcHkXyMi7+v18L4cP/JXuabrV+d8PhANunDP2Yd" +
                    "OJJBCFmVFLNx7a03+G+0tqem5V0sq21VdtCElrm5TPH3nj9v1pQAb/P2syyMD3/+qWWWLNOkaD10LLO6rkmpUovFQlepk4+X" +
                    "e1x0aOTMIELIs6/8q7PL4vtw3l7jdm1/gZ9NLFmY31lAAMODpml1Z/f54suFJVVvvLph5lBpwY3GPlV757kLFUWXana994L7" +
                    "2DFM+bGTuXu/T6fp/uxRBoOxrkFR16A49WvR269tGvI/70Zjn0aj1Wi0jU03Tv9aZHJb20huftlnKUe5GQ4VLUpFi1LV3vnW" +
                    "a5v42aGuXvH2Byns3yYNBqNGo21uUUqljowAbISHTaxYGCPAXYTpaDXd2q8P/JyRXdzXR+89mGYiAGYbZZv6o8++q7nSrOvV" +
                    "5xdWLls0hxBSWFL9zcE0QoiDWLR184qIsKAuTc+BI6czsotblR0f7t6/a/sLAoG9ydHWLItrV3flX6j49lB6j7b3aGqOq4sz" +
                    "c0Abqaq9tvuLH4x9fRRFViXFLk6IlDo7Xr+pqqhuuDqQftSk/bYc9uiJbL3eQFHkT1vWhocF9vYamhStZwvKxGIhswGb8l+r" +
                    "6/3d1g8IIUKB/b4v3uYehJ9NLFmYx1kgBhg2To4O61f3/3OqsemGSeJYBrnMJSTYj1nuG0hrfvjHLGZh3cpHo2cHi0QCmZt0" +
                    "6+YVTFJoxfW2nPwh3h+mKOLm6rx4fuSWp5czJYeOZep0etsbvP+H00xq9YXxERseXyCXuYhEAm+vcYviI36/KYm3HZiv3zk6" +
                    "OsyNnCYWCaXOkikB3ps3LNnw+ALbD8LPJpYsjCB4tLwgcpsMmEqV+mJpLXP7hgb7EUK6e3Rsik9uXmWKouIHVpldLBETNX2s" +
                    "3JUQounWVlQ12NjUHm1v+eV6ZnnFkug7aATmgwYajfa9nXszsoubFK30MPOCjsQm5haGCzRKaLq13x3JYJZ9vNxFokGNP3Ak" +
                    "48BArVzm8tQTiyZ6jyeEKNvUjJsrFglNPm/B+q+tbR3Wf9rLYyyTMOtGa7uNrVW2dTAJa4VCgftYt9tuz20/IeStbRuH/PYR" +
                    "ISRh3szi0lpCSEl5XUl5HRlIZrFuZbyL1NG2tvGxiSUL8zsLCGAYmJiVoqiNVrOB0zQtG3P7L1mw/SZFqNvG3/eONaIjgw16" +
                    "48H/nbl+U8WUdHb1nDxdUFlzdcc7z9nbjWhUt9EmNloYI8CdhKIoZydJoN+EVUkxUwJ8zIPIFUtjfsko+O/+n9tUnR999t0n" +
                    "O16ROIjkMheKomia1vXqO9Qabod3c6A7t/7dF5omTS2t3A5SYN8fHeoNRnYzdpmplctc7eyovj5arzfcaFWNH+dmS6Bvoyni" +
                    "okPiokPqr14vv1xffKmm6FINIaS+saW6tomZXbUOP5tYsjCC4NGYBfo+5d2DX72TsvvPb7z6pPndzyAU2D+2cA6T5LlDrTmR" +
                    "nkcIcZSIA3z7p/C5X1ykafrMwGrodGu+bHbeJeZLdU6ODlODJhJC3MZITW4XQsiNgf7YzU1KCJE4iKYFTWInHO+GWSZ5j09K" +
                    "jHpz28bYOf2Z09s7Om3ZkbdNhrQwguB7CzbiPHm6gPmMCtsnHTqWmVtQptcbVO2dyXuOKVqUhBAPd5lJNji242/v6Dp5uuCL" +
                    "r39iStaueFQsEhJCQoJ9mXRxeYUVJWV1Op2+pKyO+Z4XRVFsaLh+dQLjkKSdOb/v8CmlSq3XG5pblOmZF7769gTvE/z488N7" +
                    "9p8sraxXtqkNBuPVpptXGhRMled4uY0H4WcTSxaGC3QPETbd39NDzjxsOnu+PDZq+qyQgE3rEpnp/H8nHzJxBv76ynqTCW/z" +
                    "qIMQsioplp3z9nCXPbZozk8/n9Xp9Nt3fmNyc7DeTpC/94t/WJW855hebzhyPPvI8WxOI/2s/+KUAO/tf3t2yBNsa+/MzitN" +
                    "TTPtfcNDA82jUkvwsIkVC/M4Cwjg7oUKJCkx6qu9qYSQ1LRzzOVZuTQmeMqk42l5FVUNHWqNwN7ec7wscuaUpMQoSzn17ewo" +
                    "iUQ88CpEhP/kR7i1Tz+xyHei56mswvrGlh6tTuIgnuTjkRg3i/VGGObNmRHoOyE1vf9VCEKITOYS5Oe9IG4m7xN8YlVCfmFl" +
                    "ZXVjm0qt7upmzmVuZPDyxXOHdRweNrFi4fv1bkFaFPAwgLQoADwcQTAAEAAAEAAAEAAAEAAAEAAAEAAAEAAAEAAAEACAAACA" +
                    "AACAAACAAACAAACAAACAAACAAACAAACAAACAAACAAACAAACAAACAAACAAACAAACAAACAAACAAACAAACAAACAAACAAACAAACA" +
                    "AACAAACAAACAAACAAACAAACAAACAAACAAACAAACAAACAAACAAACAAACAAADgicBSxcuv74B1AEYAACAAAB5QqKqsZFgBYAQA" +
                    "AAIAAAIAAAIA4KHg/xqFnZ40Sl04AAAAAElFTkSuQmCC",
                ["farm-guide"] =
                    "iVBORw0KGgoAAAANSUhEUgAAAgAAAAEACAIAAABK8lkwAABFE0lEQVR42u2dd1gU1/rHz/ZCb1I2IF2aVEGMogKCJSYxlkSj" +
                    "iSZGU4ymmPrz3mvKNTfNxJhEY2ISjS1iiT0oKr1JEel96Sydhe1s+f0xMC4LLLMLGNT38/j4nDlz5uw77ynfU2YGUnnSfgQA" +
                    "AAA8fJDBBQAAACAAAAAAAAgAAAAAAAIAAAAAPJBQNY63bv8cnAIAAPBA8v3uD2AGAAAAAIAAAAAAPKxQCc4UAAAAgPuUkdb2" +
                    "YQYAAADwkAICAAAAAAIAAAAAgAAAAAAAIAAAAAAACAAAAADwAEEFF9wzikprPvry8IglQaWwmAwrCxNnR7uZQZ5+3s4kEkl7" +
                    "hgqlcvNbu3t6RRrx3332up2NhU5mvL9t9Qz/aUMTr3rxY/XDrZuemjvLV3tWFDKZQiUz6HRDA6apiaG1lbmLo62fj6uttTlx" +
                    "X4nE0lu5JUWlNdU1zT0CkUAoptGoRgYsK0tTr2lT/X1c3V0e0c/VJBKJyaAZGrDsbCw93OwfDfHR4quxe3ssVUIDGpVy/Od/" +
                    "3Zv6oKuvhs2ERELf7XrddoRLcvMr/rfn+ND43/a+a2TIhu4CBODhQi5X9ApEvQJRdW3z9cScqfbWr734pPNUWy2X3M6vGNra" +
                    "EUKJaXfWLI/Q6ddPnLkZ5Oc+quQQ7CgVMqVMJu8ViJpbOkvK6xJS8xBCXu5TVy+P8HR30H65VNZ39lJy7I1bIrFUwz9isbS1" +
                    "vbuotObU+UQPN/s1KyK93Kfqap5KpRJLZGKJrK2Df6eoKuZ84oJ5gRvXLqFQRpkNj6O3J4hxt1BvXw1cjq7GZ29Ys3DYs7E3" +
                    "bkGrhyUgYHhq61t2fn6ovKpBS5rE1DvDxiel56tUuv1cXWNrckbBhN5RcXntzi9+P3rquhbbWlq7duz69eylZI3efyilFfUf" +
                    "f3n43JXUMVqlUqniEnJ+/PXcqCnH0dsTxERbSNxXOAmpeVJZ37AFnVdYCc0cBAAYEYlUtveXs31yxbBnhUJJzp3yYU+1d/CL" +
                    "Srm6/tzJv+IVCuWE3pFKhc7/nXrwyOVhz/J7hDu/PFRb30IwN6VSdez09b8up4zdsOSMgkpuo5YE4+7tceeeWTiqrwZZJZIk" +
                    "pw8zsIiNz5okqvmQA0tA/ySvbHg8cm4gtmzS3sFPySg4czFJvcdvae3Kvl02K9hr6LWptwpH0gZs1u/j6aSTMa3t3dcTcxZG" +
                    "BI/9jpRKlVAkbm7pLCzhXo3P6uzqVU9zLSHb/pEpi4b80Df7TnV09qjHMBn0px6bEzrDy8rSVCqVlVc1XIhNKyqtUU9z4uxN" +
                    "VyfOdC8ngq4WS2RV3MaDR680NrerJ8i6XebqxBnp8nH39qh26so4WjhGX2lwNT5rwbxBdySV9cWn3IbmDzMAoB8KmWxtZbbi" +
                    "8bnPPBWucSq/uHqkJq1+qLEpmpFdIpX26WrG6YtJw07Yda5VZJKRIdvd5ZHlS8O+/9829X1jjOOnr/cKxOox2XnlxeW16jFG" +
                    "huzP/vXS8qVhdjYWNCrF0IAV6Ov20Xvro8NnaKxLHDt9nbhtLCbdx9Npy8ZlGvG81k5tqysT4O1xXv+ZAAv185UGNXW8ssr6" +
                    "QXOI9AKhUAKtHgQA0CQkwEMjprOrZ2iyZl6HxvbA5ueXGhqw1JePMnNKdP31br7g7+uZ43tHdDr19ZeW+Xm7qEeKJbLLcRnq" +
                    "MRdj0zQu3Lh2sT3HamiGLz6rGV9V01Q4eFowKi6OdlQqRT1GNrLyTZC3x5EJtVAnX+GoTxFib2YNmhPcvLv96+bMgVYPAgAM" +
                    "jGeJJUsYPNxzeGTKVHvrmUGeWoaEWggOuPsA6Lm/U4WicR6dkUik9asXajxhlJVbqq4HGoNEKwuTR0N8hp8tUchLo2dpRN7R" +
                    "fUdRpRzkbFMTw3vj7Ylgoi0k7iucxZEhavOPYn6PEAuXVtTVDGzzkEgkjfkcAALwUHNLrVvEMDcz1myNKpScnq8eMzvEB/8f" +
                    "p6CE2zHc7GEoK5+YR6f37wYJhZLzf6eO+33Zc6ymPmKtHlPX2Ip3CmUVdQrloP1n/+muWh5JDfB104gpLqvVyZ6K6gaNX/Sa" +
                    "5jh83zfe3h7/QcMEW0jcV+pM93bGXxqQyxU3knKx8N9qT38G+blbWZhCq/8HgU3gSYFSqWrv5KdkFJy+kKhxytfLWSOmqKym" +
                    "rYOvHvNoiDdCyNvD0cTYAO9VVSpVcnr+siVzRv11c1OjRREhFwYWYa7EZT4WFWpibDC+9+jqzKkZ/IRPWwcf+5XO7l6NxA6c" +
                    "KVqyMjMxNDRgCYR3dxGId20Sqay8quHXo1fUI6dYmoYOHi9PnLe189Ohiz8dujjsqU3PPxY9f8a9tFBXXw2a9iG0MCL49+Ox" +
                    "2OG1hOxlS+bwe4XqK1GLIoOh7YMAPLxoae0Y1lPM1Ndn+ufygx/3dna0tZlijhAik0mhM7yuqq23JqTeIdjgn3pszvXEHOzp" +
                    "e6ms7/TFpI1rF4/vzRobaSpKr0CkEcAxUFu/HhYNARiaA3FXmxgbvLt1NY02fFuYCG+PL+Nuod6+0iB8tv+JMzclUhlCqKOz" +
                    "J+dOeU0dD3/U2Nba3NfLpbisBvoBWAIChoHJoG/btFxj/00q68vMKR4631cf+uE0NrdX1TQR+S1DA9aTi2fjh9cTc9rau8d7" +
                    "pUKlZRFj6PhxlNwGb5eQkD7vMDMYtKj5QV9//IqjvfWwCSbI2+PIPbNwVF8NhcViqD8AdiUuIy4xBz9cGBE8Hi+eAyAADyJT" +
                    "7a0//mDD0C/eZOaUiCUy9ZhZwXcbuaebg5mpkfrZhFSiW3/qyz5yueLkuYTxvSN8LUJtTsDWCOAIRtuI1niO0NCQpWcDIJNZ" +
                    "TMZIZyfO2+PFvbRQu6+GZaHaIk9haU3XwFofg06bP9sfmjksAQH9UChkNothZWHq7Gg7M9DTz8dl2C/zDH3c28rC5O5AmESa" +
                    "Fex1Je7uo5ypmYUbVi8k8vEWBoO2Yunc347/jR0mpeerzwnGztDXRy3N+y03H9xJIYTqGrS9D9zNF6iv/yCELIZslRMaPkv7" +
                    "rt7MquI2ffLBhmGXNSbO2yOh64tg98zCUX01LA6cKV7Tpg7dog+b5WvAZkKrBwF4qNG1tXd29RYUD3qnv7yqQeODnRr0CkQ5" +
                    "d8pDAj2I5B81P+jitXRs8UelUv35183xutOa+pa6hlaNrgGfcExztSeTSUq1Zw3zCipVKjTSEkFufoVGjOdoX4XDXC0USSqq" +
                    "G4+dvl5Tx1NXpiOn4l58dvE99vbYmSAL9fCVFhZFhAwVgDG+cA7AEtDDSFJ6vkr3T6gQfwCcSqU8/eR8/HDoM6n6oVSqDp+I" +
                    "1YgMVuuDWCzGNFd79bNtHfz07KJhc1MolRovkSGE/H1ciFhiwGb6+7h89O569WEyQujqzaz6xrZ77O1JXh908pUWQoI8zM0G" +
                    "zfA83ByIbyQAIAAA3uD16Vxy8ys0vrughXmP+g77/q3eSKV93/18RuNNXRaT/lhUqHrM4wsf1bjw1yGfoME4dOKqxmTCeaqt" +
                    "Tp/iMTBgblizSEOiTpy9ce+9PfnrA0FfaYFCJkfNCxo8J4DhPwgAoCNVNU06jbxw5HJF6q1CgolJJNLqp8b6dXuVSiUQiiuq" +
                    "G09fTNr6wd60W5pj+WdXLjAavG07w3+ah9ugSUBPr+jD/x7863JKc0tnn1whFEryCio//uoPje/Ik0ho7coFuloYEujh6mSn" +
                    "HpN1u0z9CZl74+3JXx+I+GpUFswLwncdTE0MQ2d4QXOeJMAewH2DxsydRqX8+t27LNbwT2W8//HP1bXNd69NvUN82BUS6OHm" +
                    "zKmobtTDyFHfbEAIRc+fMdQYEgm9/drTH3zys/qnQ8Vi6fEzN46f0TbefGZZuK+3sx6mPrMsfNe3x9RjTv4V/39vrb3H3p78" +
                    "9WFUX42KqYnhn7/8G5owzAAAPVEolKmZg0ZtgX7uI7V2hNDsmYM+A1DJbRx2OWUknl0ROSG1jUxau3LBS889NuxZMxPDj97b" +
                    "QHwBikQirX4qfMXjc/Uzxn+6q8bGw+2CSuyTavfY25O/PmjxFQACAEw4uUP+2t+cmT5a0s8O8dF4hEanzUkfT6ehn6AYCyQS" +
                    "aYa/+6cfvrhsyWwtr//YWpv/71+bli2ZzWLStWfo7vLIznef17v3x3h62XyNmJN/xd97b0/++qDFV8B9DSwB3R9oNFcmgx7o" +
                    "664lvYW58TRX+9KKu5/YTErPX7M8gvhf/X12ZWT+J9X6DfMpZDKDQTcwYJqZGNlMMXOeajfD393K0pTI5QwGbe3KBcuWzLmV" +
                    "W1pYyq2uae4ViARCMZ1GNcT+KLz71KEDUv3w9XL2cp+q/ncI8ouri8tr7723J399GMlXevxZZmDyQCpP2q9+vHX751jg+90f" +
                    "gHcAAAAeAEbq2GEJCAAA4CEFBAAAAAAEAAAAAAABAAAAAEAAAAAAABAAAAAAAAQAAAAAAAEAAAAAQAAAAAAAEAAAAAAABAAA" +
                    "AAAAAQAAAABAAAAAAAAQAAAAAAAEAAAAAAABAAAAAEAAAAAAABAAAAAAAAQAAAAAGAvwR+HvES+9+TW/R6g9jfUUsx8+3zYJ" +
                    "jc8rqNz17TE3Z85n/3oJISSXK05dSEy9VdjewVcolAihDWsWPhYVOgktv5emXkvI/uWPywghI0PWb3vf0xIJACAAwH1DVl4Z" +
                    "QmiG/zTs8OS5+HNXUu8Ly+8jU+8l3XzBprd2Y+FdOza6uzxy73MAQAAeIg7ueQcP38ot/eqHk1j43+885+vlPMmNz7lTjhCa" +
                    "EdAvAGm3irDA0ujQdauiKJTJu5B4H5kKACAAwKSjura5o7PH2srMgTMFi+no7MEC01ztJ3mX+o+bGj1/RvT8GVCLABAAYHT+" +
                    "OHnt4tV0hJDXtKkfv78Bj78Qm3YkJg4hZGdj8d1nr6uvLP+y550zF5PiU/L4fIGNtfmiiJDo8EE9jkqlSkrPT0rL59bxRCIJ" +
                    "m81wceIsiggO8nMnYlK22vrP3p/PJmcU4Kd27zvVH/j01c7Onl3fHrtbsagUEyMDN2fOosgQbw9HPF7d8gPfbI85l5Ccnt/Z" +
                    "3fvOlqc7u3p+PfZ3/6ndbx89fT0lo6BPrvD1cn7h2UUWZsbZeWWnLyTVN7WamRgF+buvfiqCxaSPZLYWUx04U5RKVXzK7ZSM" +
                    "gtqGFpFIymTRHThTZof4LJgXhOuEFlNDAj0IFujQPYATZ2+evZSMEHJxtPv8P5uGprQwM9731ZtkMknvglMolVfiMtNuFTXx" +
                    "2qXSPjMzIztrCz8fl7BZvmYmhju/OFRcVosn3rHrVywQFjp92+bl2H6P9nLUngN+g/Ycq28+fQ07dSMp96dDFxFCbBbj8I8f" +
                    "ELETegMQgIeOJQtmXonLVCiVxWW1DU1tj9hZYfF4XxY5N1Djku8OnEnPKsbC9Y1tvxy53Nzauf6ZaCxGJpN/sfdEfnE1nr5X" +
                    "IM4rqMwrqFwaHbp+9cLRBeB2mfr6D0HkckVHV09HTk9mbskrG56ICAvQSKBSoW/3n8q6XTb0WpUKfbP/VHZeOXaYmVNSU897" +
                    "PHrWwaNXsJiWtq4rcZmdnT3btzyth5Ol0r7Pvj1WXH63CxMKJSXldSXldQmpd/7zznMsFoOgqXoQNS/o3JUUpVJVVdNU19iK" +
                    "z6sSUvKwwIJ5gWQyaSwFd/DI5euJufhhewe/vYOfX1xdUFy94+1141uOWiAhkvYE42gnAALwIGBpYTIzyDMtqwghdDU+e+Pa" +
                    "xQihxub2mjoeNhybP9tfPX2vQNzVLfj+860GbGbMuYTYm1kIoUtX02eHeLs6cRBCx05fxzoRMxPDN15e4erMKa9s+PrHkyKx" +
                    "9NK1jGmu9qEzvLQtoXT1cOt4BgZMT3cHhNC2zcu3bV7+zEufKJUqhND211bhlztwppz6bScWVipV3XzBleuZ5/9OVanQkZi4" +
                    "ubN8qVSKes4CobiJ1/Hphy+4OHFoVApCKPbGLfyUQqH85dvt/B7hh/892Ncnb2ntOnj0ytqVCxaGz4hLzMEmQxk5JZ1dveZm" +
                    "RsNarsXUX49ewXp/JoO+bfNyH0+n8qr6PT+dEQjFldzG347Hbtn4pHZTx1jEgb5umLwlpOQ9/0w0QqiJ11FR3YgQopDJmMaP" +
                    "peCS0wsQQmQy6aP3Nrg5c3qF4uaWjls5pUKRGCH08fsbtGzh+k93HbUctedwVwDIowiAdjuBewDsiU06li6chQUS0+5IpX0I" +
                    "oZSB4X9wwDRjI7ZG+k3PP2YzxdzIkL1+9UITYwMsMiWjEBu+3Uy+jcWsXh7h7eHIoNOmeznh04ir8dlEhv+B090oZB2qCplM" +
                    "MjczevrJ+XgHyq3jDU22ZeMyDzeHYbvUF55dbGpiONXeGh8g29lYLFsym8ViqEtgc0uHru6VyxUJqXewcHT4jOCAaSwm3c/b" +
                    "5fEBt6dkFkikMuKm6kF0eDAWSErPVyiVCKH4geF/cMA0M1OjMRYcnU7DJi681s62Dr6xIdvLfeqGNQu3bFymW+9ArBxHngGM" +
                    "wnjZCcAM4MHBzZnj7vJIeVWDWCxNzshfMC8oJbOwf3FgbpBGYhqVYm/X30VSqRR7OyvsbYMmXjtCqK2Dj/dl+3+/sP/3CxqX" +
                    "19a3jCIAeUTXf+RyRezNrKzc0obmNqFIgj10j9PNF2jWPCrF1clu2KxoNKqttTkWxldjHO1tsACTcXfdf2hPPSrqPnFRMwAP" +
                    "y+UKXkuno4MNEVP1w9/HZYqlaWt7N79HmFdQGejrlpyer64NYyy48Dn+F2LTVCrVvt/OY7MKO1uL6Z7OS6NDrSxNx7ccx8JY" +
                    "7ARAAB5YHl84C9u0vHozy9HBltfaiRCaYmk6fbwfGNXegUqksqLSGgqFHODjOmpWX/1wMje/YqSzCoVCI4bFpJNIw48RmQza" +
                    "3VHkQBLmwH6vUqUc0z2rVGpDVNJw0Yi4qfpBIpGi5gcdO30DWwWiUikdXT0IIVtrcx9Pp7EX3LpVCzi2lulZRdw6Hr9HqFAq" +
                    "6xvb6hvbMnKKd3/yqqEBaxzLUQtKNZ+KxdLxtRMAAXhgCQn0tLI0bWvvrqlvOfxnLBYZOTdwaC/UJ1c0NLXZc6ywsVt9UxsW" +
                    "b2tjgRCysjBhMuhYZ7F101NzZ/nqZEZeQWWfXOHr7ayxKTqUjs4evNdY+cS8pVGhBgbMXoH4xW1fTjbfWlmaMhg0bG2tqqZp" +
                    "VnD/Sjq3thkf79sMzD8mjoiwwJPnEuRyRfadcomsr3/4P38GVsRjLDgSiRQRFoBt2EqkMm4tb/e+GH6PsLOrt7isNiTQYyQ9" +
                    "I16OWhQRn6IJRRI8soLbqIed0BVMNLAHMClLhUxasmAmFi6tqMdiwuf4D5v44JHLLW1dvQLR4T+v4l+bmDNzOtaXhYf1X/XH" +
                    "yWvZeWUisVQskTU0taVkFn79Y8yV65lazMBeAA4OGL0dqj9iz2LS6XRqW3v3gcMXJ+OQh0oJH9hFuBaflZ1XLpHK8ourL15N" +
                    "G3Cdj/oq0wRhbMQODfLEZDuvoBJb+Jo/UMRjLLhd3xw9dT6xorqR3yOkUalMtYdl5QoFQojFZOA9eGlFHbYPoVM5jpQDNo/B" +
                    "5SQ1s1Aslsan5GVkF+thJwAzgIeUyLCAmHPxYkn/TD/Q193MdJjHXQwNWMbGBq+/v1c9cknUTDdnDhZeu3JBQ1NbQTGX3yP8" +
                    "Yu+fGpdrWdpWKlW38ysQQjMIvC5gamLo7+OSV1iFEDoSE4c9pbNgXuDk9O26VVF1Da3F5bViieyLvScGO4TzwrOL740Z0eHB" +
                    "+O4OQmh2iLf6oofeBYcQam7tzCusijmfoDn7sTDBVvPodKqHm31JeZ16eWFTDYLlqCWHID93bIcDIbTnwBmEEINOiwgLUH/i" +
                    "k6CdAAjAQwqLxYgIC7wcl4EdjtSZkkjozZdXnLazSkjN6+YLrK3MFkWGLIoIxhMw6LR/b38uOaMgOT2fW8cTCMRsNsPczNjJ" +
                    "wSYk0NPP22UkA0or63oFYkcHG0sLEyIGv/XqqtMXEjNySrq7e83NjaPmBT0WFTq0zU8GGAzazvfW30y+nZJZUFPPE4ulLCbD" +
                    "njNl9kyfKLUXwSYaT3cHe45VfWP/ql3U4BeG9S44hNDOd9enZxXl5lfwWju7uwVUGsXa0szPx+XJxbPx1bytm5YfOhFbUl4r" +
                    "EIrV9z+Il+NIOdBo1J3vrT8SE1dWUSeSSD1cHZ57OqqS2zg0EyJ2AhMKqTxp/6BC3f45Fvh+9wfgnX+WkvK6/3z+O0LI3Mxo" +
                    "/1dvkdWeqr4H35jE3kle+cS8Z5bNh7IAgPuakTp22AOYpAiEYnz4v2TBTDKZdI8NyL5TjhAK9p8GZQEADyqwBDTpUH/HEiFk" +
                    "a2OxKDLk3pux97PXoSwAAAQA+GcwMmRP93R6/ploBp0G3gAAAATgwcfUxBD/GMtIwEeGAQAYO7AHAAAAAAIAAAAAgAAAAAAA" +
                    "IAAAAAAACAAAAAAAAgAAAACAAAAAAAAgAAAAAAAIAAAAAAACAAAAAIAAAAAAACAAAAAAAAgAAAAAAAIAAAAAgAAAAAAAEwr8" +
                    "PYARKSqt+ejLw+oxFDKZzWZybC2CAzwWRgRP/r/TMpY/HTzuf3Z43DPMyC7eve8UFh71LyjcY9smiHG85UlYM++XUgABeEhR" +
                    "KJW9AlFphai0oj49q+i//7eRQoEpFAAAIAAPNO++/kxIoIdU2hdzPuFCbBpCqJLblJ5VNCd0OjgHAAAQgAcfBoO28ol5mAAg" +
                    "hLh1PBAAABgv4A+dggDcT9Bp/d7LK6jc9e2xuz6lUkyMDNycOYsiQ7w9HPF49SXOA99sjzmXkJye39nd+86Wp7t7BPipX797" +
                    "90Js2rX4bH6P0NrKLDp8RnR4MIl093dfe3dPWwcfIbR2ZeSyJXN0NZugteoolMozF5PiU/L4fIGNtfmiiJDo8EENVaVSJaXn" +
                    "J6Xlc+t4IpGEzWa4OHEWRQQH+bmPag/Ba/k9wuNnbuTcKRdLpE4OtquXRxC/ZYVSeSUuM+1WUROvXSrtMzMzsrO28PNxCZvl" +
                    "a2ZiONQeLf4fe1mHBHpMhls+cfbm2UvJCCF7jtU3n76GXXUjKfenQxcRQmwW4/CPHwy9l1/2vKOlJhBPOSwj7QEQcZdORQyA" +
                    "AOiDVNaHtRmEEImEgvyH793kckVHV09HTk9mbskrG56ICAsY0sWgb/efyrpdNuzlPxw8l5Sej4XrGlsPHr2iUqkWRYZM0E2N" +
                    "ai1C6LsDZ9KzirFwfWPbL0cuN7d2rn8mGouRyeRf7D2RX1yNp+8ViPMKKvMKKpdGh65fvVDLrxO8tlcg+r//Hmxt78YOyyrr" +
                    "P/36j/A5AQTv8eCRy9cTc/HD9g5+ewc/v7i6oLh6x9vrxuJ/Pcp6st0yCZGI1xbtNUG/lNoh6C6dihgAAdCNr344OWjsT6du" +
                    "XLvE1YmDHfpPd8UfyVAqVd18wZXrmef/TlWp0JGYuLmzfKlUivrlAqG4idfx6YcvuDhxaFQKNvbBK3cTr/27z15nsxh7Dpwp" +
                    "Kq1BCF2OyxxHAdDV2l6BuKtb8P3nWw3YzJhzCbE3sxBCl66mzw7xxjxw7PR1rH2amRi+8fIKV2dOeWXD1z+eFImll65lTHO1" +
                    "D53hNZIxBK89fuYG1hWymPQ3X1np6T61oLh6789nCd5ycnoBQohMJn303gY3Z06vUNzc0nErp1QoEmukHNX/Yy/r34/HTqpb" +
                    "JpGJCsCoNUGPlKNCsIYQv19AHXiIRR9kMnlyen5Pr2gYh5JJ5mZGTz85H2//3Dre0GRbNi7zcHOgDe4sMDavf9zOxsLUxDBi" +
                    "YLjX2t6lUConpPiJWbvp+cdsppgbGbLXr15oYmyARaZkFGJD4JvJt7GY1csjvD0cGXTadC+nyLmBWOTV+Gwtkw8i1yqVqtRb" +
                    "RVhM1PwZgb5uLCY9JNAjPIzocJhOp2GDcV5rZ1sH39iQ7eU+dcOahVs2LhuL//Uo60l4yyRdKoyWmqB3Su3TU4K1S6ciBmAG" +
                    "oBvYU0AKhbKiuvGbfTFdfEFhac2Pv5778M1nsWoaezMrK7e0oblNKJIoFIM6i26+QNPpVIqrk92wP0SjUhztbbAwm83AR5p9" +
                    "fXIKg44d7vvqzTGu+ehkLY1Ksbebgltub2fF7xEihJp47Qihtg6+RCrDzu7//cL+3y9oXF5b3zKSJQSv7eYLxGIpFuM01RY/" +
                    "6+RgQ/CWw+f4X4hNU6lU+347jxCikMl2thbTPZ2XRodaWZrq5P8xlvUkvGXiaK8J+qXUDvHaNRH3CwIADIJCIXu42S+YH3Tq" +
                    "fCJCKDe/QirtYzBoX/1wMje/YqSrFAqFRgyLSSeRhh94MZl0/MxIaca+nKWTtWMEb8D3+Fp11q1awLG1TM8q4tbx+D1ChVJZ" +
                    "39hW39iWkVO8+5NXDQ1YxP0/jmU9SW5ZqVLhYVx17hdwdxG/XwAEYEyotRckFEkEQjHeI6x8Yt7SqFADA2avQPziti8nyICx" +
                    "PAXU0dmjq7V9ckVDU5s9xwob/9Y3tWHxtjYWCCErCxMmg461w62bnpo7y5e4MQSvNTUxZLEYWN/ErW2eM9MHix92vWVYSCRS" +
                    "RFgAtkMrkcq4tbzd+2L4PcLOrt7istqQQI+J896kvWXmwIRSKJLgV1VwG7Xkqb0m6JdyXNw1jkX8sAF7ADqgUChLK+qvJ+Zg" +
                    "hybGBmamhuovA7OYdDqd2tbefeDwxUk7idHD2oNHLre0dfUKRIf/vIrN5RFCc2ZOxyb44WH+WMwfJ69l55WJxFKxRNbQ1JaS" +
                    "Wfj1jzFXrmeOOPogdi2ZTHo02BtLFpeQnZtfIZHKsm6XxQ+sDo/Krm+OnjqfWFHdyO8R0qhUJpN+d0FMlxnP2Mt68tyyrbU5" +
                    "rmqpmYVisTQ+JS8ju1jvmqB3yrG7axyLGGYAwPDLJkNHHBvWLCKRSKYmhv4+LnmFVQihIzFxR2LiEEIL5gVOzhvRw1pDA5ax" +
                    "scHr7+9Vj1wSNdPNuf9xjrUrFzQ0tRUUc/k9wi/2/qlx+Ui7HTpd++yKyPzi6rb2brFE9r89x7EuMnxOwI2kXCJ33dzamVdY" +
                    "FXM+YegAM8DHdUK9N2lvOcjPfYqlKfag0Z4DZxBCDDotIixA/WFKXWuCHinHy13jVcQgAIA26HSqmanRNFf7RREheG1+69VV" +
                    "py8kZuSUdHf3mpsbR80LeiwqVEtD+mfR1VoSCb358orTdlYJqXndfIG1ldmiyJBFEcF4Agad9u/tzyVnFCSn53PreAKBmM1m" +
                    "mJsZOznYhAR6+nm7aDGG4LXGRuxdOzaeOHMjO69cIpU5OdisWREpEIgI9oY7312fnlWUm1/Ba+3s7hZQaRRrSzM/H5cnF89m" +
                    "sRgT6r1Je8s0GnXne+uPxMSVVdSJJFIPV4fnno6q5DaOpSbokXK83DWORfxQQSpP2q9+vHX751jg+90fgHcAACD+kU74nOek" +
                    "ZaSOHfYAAAAAHlJAAAAAAEAAAAAAgIcJ2AQGAEAbxL/SDN9zhhkAAAAAAAIAAAAAgAAAAAAAIAAAAAAACAAAAAAAAgAAAACA" +
                    "AAAAAAAgAAAAAAAIAAAAAAACAAAAAIAAAAAAACAAAAAAAAgAAAAAAAIAAAAAgAAAAAAAIADA/c3Pf1xa9eLHJ87eBFcAwFiA" +
                    "PwijM0WlNR99eVgjksWk20wxD/JzXxo9y8CACV66T2nv4Mcl5hSWcJtbOkViCZNBNzJkW1maeLpNDfJ3d55qO6G/npFdvHvf" +
                    "KSx86redUBw48OfmQQAmNWKJjFvH49bx4lPyPvnwhSmWpg+5Q7r5gk1v7cbCu3ZsdHd5ZJIbrFKhvy4nx5xLUCiVeKRQJBGK" +
                    "JLzWzoJiLreu+b2tq6GqQ50BAQD6eff1Z0ICPaTSvrSsov2/n1epUEdXz8Ejl//vrbXgnPuLY6evn/87FQv7+7g89ViYs6Mt" +
                    "Qqijs6e5pSPrdhm4CAABAIaBwaCFz/HPyC7Oza9ACN0pqpLK+hh0GnjmfqGiuhHv/R8N9n7zlZUkUv8pjq0lx9Zyhv808BIA" +
                    "AgCMiI21ORZQKlW9vSKGhQlCSKVSJaXnJ6Xlc+t4IpGEzWa4OHEWRQQH+bnjF6qvb/763bsXYtOuxWfze4TWVmbR4TOiw4Px" +
                    "zggh9Nq7e9o6+AihtSsjly2ZM5Ix1bXN73/8M0KISqUc+uF9TI0+/uqPwhIuQuiDN9ZgBpz/O/XoqesIIa9pUz9+f8PAYsjo" +
                    "NiuUyitxmWm3ipp47VJpn5mZkZ21hZ+PS9gsXzMTw51fHCouq8UT79j1KxYIC52+bfPySVh2V+IysACFTN6wZqG6w4clr6By" +
                    "17fH7rYiKsXEyMDNmbMoMsTbw3HYkj3wzfaYcwnJ6fmd3b3vbHk6JNCD3yM8fuZGzp1ysUTq5GC7ennESD/3T9UiXbNVKlXx" +
                    "KbdTMgpqG1pEIimTRXfgTJkd4rNgXhCFQtbuk0fsLOsb24jUGZVKpd0MAATgH4DX0okFyGSSkREbISSTyb/YeyK/uBpP0ysQ" +
                    "5xVU5hVULo0OXb964dBMfjh4Lik9HwvXNbYePHpFpVItigzR1RgnB1tjI3ZPr0guV5RV1Pt6O/fJFeVV9djZwtIarO/A9AAh" +
                    "5OftggUI2nzwyOXribl4mvYOfnsHP7+4uqC4esfb6+67sisqren321QbM1MjXS+XyxUdXT0dOT2ZuSWvbHgiIixgSA+Ovt1/" +
                    "Sn0dqVcg+r//Hmxt78YOyyrrP/36j/A5AUMz/wdrkU7ZSqV9n317rLj8rvALhZKS8rqS8rqE1Dv/eec5Fouh3SfjYgYAAnCv" +
                    "kcr60m4V3S6owDtTbMR97PR1rN2amRi+8fIKV2dOeWXD1z+eFImll65lTHO1D53hpZ5Pr0DcxGv/7rPX2SzGngNnsF7pclym" +
                    "HpWbREK+Xs4pmYUIocJSrq+3c0VVg0wm7+/vSrgIIYVCWVJR12+zT78AELQ5Ob0Ak7qP3tvg5szpFYqbWzpu5ZQKRWKE0Mfv" +
                    "b7iPNvSUSlV3jwALW1qY4vEJqXk//npePeVXH73s6GCDEPKf7oo/paNUqrr5givXM8//napSoSMxcXNn+VKpFPULBUJxE6/j" +
                    "0w9fcHHi0KgUhNCBwxex3p/FpL/5ykpP96kFxdV7fz471Lx/sBbplO3RU3FY789k0LdtXu7j6VReVb/npzMCobiS2/jb8dgt" +
                    "G5/U7hMidWaC7u6hBd4DGBNf/XBy1Ysfr3vls32/nVepEELIzNRo49ol2KjwZvJtLNnq5RHeHo4MOm26l1Pk3EAs8mp89tAM" +
                    "N69/3M7GwtTEMGJgMNja3qX+XApx8D4daySFpVyEEPYgY019i1AoqahukEr7sMk4Fk/cZjqdhg3ieK2dbR18Y0O2l/vUDWsW" +
                    "btm47KFrQmSSuZnR00/Ox/s1bh1vaLItG5d5uDlgPZ1SqUq9VYTFR82fEejrxmLSQwI9wodMHf7xWkQwW7lckZB6B4uMDp8R" +
                    "HDCNxaT7ebs8vnAWFpmSWSCRyrT4ZFzMAGAG8M/AYNBsp5gH+rkvjZ5lZMhCCLV18PEav//3C/t/v6BxSW19i0YMjUpxtLfB" +
                    "wmw2Ax9g9vXJKQw6drjvqzeJCsDAqk4Vt0kilRWV1CCE5s/2FwjFre3dRWU1dQ39Bkz3ciaRSDrZHD7H/0Jsmkql2vfbeYQQ" +
                    "hUy2s7WY7um8NDrU6n57BJZMJpkYG3bzBdhaFh4/f7b//Nn+NXW8dz86MLRfjr2ZlZVb2tDcJhRJFIpBvQ+W1aBmRqW4Otmp" +
                    "JxCLpQOLTrZqC3c2Ghf+47WIYLbqdrqo3SkelssVvJZOR7Ub1PDJuJgBHREIwD0FewxUv2uHDoiYTDq+l0Ua866WmamRPceq" +
                    "vrFNoVTeKayqqG5ACHl7OtbU824m3y4qramp52lIBXGb161awLG1TM8q4tbx+D1ChVJZ39hW39iWkVO8+5NXDQ1Y91c5ens4" +
                    "pmYWIoS4dc38HqGJscGoMz/sia9hUSgUGjEsJp00MduUE12LiGaLzX+xs4g0XDQaF59M0N2BAADjjJWFCZNBx9rn1k1PzZ3l" +
                    "Oy7ZEn9+A+vZsScrzl5O7pMrjI3Y9nZTvD0cbybfvl1QgY928cUi4jaTSKSIsABst1MilXFrebv3xfB7hJ1dvcVltSGBHvdX" +
                    "41wUEYIJgEKhPHoqTvtCVkdnD977r3xi3tKoUAMDZq9A/OK2Lwn+nKmJIYvFwCYB3NrmOTN9sPiha0eToRYRqu2WpgwGDVtR" +
                    "rKppmhXcvy3BrW3Gx/v4Y3JaKhX0G/d6+gsumChppVLCw/yx8B8nr2XnlYnEUrFE1tDUlpJZ+PWPMVeuZ060DfjQvrqmGRvn" +
                    "kkjIx8MJIdTc0tknVyCEOLaWFmbGutq865ujp84nVlQ38nuENCqVybw7+5YrFAghFpOBt+fSirpJvkTr4Wa/ZMFMLJyQeufr" +
                    "H2MquY19fXKxRFZV06SRGH+iERvG0unUtvbuA4cv6rTo9GiwNxaOS8jOza+QSGVZt8viB5b7J1UtIlrbZ/fbeS0+KzuvXCKV" +
                    "5RdXX7yahkXOmenDHG2J5v6qMzADAEZh7coFDU1tBcVcfo/wi71/apzVYwFUV7ymOdKoFKyjR6i/6zc3M7K1Nm8eeGgVH/7r" +
                    "ZHNza2deYVXM+YShI9YAH1eEEJ1O9XCzLymvQwgdiYk7EhM3vmPYcWfDmoVMJv2vyykqlSozpyQzp0TL+N3fxyWvsEr91hbM" +
                    "C9Tp555dEZlfXN3W3i2WyP635zimCuFzAm4k5U62WkSQdaui6hpai8trxRLZF3tPDDaS88Kzi0fN4b6rMyAAgDYYdNq/tz+X" +
                    "nFGQnJ7PreMJBGI2m2FuZuzkYBMS6El85V1v6HSqp/tU/BFy/B0lbw+nuwIw2AyCNu98d316VlFufgWvtbO7W0ClUawtzfx8" +
                    "XJ5cPBt/3HvrpuWHTsSWlNcKhGIta8GTBBKJtGZ5xPxH/a4l5hSVcFvbuyUSmYEB08TYwNLcxMfTydfLeerA9uNbr646fSEx" +
                    "I6eku7vX3Nw4al7QY1Gh6i9GjIqxEXvXjo0nztzABstODjZrVkQKBKKhAvCP1yKitZ1B2/ne+pvJt1MyC2rqeWKxlMVk2HOm" +
                    "zJ7pE6X2Iph27q868wBAKk/aP6gAtn+OBb7f/QF4B5ic/PzHpbiEnOVLw9aM/PYsAACjduywBwAAAPCQAgIAAAAAAgAAAAA8" +
                    "TMAmMHD/sfn5pZufXwp+AACYAQAAAAAgAAAAAAAIAAAAAAACAAAAAIAAAAAAACAAAAAAIAAAAAAACAAAAAAAAgAAAACAAAAA" +
                    "AAAgAAAAAAAIAAAAAAACAAAAAIAAAAAAAPcJ8DnoyU5Rac1HXx7Gwu++/kxIoAd+KvbGrV+P/Y2F9+zawrG1vH9vs6W16/TF" +
                    "pJLy2s7uXgszY1cnu/CwgOmeziQSVIFJyrWE7F/+uIwQMjJk/bb3PXAICAAA6ENNfcu/P/tNIpVhh7zWTl5rZ0pm4Vcfvezo" +
                    "YAP++Qfp5gs2vbUbC+/asdHd5RHwCQgAAIwn566kYL2/u8sj721dTSaTuHW8+OTbZDIsUQIACADwQNPZ1YMF7DlTTIwNEEK+" +
                    "Xs6+Xs7gGQAAAQB0Q6lUxafcTskoqG1oEYmkTBbdgTNldojPgnlBFEr/mPrE2ZtnLyUjhOw5Vt98+hoWeSMp96dDFxFCbBbj" +
                    "8I8fYJEKpfJKXGbaraImXrtU2mdmZmRnbeHn4xI2y9fMxBBLo1KpktLzk9LyuXU8kUjCZjNcnDiLIoKD/NyJGOzixCkpr0MI" +
                    "pd0qXPH4XCsLEz3uWqVC6dlFial3uLXNvUKxiZGBqzPn8YWzprnaE3cLwWTqy9+/7HnnzMWk+JQ8Pl9gY22+KCIkOnwGnhsR" +
                    "77327p62Dj5CaO3KyGVL5mi5RyK5jWPp7/ziUHFZLf7rO3b9igXCQqdv27x8sPNVF2LTrsVn83uE1lZm0eEzosODYf8GBAC4" +
                    "10ilfZ99e6y4/G67FQolJeV1JeV1Cal3/vPOcywWQz09CY3STA8euXw9MRc/bO/gt3fw84urC4qrd7y9DiEkk8m/2Hsiv7ga" +
                    "T9MrEOcVVOYVVC6NDl2/euGoNvt5OV+6mo4QEktkX/9w8qP3N7CYdJ3uuq9P/tUPJ28XVOIxHV09HTk9WbdLTx78D3G36Oo9" +
                    "hNB3B86kZxVj4frGtl+OXG5u7Vz/TDRB7+nEqLmNe+kT5IeD55LS87FwXWPrwaNXVCrVosgQaI8gAMC48dUPJ0dNc/RUHNb+" +
                    "mQz6ts3LfTydyqvq9/x0RiAUV3Ibfzseu2Xjk4O6APIoXUByegFCiEwmffTeBjdnTq9Q3NzScSunVCgSYwmOnb6O9f5mJoZv" +
                    "vLzC1ZlTXtnw9Y8nRWLppWsZ01ztQ2d4ack/ITXvwOFL+GF1bfOX3/+54621VCoFIfTmjh8bm9sRQod+eN+AzRwpk2Onb2C9" +
                    "P4mEViydGzk3kM1m1tTx/r6eqZNbdPVer0Dc1S34/vOtBmxmzLmE2JtZCKFLV9Nnh3i7OnGIeE8nRs1tfEv/4/c3ENkE7hWI" +
                    "m3jt3332OpvF2HPgTFFpDULoclwmCMDkBzbZHijkckVC6h0sHB0+IzhgGotJ9/N2eXzhLCwyJbMAf9hmYAw4CnQ6DVtg4bV2" +
                    "tnXwjQ3ZXu5TN6xZuGXjMuwXbybfxlKuXh7h7eHIoNOmezlFzg3EIq/GZ2vJvLSifv/vF+RyBYmEIsICSCQSQqiwhPv9L3+p" +
                    "VCqZTM5r6UQImZkaaen95XLFzeT+cXHYLN9nngq3tDBhsxhe06Zu3/I0cbfo4T2E0KbnH7OZYm5kyF6/eiG2gYEQSskoJOI9" +
                    "XRm1LMa99Amyef3jdjYWpiaGEXMCsJjW9i6FUglNEmYAwLih5T0AjLYOPt7CXZzs8Hg8LJcreC2dOj1bGT7H/0Jsmkql2vfb" +
                    "eYQQhUy2s7WY7um8NDrUytJU/Rf3/35h/+8XNC6vrW/RkvnluAylUoUQipwb9PL6pe4uj2DL0GlZRcbGBjZTzLBOJGC6q5ZM" +
                    "2jr4Ykm/Df4+rsMmIOIWBoOuq/doVIq93ZT+tkSl2NtZ8XuECKEmXjsR72Fp9n315riXxXiVPhFoVIqjfX+ebHb/EpNSqerr" +
                    "k1MYdGi2IADAvUKlUhvckYaL1kSpdk4slg5NsG7VAo6tZXpWEbeOx+8RKpTK+sa2+sa2jJzi3Z+8OqpFQ4fM6jQ193eUbs4c" +
                    "hFDk3EChSHIkJg6TN2wViERCiwkvJpDG4hbdvTcq2r1naMAax9wmovSJwGTS8f1eEuz8ggAA/xRWlqYMBk0q7UMIVdU0zQru" +
                    "X3zn1jbjo1Qba3OEEHNgaCYUSfDLK7iNw3SpJFJEWEBEWADWm3Nrebv3xfB7hJ1dvcVltYG+bsyBgfPWTU/NneWrk8GGhqyB" +
                    "taA67CeeWPRor0B87koKNmJFCC1ZEKp90GplYcJi0rFJQF5h1ZzQ6fq5hUqhEPQeTp9c0dDUZs+xwqytb2rD4m1tLIh4D5vP" +
                    "EX8KaNSymIjSh2b1AAN7AA+WnlMp4bP9sfC1+KzsvHKJVJZfXH3xahoWOWemD9b4bQc6so7OntTMQrFYGp+Sl5FdPDTPXd8c" +
                    "PXU+saK6kd8jpFGpTLXnc+QKBZVKCQ/r/8U/Tl7LzisTiaViiayhqS0ls/DrH2OuDGzDDkvYQGedkJp3+mISv0coFErsOVY0" +
                    "KgVP84id1eh3Hda/9JyUfufU+cSOrh6xWFpaUfe/PceJu4W499Q5eORyS1tXr0B0+M+r2PoPQmjOzOlEvKdr+Y5eFuNd+iwm" +
                    "A9eA0oo6WNaHGQAwqVm3KqquobW4vFYskX2x94T6KVcnzgvPLsbCQX7uUyxNW9u7EUJ7DpxBCDHotIiwAPWnDDGaWzvzCqti" +
                    "zicMHXcH+LgihNauXNDQ1FZQzOX3CL/Y+6dGMle1xeihRM4NLKmoS0rLV6nQyb/iT/4VPzTNz39cYrMYj4Z4a8ln7YoFjU3t" +
                    "d4qqVCoUcz4Bt5Y88JQLQbcQTHZ3BmPAMjY2eP39veqRS6JmYitaRLynE6PmNu6lT6dTPdzssbc0jsTEYatzekz1gMkJZesL" +
                    "S9WP/76W0l+JF84B70wG2tq78Uc7Zof4qH/xrZLbiD/5vigyxNiIjY125z3qZ2FmLJHKRGKJQqFgs5gujnZPLpm96bnHGHRa" +
                    "f8FTyMEBHp1dvUKhWIVUPh5Ob726EiGUnVeOEKLRqE891l8BggM8zM2MFAqlUqmUSfvodCrHxnLuo75bNi7DFnCoFMrcWb42" +
                    "1uYyWZ9YIpP3KQwNmLbWFgHTXZ9eFh4W6qv+mtXQFYaZgZ6O9jZisVQklsjlCmMjNsfGck7o9I1rl0ilsrqGVoRQVm6po4ON" +
                    "nc2IX7ujUMhhs3ztbC37+uRiiaxPLjczNfTzdnl5w+OW5ibE3UIwWVVNU+6dCoQQg0H7bMdLiERqbe+W9cltrS2eXjZ/1RPz" +
                    "cMNG9R5C6HJchkgsRQj5ejl7uDloqQyjl8V4lz5CyMfTua2ju6dX2Ncnx2JmBnlOtbe+6wQ67cnFs/slqqUjJbP/CajlS8Oo" +
                    "ajM54B9kpI6dVJ60X/146/bPscD3uz8ArwHAsMCHMIH7i5E6dtgDAAAAeEgBAQAAAAABAAAAAB4m4CkgANCZ6PkzoufPAD8A" +
                    "MAMAAAAAQAAAAAAAEAAAAAAABAAAAAAAAQAAAABAAAAAAAAQAAAAAAAEAAAAAAABAAAAAEAAAAAAABAAAAAAAAQAAAAAAAEA" +
                    "AAAAQAAAAAAAEAAAAABAC/fN3wPokys2vfm1UCTBDmcFe7396ipdM5mgP+UKfyF2cvpQLlecupCYequwvYOvUCgRQhvWLHws" +
                    "KvRhcBTUSV19+HB67L4RgOzbZXjvjx8asJn3zIBuvmDTW7ux8K4dG91dHoGGNMk5eS7+3JXUSWUS1CIABEAfEtPuaEwI0rOK" +
                    "FswLgiIERiLtVhEWWBodum5VFIUCC54AcB8KQE+vKK+gEgvbWps3t3QihBLT8kEAAC10dPZggWmu9g9b7w9/tBI89uAIQEpm" +
                    "gUKpRAixWYwXn12869tjCKHSirqWti5rKzM8mUKpvBKXmXarqInXLpX2mZkZ2Vlb+Pm4hM3yNTMxHDZnqbTv870nCku4CCFL" +
                    "C5N/vb2OY2s5NNnOLw4Vl9Xihzt2/YoFwkKnb9u8XD2lSqW6EJt2LT6b3yO0tjKLDp8RHR5MIg1KkJSen5SWz63jiUQSNpvh" +
                    "4sRZFBEc5Oc+qh+USlV8yu2UjILahhaRSMpk0R04U2aH+CyYF4R1cNW1ze9//DNCiEqlHPrhfQadhhD6+Ks/sBv84I012K+c" +
                    "/zv16KnrCCGvaVM/fn8DGrwA+ut37479FtQzPPDN9phzCcnp+Z3dve9seTok0EP7bSqUyjMXk+JT8vh8gY21+aKIkOjwGRpO" +
                    "1m7A3p/PJmcU4Ol37zvVH/j0VQfOlFHdOKr9+hWi9lqUV1CJVez+lkmlmBgZuDlzFkWGeHs4asl22Do86jL3qKXM7xEeP3Mj" +
                    "5065WCJ1crBdvTxCIBDhnjz1207tJai9JZ44e/PspWSEkD3H6ptPX8OuupGU+9Ohi1gzP/zjB0Nt/mXPO1oqBvGUwzLSHgCR" +
                    "staj5wEB0Gf9JzjAw9fbxcTYgN8jRAglpeWvenIenuzgkcvXE3Pxw/YOfnsHP7+4uqC4esfb64ZmKxZLP9tzrLSiHquIO95e" +
                    "Z2FmPEZTfzh4Lik9HwvXNbYePHpFpVItigzBYmQy+Rd7T+QXV+PpewXivILKvILKpdGh61cv1N7OP/v2WHH53R5EKJSUlNeV" +
                    "lNclpN75zzvPsVgMJwdbYyN2T69ILleUVdT7ejv3yRXlVfVY+sLSGqzWYp0FQsjP22Wib0GlQt/uP5V1u4y4D787cCY9qxgL" +
                    "1ze2/XLkcnNr5/pnosfuQ4Ju1G7/GA0giFyu6Ojq6cjpycwteWXDExFhAcMm07sOay/lXoFox65fW9q6sMOyyvpPv/4jfE4A" +
                    "QeOJt0QSIo1XxdAvpXYIlrWuPQ8IgA40NrdX1zRj4UdDvMlk0swgz2vx2QihxPQ76gKQnF6AECKTSR+9t8HNmdMrFDe3dNzK" +
                    "KRWKxEOzFQol//3mSCW3CVsi+PCNZw0MRtxS/vj9DUS273oF4iZe+3efvc5mMfYcOFNUWoMQuhyXiberY6evY5XJzMTwjZdX" +
                    "uDpzyisbvv7xpEgsvXQtY5qrfegMr5FsOHoqDuu2mAz6ts3LfTydyqvq9/x0RiAUV3Ibfzseu2XjkyQS8vVyTsksRAgVlnJ9" +
                    "vZ0rqhpkMjmWQ1EJFyGkUChLKur6BcDHZaJvQSAUN/E6Pv3wBRcnDo1KGbW4ewXirm7B959vNWAzY84lxN7MQghdupo+O8Tb" +
                    "1YlD0IBtm5dv27z8mZc+USpVCKHtr63Crfr16JVR3ajd/t+Px+pXiNprkf90V3xYrVSquvmCK9czz/+dqlKhIzFxc2f5Uod4" +
                    "T6c6rFMpHz9zA+v9GQzaG5tX+Hg43imq2vvLXwTbLPGWSCITFYBRK4YeKUeFYG3XqeeZVNwHC6MJqf3DfwM209fLGSE0K9gb" +
                    "i2lp7SqrrMdT0uk0bMjGa+1s6+AbG7K93KduWLNwy8ZlGnn29Sk++vIQ1nJm+Lv/553nCbacUdm8/nE7GwtTE8OIgeFSa3sX" +
                    "tn4llytuJt/GIlcvj/D2cGTQadO9nCLnBmKRV+OztYwHcT9Eh88IDpjGYtL9vF0eXzgLXyWTSGXqfTrWqgtLuQgh56m2CKGa" +
                    "+hahUFJR3SCV9mFTXSx+om9hy8ZlHm4ORHp/jE3PP2YzxdzIkL1+9UITY4P+G8woHKMPdXLjSPaP0QCizZJMMjczevrJ+bgI" +
                    "cet441uHtZSyUqlKHdg/j5oXFBwwjcVihM7wCp/tTzBz4i2RpItbtFQMvVNqry0Ey5r4/cIMQDdUKlVyRj6+/oMNgrynTcVX" +
                    "gRLT7kxztccShM/xvxCbplKp9v12HiFEIZPtbC2mezovjQ61sjRVz1YildXUtyCEXBzt3nn9GQp5fISQRqU42ttgYTabgY/m" +
                    "+vrkFAa9rYOPdy77f7+w//cLGpfX1reMlLP6tS5Odng8HpbLFbyWTkcHG3xVp4rbJJHKikpqEELzZ/sLhOLW9u6ispq6hv5f" +
                    "me7lTCKRJvoWqFSKq5rBRHxobzcFv9bezgor6CZeu4YfdPWhTm4cyf4xGqC9u4m9mZWVW9rQ3CYUSbAXF3C6+QKN9GOpw9pL" +
                    "uZsvEIul/Z5xHOylBEL5E2+J41Ux9EupHeJlPRH3CwKAEEKFJTX4sxwJqXkJqXkaCdKyil54djE2Olu3agHH1jI9q4hbx+P3" +
                    "CBVKZX1jW31jW0ZO8e5PXjU0YN0dd5BIKpUKIVRV0/R3XObSgQHgGGEy6XiPOrRvHZWhY89BS9HDLZuqRfdjZmpkz7Gqb2xT" +
                    "KJV3CqsqqhsQQt6ejjX1vJvJt4tKa2rqeVo2AMb9FlhMuh756I02H+riRr3tH8WAkfnqh5O5+RUjnVUoFJpj5zHUYeKlrF/Z" +
                    "EW+JSjXX46pzv4CXNfH7BQHQjaT0O9oTCIWSnDvloUGeWGWNCAvAtsskUhm3lrd7Xwy/R9jZ1VtcVqv+8ImhAXPlE/N+Px6L" +
                    "EDp88ppSpXpi0aPaf2jsvZiVhQmTQccqzdZNT82d5avDtZamDAYNW7qpqmmaFdy/ysytbcZHOjbW5njPXt/YhhA6ezm5T64w" +
                    "NmLb203x9nC8mXz7dkFFewd/pA2ACb0FgvTJFQ1NbfYcK2xQXN/UhsXb2liM3QCd3DgRHhipFnV09uC9/8on5i2NCjUwYPYK" +
                    "xC9u+3KkrPSowwQxMzVksxgisRQhVFPPmz3TB59TEr9N7S2RyaD3t1+1tzsruI16Vwz9Uo5XbSfe80w2JvUegFTWl5FdgoVf" +
                    "Wrfk1G871f8F+rphpxIHVnV3fXP01PnEiupGfo+QRqUymfS78+shA6glC2Y+93QUFj4SE3fuSop2Y1hMBt56SyvqsNVS3cSW" +
                    "SgkP619F/ePktey8MpFYKpbIGpraUjILv/4x5sr1TG3XDqzAXovPys4rl0hl+cXVF6+mYZFzZvrgjQof2mOb594ejiQS8vFw" +
                    "Qgg1t3T2yRUIIY6tpR6PPI3lFohz8MjllrauXoHo8J9Xsck7QmjOzOljN0AnN06EB0aqReqvKbCYdDqd2tbefeDwRe2O0rUO" +
                    "E+++Hw3p7/TjEnLyCirFEllGdnH8kPn3SIzaEm0HVLajsyc1s1Aslsan5GVkF+tdMfROOS5lrVPPAzMAomTmlOCTLLy7xwn0" +
                    "dcMGTbcLKnoFIiNDdnNrZ15hVcz5hKFKHuDjOjT/JxY9KuuTn/wrHiF07PQNpVK1fGnYSMbQ6VQPN/uS8jqssR2JidNjDLh2" +
                    "5YKGpraCYi6/R/jF3j81zmpfK1+3KqquobW4vFYskX2x98TgCzkvPLsYP/Sa5kijUrCOHqH+rt/czAh/h06/4f/Yb4EIhgYs" +
                    "Y2OD19/fO6ini5rp5swZFwOIu3EiPKClFvn7uOQVVqnHL5gXOKoxOtVh4jy7IjK/qKq1vVsokmBvJ5DJpPA5ATeScolcPmpL" +
                    "DPJzn2Jp2trejRDac+AMQohBp0WEBag/TKlrxdAj5XiVta49DwgAsfWftP7t30fsrIbupQQMSIJCoUzJKFy8IGTnu+vTs4py" +
                    "8yt4rZ3d3QIqjWJtaebn4/Lk4tkaD3fjrHx8bl+fHHsn5cTZm0qlcuUT80ayZ+um5YdOxJaU1wqEYi2rxlpg0Gn/3v5cckZB" +
                    "cno+t44nEIjZbIa5mbGTg01IoOewi/J3r2XQdr63/mby7ZTMgpp6nlgsZTEZ9pwps2f6RKm9wYT1Mp7uU/GHl/HXiLw9nO4K" +
                    "gLeeAjCWWyA2/ERvvrzitJ1VQmpeN19gbWW2KDJkUUTweBlA3I0T5IGRatFbr646fSExI6eku7vX3Nw4al7QY1GhWjpE/eow" +
                    "QYwMWZ/966XjZ25k55WJJTInB5s1KyIbGluxs+wRWhPOqC2RRqPufG/9kZi4soo6kUTq4erw3NNRldxGLfc7asXQI+V4lbUe" +
                    "Pc8kgVSetH9Q7dz+ORb4fvcHCAAAYICvfjh5K7cUIeTp7vDJBy/cmx8l/pFO+ACqtmHHCB07FVwDAMBQ/rfneHCAh/e0qZYW" +
                    "Jl18QVxCDtb7I4Siw4PBPw8GIAAAAAxDfWPrsI+lLl04a87Ac0EACAAAAA8gO95edy0hu7CY29LepVKpzEyM3F0eWTA/yMt9" +
                    "KjgHBAAAgAcZjq3lC2sW/eNmEP9KM3wBWw/gT2QAAACAAAAAAAAgAAAAAAAIAAAAAAACAAAAAIAAAAAAACAAAAAAAAgAAAAA" +
                    "AAIAAAAAgAAAAAAAIAAAAAAACAAAAAAAAgAAAACAAAAAAAAgAAAAAAAIAAAAAAACAAAAAIAAAAAAACAAAAAAAAgAAAAAAAIA" +
                    "AAAAgAAAAAAAIAAAAAAACAAAAAAIAAAAAAACAAAAAIAAAAAAACAAAAAAAAgAAAAAAAIAAAAAgAAAAAAAIAAAAAAACAAAAAAA" +
                    "AgAAAACAAAAAAAAgAAAAAMA/BXWkE1u3fw7eAQAAgBkAAAAAAAIAAAAAPBCQypP2gxcAAABgBgAAAACAAAAAAAAgAAAAAAAI" +
                    "AAAAAPCA8P8PsCSR5H4qVgAAAABJRU5ErkJggg==",
            };

        private static byte[] SignPng(string image)
        {
            byte[] png;
            string base64;
            if (image == null)
                return null;
            if (SignPngs.TryGetValue(image, out png))
                return png;
            if (!SignImages.TryGetValue(image, out base64) && !ExperienceSignImages.TryGetValue(image, out base64))
                return null;
            png = Convert.FromBase64String(base64);
            SignPngs[image] = png;
            return png;
        }

        private static bool HasSignImage(string image) => image != null &&
            (SignImages.ContainsKey(image) || ExperienceSignImages.ContainsKey(image));

        // Signage.UpdateSign's checks and storage for texture 0 without a player, then the lock: Locked lets only
        // OwnerID and admins repaint (Signage.CanUpdateSign), and BlockUnlocking makes the UnLockSign RPC refuse
        // everyone, admins too. Returns null once painted, otherwise the reason it was not.
        private static string PaintSign(Signage sign, byte[] png, ulong owner)
        {
            if (sign == null || sign.IsDestroyed)
                return "the sign is gone";
            if (sign.net == null)
                return "the sign is not spawned";
            // Server prefabs keep the paintableSources array but strip its client-only elements, so the length still
            // counts the textures while Signage.TextureSize throws.
            if (sign.paintableSources == null || sign.paintableSources.Length == 0)
                return "the sign has no texture 0";
            int maxWidth = sign.overrideMaxImageWidth > 0 ? sign.overrideMaxImageWidth : 1024;
            int maxHeight = sign.overrideMaxImageHeight > 0 ? sign.overrideMaxImageHeight : 1024;
            if (!ImageProcessing.IsValidPNG(png, maxWidth, maxHeight))
                return $"IsValidPNG refuses the image at {maxWidth} x {maxHeight}: " +
                       DescribePng(png, maxWidth, maxHeight);
            byte[] tagged = SignTag(png, sign.net.ID.Value);
            if (tagged == null)
                return "the image ends inside its IHDR chunk";
            if (!ImageProcessing.IsValidPNG(tagged, maxWidth, maxHeight))
                return $"IsValidPNG refuses the tagged image at {maxWidth} x {maxHeight}: " +
                       DescribePng(tagged, maxWidth, maxHeight);
            sign.EnsureInitialized();
            uint old = sign.textureIDs[0];
            if (old != 0)
                FileStorage.server.RemoveExact(old, FileStorage.Type.png, sign.net.ID, 0u);
            sign.textureIDs[0] = FileStorage.server.Store(tagged, FileStorage.Type.png, sign.net.ID, 0u);
            LockSign(sign, owner);
            return null;
        }

        // Compares the stored bytes, tag included, with what PaintSign would store now. A row missing from
        // FileStorage's in-memory cache is read from sv.files.<protocol>.db on the calling thread.
        private static bool SignPainted(Signage sign, byte[] png)
        {
            if (sign == null || sign.IsDestroyed || sign.net == null || sign.textureIDs == null ||
                sign.textureIDs.Length == 0 || sign.textureIDs[0] == 0)
                return false;
            byte[] expected = SignTag(png, sign.net.ID.Value);
            byte[] stored = FileStorage.server.Get(sign.textureIDs[0], FileStorage.Type.png, sign.net.ID, 0u);
            return expected != null && stored != null && stored.AsSpan().SequenceEqual(expected);
        }

        private static bool SignLocked(Signage sign, ulong owner) =>
            sign != null && !sign.IsDestroyed && sign.IsLocked() && sign.HasFlag(Signage.BlockUnlocking) &&
            sign.OwnerID == owner;

        // png with the tag of texture 0 of the sign with this net id right after IHDR, in place of a tag an earlier
        // call left there; null when png ends inside IHDR.
        private static byte[] SignTag(byte[] png, ulong netId)
        {
            if (png == null || png.Length < PngIhdrEnd)
                return null;
            int rest = PngIhdrEnd + OldSignTagLength(png);
            byte[] text = Encoding.ASCII.GetBytes(SignTagPrefix + netId.ToString(CultureInfo.InvariantCulture) + "/0");
            var tagged = new byte[PngIhdrEnd + 12 + text.Length + png.Length - rest];
            Buffer.BlockCopy(png, 0, tagged, 0, PngIhdrEnd);
            WriteBigEndian(tagged, PngIhdrEnd, (uint)text.Length);
            Encoding.ASCII.GetBytes("tEXt", 0, 4, tagged, PngIhdrEnd + 4);
            Buffer.BlockCopy(text, 0, tagged, PngIhdrEnd + 8, text.Length);
            WriteBigEndian(tagged, PngIhdrEnd + 8 + text.Length, PngCrc32(tagged, PngIhdrEnd + 4, 4 + text.Length));
            Buffer.BlockCopy(png, rest, tagged, PngIhdrEnd + 12 + text.Length, png.Length - rest);
            return tagged;
        }

        // Length of the chunk after IHDR when an earlier SignTag put it there, otherwise 0.
        private static int OldSignTagLength(byte[] png)
        {
            const int at = PngIhdrEnd;
            if (png.Length < at + 12 + SignTagPrefix.Length || png[at + 4] != 't' || png[at + 5] != 'E' ||
                png[at + 6] != 'X' || png[at + 7] != 't')
                return 0;
            uint length = ReadBigEndian(png, at);
            if (length < SignTagPrefix.Length || at + 12L + length > png.Length)
                return 0;
            for (int i = 0; i < SignTagPrefix.Length; i++)
                if (png[at + 8 + i] != SignTagPrefix[i])
                    return 0;
            return 12 + (int)length;
        }

        // The first ImageProcessing.IsValidPNG check that png fails at these limits.
        private static string DescribePng(byte[] png, int maxWidth, int maxHeight)
        {
            if (png == null)
                return "no image";
            long maxBytes = 29L + 4L * maxWidth * maxHeight;
            if (png.Length < 29 || png.Length > maxBytes)
                return $"{png.Length} bytes, outside 29..{maxBytes}";
            for (int i = 0; i < PngSignature.Length; i++)
                if (png[i] != PngSignature[i])
                    return "no PNG signature";
            if (ReadBigEndian(png, 8) != 13 || png[12] != 'I' || png[13] != 'H' || png[14] != 'D' || png[15] != 'R')
                return "the first chunk is not a 13-byte IHDR";
            // IsValidPNG reads these as signed ints; above int.MaxValue both readings fail.
            uint width = ReadBigEndian(png, 16);
            uint height = ReadBigEndian(png, 20);
            if (width < 1 || width > maxWidth || height < 1 || height > maxHeight)
                return $"{width} x {height} pixels";
            if (png[24] != 8 && png[24] != 16)
                return $"bit depth {png[24]}, not 8 or 16";
            if (png[25] != 2 && png[25] != 6)
                return $"color type {png[25]}, not 2 (RGB) or 6 (RGBA)";
            if (png[26] != 0 || png[27] != 0)
                return $"compression method {png[26]} and filter method {png[27]}, not 0";
            return png[28] != 0 ? $"interlace method {png[28]}, not 0" : "a check DescribePng does not know";
        }

        private static uint PngCrc32(byte[] data, int offset, int count)
        {
            uint crc = 0xFFFFFFFFu;
            for (int i = offset; i < offset + count; i++)
                crc = PngCrcTable[(crc ^ data[i]) & 0xFF] ^ (crc >> 8);
            return crc ^ 0xFFFFFFFFu;
        }

        private static uint[] MakePngCrcTable()
        {
            var table = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                uint c = n;
                for (int k = 0; k < 8; k++)
                    c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                table[n] = c;
            }
            return table;
        }

        private static uint ReadBigEndian(byte[] data, int at) =>
            (uint)data[at] << 24 | (uint)data[at + 1] << 16 | (uint)data[at + 2] << 8 | data[at + 3];

        private static void WriteBigEndian(byte[] data, int at, uint value)
        {
            data[at] = (byte)(value >> 24);
            data[at + 1] = (byte)(value >> 16);
            data[at + 2] = (byte)(value >> 8);
            data[at + 3] = (byte)value;
        }

        private static void LockSign(Signage sign, ulong owner)
        {
            sign.OwnerID = owner;
            sign.SetFlagLocal(BaseEntity.Flags.Locked, true);
            sign.SetFlagLocal(Signage.BlockUnlocking, true);
            sign.SendNetworkUpdate();
        }

        #endregion

        #region Status

        private static readonly string[] EventKinds =
        {
            "stale", "topup", "cull", "birth", "adopt", "stray", "released", "reseed", "breeding", "death", "needs",
            "destroyed", "full", "placement", "repair", "owner", "failure", "build", "upgrade", "care", "bees",
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
            StatusSection(sb, "record", () => pass = BeginPass(false));
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
                    int over = CapCount(herd) - CapOf(species);
                    if (over > 0)
                    {
                        string reason;
                        string blocked;
                        bool due;
                        LivestockAnimal next = PickCull(herd, CapOf(species), a => !_data.CapExempt.Contains(IdOf(a)),
                            out reason, out due);
                        sb.AppendLine($"  over its cap by {over}: " + (next == null
                            ? reason
                            : _cullBlocked.TryGetValue(species, out blocked)
                                ? blocked
                                : $"the next pass culls {Describe(next)} ({reason})"));
                    }
                    string hold = BreedingHold(species, herd);
                    if (hold != null)
                        sb.AppendLine($"  mating held: {hold}");
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
                    sb.AppendLine($"  {link}: {WireStatus(link, pass)}");
            });
            StatusSection(sb, "power", () => AppendPower(sb, pass));
            StatusSection(sb, "lighting", () => sb.AppendLine("lighting: " + FarmLightingSummary(pass)));
            StatusSection(sb, "care", () => sb.AppendLine("care: " + AssistedCareSummary(pass)));
            StatusSection(sb, "bees", () => AppendBeeStatus(sb, pass));
            StatusSection(sb, "buildings", () => AppendBuildings(sb, pass));
            StatusSection(sb, "parts", () => AppendParts(sb, pass));
            StatusSection(sb, "upgrade", () => sb.AppendLine(UpgradeSummary(pass)));
            StatusSection(sb, "storage", () =>
            {
                sb.AppendLine("storage:");
                foreach (KeyValuePair<string, List<BaseEntity>> role in pass.Roles)
                    foreach (BaseEntity entity in role.Value)
                    {
                        ItemContainer inventory = InventoryOf(entity);
                        if (inventory == null || entity is PlanterBox)
                            continue;
                        string retired = pass.Retired.Contains(IdOf(entity)) ? " retired" : string.Empty;
                        string line = $"  {pass.Name(entity)} [{IdOf(entity)}]{retired}: {Contents(inventory)}";
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
                    .Where(r => (r.Missing || (r.Pending && r.NetId == 0)) &&
                                entries.TryGetValue(r.Id, out layoutEntry) && !NeverPlaced(layoutEntry, records))
                    .ToList();
                string hold = AutoRepairHold();
                if (hold != null)
                    sb.AppendLine(hold);
                if (missing.Count > 0)
                    sb.AppendLine("missing structures (" + (AutoRepairRuns
                                      ? "auto-repair re-places each on the next pass unless a wait is shown"
                                      : "farm.repair respawns them") + "): " +
                                  string.Join(", ", missing.Select(r => $"{r.Id} ({r.Role}{RepairWait(r.Id)})")));
                List<StructureRecord> orphans = _data.Structures.Where(r => r.Missing && !entries.ContainsKey(r.Id))
                    .ToList();
                if (orphans.Count > 0)
                    sb.AppendLine("missing structures no longer in the layout (they stay gone): " +
                                  string.Join(", ", orphans.Select(r => $"{r.Id} ({r.Role})")));
                List<StructureRecord> retired = _data.Structures.Where(r => r.Retired && !r.Missing).ToList();
                if (retired.Count > 0)
                    sb.AppendLine("retired by farm.upgrade but still standing (the next farm.upgrade confirm tries " +
                                  "again): " + string.Join(", ", retired.Select(r => $"{r.Id} ({r.Role})")));
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
                        StructureRecord under;
                        string why = !records.TryGetValue(e.Id, out rec)
                            ? "added to the layout after the build"
                            : rec.Role == RolePlaceholder
                                ? "an old placeholder record"
                                : Unplaced(rec)
                                    ? $"not placed yet{RepairWait(e.Id)}"
                                    : !string.IsNullOrEmpty(e.On) &&
                                      (!records.TryGetValue(e.On, out under) || Unplaced(under))
                                        ? $"stands on {e.On}, which the farm never placed"
                                        : $"its parent {e.Parent} was never placed";
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

        // Windmills, batteries, and fridges; solar panels and the pump show their state through the wiring and crops
        // lines.
        private static void AppendPower(StringBuilder sb, UpkeepPass pass)
        {
            var parts = new List<string>();
            foreach (ElectricWindmill windmill in pass.Role(RoleWindmill).OfType<ElectricWindmill>())
                parts.Add($"{pass.Name(windmill)} [{IdOf(windmill)}] output {windmill.currentEnergy} of " +
                          $"{windmill.maxPowerGeneration}");
            foreach (ElectricBattery battery in pass.Role(RoleBattery).OfType<ElectricBattery>())
                parts.Add($"{pass.Name(battery)} [{IdOf(battery)}] charge {battery.rustWattSeconds:0} of " +
                          $"{battery.maxCapactiySeconds:0} rW s " +
                          $"({100f * battery.rustWattSeconds / Mathf.Max(1f, battery.maxCapactiySeconds):0}%)");
            foreach (Fridge fridge in pass.Role(RoleFridge).OfType<Fridge>())
                parts.Add($"{pass.Name(fridge)} [{IdOf(fridge)}] {(fridge.IsPowered() ? "powered" : "unpowered")}, " +
                          $"{fridge.currentEnergy} of {fridge.ConsumptionAmount()} rW");
            if (parts.Count > 0)
                sb.AppendLine("power: " + string.Join("; ", parts));
        }

        // Each layout Building: its base height T, blocks, inserts, and other parts standing, the socket links among
        // its blocks and inserts, and blocks whose grade or skin differs from the farm's or the layout's.
        private void AppendBuildings(StringBuilder sb, UpkeepPass pass)
        {
            Dictionary<string, LayoutEntry> byId = EntriesById();
            List<string> buildings = _config.Layout.Entries.Select(e => BuildingOf(e, byId)).Where(b => b != null)
                .Distinct().ToList();
            if (buildings.Count == 0)
                return;
            sb.AppendLine("buildings:");
            foreach (string building in buildings)
            {
                List<LayoutEntry> members = _config.Layout.Entries.Where(e => BuildingOf(e, byId) == building).ToList();
                List<LayoutEntry> blocks = members.Where(IsBuildingBlock).ToList();
                List<LayoutEntry> inserts = members.Where(e => e.Role == RoleDoor || e.Role == RoleInsert).ToList();
                float t;
                string height = _data.Buildings.TryGetValue(building, out t) ? $"T {t:0.00} m" : "T not set yet";
                int links = members.Select(e => pass.ById(e.Id)).Where(e => e != null).Sum(LinkCount);
                var drift = new List<string>();
                foreach (LayoutEntry entry in blocks)
                {
                    var block = pass.ById(entry.Id) as BuildingBlock;
                    StructureRecord rec = _data.Structures.FirstOrDefault(r => r.Id == entry.Id);
                    BuildingGrade.Enum grade;
                    ulong skin;
                    if (block == null || rec == null || !TargetGrade(entry, out grade, out skin))
                        continue;
                    if (rec.Grade != null && (block.grade.ToString() != rec.Grade || block.skinID != (rec.Skin ?? 0UL)))
                        drift.Add($"{entry.Id} {block.grade} {block.skinID} (kept at {rec.Grade} {rec.Skin ?? 0UL})");
                    else if (block.grade != grade || (block.skinID != skin && HasGradeSkin(block, grade, skin)))
                        drift.Add($"{entry.Id} {block.grade} {block.skinID} (layout {grade} {skin}; farm.upgrade " +
                                  "applies it)");
                }
                sb.AppendLine($"  {building}: {height}, blocks {StandingCount(blocks, pass)}, inserts " +
                              $"{StandingCount(inserts, pass)}, other parts " +
                              $"{StandingCount(members.Except(blocks).Except(inserts).ToList(), pass)}, {links} " +
                              "socket links, grade and skin " +
                              (drift.Count == 0 ? "as laid out" : $"differ on {string.Join(", ", drift)}"));
            }
        }

        // Inserts (doors open or closed), decor, beds, static planters, and signs (painted and locked or not).
        private void AppendParts(StringBuilder sb, UpkeepPass pass)
        {
            var lines = new List<string>();
            List<LayoutEntry> inserts = _config.Layout.Entries
                .Where(e => e.Role == RoleDoor || e.Role == RoleInsert).ToList();
            if (inserts.Count > 0)
                lines.Add("inserts: " + string.Join(", ", inserts.Select(e =>
                {
                    BaseEntity entity = pass.ById(e.Id);
                    var door = entity as Door;
                    return entity == null ? $"{e.Id} MISSING" : door != null && door.IsOpen() ? $"{e.Id} open" : e.Id;
                })));
            foreach (string role in new[] { RoleDecor, RoleBed, RolePlanterStatic })
            {
                List<LayoutEntry> entries = _config.Layout.Entries.Where(e => e.Role == role).ToList();
                if (entries.Count > 0)
                    lines.Add($"{role}: {StandingCount(entries, pass)}");
            }
            List<LayoutEntry> signs = _config.Layout.Entries.Where(e => e.Role == RoleSign).ToList();
            if (signs.Count > 0)
                lines.Add("signs: " + string.Join(", ", signs.Select(e =>
                {
                    var sign = pass.ById(e.Id) as Signage;
                    if (sign == null)
                        return $"{e.Id} MISSING";
                    byte[] png = e.Image == null ? null : SignPng(e.Image);
                    if (png == null)
                        return $"{e.Id} blank";
                    return $"{e.Id} " + (SignPainted(sign, png) ? "painted" : "NOT painted") +
                           (SignLocked(sign, pass.Owner) ? " and locked" : ", NOT locked");
                })));
            foreach (string line in lines)
                sb.AppendLine(line);
        }

        private static string StandingCount(List<LayoutEntry> entries, UpkeepPass pass)
        {
            List<string> missing = entries.Where(e => pass.ById(e.Id) == null).Select(e => e.Id).ToList();
            string text = $"{entries.Count - missing.Count}/{entries.Count}";
            if (missing.Count == 0)
                return text;
            return text + " (missing " + string.Join(", ", missing.Take(12)) +
                   (missing.Count > 12 ? $" and {missing.Count - 12} more" : string.Empty) + ")";
        }

        // Socket links counted from this entity's side.
        private static int LinkCount(BaseEntity entity) =>
            entity.GetEntityLinks(false).Sum(link => link.connections.Count);

        [ConsoleCommand("farm.links")]
        private void LinksCommand(ConsoleSystem.Arg arg)
        {
            if (!Authorized(arg) || Refused(arg))
                return;
            string building = arg.GetString(0);
            if (!_data.Built || string.IsNullOrEmpty(building))
            {
                arg.ReplyWith(_data.Built ? "usage: farm.links <building>" : "no farm built");
                return;
            }
            arg.ReplyWith(Links(building));
        }

        // One JSON object per piece of the building, its socket links in the form of research/farm/v2/construction/
        // data/sockets-grid-check.json: "<own socket after /sockets/> ~ [<other entry Id>] <other socket>".
        private string Links(string building)
        {
            UpkeepPass pass = BeginPass();
            Dictionary<string, LayoutEntry> byId = EntriesById();
            List<LayoutEntry> members = Ordered(_config.Layout.Entries)
                .Where(e => BuildingOf(e, byId) == building)
                .ToList();
            if (members.Count == 0)
                return $"no layout entry is in building {building}";
            var rows = new List<string>();
            int standing = 0;
            int links = 0;
            foreach (LayoutEntry entry in members)
            {
                BaseEntity entity = pass.ById(entry.Id);
                if (entity == null)
                {
                    rows.Add(JsonConvert.SerializeObject(new Dictionary<string, object>
                    {
                        ["id"] = entry.Id,
                        ["missing"] = true,
                    }));
                    continue;
                }
                standing++;
                var found = new List<string>();
                foreach (EntityLink link in entity.GetEntityLinks(false))
                    foreach (EntityLink other in link.connections)
                    {
                        string label = pass.Name(other.owner);
                        if (label == null || !pass.Names.ContainsKey(IdOf(other.owner)))
                            label = $"{other.owner?.ShortPrefabName} {IdOf(other.owner)}";
                        found.Add($"{SocketTail(link.socket?.socketName)} ~ [{label}] {other.socket?.socketName}");
                    }
                found.Sort(StringComparer.Ordinal);
                links += found.Count;
                var stability = entity as StabilityEntity;
                var block = entity as BuildingBlock;
                var combat = entity as BaseCombatEntity;
                var row = new Dictionary<string, object>
                {
                    ["id"] = entry.Id,
                    ["netid"] = IdOf(entity),
                    ["prefab"] = entity.ShortPrefabName,
                    ["stability"] = stability != null ? (object)Math.Round(stability.cachedStability, 4) : null,
                };
                if (block != null)
                {
                    row["grade"] = block.grade.ToString();
                    row["skin"] = block.skinID;
                }
                if (combat != null)
                    row["health"] = $"{combat.Health():0}/{combat.MaxHealth():0}";
                row["links"] = found;
                rows.Add(JsonConvert.SerializeObject(row));
            }
            float t;
            string height = _data.Buildings.TryGetValue(building, out t) ? $"T {t:0.00} m (stored)" : "T not set yet";
            return $"building {building}: {height}, {standing}/{members.Count} pieces standing, {links} links\n" +
                   string.Join("\n", rows);
        }

        private static string SocketTail(string socketName)
        {
            if (socketName == null)
                return "-";
            int i = socketName.IndexOf("/sockets/", StringComparison.Ordinal);
            return i >= 0 ? socketName.Substring(i + 9) : socketName;
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
