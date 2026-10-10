using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;
using Verse.AI;

namespace CharacterTraits
{
    // ---------- Данные из XML ----------
    public class CTRule { public string stat; public string cond = "Always"; public float offset; public float factor = 1f; public string scale; }
    public class CTEvent { public string ev; public string thought; }
    public class CTPulse { public string thought; public float radius = 8f; public float chance = 0.1f; public string cond = "Always"; }
    public class CTAura { public string stat; public float offset; public float factor = 1f; public float radius = 8f; public string cond = "Always"; }

    public class CTExt : DefModExtension
    {
        public List<CTRule> rules;
        public List<CTEvent> events;
        public List<CTPulse> pulses;
        public List<CTAura> auras;
        public List<string> passions;
        public float breakBerserk;
        public float luck;
        public float vsArmor;
        public float sleepLoot;
    }

    public class CTCondExt : DefModExtension { public string cond; }

    public static class CT
    {
        static HashSet<string> auraStats;
        public static HashSet<string> AuraStats
        {
            get
            {
                if (auraStats == null)
                {
                    auraStats = new HashSet<string>();
                    foreach (TraitDef d in DefDatabase<TraitDef>.AllDefs)
                    {
                        CTExt e = d.GetModExtension<CTExt>();
                        if (e != null && e.auras != null) foreach (CTAura a in e.auras) auraStats.Add(a.stat);
                    }
                }
                return auraStats;
            }
        }

        public static bool Has(Pawn p, string trait)
        {
            if (p == null || p.story == null || p.story.traits == null) return false;
            TraitDef d = DefDatabase<TraitDef>.GetNamedSilentFail(trait);
            return d != null && p.story.traits.HasTrait(d);
        }

        public static void Mem(Pawn p, string thought)
        {
            if (p == null || p.needs == null || p.needs.mood == null || p.needs.mood.thoughts == null) return;
            ThoughtDef d = DefDatabase<ThoughtDef>.GetNamedSilentFail(thought);
            if (d != null) p.needs.mood.thoughts.memories.TryGainMemory(d);
        }

        public static void Forget(Pawn p, string thought)
        {
            if (p == null || p.needs == null || p.needs.mood == null || p.needs.mood.thoughts == null) return;
            ThoughtDef d = DefDatabase<ThoughtDef>.GetNamedSilentFail(thought);
            if (d != null) p.needs.mood.thoughts.memories.RemoveMemoriesOfDef(d);
        }

        public static bool Knows(Pawn p, string thought)
        {
            if (p == null || p.needs == null || p.needs.mood == null || p.needs.mood.thoughts == null) return false;
            ThoughtDef d = DefDatabase<ThoughtDef>.GetNamedSilentFail(thought);
            return d != null && p.needs.mood.thoughts.memories.GetFirstMemoryOfDef(d) != null;
        }

        // Выдать память по событию всем чертам pawn, которые на него подписаны
        public static void Fire(Pawn p, string key)
        {
            if (p == null || p.story == null || p.story.traits == null) return;
            foreach (Trait t in p.story.traits.allTraits)
            {
                CTExt e = t.def.GetModExtension<CTExt>();
                if (e == null || e.events == null) continue;
                foreach (CTEvent ev in e.events) if (ev.ev == key) Mem(p, ev.thought);
            }
        }

        public static void FireMap(Map map, string key, Pawn except)
        {
            if (map == null) return;
            foreach (Pawn o in map.mapPawns.FreeColonistsSpawned.ToList())
                if (o != except) Fire(o, key);
        }

        public static bool IsColonist(Pawn p)
        {
            return p != null && p.Faction == Faction.OfPlayer && p.RaceProps != null && p.RaceProps.Humanlike;
        }

        public static bool HoldsKnife(Pawn p)
        {
            if (p == null || p.equipment == null || p.equipment.Primary == null) return false;
            return p.equipment.Primary.def.defName.IndexOf("Knife", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public static bool HasRifle(Pawn p)
        {
            if (p == null) return false;
            if (p.equipment != null && p.equipment.Primary != null && p.equipment.Primary.def.defName == "Gun_HuntingRifle") return true;
            if (p.inventory != null && p.inventory.innerContainer.Any(t => t.def.defName == "Gun_HuntingRifle")) return true;
            return false;
        }

        public static List<Pawn> AllPlayerPawns()
        {
            List<Pawn> list = new List<Pawn>();
            foreach (Map m in Find.Maps) list.AddRange(m.mapPawns.PawnsInFaction(Faction.OfPlayer));
            if (Find.WorldObjects != null)
                foreach (Caravan c in Find.WorldObjects.Caravans) list.AddRange(c.PawnsListForReading);
            return list;
        }

        public static bool IsGuilty(Pawn p)
        {
            try { return Traverse.Create(p).Field("guilt").Property("IsGuilty").GetValue<bool>(); }
            catch (Exception) { return false; }
        }
    }

    // ---------- Условия (используются в правилах, настроениях, пульсах) ----------
    public static class Cond
    {
        static float Num(string s) { return float.Parse(s.Trim(), CultureInfo.InvariantCulture); }

        public static bool Check(string cond, Pawn p)
        {
            if (string.IsNullOrEmpty(cond) || p == null) return true;
            foreach (string part in cond.Split('&'))
            {
                string c = part.Trim();
                if (c.Length == 0) continue;
                bool neg = c.StartsWith("!");
                if (neg) c = c.Substring(1);
                bool r;
                try { r = One(c, p); } catch (Exception) { r = false; }
                if (r == neg) return false;
            }
            return true;
        }

        public static float Scale(string s, Pawn p)
        {
            if (string.IsNullOrEmpty(s)) return 1f;
            if (s == "Rage")
            {
                float hp = p.health.summaryHealth.SummaryHealthPercent;
                return Mathf.Clamp01((0.6f - hp) / 0.5f);
            }
            if (s == "Kills")
            {
                GameComponent_CT g = GameComponent_CT.Get;
                int k = 0;
                if (g != null) g.knifeKills.TryGetValue(p.thingIDNumber, out k);
                return Mathf.Min(12, 2 + k / 2);
            }
            return 1f;
        }

        static int ColonistsNear(Pawn p, float r)
        {
            int n = 0;
            foreach (Pawn o in p.Map.mapPawns.FreeColonistsSpawned)
                if (o != p && o.Position.InHorDistOf(p.Position, r)) n++;
            return n;
        }

        static int HostilesNear(Pawn p, float r)
        {
            int n = 0;
            foreach (Pawn o in p.Map.mapPawns.AllPawnsSpawned)
            {
                if (o == p || o.Dead || o.Downed) continue;
                if (!(o.RaceProps.Humanlike || o.RaceProps.IsMechanoid)) continue;
                if (o.HostileTo(p) && o.Position.InHorDistOf(p.Position, r)) n++;
            }
            return n;
        }

        static bool AnimalNear(Pawn p, float r, string[] names)
        {
            foreach (Pawn a in p.Map.mapPawns.SpawnedPawnsInFaction(p.Faction))
            {
                if (!a.RaceProps.Animal || !a.Position.InHorDistOf(p.Position, r)) continue;
                if (names == null) return true;
                foreach (string n in names) if (a.def.defName.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            return false;
        }

        static bool ThingNear(Pawn p, string[] names, float r)
        {
            foreach (Thing t in GenRadial.RadialDistinctThingsAround(p.Position, p.Map, r, true))
                foreach (string n in names)
                    if (t.def.defName.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        static bool One(string c, Pawn p)
        {
            string name = c, arg = "";
            int i = c.IndexOf(':');
            if (i >= 0) { name = c.Substring(0, i); arg = c.Substring(i + 1); }
            Map map = p.Spawned ? p.Map : null;

            switch (name)
            {
                case "Always": return true;
                case "InCaravan": return p.IsCaravanMember();
                case "NoCaravan":
                    {
                        GameComponent_CT g = GameComponent_CT.Get;
                        return g != null && Find.TickManager.TicksGame - g.lastCaravan > 15 * 60000;
                    }
                case "Mem": return CT.Knows(p, arg);
                case "Has": return CT.Has(p, arg);
                case "Hurt": return p.health.summaryHealth.SummaryHealthPercent < 0.5f;
                case "Critical": return p.health.summaryHealth.SummaryHealthPercent < 0.25f;
                case "Asleep": return !p.Awake();
                case "Moving": return p.pather != null && p.pather.Moving;
                case "Melee": return p.equipment == null || p.equipment.Primary == null || !p.equipment.Primary.def.IsRangedWeapon;
                case "Ranged": return p.equipment != null && p.equipment.Primary != null && p.equipment.Primary.def.IsRangedWeapon;
                case "Knife": return CT.HoldsKnife(p);
                case "Rifle": return CT.HasRifle(p);
                case "SleepFloor": return !p.Awake() && p.CurrentBed() == null;
                case "Idle":
                    {
                        JobDef j = p.CurJobDef;
                        return j != null && (j.defName == "GotoWander" || j.defName == "Wait_Wander");
                    }
            }

            if (map == null) return false;

            switch (name)
            {
                case "Outdoors": return !p.Position.Roofed(map);
                case "Roofed": return p.Position.Roofed(map);
                case "Indoors": { Room r = p.GetRoom(); return r != null && !r.PsychologicallyOutdoors; }
                case "NaturalRoof": { RoofDef roof = map.roofGrid.RoofAt(p.Position); return roof != null && roof.isNatural; }
                case "Home": { Area h = map.areaManager.Home; return h != null && h[p.Position]; }
                case "Day": return map.skyManager.CurSkyGlow > 0.6f;
                case "Night": return map.skyManager.CurSkyGlow < 0.3f;
                case "Dark": return map.glowGrid.GroundGlowAt(p.Position) < 0.3f;
                case "Sunny": return map.weatherManager.curWeather == WeatherDefOf.Clear && map.skyManager.CurSkyGlow > 0.6f;
                case "Cloudy": return map.weatherManager.curWeather != WeatherDefOf.Clear;
                case "Rain": return map.weatherManager.RainRate > 0.1f;
                case "Storm": return map.weatherManager.curWeather.defName.IndexOf("Thunder", StringComparison.OrdinalIgnoreCase) >= 0;
                case "Dry": return map.weatherManager.curWeather == WeatherDefOf.Clear && map.mapTemperature.OutdoorTemp > 28f;
                case "Cold": return p.AmbientTemperature < 5f;
                case "Hot": return p.AmbientTemperature > 32f;
                case "Mud":
                    {
                        string t = p.Position.GetTerrain(map).defName;
                        return t.IndexOf("Mud", StringComparison.OrdinalIgnoreCase) >= 0 || t.IndexOf("Marsh", StringComparison.OrdinalIgnoreCase) >= 0;
                    }
                case "Rough":
                    {
                        string t = p.Position.GetTerrain(map).defName;
                        return map.snowGrid.GetDepth(p.Position) > 0.2f || t.IndexOf("Rough", StringComparison.OrdinalIgnoreCase) >= 0
                            || t.IndexOf("Ice", StringComparison.OrdinalIgnoreCase) >= 0 || t.IndexOf("Gravel", StringComparison.OrdinalIgnoreCase) >= 0;
                    }
                case "Fighting": return GenHostility.AnyHostileActiveThreatToPlayer(map);
                case "Alone": return ColonistsNear(p, 10f) == 0;
                case "Near":
                    {
                        string[] a = arg.Split(',');
                        return ColonistsNear(p, Num(a[1])) >= (int)Num(a[0]);
                    }
                case "Duel": return HostilesNear(p, 8f) == 1 && ColonistsNear(p, 8f) == 0;
                case "Surrounded": return HostilesNear(p, 6f) >= 3;
                case "NearTrait":
                    {
                        string[] a = arg.Split(',');
                        float rad = Num(a[1]);
                        foreach (Pawn o in map.mapPawns.FreeColonistsSpawned)
                            if (o != p && CT.Has(o, a[0]) && o.Position.InHorDistOf(p.Position, rad)) return true;
                        return false;
                    }
                case "NearThing":
                    {
                        int k = arg.LastIndexOf(',');
                        return ThingNear(p, arg.Substring(0, k).Split('|'), Num(arg.Substring(k + 1)));
                    }
                case "Pets": return AnimalNear(p, Num(arg), null);
                case "Cat": return AnimalNear(p, Num(arg), new[] { "Cat" });
                case "Dog": return AnimalNear(p, Num(arg), new[] { "Labrador", "Husky", "Yorkshire", "Dog", "Terrier" });
                case "Rich": return map.resourceCounter.GetCount(ThingDefOf.Silver) >= 1500;
                case "Poor": return map.resourceCounter.GetCount(ThingDefOf.Silver) < 150;
                case "Wood": return map.resourceCounter.GetCount(ThingDefOf.WoodLog) >= 500;
                case "Lux": { Room r = p.GetRoom(); return r != null && !r.PsychologicallyOutdoors && r.GetStat(RoomStatDefOf.Impressiveness) >= 45f; }
                case "LuxNew":
                case "LuxAdapted":
                    {
                        Room r = p.GetRoom();
                        if (r == null || r.PsychologicallyOutdoors || r.GetStat(RoomStatDefOf.Impressiveness) < 45f) return false;
                        GameComponent_CT g = GameComponent_CT.Get;
                        int start;
                        if (g == null || !g.survivalStart.TryGetValue(p.thingIDNumber, out start)) return false;
                        int adaptDays = 14 + Mathf.Abs(p.thingIDNumber) % 7;
                        bool adapted = Find.TickManager.TicksGame - start >= adaptDays * 60000;
                        return name == "LuxAdapted" ? adapted : !adapted;
                    }
                case "Pretty": { Room r = p.GetRoom(); return r != null && !r.PsychologicallyOutdoors && r.GetStat(RoomStatDefOf.Beauty) >= 1.5f; }
                case "Ugly": { Room r = p.GetRoom(); return r != null && !r.PsychologicallyOutdoors && (r.GetStat(RoomStatDefOf.Beauty) < -1f || r.GetStat(RoomStatDefOf.Cleanliness) < -1f); }
                case "Clean": { Room r = p.GetRoom(); return r != null && !r.PsychologicallyOutdoors && r.GetStat(RoomStatDefOf.Cleanliness) >= 0.4f; }
                case "Dirty": { Room r = p.GetRoom(); return r != null && !r.PsychologicallyOutdoors && r.GetStat(RoomStatDefOf.Cleanliness) < -1f; }
                case "Intruder":
                    {
                        Building_Bed bed = p.ownership != null ? p.ownership.OwnedBed : null;
                        if (bed == null) return false;
                        Room r = p.GetRoom();
                        if (r == null || bed.GetRoom() != r) return false;
                        foreach (Pawn o in map.mapPawns.FreeColonistsSpawned)
                            if (o != p && o.GetRoom() == r) return true;
                        return false;
                    }
                case "AllyCorpse": return Corpses.Sees(p, true);
                case "EnemyCorpse": return Corpses.Sees(p, false);
            }
            return false;
        }
    }

    public static class Corpses
    {
        public static bool Sees(Pawn p, bool ally)
        {
            if (!p.Spawned || p.Map == null || p.Faction == null) return false;
            foreach (Thing t in p.Map.listerThings.ThingsInGroup(ThingRequestGroup.Corpse))
            {
                Corpse c = t as Corpse;
                if (c == null || c.InnerPawn == null) continue;
                if (!c.Position.InHorDistOf(p.Position, 12f)) continue;
                Pawn d = c.InnerPawn;
                if (d.Faction == null || !d.RaceProps.Humanlike) continue;
                bool isAlly = d.Faction == p.Faction;
                bool isEnemy = d.Faction.HostileTo(p.Faction);
                if ((ally && !isAlly) || (!ally && !isEnemy)) continue;
                if (!GenSight.LineOfSight(p.Position, c.Position, p.Map)) continue;
                return true;
            }
            return false;
        }
    }

    // Универсальный ситуативный ThoughtWorker
    public class TW_Cond : ThoughtWorker
    {
        protected override ThoughtState CurrentStateInternal(Pawn p)
        {
            CTCondExt e = def.GetModExtension<CTCondExt>();
            return e != null && Cond.Check(e.cond, p);
        }
    }

    // ---------- Динамические статы ----------
    public class StatPart_CT : StatPart
    {
        public override void TransformValue(StatRequest req, ref float val)
        {
            if (!req.HasThing) return;
            Pawn p = req.Thing as Pawn;
            if (p == null || p.story == null || p.story.traits == null || parentStat == null) return;
            string stat = parentStat.defName;
            try
            {
                foreach (Trait t in p.story.traits.allTraits)
                {
                    CTExt e = t.def.GetModExtension<CTExt>();
                    if (e == null || e.rules == null) continue;
                    foreach (CTRule r in e.rules)
                    {
                        if (r.stat != stat) continue;
                        if (!Cond.Check(r.cond, p)) continue;
                        float s = Cond.Scale(r.scale, p);
                        if (s <= 0f) continue;
                        val += r.offset * s;
                        val *= 1f + (r.factor - 1f) * s;
                    }
                }
                if (p.Spawned && CT.AuraStats.Contains(stat))
                {
                    foreach (Pawn o in p.Map.mapPawns.FreeColonistsSpawned)
                    {
                        if (o == p || o.story == null || o.Faction != p.Faction) continue;
                        foreach (Trait t in o.story.traits.allTraits)
                        {
                            CTExt e = t.def.GetModExtension<CTExt>();
                            if (e == null || e.auras == null) continue;
                            foreach (CTAura a in e.auras)
                            {
                                if (a.stat != stat) continue;
                                if (!o.Position.InHorDistOf(p.Position, a.radius)) continue;
                                if (!Cond.Check(a.cond, o)) continue;
                                val += a.offset;
                                val *= a.factor;
                            }
                        }
                    }
                }
            }
            catch (Exception) { }
        }

        public override string ExplanationPart(StatRequest req) { return null; }
    }

    // ---------- Компонент игры ----------
    public class GameComponent_CT : GameComponent
    {
        public Dictionary<int, int> knifeKills = new Dictionary<int, int>();
        public Dictionary<int, int> survivalStart = new Dictionary<int, int>();
        public int lastCaravan;

        public GameComponent_CT(Game game) { }

        public static GameComponent_CT Get
        {
            get { return Current.Game == null ? null : Current.Game.GetComponent<GameComponent_CT>(); }
        }

        public override void ExposeData()
        {
            Scribe_Collections.Look(ref knifeKills, "knifeKills", LookMode.Value, LookMode.Value);
            Scribe_Collections.Look(ref survivalStart, "survivalStart", LookMode.Value, LookMode.Value);
            Scribe_Values.Look(ref lastCaravan, "lastCaravan", 0);
            if (knifeKills == null) knifeKills = new Dictionary<int, int>();
            if (survivalStart == null) survivalStart = new Dictionary<int, int>();
        }

        public override void FinalizeInit()
        {
            if (lastCaravan == 0) lastCaravan = Find.TickManager.TicksGame;
            Process();
        }

        public override void GameComponentTick()
        {
            if (Find.TickManager.TicksGame % 600 == 0) Process();
        }

        static void ApplyPassions(Pawn p, CTExt e)
        {
            if (e.passions == null) return;
            foreach (string s in e.passions)
            {
                try
                {
                    string[] a = s.Split(':');
                    SkillDef sd = DefDatabase<SkillDef>.GetNamedSilentFail(a[0]);
                    if (sd == null) continue;
                    SkillRecord rec = p.skills.GetSkill(sd);
                    if (rec == null || rec.TotallyDisabled) continue;
                    Passion want = a[1] == "2" ? Passion.Major : Passion.Minor;
                    if ((int)rec.passion < (int)want) rec.passion = want;
                }
                catch (Exception) { }
            }
        }

        void Process()
        {
            int now = Find.TickManager.TicksGame;
            if (Find.WorldObjects != null && Find.WorldObjects.Caravans.Any(c => c.IsPlayerControlled)) lastCaravan = now;

            foreach (Pawn p in CT.AllPlayerPawns())
            {
                if (p == null || p.Dead || p.story == null || p.story.traits == null || p.skills == null) continue;
                foreach (Trait t in p.story.traits.allTraits)
                {
                    CTExt e = t.def.GetModExtension<CTExt>();
                    if (e != null) ApplyPassions(p, e);
                    if (t.def.defName == "CT_Survivalist" && !survivalStart.ContainsKey(p.thingIDNumber))
                        survivalStart[p.thingIDNumber] = now;
                }
            }

            foreach (Map m in Find.Maps)
            {
                foreach (Pawn p in m.mapPawns.FreeColonistsSpawned.ToList())
                {
                    if (p.Dead || p.story == null || p.story.traits == null) continue;
                    foreach (Trait t in p.story.traits.allTraits)
                    {
                        CTExt e = t.def.GetModExtension<CTExt>();
                        if (e == null) continue;

                        if (e.pulses != null)
                        {
                            foreach (CTPulse pu in e.pulses)
                            {
                                if (!Rand.Chance(pu.chance) || !Cond.Check(pu.cond, p)) continue;
                                foreach (Pawn o in m.mapPawns.FreeColonistsSpawned)
                                    if (o != p && o.Position.InHorDistOf(p.Position, pu.radius)) CT.Mem(o, pu.thought);
                            }
                        }

                        if (e.sleepLoot > 0f && !p.Awake() && Cond.Check("Night", p) && Rand.Chance(e.sleepLoot))
                        {
                            ThingDef d; int n; float r = Rand.Value;
                            if (r < 0.7f) { d = ThingDefOf.Silver; n = Rand.RangeInclusive(8, 25); }
                            else if (r < 0.9f) { d = ThingDefOf.MedicineHerbal; n = 1; }
                            else { d = ThingDefOf.ComponentIndustrial; n = 1; }
                            Thing loot = ThingMaker.MakeThing(d);
                            loot.stackCount = n;
                            GenPlace.TryPlaceThing(loot, p.Position, m, ThingPlaceMode.Near);
                            CT.Fire(p, "SleepFind");
                        }
                    }
                }
            }
        }
    }

    // ---------- События ----------
    public static class Events
    {
        public static Dictionary<int, int> tendedBy = new Dictionary<int, int>();

        public static void OnKill(Pawn victim, DamageInfo? dinfo)
        {
            if (victim == null) return;
            Map map = victim.MapHeld;
            Pawn killer = dinfo.HasValue ? dinfo.Value.Instigator as Pawn : null;

            int docId;
            if (tendedBy.TryGetValue(victim.thingIDNumber, out docId))
            {
                tendedBy.Remove(victim.thingIDNumber);
                Pawn doc = CT.AllPlayerPawns().FirstOrDefault(x => x.thingIDNumber == docId);
                if (doc != null && doc != victim) CT.Fire(doc, "PatientDeath");
            }

            if (map != null)
            {
                if (CT.IsColonist(victim)) CT.FireMap(map, "AllyDeath", victim);
                else if (victim.RaceProps.Animal && victim.Faction == Faction.OfPlayer) CT.FireMap(map, "PetDeath", victim);
            }

            if (killer == null || killer == victim || !dinfo.HasValue) return;

            bool ranged = dinfo.Value.Def != null && dinfo.Value.Def.isRanged;
            bool melee = !ranged && killer.Position.DistanceTo(victim.Position) < 2.5f;

            if (victim.RaceProps.Animal && ranged && killer.CurJobDef == JobDefOf.Hunt)
            {
                CT.Fire(killer, "HuntKill");
                ThingDef w = dinfo.Value.Weapon;
                if (w != null && w.defName == "Gun_HuntingRifle") CT.Fire(killer, "HuntRifleKill");
            }

            if (!victim.HostileTo(killer)) return;

            CT.Fire(killer, "Kill");
            if (melee) CT.Fire(killer, "KillMelee");
            if (melee && Cond.Check("Duel", killer)) CT.Fire(killer, "KillDuel");
            if (melee && CT.HoldsKnife(killer))
            {
                if (CT.Has(killer, "CT_Maniac"))
                {
                    GameComponent_CT c = GameComponent_CT.Get;
                    if (c != null)
                    {
                        int n;
                        c.knifeKills.TryGetValue(killer.thingIDNumber, out n);
                        c.knifeKills[killer.thingIDNumber] = n + 1;
                    }
                }
                CT.Fire(killer, "KillKnife");
            }
            if (ranged && killer.Position.DistanceTo(victim.Position) >= 25f) CT.Fire(killer, "KillLong");
            if (killer.health.summaryHealth.SummaryHealthPercent < 0.5f) CT.Fire(killer, "KillHurt");
            if (dinfo.Value.Def != null && dinfo.Value.Def.isExplosive) CT.Fire(killer, "KillExplosive");
            if (dinfo.Value.Def == DamageDefOf.Flame) CT.Fire(killer, "KillFire");

            if (CT.Has(killer, "CT_Avenger") && CT.Knows(killer, "CT_Av_Wish"))
            {
                CT.Forget(killer, "CT_Av_Wish");
                CT.Mem(killer, "CT_Av_Done");
            }
            if (killer.Faction == Faction.OfPlayer && map != null) CT.FireMap(map, "SquadWin", null);
        }

        public static void OnHit(Pawn victim, ref DamageInfo dinfo)
        {
            if (victim == null) return;
            Pawn attacker = dinfo.Instigator as Pawn;

            // защита жертвы
            if (victim.story != null && victim.story.traits != null && dinfo.Def != null && dinfo.Def.harmsHealth)
            {
                foreach (Trait t in victim.story.traits.allTraits)
                {
                    CTExt e = t.def.GetModExtension<CTExt>();
                    if (e != null && e.luck > 0f && Rand.Chance(e.luck)) { dinfo.SetAmount(0f); break; }
                }
                CT.Fire(victim, "Hurt");
            }

            if (attacker == null || attacker == victim) return;
            if (dinfo.Def == null) return;

            // бонус против брони (бронебой)
            if (dinfo.Def.isRanged && victim.apparel != null && victim.apparel.WornApparelCount > 0 && attacker.story != null && attacker.story.traits != null)
            {
                foreach (Trait t in attacker.story.traits.allTraits)
                {
                    CTExt e = t.def.GetModExtension<CTExt>();
                    if (e != null && e.vsArmor > 0f) { dinfo.SetAmount(dinfo.Amount * (1f + e.vsArmor)); break; }
                }
            }

            if (dinfo.Def.isRanged) return;
            if (attacker.Position.DistanceTo(victim.Position) > 2.5f) return;

            if (victim.Faction == attacker.Faction && victim.RaceProps.Humanlike)
                CT.Fire(attacker, CT.IsGuilty(victim) ? "HitGuilty" : "HitAlly");
            if (victim.Downed && victim.HostileTo(attacker)) CT.Fire(attacker, "HitDowned");
        }

        public static void OnTend(Pawn doctor, Pawn patient)
        {
            if (doctor == null || patient == null) return;
            tendedBy[patient.thingIDNumber] = doctor.thingIDNumber;
            CT.Fire(doctor, "Tend");
            CT.Fire(doctor, "Work");
            if (patient.Downed) CT.Fire(doctor, "TendDowned");
        }

        public static void OnBuilt(Pawn worker)
        {
            if (worker == null) return;
            CT.Fire(worker, "Built");
            CT.Fire(worker, "Work");
        }

        public static void OnResearch(Pawn researcher)
        {
            if (researcher != null) { CT.Fire(researcher, "Research"); CT.Fire(researcher, "Work"); return; }
            foreach (Map m in Find.Maps) CT.FireMap(m, "Research", null);
        }

        public static void OnHarvest(Pawn by)
        {
            if (by == null) return;
            CT.Fire(by, "Harvest");
            CT.Fire(by, "Work");
        }

        public static void OnIngest(Thing food, Pawn ingester)
        {
            if (food == null || ingester == null || food.def.ingestible == null) return;
            switch (food.def.ingestible.preferability)
            {
                case FoodPreferability.MealLavish: CT.Fire(ingester, "EatLavish"); break;
                case FoodPreferability.MealFine: CT.Fire(ingester, "EatFine"); break;
                case FoodPreferability.MealSimple: CT.Fire(ingester, "EatSimple"); break;
                case FoodPreferability.MealAwful: CT.Fire(ingester, "EatAwful"); break;
                case FoodPreferability.RawTasty:
                case FoodPreferability.RawBad: CT.Fire(ingester, "EatRaw"); break;
            }
        }
    }

    // ---------- Harmony ----------
    public static class Patches
    {
        public static void KillPostfix(Pawn __instance, DamageInfo? dinfo)
        {
            try { Events.OnKill(__instance, dinfo); }
            catch (Exception e) { Log.ErrorOnce("[CharacterTraits] Kill: " + e, 810001); }
        }

        public static void HitPrefix(Pawn __instance, ref DamageInfo dinfo)
        {
            try { Events.OnHit(__instance, ref dinfo); }
            catch (Exception e) { Log.ErrorOnce("[CharacterTraits] Hit: " + e, 810002); }
        }

        public static void TendPostfix(Pawn doctor, Pawn patient)
        {
            try { Events.OnTend(doctor, patient); }
            catch (Exception e) { Log.ErrorOnce("[CharacterTraits] Tend: " + e, 810003); }
        }

        public static void BuildPostfix(Pawn worker)
        {
            try { Events.OnBuilt(worker); }
            catch (Exception e) { Log.ErrorOnce("[CharacterTraits] Build: " + e, 810004); }
        }

        public static void ResearchPostfix(Pawn researcher)
        {
            try { Events.OnResearch(researcher); }
            catch (Exception e) { Log.ErrorOnce("[CharacterTraits] Research: " + e, 810005); }
        }

        public static void HarvestPostfix(Pawn by)
        {
            try { Events.OnHarvest(by); }
            catch (Exception e) { Log.ErrorOnce("[CharacterTraits] Harvest: " + e, 810006); }
        }

        public static void IngestPostfix(Thing __instance, Pawn ingester)
        {
            try { Events.OnIngest(__instance, ingester); }
            catch (Exception e) { Log.ErrorOnce("[CharacterTraits] Ingest: " + e, 810007); }
        }

        public static bool BreakPrefix(Pawn ___pawn, ref bool __result)
        {
            try
            {
                float chance = 0f;
                foreach (Trait t in ___pawn.story.traits.allTraits)
                {
                    CTExt e = t.def.GetModExtension<CTExt>();
                    if (e != null && e.breakBerserk > chance) chance = e.breakBerserk;
                }
                if (chance > 0f && Rand.Chance(chance))
                {
                    if (___pawn.mindState.mentalStateHandler.TryStartMentalState(MentalStateDefOf.Berserk, "нервный срыв", false, true))
                    {
                        __result = true;
                        return false;
                    }
                }
            }
            catch (Exception) { }
            return true;
        }
    }

    [StaticConstructorOnStartup]
    public static class Init
    {
        static int ok = 0;
        static int bad = 0;

        static Init()
        {
            Harmony h = new Harmony("local.charactertraits");
            Try(h, typeof(Pawn), "Kill", null, "KillPostfix");
            Try(h, typeof(Pawn), "PreApplyDamage", "HitPrefix", null);
            Try(h, typeof(TendUtility), "DoTend", null, "TendPostfix");
            Try(h, typeof(Frame), "CompleteConstruction", null, "BuildPostfix");
            Try(h, typeof(ResearchManager), "FinishProject", null, "ResearchPostfix");
            Try(h, typeof(Plant), "PlantCollected", null, "HarvestPostfix");
            Try(h, typeof(Thing), "Ingested", null, "IngestPostfix");
            Try(h, AccessTools.TypeByName("MentalBreaker"), "TryDoRandomMoodCausedMentalBreak", "BreakPrefix", null);
            Log.Message("[CharacterTraits] патчей применено: " + ok + ", не применено: " + bad);
        }

        static void Try(Harmony h, Type t, string name, string prefix, string postfix)
        {
            try
            {
                if (t == null) { bad++; Log.Warning("[CharacterTraits] не найден тип для " + name); return; }
                MethodInfo m = AccessTools.Method(t, name);
                if (m == null) { bad++; Log.Warning("[CharacterTraits] не найден метод " + t.Name + "." + name); return; }
                HarmonyMethod pre = prefix == null ? null : new HarmonyMethod(typeof(Patches), prefix);
                HarmonyMethod post = postfix == null ? null : new HarmonyMethod(typeof(Patches), postfix);
                h.Patch(m, pre, post);
                ok++;
            }
            catch (Exception e)
            {
                bad++;
                Log.Warning("[CharacterTraits] патч " + name + " не применился: " + e.Message);
            }
        }
    }
}