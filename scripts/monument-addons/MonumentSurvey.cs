// Maintenance tool, not a shipped plugin: copy this file into server/carbon/plugins/ only for the duration of an
// active survey (re-running placements after a map wipe, or for a monument Default.json does not cover yet), then
// remove it again. Carbon hot-loads every .cs it finds there, and this adds unauthenticated survey.* console
// commands, so it must not sit in server/carbon/plugins/ during normal operation. See docs/monument-survey.md for
// the console command reference (survey.dump, survey.solve, survey.labdump, survey.labsolve, survey.labscan,
// survey.labprobe) and scripts/monument-addons/make-profile.sh for turning survey.solve's placements.json into a
// Monument Addons profile.
//
// Compile-check a change against the local install the way Carbon compiles the shipped plugins:
//   PLUGINS_DIR=scripts/monument-addons MANAGED_DIR=server/RustDedicated_Data/Managed \
//     CARBON_DIR=server/carbon/managed scripts/check-mods.sh
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("Monument Survey", "local", "1.0.0")]
    [Description("Dumps monument/recycler layout and searches recycler placements for monument types that have none")]
    public class MonumentSurvey : RustPlugin
    {
        // recycler_static is the vanilla monument recycler; the yellow/red variants share the same Recycler
        // component and collider layout, so measuring this one prefab is enough for the clearance checks below.
        private const string RecyclerPrefab = "assets/bundled/prefabs/static/recycler_static.prefab";
        private const float NearestFallbackMaxDistance = 200f;

        private int _groundMask;
        private int _clearanceMask;
        private int _roadRailTopologyMask;
        private Footprint? _footprint;

        private struct Footprint
        {
            public float HalfX;
            public float HalfZ;
            public float BottomY;
            public float TopY;
        }

        private class RecyclerAssignment
        {
            public Recycler Entity;
            public MonumentInfo Owner;
            public string AssignedBy;
        }

        private class HitPoint
        {
            public Vector2 Offset;
            public RaycastHit Hit;
            public bool IsIndoorAnchor;
        }

        private class Candidate
        {
            public float LocalX;
            public float LocalY;
            public float LocalZ;
            public float LocalYaw;
            public Vector3 WorldPoint;
            public float NormalY;
            public bool OnRoadRail;
            public bool Outdoor;
            public bool Clear;
            public string Blockers;
            public bool FrontClear;
            public float DistanceFromCenter;
            public float Score;
            public float Margin;
        }

        private class VerifyResult
        {
            public bool AllOk;
            public int OkCount;
            public List<string> PerInstance = new List<string>();
        }

        private void OnServerInitialized()
        {
            _groundMask = LayerMask.GetMask("Terrain", "World", "Construction");
            _clearanceMask = SafeLayerMask("Terrain", "World", "Construction", "Deployed", "Debris", "Resource", "Default");
            _roadRailTopologyMask = ComputeRoadRailTopologyMask();
            Puts($"ground mask {_groundMask}, clearance mask {_clearanceMask}, road/rail topology mask {_roadRailTopologyMask}");
        }

        private static int SafeLayerMask(params string[] names)
        {
            int mask = 0;
            foreach (string name in names)
            {
                int layer = LayerMask.NameToLayer(name);
                if (layer >= 0)
                    mask |= 1 << layer;
            }
            return mask;
        }

        // TerrainTopology.Enum ships in Assembly-CSharp but was not present in the local decompile dump, so its
        // Road/Rail members are resolved by reflection instead of a compile-time reference: if Facepunch ever
        // renames them this just disables the road/rail scoring term instead of failing the whole plugin to compile.
        private int ComputeRoadRailTopologyMask()
        {
            try
            {
                Type enumType = null;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    Type[] types;
                    try
                    {
                        types = asm.GetTypes();
                    }
                    catch (ReflectionTypeLoadException loadEx)
                    {
                        types = loadEx.Types.Where(t => t != null).ToArray();
                    }
                    catch
                    {
                        continue;
                    }
                    foreach (var candidate in types)
                    {
                        if (!candidate.IsEnum)
                            continue;
                        bool nested = candidate.Name == "Enum" && candidate.DeclaringType != null && candidate.DeclaringType.Name == "TerrainTopology";
                        bool namespaced = candidate.Name == "Enum" && candidate.Namespace == "TerrainTopology";
                        if (nested || namespaced)
                        {
                            enumType = candidate;
                            break;
                        }
                    }
                    if (enumType != null)
                        break;
                }
                if (enumType == null)
                {
                    PrintWarning("TerrainTopology.Enum not found by reflection in any loaded assembly; road/rail preference disabled");
                    return 0;
                }
                Puts($"resolved topology enum type: {enumType.AssemblyQualifiedName}");
                int mask = 0;
                foreach (string name in new[] { "Road", "Roadside", "Rail", "Railside" })
                {
                    if (Enum.IsDefined(enumType, name))
                        mask |= Convert.ToInt32(Enum.Parse(enumType, name));
                }
                return mask;
            }
            catch (Exception ex)
            {
                PrintWarning($"topology enum reflection failed: {ex.Message}");
                return 0;
            }
        }

        private static double R(float v)
        {
            return Math.Round((double)v, 3);
        }

        private static object[] V3(Vector3 v)
        {
            return new object[] { R(v.x), R(v.y), R(v.z) };
        }

        private static string PosStr(Vector3 v)
        {
            return $"({v.x:F0},{v.y:F0},{v.z:F0})";
        }

        // Matches MonumentFinder.BaseMonumentAdapter.GetShortName exactly: last path segment, ".prefab" stripped.
        // MonumentFinder keys its DungeonBaseLink (lab module) adapters by this, so Monument Addons profiles must too.
        private static string GetShortName(string prefabName)
        {
            int slashIndex = prefabName.LastIndexOf("/");
            string baseName = slashIndex == -1 ? prefabName : prefabName.Substring(slashIndex + 1);
            return baseName.Replace(".prefab", "");
        }

        private static float Median(List<float> values)
        {
            if (values.Count == 0)
                return 0f;
            var sorted = values.OrderBy(v => v).ToList();
            int n = sorted.Count;
            if (n % 2 == 1)
                return sorted[n / 2];
            return (sorted[n / 2 - 1] + sorted[n / 2]) / 2f;
        }

        private static string DataDir
        {
            get { return Path.Combine(Directory.GetCurrentDirectory(), "carbon", "data", "MonumentSurvey"); }
        }

        // Assigns every live Recycler to the MonumentInfo whose OBB contains it; falls back to nearest-monument
        // for the rare recycler that sits just outside a tightly drawn Bounds (e.g. on a porch at the edge).
        private List<RecyclerAssignment> AssignRecyclers(List<MonumentInfo> monuments)
        {
            var list = new List<RecyclerAssignment>();
            foreach (var networkable in BaseNetworkable.serverEntities)
            {
                var recycler = networkable as Recycler;
                if (recycler == null)
                    continue;

                MonumentInfo owner = null;
                for (int i = 0; i < monuments.Count; i++)
                {
                    if (monuments[i].IsInBounds(recycler.transform.position))
                    {
                        owner = monuments[i];
                        break;
                    }
                }

                string assignedBy = "bounds";
                if (owner == null)
                {
                    float best = float.MaxValue;
                    for (int i = 0; i < monuments.Count; i++)
                    {
                        float d = monuments[i].Distance(recycler.transform.position);
                        if (d < best)
                        {
                            best = d;
                            owner = monuments[i];
                        }
                    }
                    if (best < NearestFallbackMaxDistance)
                        assignedBy = "nearest";
                    else
                    {
                        owner = null;
                        assignedBy = "unassigned";
                    }
                }

                list.Add(new RecyclerAssignment { Entity = recycler, Owner = owner, AssignedBy = assignedBy });
            }
            return list;
        }

        private static bool InScope(MonumentInfo mon)
        {
            return mon.shouldDisplayOnMap && mon.displayPhrase.IsValid() && !string.IsNullOrEmpty(mon.displayPhrase.english);
        }

        [ConsoleCommand("survey.dump")]
        private void CmdDump(ConsoleSystem.Arg arg)
        {
            var monuments = TerrainMeta.Path.Monuments;
            var assignments = AssignRecyclers(monuments);

            var monumentDumps = new List<object>();
            foreach (var mon in monuments)
            {
                monumentDumps.Add(new Dictionary<string, object>
                {
                    ["prefab"] = mon.name,
                    ["shortName"] = Path.GetFileNameWithoutExtension(mon.name),
                    ["displayName"] = mon.displayPhrase.IsValid() ? mon.displayPhrase.english : "",
                    ["shouldDisplayOnMap"] = mon.shouldDisplayOnMap,
                    ["inScope"] = InScope(mon),
                    ["tier"] = (int)mon.Tier,
                    ["type"] = mon.Type.ToString(),
                    ["position"] = V3(mon.transform.position),
                    ["rotationEuler"] = V3(mon.transform.rotation.eulerAngles),
                    ["boundsCenter"] = V3(mon.Bounds.center),
                    ["boundsSize"] = V3(mon.Bounds.size),
                });
            }

            var excluded = new List<object>();
            foreach (var group in monuments.GroupBy(m => m.name))
            {
                var rep = group.First();
                if (InScope(rep))
                    continue;
                excluded.Add(new Dictionary<string, object>
                {
                    ["prefab"] = rep.name,
                    ["shortName"] = Path.GetFileNameWithoutExtension(rep.name),
                    ["instances"] = group.Count(),
                    ["shouldDisplayOnMap"] = rep.shouldDisplayOnMap,
                    ["hasDisplayPhrase"] = rep.displayPhrase.IsValid(),
                    ["type"] = rep.Type.ToString(),
                });
            }

            var recyclerDumps = new List<object>();
            foreach (var a in assignments)
            {
                Vector3? localPos = a.Owner != null ? (Vector3?)a.Owner.transform.InverseTransformPoint(a.Entity.transform.position) : null;
                Vector3? localEuler = a.Owner != null
                    ? (Vector3?)(Quaternion.Inverse(a.Owner.transform.rotation) * a.Entity.transform.rotation).eulerAngles
                    : null;
                recyclerDumps.Add(new Dictionary<string, object>
                {
                    ["prefab"] = a.Entity.PrefabName,
                    ["netId"] = a.Entity.net != null ? a.Entity.net.ID.Value.ToString() : null,
                    ["worldPosition"] = V3(a.Entity.transform.position),
                    ["worldRotationEuler"] = V3(a.Entity.transform.rotation.eulerAngles),
                    ["monumentPrefab"] = a.Owner != null ? a.Owner.name : null,
                    ["monumentShortName"] = a.Owner != null ? Path.GetFileNameWithoutExtension(a.Owner.name) : null,
                    ["localPosition"] = localPos.HasValue ? V3(localPos.Value) : null,
                    ["localRotationEuler"] = localEuler.HasValue ? V3(localEuler.Value) : null,
                    ["assignedBy"] = a.AssignedBy,
                    ["isDestroyed"] = a.Entity.IsDestroyed,
                    ["health"] = R(a.Entity.health),
                    ["maxHealth"] = R(a.Entity.MaxHealth()),
                    ["hasNetworkId"] = a.Entity.net != null,
                    ["recyclerType"] = a.Entity.GetRecyclerType().ToString(),
                    ["isSafezoneRecycler"] = a.Entity.IsSafezoneRecycler(),
                    ["recyclerState"] = a.Entity.GetRecyclerState().ToString(),
                    ["requiresPowergrid"] = a.Entity.RequiresPowergrid(),
                });
            }

            Directory.CreateDirectory(DataDir);
            var payload = new Dictionary<string, object>
            {
                ["generatedAtUtc"] = DateTime.UtcNow.ToString("o"),
                ["monumentCount"] = monumentDumps.Count,
                ["recyclerCount"] = recyclerDumps.Count,
                ["monuments"] = monumentDumps,
                ["recyclers"] = recyclerDumps,
                ["excludedTypes"] = excluded,
            };
            File.WriteAllText(Path.Combine(DataDir, "dump.json"), JsonConvert.SerializeObject(payload, Formatting.Indented));
            arg.ReplyWith($"survey.dump: {monumentDumps.Count} monuments, {recyclerDumps.Count} recyclers, {excluded.Count} excluded types -> carbon/data/MonumentSurvey/dump.json");
        }

        private bool RaycastGround(Vector3 worldXZAtY, out RaycastHit hit)
        {
            float startY = Mathf.Max(TerrainMeta.HighestPoint.y, worldXZAtY.y) + 250f;
            Vector3 origin = new Vector3(worldXZAtY.x, startY, worldXZAtY.z);
            return Physics.Raycast(origin, Vector3.down, out hit, 2000f, _groundMask, QueryTriggerInteraction.Ignore);
        }

        private bool IsClear(Vector3 center, Vector3 halfExtents, Quaternion rot, HashSet<Collider> ignore, out string blockers)
        {
            Collider[] hits = Physics.OverlapBox(center, halfExtents, rot, _clearanceMask, QueryTriggerInteraction.Ignore);
            var real = new List<Collider>();
            foreach (var c in hits)
            {
                if (ignore == null || !ignore.Contains(c))
                    real.Add(c);
            }
            if (real.Count == 0)
            {
                blockers = null;
                return true;
            }
            blockers = string.Join(",", real.Select(c => c.name).Distinct().Take(5).ToArray());
            return false;
        }

        // Measures recycler_static's real solid-collider footprint by spawning one far outside the map (so it
        // cannot overlap terrain or a monument), reading collider bounds, then killing it immediately.
        private Footprint MeasureFootprint()
        {
            if (_footprint.HasValue)
                return _footprint.Value;

            Footprint fallback = new Footprint { HalfX = 0.9f, HalfZ = 0.9f, BottomY = 0f, TopY = 2.3f };
            Vector3 farPos = new Vector3(TerrainMeta.Position.x - 400f, 350f, TerrainMeta.Position.z - 400f);
            var ent = GameManager.server.CreateEntity(RecyclerPrefab, farPos, Quaternion.identity);
            if (ent == null)
            {
                PrintWarning("could not spawn recycler_static to measure its footprint, using fallback dimensions");
                _footprint = fallback;
                return fallback;
            }
            ent.Spawn();
            var colliders = ent.GetComponentsInChildren<Collider>(true).Where(c => !c.isTrigger).ToArray();
            if (colliders.Length == 0)
            {
                ent.Kill();
                PrintWarning("recycler_static has no solid colliders, using fallback dimensions");
                _footprint = fallback;
                return fallback;
            }
            float minX = float.MaxValue, maxX = float.MinValue, minZ = float.MaxValue, maxZ = float.MinValue;
            float minY = float.MaxValue, maxY = float.MinValue;
            foreach (var c in colliders)
            {
                Bounds b = c.bounds;
                minX = Mathf.Min(minX, b.min.x - farPos.x);
                maxX = Mathf.Max(maxX, b.max.x - farPos.x);
                minZ = Mathf.Min(minZ, b.min.z - farPos.z);
                maxZ = Mathf.Max(maxZ, b.max.z - farPos.z);
                minY = Mathf.Min(minY, b.min.y - farPos.y);
                maxY = Mathf.Max(maxY, b.max.y - farPos.y);
            }
            ent.Kill();
            _footprint = new Footprint
            {
                HalfX = Mathf.Max(Mathf.Abs(minX), Mathf.Abs(maxX)),
                HalfZ = Mathf.Max(Mathf.Abs(minZ), Mathf.Abs(maxZ)),
                BottomY = minY,
                TopY = maxY,
            };
            Puts($"measured recycler_static footprint: halfX={_footprint.Value.HalfX:F2} halfZ={_footprint.Value.HalfZ:F2} bottomY={_footprint.Value.BottomY:F2} topY={_footprint.Value.TopY:F2}");
            return _footprint.Value;
        }

        // Fallback for monuments where the sky-down sweep finds nothing, such as Underwater Lab: every room is
        // roofed by the hull, so a raycast from above the water always stops on the roof, never the interior
        // floor. Instead this starts short downward rays from just above existing entities inside the monument's
        // bounds (loot containers, NPCs, props the game itself placed on a real floor) to find indoor floors.
        private List<HitPoint> FindIndoorAnchorHits(MonumentInfo mon)
        {
            var anchors = new List<Vector3>();
            foreach (var networkable in BaseNetworkable.serverEntities)
            {
                var ent = networkable as BaseEntity;
                if (ent == null)
                    continue;
                if (ent is Recycler)
                    continue;
                if (!mon.IsInBounds(ent.transform.position))
                    continue;
                if (WaterLevel.Test(ent.transform.position, true, true))
                    continue;
                anchors.Add(ent.transform.position);
                if (anchors.Count >= 160)
                    break;
            }

            var ringOffsets = new List<Vector2> { Vector2.zero };
            for (int ring = 0; ring < 2; ring++)
            {
                float rr = ring == 0 ? 1.4f : 2.4f;
                for (int i = 0; i < 8; i++)
                {
                    float ang = i * 45f * Mathf.Deg2Rad;
                    ringOffsets.Add(new Vector2(Mathf.Cos(ang) * rr, Mathf.Sin(ang) * rr));
                }
            }

            var found = new List<HitPoint>();
            foreach (var anchor in anchors)
            {
                foreach (var ring in ringOffsets)
                {
                    Vector3 probe = anchor + new Vector3(ring.x, 2f, ring.y);
                    RaycastHit hit;
                    if (!Physics.Raycast(probe, Vector3.down, out hit, 4f, _groundMask, QueryTriggerInteraction.Ignore))
                        continue;
                    if (hit.normal.y < 0.97f)
                        continue;
                    if (WaterLevel.Test(hit.point, true, true))
                        continue;
                    Vector3 localHit = mon.transform.InverseTransformPoint(hit.point);
                    found.Add(new HitPoint { Offset = new Vector2(localHit.x, localHit.z), Hit = hit, IsIndoorAnchor = true });
                }
            }
            Puts($"indoor anchor fallback for {Path.GetFileNameWithoutExtension(mon.name)}: {anchors.Count} anchor entities, {found.Count} floor hits");
            return found;
        }

        // Local-space radial grid search around a representative instance of one monument type. Scores candidates
        // by distance to the monument centre, flatness, road/rail topology, indoor/outdoor, and clearance, and
        // returns them best-first; VerifyAtAllInstances re-checks the winner at every instance before it is used.
        private List<Candidate> SearchCandidates(MonumentInfo mon, Footprint fp, bool isOilRig)
        {
            float maxExtent = Mathf.Max(mon.Bounds.extents.x, mon.Bounds.extents.z);
            float maxRadius = Mathf.Clamp(maxExtent * 1.15f, 12f, 70f);

            var offsets = new List<Vector2> { Vector2.zero };
            for (float r = 3f; r <= maxRadius; r += 3f)
            {
                int steps = Mathf.Clamp(Mathf.RoundToInt(r / 2f), 8, 24);
                for (int i = 0; i < steps; i++)
                {
                    float ang = i * (360f / steps) * Mathf.Deg2Rad;
                    offsets.Add(new Vector2(Mathf.Cos(ang) * r, Mathf.Sin(ang) * r));
                }
            }

            var hitPoints = new List<HitPoint>();
            foreach (var off in offsets)
            {
                Vector3 localGuess = new Vector3(off.x, mon.Bounds.center.y, off.y);
                Vector3 worldGuess = mon.transform.TransformPoint(localGuess);
                RaycastHit hit;
                if (!RaycastGround(worldGuess, out hit))
                    continue;
                if (hit.normal.y < 0.97f)
                    continue;
                if (WaterLevel.Test(hit.point, true, true))
                    continue;
                hitPoints.Add(new HitPoint { Offset = off, Hit = hit });
            }

            bool indoorFallback = hitPoints.Count == 0;
            if (indoorFallback)
                hitPoints.AddRange(FindIndoorAnchorHits(mon));

            float medianY = Median(hitPoints.Select(h => h.Hit.point.y).ToList());
            float terrainDeltaLimit = isOilRig ? float.MaxValue : 2.0f;
            float medianDeltaLimit = isOilRig ? 4.0f : 2.5f;

            var results = new List<Candidate>();
            foreach (var hp in hitPoints)
            {
                RaycastHit hit = hp.Hit;
                // Indoor-anchor points have no meaningful outdoor terrain reference (they can be well below sea
                // level), so they only need to agree with each other, not with TerrainMeta.HeightMap.
                if (hp.IsIndoorAnchor)
                {
                    bool nearAnchorMedian = Mathf.Abs(hit.point.y - medianY) <= 2.5f;
                    if (!nearAnchorMedian)
                        continue;
                    var indoorCandidate = ScoreCandidate(mon, hit, medianY, fp, 0.15f);
                    if (indoorCandidate != null)
                        results.Add(indoorCandidate);
                    continue;
                }
                float terrainH = TerrainMeta.HeightMap.GetHeight(hit.point);
                bool nearTerrain = Mathf.Abs(hit.point.y - terrainH) <= terrainDeltaLimit;
                bool nearMedian = Mathf.Abs(hit.point.y - medianY) <= medianDeltaLimit;
                if (!nearTerrain && !nearMedian)
                    continue;

                results.Add(ScoreCandidate(mon, hit, medianY, fp, 0.3f));
            }

            return results.OrderByDescending(c => c.Score).ToList();
        }

        // Scores one already-accepted ground hit: snaps yaw to the monument's axes facing the local centre,
        // checks recycler-footprint clearance and 1.5m front standing room, and combines distance-to-centre,
        // road/rail topology, indoor/outdoor, and flatness into a single ranking score (higher is better).
        // margin shrinks to 0.15m for indoor-anchor candidates (tight monument interiors like Underwater Lab);
        // the outdoor default is the task's ~0.3m.
        private Candidate ScoreCandidate(MonumentInfo mon, RaycastHit hit, float medianY, Footprint fp, float margin)
        {
            Vector3 localHit = mon.transform.InverseTransformPoint(hit.point);
            float yawToCenter = Mathf.Atan2(0f - localHit.x, 0f - localHit.z) * Mathf.Rad2Deg;
            float snappedYaw = Mathf.Repeat(Mathf.Round(yawToCenter / 90f) * 90f, 360f);
            Quaternion worldRot = mon.transform.rotation * Quaternion.Euler(0f, snappedYaw, 0f);

            float halfY = (fp.TopY - fp.BottomY) / 2f;
            Vector3 boxCenter = hit.point + Vector3.up * (halfY + fp.BottomY + 0.05f);
            string blockers;
            bool clear = IsClear(boxCenter, new Vector3(fp.HalfX + margin, halfY, fp.HalfZ + margin), worldRot, null, out blockers);

            Vector3 forward = worldRot * Vector3.forward;
            Vector3 frontXZ = hit.point + forward * 1.5f;
            RaycastHit frontHit;
            bool frontOk = RaycastGround(frontXZ, out frontHit) && frontHit.normal.y >= 0.9f && !WaterLevel.Test(frontHit.point, true, true);
            if (frontOk)
            {
                string frontBlockers;
                frontOk = IsClear(frontHit.point + Vector3.up * 0.9f, new Vector3(0.4f, 0.9f, 0.4f), Quaternion.identity, null, out frontBlockers);
            }

            bool onRoadRail = _roadRailTopologyMask != 0 && (TerrainMeta.TopologyMap.GetTopology(hit.point) & _roadRailTopologyMask) != 0;
            RaycastHit upHit;
            bool outdoor = !Physics.Raycast(hit.point + Vector3.up * 0.3f, Vector3.up, out upHit, 5f, _groundMask, QueryTriggerInteraction.Ignore);

            float dist = new Vector2(localHit.x, localHit.z).magnitude;
            float score = 0f - dist
                          - (onRoadRail ? 25f : 0f)
                          - (outdoor ? 0f : 4f)
                          - (clear ? 0f : 1000f)
                          - (frontOk ? 0f : 15f)
                          - Mathf.Abs(hit.point.y - medianY) * 0.5f
                          + (hit.normal.y - 0.97f) * 10f;

            return new Candidate
            {
                LocalX = localHit.x,
                LocalY = localHit.y,
                LocalZ = localHit.z,
                LocalYaw = snappedYaw,
                WorldPoint = hit.point,
                NormalY = hit.normal.y,
                OnRoadRail = onRoadRail,
                Outdoor = outdoor,
                Clear = clear,
                Blockers = blockers,
                FrontClear = frontOk,
                DistanceFromCenter = dist,
                Score = score,
                Margin = margin,
            };
        }

        // Spawns a real recycler_static at every instance of the monument type, checks it for collider overlap
        // and a sub-10cm resting gap, then kills the test spawn. This is the "physically verify" step.
        private VerifyResult VerifyAtAllInstances(List<MonumentInfo> instances, float localX, float localZ, float localYaw, Footprint fp, float margin)
        {
            var result = new VerifyResult();
            foreach (var inst in instances)
            {
                Vector3 worldGuess = inst.transform.TransformPoint(new Vector3(localX, inst.Bounds.center.y, localZ));
                RaycastHit hit;
                if (!RaycastGround(worldGuess, out hit))
                {
                    result.PerInstance.Add($"{PosStr(inst.transform.position)}: FAIL no ground hit");
                    continue;
                }

                Quaternion worldRot = inst.transform.rotation * Quaternion.Euler(0f, localYaw, 0f);
                Vector3 spawnPos = hit.point + Vector3.up * 0.02f;
                var spawned = GameManager.server.CreateEntity(RecyclerPrefab, spawnPos, worldRot);
                if (spawned == null)
                {
                    result.PerInstance.Add($"{PosStr(inst.transform.position)}: FAIL spawn returned null");
                    continue;
                }
                spawned.Spawn();
                var own = new HashSet<Collider>(spawned.GetComponentsInChildren<Collider>(true));

                // Same 0.05m lift as SearchCandidates: the box bottom must clear the ground mesh it rests on
                // (Terrain/World are both in _clearanceMask), or Physics.OverlapBox flags the ground itself.
                float halfY = (fp.TopY - fp.BottomY) / 2f;
                Vector3 boxCenter = spawnPos + Vector3.up * (halfY + fp.BottomY + 0.05f);
                string blockers;
                bool clear = IsClear(boxCenter, new Vector3(fp.HalfX + margin, halfY, fp.HalfZ + margin), worldRot, own, out blockers);

                RaycastHit restHit;
                Physics.Raycast(spawnPos + Vector3.up * 0.5f, Vector3.down, out restHit, 5f, _groundMask, QueryTriggerInteraction.Ignore);
                float gap = restHit.collider != null ? Mathf.Abs(restHit.point.y - hit.point.y) : 999f;

                bool ok = clear && gap < 0.1f;
                if (ok)
                {
                    result.OkCount++;
                    result.PerInstance.Add($"{PosStr(inst.transform.position)}: ok gap={gap:F3}m");
                }
                else
                {
                    string reason = !clear ? "overlap:" + blockers : $"gap={gap:F2}m";
                    result.PerInstance.Add($"{PosStr(inst.transform.position)}: FAIL {reason}");
                }
                spawned.Kill();
            }
            result.AllOk = instances.Count > 0 && result.OkCount == instances.Count;
            return result;
        }

        [ConsoleCommand("survey.solve")]
        private void CmdSolve(ConsoleSystem.Arg arg)
        {
            string filter = arg.Args != null && arg.Args.Length > 0 ? (string)arg.Args[0] : null;
            var fp = MeasureFootprint();
            var monuments = TerrainMeta.Path.Monuments;
            var assignments = AssignRecyclers(monuments);
            var ownedPrefabs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var a in assignments)
            {
                if (a.Owner != null)
                    ownedPrefabs.Add(a.Owner.name);
            }

            var output = new List<object>();
            int solved = 0;
            foreach (var group in monuments.GroupBy(m => m.name))
            {
                var instances = group.ToList();
                var rep = instances[0];
                if (!InScope(rep))
                    continue;
                if (filter != null &&
                    rep.name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0 &&
                    Path.GetFileNameWithoutExtension(rep.name).IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                bool hasRecycler = ownedPrefabs.Contains(rep.name);
                var entry = new Dictionary<string, object>
                {
                    ["prefab"] = rep.name,
                    ["shortName"] = Path.GetFileNameWithoutExtension(rep.name),
                    ["displayName"] = rep.displayPhrase.english,
                    ["instances"] = instances.Count,
                    ["hasVanillaRecycler"] = hasRecycler,
                };

                if (hasRecycler)
                {
                    entry["placement"] = null;
                    entry["notes"] = $"vanilla recycler present at {instances.Count} instance(s)";
                    output.Add(entry);
                    continue;
                }

                bool isOilRig = rep.IsOilRig();
                var candidates = SearchCandidates(rep, fp, isOilRig);
                var candidateNotes = candidates.Take(12).Select(c =>
                    $"({c.LocalX:F1},{c.LocalZ:F1})yaw{c.LocalYaw:F0} score={c.Score:F1} n={c.NormalY:F2} clear={c.Clear} road={c.OnRoadRail} out={c.Outdoor} front={c.FrontClear}"
                ).ToList();

                Candidate chosen = null;
                VerifyResult chosenResult = null;
                int tries = 0;
                foreach (var cand in candidates)
                {
                    if (!cand.Clear)
                        continue;
                    if (tries >= 6)
                        break;
                    tries++;
                    var vr = VerifyAtAllInstances(instances, cand.LocalX, cand.LocalZ, cand.LocalYaw, fp, cand.Margin);
                    if (chosenResult == null || vr.OkCount > chosenResult.OkCount)
                    {
                        chosen = cand;
                        chosenResult = vr;
                    }
                    if (vr.AllOk)
                        break;
                }

                string verdict;
                if (chosen != null)
                {
                    entry["placement"] = new Dictionary<string, object>
                    {
                        ["position"] = new object[] { R(chosen.LocalX), R(chosen.LocalY), R(chosen.LocalZ) },
                        ["rotation"] = new object[] { 0.0, R(chosen.LocalYaw), 0.0 },
                    };
                    verdict = chosenResult.AllOk
                        ? $"verified clean at all {instances.Count} instance(s)"
                        : $"best effort: clean at {chosenResult.OkCount}/{instances.Count} instances, see verify log";
                    solved++;
                }
                else
                {
                    entry["placement"] = null;
                    verdict = candidates.Count == 0
                        ? "no flat/dry candidate found in search radius"
                        : "every scored candidate failed the clearance check";
                }

                entry["notes"] = verdict
                    + " | top candidates: " + string.Join("; ", candidateNotes.ToArray())
                    + (chosenResult != null ? " | verify: " + string.Join(" / ", chosenResult.PerInstance.ToArray()) : "");
                output.Add(entry);
            }

            Directory.CreateDirectory(DataDir);
            var final = new Dictionary<string, object> { ["monuments"] = output };
            File.WriteAllText(Path.Combine(DataDir, "placements.json"), JsonConvert.SerializeObject(final, Formatting.Indented));
            arg.ReplyWith($"survey.solve: {output.Count} in-scope types, {solved} placements found -> carbon/data/MonumentSurvey/placements.json");
        }

        // Dumps every non-posts DungeonBaseLink (Type != End; End links are the posts holding up lab modules,
        // per MonumentFinder's own comment) with its prefab path, GetShortName, world transform, and distance to
        // the nearest underwater_lab_* MonumentInfo root, plus a per-shortName instance count for this lab.
        [ConsoleCommand("survey.labdump")]
        private void CmdLabDump(ConsoleSystem.Arg arg)
        {
            var links = TerrainMeta.Path.DungeonBaseLinks;
            MonumentInfo labMonument = null;
            foreach (var mon in TerrainMeta.Path.Monuments)
            {
                if (Path.GetFileNameWithoutExtension(mon.name).StartsWith("underwater_lab", StringComparison.OrdinalIgnoreCase))
                {
                    labMonument = mon;
                    break;
                }
            }
            Vector3 labPos = labMonument != null ? labMonument.transform.position : Vector3.zero;

            var dumps = new List<object>();
            foreach (var link in links)
            {
                dumps.Add(new Dictionary<string, object>
                {
                    ["prefab"] = link.name,
                    ["shortName"] = GetShortName(link.name),
                    ["type"] = link.Type.ToString(),
                    ["position"] = V3(link.transform.position),
                    ["rotationEuler"] = V3(link.transform.rotation.eulerAngles),
                    ["lossyScale"] = V3(link.transform.lossyScale),
                    ["distanceToLabRoot"] = labMonument != null ? R(Vector3.Distance(link.transform.position, labPos)) : (double?)null,
                });
            }

            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var typeByShortName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var link in links)
            {
                if (link.Type == DungeonBaseLinkType.End)
                    continue;
                string sn = GetShortName(link.name);
                int cur;
                counts[sn] = counts.TryGetValue(sn, out cur) ? cur + 1 : 1;
                typeByShortName[sn] = link.Type.ToString();
            }
            var moduleCounts = new List<object>();
            foreach (var kv in counts.OrderBy(kv => kv.Value))
            {
                moduleCounts.Add(new Dictionary<string, object>
                {
                    ["shortName"] = kv.Key,
                    ["count"] = kv.Value,
                    ["type"] = typeByShortName[kv.Key],
                });
            }

            Directory.CreateDirectory(DataDir);
            var payload = new Dictionary<string, object>
            {
                ["labMonumentPrefab"] = labMonument != null ? labMonument.name : null,
                ["totalLinks"] = links.Count,
                ["links"] = dumps,
                ["moduleCounts"] = moduleCounts,
            };
            File.WriteAllText(Path.Combine(DataDir, "labdump.json"), JsonConvert.SerializeObject(payload, Formatting.Indented));
            arg.ReplyWith($"survey.labdump: {links.Count} dungeon base links, {moduleCounts.Count} distinct non-End module short names -> carbon/data/MonumentSurvey/labdump.json");
        }

        // A single Physics.Raycast stops at the FIRST collider, which in a moonpool room is the wet pool/deck
        // mesh a couple of metres above the real dry floor (confirmed with survey.labprobe: RaycastAll shows a
        // "PrimitiveColliders" dry hit sitting right behind the first wet hit). RaycastAll plus a walk past every
        // wet or non-flat hit is required to ever reach the actual floor.
        private bool ProbeModuleFloor(Vector3 worldXZAt, float startY, float maxDistance, float minNormalY, out RaycastHit hit)
        {
            Vector3 origin = new Vector3(worldXZAt.x, startY, worldXZAt.z);
            RaycastHit[] hits = Physics.RaycastAll(origin, Vector3.down, maxDistance, _groundMask, QueryTriggerInteraction.Ignore);
            Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
            foreach (var h in hits)
            {
                if (h.normal.y >= minNormalY && !WaterLevel.Test(h.point, true, true))
                {
                    hit = h;
                    return true;
                }
            }
            hit = default(RaycastHit);
            return false;
        }

        // Same scoring shape as ScoreCandidate, but framed on an arbitrary Transform (a DungeonBaseLink module,
        // not a MonumentInfo) and probing a short distance from the link's own height instead of from the sky:
        // module rooms sit under the lab's hull, so a sky raycast would stop on the roof, never reach the floor.
        private Candidate ScoreModuleCandidate(Transform frame, RaycastHit hit, float medianY, Footprint fp, float margin)
        {
            Vector3 localHit = frame.InverseTransformPoint(hit.point);
            float yawToCenter = Mathf.Atan2(0f - localHit.x, 0f - localHit.z) * Mathf.Rad2Deg;
            float snappedYaw = Mathf.Repeat(Mathf.Round(yawToCenter / 90f) * 90f, 360f);
            Quaternion worldRot = frame.rotation * Quaternion.Euler(0f, snappedYaw, 0f);

            float halfY = (fp.TopY - fp.BottomY) / 2f;
            Vector3 boxCenter = hit.point + Vector3.up * (halfY + fp.BottomY + 0.05f);
            string blockers;
            bool clear = IsClear(boxCenter, new Vector3(fp.HalfX + margin, halfY, fp.HalfZ + margin), worldRot, null, out blockers);

            Vector3 forward = worldRot * Vector3.forward;
            Vector3 frontProbeXZ = hit.point + forward * 1.5f;
            RaycastHit frontHit;
            bool frontOk = ProbeModuleFloor(frontProbeXZ, frame.position.y + 9f, 13f, 0.9f, out frontHit);
            if (frontOk)
            {
                string frontBlockers;
                frontOk = IsClear(frontHit.point + Vector3.up * 0.9f, new Vector3(0.35f, 0.9f, 0.35f), Quaternion.identity, null, out frontBlockers);
            }

            float dist = new Vector2(localHit.x, localHit.z).magnitude;
            float score = 0f - dist
                          - (clear ? 0f : 1000f)
                          - (frontOk ? 0f : 15f)
                          - Mathf.Abs(hit.point.y - medianY) * 0.5f
                          + (hit.normal.y - 0.97f) * 10f;

            return new Candidate
            {
                LocalX = localHit.x,
                LocalY = localHit.y,
                LocalZ = localHit.z,
                LocalYaw = snappedYaw,
                WorldPoint = hit.point,
                NormalY = hit.normal.y,
                OnRoadRail = false,
                Outdoor = false,
                Clear = clear,
                Blockers = blockers,
                FrontClear = frontOk,
                DistanceFromCenter = dist,
                Score = score,
                Margin = margin,
            };
        }

        // Local grid search around a DungeonBaseLink's own origin (module rooms are compact, unlike a whole
        // monument), short-range probes anchored near the link's height for the same roof-blocking reason as
        // ScoreModuleCandidate.
        private List<Candidate> SearchModuleCandidates(Transform frame, Footprint fp, float margin)
        {
            var offsets = new List<Vector2> { Vector2.zero };
            for (float r = 0.5f; r <= 10f; r += 0.5f)
            {
                int steps = Mathf.Clamp(Mathf.RoundToInt(r * 6f), 8, 32);
                for (int i = 0; i < steps; i++)
                {
                    float ang = i * (360f / steps) * Mathf.Deg2Rad;
                    offsets.Add(new Vector2(Mathf.Cos(ang) * r, Mathf.Sin(ang) * r));
                }
            }

            var hitPoints = new List<HitPoint>();
            foreach (var off in offsets)
            {
                Vector3 worldGuess = frame.TransformPoint(new Vector3(off.x, 0f, off.y));
                RaycastHit hit;
                if (!ProbeModuleFloor(worldGuess, frame.position.y + 9f, 13f, 0.97f, out hit))
                    continue;
                hitPoints.Add(new HitPoint { Offset = off, Hit = hit });
            }

            float medianY = Median(hitPoints.Select(h => h.Hit.point.y).ToList());

            var results = new List<Candidate>();
            foreach (var hp in hitPoints)
            {
                if (Mathf.Abs(hp.Hit.point.y - medianY) > 2.0f)
                    continue;
                results.Add(ScoreModuleCandidate(frame, hp.Hit, medianY, fp, margin));
            }
            return results.OrderByDescending(c => c.Score).ToList();
        }

        // Spawns a real recycler_static at the one DungeonBaseLink instance, same overlap+gap check as
        // VerifyAtAllInstances, then kills it.
        private VerifyResult VerifyModuleCandidate(Transform frame, float localX, float localZ, float localYaw, Footprint fp, float margin)
        {
            var result = new VerifyResult();
            Vector3 worldGuess = frame.TransformPoint(new Vector3(localX, 0f, localZ));
            RaycastHit hit;
            if (!ProbeModuleFloor(worldGuess, frame.position.y + 9f, 13f, 0.97f, out hit))
            {
                result.PerInstance.Add($"{PosStr(frame.position)}: FAIL no ground hit");
                result.AllOk = false;
                return result;
            }

            Quaternion worldRot = frame.rotation * Quaternion.Euler(0f, localYaw, 0f);
            Vector3 spawnPos = hit.point + Vector3.up * 0.02f;
            var spawned = GameManager.server.CreateEntity(RecyclerPrefab, spawnPos, worldRot);
            if (spawned == null)
            {
                result.PerInstance.Add($"{PosStr(frame.position)}: FAIL spawn returned null");
                result.AllOk = false;
                return result;
            }
            spawned.Spawn();
            var own = new HashSet<Collider>(spawned.GetComponentsInChildren<Collider>(true));

            float halfY = (fp.TopY - fp.BottomY) / 2f;
            Vector3 boxCenter = spawnPos + Vector3.up * (halfY + fp.BottomY + 0.05f);
            string blockers;
            bool clear = IsClear(boxCenter, new Vector3(fp.HalfX + margin, halfY, fp.HalfZ + margin), worldRot, own, out blockers);

            RaycastHit restHit;
            Physics.Raycast(spawnPos + Vector3.up * 0.5f, Vector3.down, out restHit, 5f, _groundMask, QueryTriggerInteraction.Ignore);
            float gap = restHit.collider != null ? Mathf.Abs(restHit.point.y - hit.point.y) : 999f;

            bool ok = clear && gap < 0.1f;
            result.OkCount = ok ? 1 : 0;
            result.AllOk = ok;
            result.PerInstance.Add(ok
                ? $"{PosStr(frame.position)}: ok gap={gap:F3}m"
                : $"{PosStr(frame.position)}: FAIL " + (!clear ? "overlap:" + blockers : $"gap={gap:F2}m"));
            spawned.Kill();
            return result;
        }

        // Tries the task's normal 0.3m margin first, falls back to 0.15m (Underwater Lab's tight-interior
        // margin) only if nothing at 0.3m verifies clean, and reports which margin actually won.
        [ConsoleCommand("survey.labsolve")]
        private void CmdLabSolve(ConsoleSystem.Arg arg)
        {
            string wantShortName = arg.Args != null && arg.Args.Length > 0 ? (string)arg.Args[0] : null;
            if (string.IsNullOrEmpty(wantShortName))
            {
                arg.ReplyWith("usage: survey.labsolve <moduleShortName>");
                return;
            }

            var fp = MeasureFootprint();
            var matches = new List<DungeonBaseLink>();
            foreach (var candidateLink in TerrainMeta.Path.DungeonBaseLinks)
            {
                if (candidateLink.Type == DungeonBaseLinkType.End)
                    continue;
                if (string.Equals(GetShortName(candidateLink.name), wantShortName, StringComparison.OrdinalIgnoreCase))
                    matches.Add(candidateLink);
            }

            if (matches.Count == 0)
            {
                arg.ReplyWith($"survey.labsolve: no non-End DungeonBaseLink with shortName '{wantShortName}'");
                return;
            }

            var link = matches[0];
            Transform frame = link.transform;

            Candidate chosen = null;
            VerifyResult chosenResult = null;
            float usedMargin = 0f;
            List<Candidate> candidates = new List<Candidate>();
            foreach (var margin in new[] { 0.3f, 0.15f, 0.08f })
            {
                candidates = SearchModuleCandidates(frame, fp, margin);
                int tries = 0;
                foreach (var cand in candidates)
                {
                    if (!cand.Clear)
                        continue;
                    if (tries >= 6)
                        break;
                    tries++;
                    var vr = VerifyModuleCandidate(frame, cand.LocalX, cand.LocalZ, cand.LocalYaw, fp, margin);
                    if (chosenResult == null || vr.OkCount > chosenResult.OkCount)
                    {
                        chosen = cand;
                        chosenResult = vr;
                        usedMargin = margin;
                    }
                    if (vr.AllOk)
                        break;
                }
                if (chosenResult != null && chosenResult.AllOk)
                    break;
            }

            var candidateNotes = candidates.Take(12).Select(c =>
                $"({c.LocalX:F1},{c.LocalZ:F1})yaw{c.LocalYaw:F0} score={c.Score:F1} n={c.NormalY:F2} clear={c.Clear} front={c.FrontClear} blockers={c.Blockers}"
            ).ToList();

            var entry = new Dictionary<string, object>
            {
                ["linkPrefab"] = link.name,
                ["shortName"] = GetShortName(link.name),
                ["type"] = link.Type.ToString(),
                ["matchingLinkCount"] = matches.Count,
                ["framePosition"] = V3(frame.position),
                ["frameRotationEuler"] = V3(frame.rotation.eulerAngles),
                ["frameLossyScale"] = V3(frame.lossyScale),
            };

            if (chosen != null)
            {
                entry["placement"] = new Dictionary<string, object>
                {
                    ["position"] = new object[] { R(chosen.LocalX), R(chosen.LocalY), R(chosen.LocalZ) },
                    ["rotation"] = new object[] { 0.0, R(chosen.LocalYaw), 0.0 },
                };
                entry["marginUsed"] = usedMargin;
                entry["notes"] = (chosenResult.AllOk ? "verified clean" : $"best effort clean={chosenResult.OkCount}/1")
                    + " | candidates: " + string.Join("; ", candidateNotes.ToArray())
                    + " | verify: " + string.Join(" / ", chosenResult.PerInstance.ToArray());
            }
            else
            {
                entry["placement"] = null;
                entry["notes"] = "no valid candidate found at 0.3m, 0.15m, or 0.08m margin | candidates: " + string.Join("; ", candidateNotes.ToArray());
            }

            Directory.CreateDirectory(DataDir);
            File.WriteAllText(Path.Combine(DataDir, "labsolve.json"), JsonConvert.SerializeObject(entry, Formatting.Indented));
            arg.ReplyWith($"survey.labsolve: shortName={GetShortName(link.name)} matches={matches.Count} placement={(chosen != null ? "found" : "NULL")} marginUsed={usedMargin} -> carbon/data/MonumentSurvey/labsolve.json");
        }

        // Scans every non-End DungeonBaseLink shortName that occurs exactly once, running the search (both
        // margins, no spawn/verify) and reporting only the single best candidate per module: whether anything is
        // fully clear, and if not, the least-blocked near-miss. Used to compare modules quickly before committing
        // a full labsolve verify pass to just one.
        [ConsoleCommand("survey.labscan")]
        private void CmdLabScan(ConsoleSystem.Arg arg)
        {
            var fp = MeasureFootprint();
            var countByShortName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var oneOfEach = new Dictionary<string, DungeonBaseLink>(StringComparer.OrdinalIgnoreCase);
            foreach (var l in TerrainMeta.Path.DungeonBaseLinks)
            {
                if (l.Type == DungeonBaseLinkType.End)
                    continue;
                string sn = GetShortName(l.name);
                int cur;
                countByShortName[sn] = countByShortName.TryGetValue(sn, out cur) ? cur + 1 : 1;
                if (!oneOfEach.ContainsKey(sn))
                    oneOfEach[sn] = l;
            }

            var lines = new List<string>();
            foreach (var kv in countByShortName)
            {
                if (kv.Value != 1)
                    continue;
                Transform frame = oneOfEach[kv.Key].transform;
                string best = "no candidates at all";
                foreach (var margin in new[] { 0.3f, 0.15f, 0.08f })
                {
                    var candidates = SearchModuleCandidates(frame, fp, margin);
                    if (candidates.Count == 0)
                        continue;
                    var top = candidates[0];
                    best = $"margin={margin} best=({top.LocalX:F1},{top.LocalZ:F1}) clear={top.Clear} front={top.FrontClear} blockers={top.Blockers}";
                    if (top.Clear)
                        break;
                }
                lines.Add($"{kv.Key} (type={oneOfEach[kv.Key].Type}): {best}");
            }

            Directory.CreateDirectory(DataDir);
            File.WriteAllText(Path.Combine(DataDir, "labscan.json"), JsonConvert.SerializeObject(lines, Formatting.Indented));
            foreach (var line in lines)
                Puts("[labscan] " + line);
            arg.ReplyWith($"survey.labscan: scanned {lines.Count} single-occurrence modules, see log for [labscan] lines");
        }

        // One-off diagnostic: RaycastAll straight down through the whole module at a few local XZ offsets, to see
        // every surface (ceiling, floor, water) along the way and calibrate SearchModuleCandidates' probe range.
        [ConsoleCommand("survey.labprobe")]
        private void CmdLabProbe(ConsoleSystem.Arg arg)
        {
            string wantShortName = arg.Args != null && arg.Args.Length > 0 ? (string)arg.Args[0] : null;
            DungeonBaseLink found = null;
            foreach (var candidateLink in TerrainMeta.Path.DungeonBaseLinks)
            {
                if (string.Equals(GetShortName(candidateLink.name), wantShortName, StringComparison.OrdinalIgnoreCase))
                {
                    found = candidateLink;
                    break;
                }
            }
            if (found == null)
            {
                arg.ReplyWith($"survey.labprobe: no link '{wantShortName}'");
                return;
            }

            Transform frame = found.transform;
            var lines = new List<string>();
            var offsetList = new List<Vector2> { Vector2.zero };
            foreach (float r in new[] { 3f, 5f, 7f })
            {
                for (int i = 0; i < 8; i++)
                {
                    float ang = i * 45f * Mathf.Deg2Rad;
                    offsetList.Add(new Vector2(Mathf.Cos(ang) * r, Mathf.Sin(ang) * r));
                }
            }
            Vector2[] offsets = offsetList.ToArray();
            foreach (var off in offsets)
            {
                Vector3 worldXZ = frame.TransformPoint(new Vector3(off.x, 0f, off.y));
                Vector3 origin = new Vector3(worldXZ.x, frame.position.y + 20f, worldXZ.z);
                RaycastHit[] hits = Physics.RaycastAll(origin, Vector3.down, 40f, _groundMask, QueryTriggerInteraction.Ignore);
                Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
                string topHit = hits.Length > 0 ? $"top y={hits[0].point.y:F2}(d={hits[0].point.y - frame.position.y:F2}) obj={hits[0].collider.name}" : "top NONE";
                string dryHit = "dry NONE";
                foreach (var h in hits)
                {
                    if (h.normal.y >= 0.9f && !WaterLevel.Test(h.point, true, true))
                    {
                        dryHit = $"dry y={h.point.y:F2}(d={h.point.y - frame.position.y:F2}) n.y={h.normal.y:F2} obj={h.collider.name}";
                        break;
                    }
                }
                lines.Add($"offset({off.x},{off.y}) hits={hits.Length}: {topHit} | {dryHit}");
            }

            Directory.CreateDirectory(DataDir);
            File.WriteAllText(Path.Combine(DataDir, "labprobe.json"), JsonConvert.SerializeObject(new Dictionary<string, object>
            {
                ["shortName"] = wantShortName,
                ["framePosition"] = V3(frame.position),
                ["lines"] = lines,
            }, Formatting.Indented));
            foreach (var line in lines)
                Puts("[labprobe] " + line);
            arg.ReplyWith($"survey.labprobe: wrote carbon/data/MonumentSurvey/labprobe.json, see log for [labprobe] lines");
        }
    }
}
