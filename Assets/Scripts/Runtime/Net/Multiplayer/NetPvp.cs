// NetPvp.cs
// Remake-only: the server's side of players hitting players (NetPlayer.HitUpRpc, never an NPC's shot): every hit is
// checked against its weapon, scaled by the server's PvP settings and written to the log.
//   Check: a hit can't be bigger than its weapon can deal (attr 9 x the strongest weapon mod's damage factor, ItemMax);
//     a bigger one (a modified game, the Debug menu's one-hit kills) is cut to that and logged as a warning. A hit with no
//     weapon item (-1) or a weapon without attr 9 is cut to MaxHit only.
//   Scale (server settings, NetServerSettings: pvpdamage, pvpblast, pvpbeam, pvpmissile, pvpmaxhit): percentages of the
//     weapon's damage between players; blasts = nukes, EMP bombs, ionizing missiles, the Shock Blast and mines; beams = the
//     beam lasers (9-11, 228, a mod's by its look); missiles = rockets, missiles, cluster missiles. pvpdamage applies to
//     every hit on top. pvpmaxhit caps one hit after scaling (0 = no cap). The defaults (100 %, no cap) are the original's.
//   Log ("Server: pvp ...", grep "Server: pvp"): each shooter's hits on one victim with one weapon are summed while they come
//     and written once they stop for FlushSeconds ("A hit B with Extinctor (46): 1 hit, 650 dealt (650 sent), biggest 650;
//     B now shield 0%, armor 0%, hull 12%"); a player destroyed by another gets every hit they took from players in the last
//     KillWindow seconds, by shooter and weapon, in one line (OnKill, from NetState.DestroyedByRpc).

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using GoF2Remake.Data;
using GoF2Remake.Flight;
using UnityEngine;

namespace GoF2Remake.Multiplayer
{
    [Unity.Scripting.LifecycleManagement.NoAutoStaticsCleanup]
    public static class NetPvp
    {
        /// <summary>A run of hits ends this long after its last hit.</summary>
        const float FlushSeconds = 3f;
        /// <summary>A kill's breakdown covers the hits of this many seconds before it.</summary>
        const float KillWindow = 20f;

        public const int DefaultPercent = 100;

        /// <summary>Server settings: percent of the weapon's damage between players.</summary>
        public static int DamagePercent = DefaultPercent, BlastPercent = DefaultPercent, BeamPercent = DefaultPercent, MissilePercent = DefaultPercent;
        /// <summary>Server setting: the most one hit may deal after scaling, 0 = no cap.</summary>
        public static int MaxHit;

        class Run
        {
            public ulong shooter, victim;
            public int item, hits;
            public double sent, dealt;
            public float biggest, last;
            public bool cut;
        }

        struct Recent { public float time; public ulong shooter; public int item; public float dealt; }

        static readonly Dictionary<(ulong, ulong, int), Run> runs = new Dictionary<(ulong, ulong, int), Run>();
        static readonly Dictionary<ulong, List<Recent>> recent = new Dictionary<ulong, List<Recent>>();
        static float maxModFactor = -1f;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            runs.Clear();
            recent.Clear();
            maxModFactor = -1f;
            DamagePercent = BlastPercent = BeamPercent = MissilePercent = DefaultPercent;
            MaxHit = 0;
        }

        /// <summary>DedicatedServer.Boot: the command line's values (-pvpdamage N ...).</summary>
        public static void Configure(int damage, int blast, int beam, int missile, int maxHit)
        {
            DamagePercent = Mathf.Clamp(damage, 0, 1000);
            BlastPercent = Mathf.Clamp(blast, 0, 1000);
            BeamPercent = Mathf.Clamp(beam, 0, 1000);
            MissilePercent = Mathf.Clamp(missile, 0, 1000);
            MaxHit = Mathf.Max(0, maxHit);
        }

        public enum Class { Gun, Blast, Beam, Missile }

        /// <summary>Which PvP setting scales 'item' (null / unknown = a gun).</summary>
        public static Class ClassOf(ItemData it)
        {
            if (it == null) return Class.Gun;
            if (Gun.IsBeamLook(it.Look)) return Class.Beam;
            switch ((Gun.Kind)it.categoryId)
            {
                case Gun.Kind.Nuke: case Gun.Kind.EmpBomb: case Gun.Kind.Ionizing: case Gun.Kind.ShockBlast: case Gun.Kind.Mine:
                    return Class.Blast;
                case Gun.Kind.Rocket: case Gun.Kind.Missile: case Gun.Kind.ClusterMissile:
                    return Class.Missile;
                default:
                    return Class.Gun;
            }
        }

        static int PercentOf(Class c) => c switch
        {
            Class.Blast => BlastPercent,
            Class.Beam => BeamPercent,
            Class.Missile => MissilePercent,
            _ => DefaultPercent,
        };

        /// <summary>The most one hit of 'it' can deal: attr 9 x the strongest weapon mod (sort 28, attr 40), 0 = unknown.</summary>
        static float ItemMax(ItemData it)
        {
            if (it == null) return 0f;
            int dmg = it.Attr(9);
            if (dmg <= 0) return 0f;
            if (maxModFactor < 0f)
            {
                maxModFactor = 1f;
                foreach (var m in NetGame.Db.Items)
                    if (m != null && m.categoryId == 28) maxModFactor = Mathf.Max(maxModFactor, 1f + m.Attr(40) / 100f);
            }
            return dmg * maxModFactor + 1f;
        }

        /// <summary>NetPlayer.HitUpRpc (server, a player's hit on another player, already checked for range and squads):
        /// what the victim takes. Logged.</summary>
        public static float OnHit(ulong shooter, NetPlayer victim, int item, float amount)
        {
            var it = item >= 0 ? NetGame.Db.Item(item) : null;
            float sent = amount;
            bool cut = false;
            float max = ItemMax(it);
            if (max > 0f && amount > max) { amount = max; cut = true; }
            amount *= PercentOf(ClassOf(it)) / 100f * (DamagePercent / 100f);
            if (MaxHit > 0 && amount > MaxHit) amount = MaxHit;
            Note(shooter, victim.OwnerClientId, item, sent, amount, cut);
            if (cut) Debug.LogWarning($"Server: pvp {Name(shooter)} sent a hit of {sent:N0} with {ItemName(item)}, more than it can deal ({max:N0}); cut");
            return amount;
        }

        static void Note(ulong shooter, ulong victim, int item, float sent, float dealt, bool cut)
        {
            float now = Time.realtimeSinceStartup;
            var key = (shooter, victim, item);
            if (!runs.TryGetValue(key, out var r)) runs[key] = r = new Run { shooter = shooter, victim = victim, item = item };
            r.hits++;
            r.sent += sent;
            r.dealt += dealt;
            r.biggest = Mathf.Max(r.biggest, dealt);
            r.cut |= cut;
            r.last = now;
            if (!recent.TryGetValue(victim, out var list)) recent[victim] = list = new List<Recent>();
            list.Add(new Recent { time = now, shooter = shooter, item = item, dealt = dealt });
            list.RemoveAll(h => now - h.time > KillWindow);
        }

        /// <summary>NetState.Update (server): the runs that stopped, into the log; 'all' = every run (the session ends).</summary>
        public static void Tick(bool all = false)
        {
            if (runs.Count == 0) return;
            float now = Time.realtimeSinceStartup;
            List<(ulong, ulong, int)> done = null;
            foreach (var kv in runs)
                if (all || now - kv.Value.last >= FlushSeconds) (done ??= new List<(ulong, ulong, int)>()).Add(kv.Key);
            if (done == null) return;
            foreach (var key in done)
            {
                Log(runs[key]);
                runs.Remove(key);
            }
            if (all) recent.Clear();
        }

        static void Log(Run r)
        {
            var v = NetSquad.Find(r.victim);
            string scaled = Math.Abs(r.sent - r.dealt) > 0.5 ? $" ({r.sent:N0} sent)" : "";
            string line = $"Server: pvp {Name(r.shooter)} hit {Name(r.victim)} with {ItemName(r.item)}: {r.hits} hit{(r.hits == 1 ? "" : "s")}, " +
                          $"{r.dealt:N0} dealt{scaled}, biggest {r.biggest:N0}{(v != null ? "; " + Name(r.victim) + " now " + v.PoolText : "")}";
            if (r.cut) Debug.LogWarning(line + "; CUT to the weapon's most");
            else Debug.Log(line);
        }

        /// <summary>NetState.DestroyedByRpc (server): a player destroyed by another; every player hit they took in the last
        /// KillWindow seconds, by shooter and weapon.</summary>
        public static void OnKill(ulong victim, ulong killer)
        {
            // The runs on this victim first, so their lines come before the summary.
            foreach (var key in runs.Keys.Where(k => k.Item2 == victim).ToList())
            {
                Log(runs[key]);
                runs.Remove(key);
            }
            float now = Time.realtimeSinceStartup;
            var sb = new StringBuilder($"Server: pvp {Name(victim)} destroyed by {Name(killer)}; player hits in the last {KillWindow:0} s:");
            if (recent.TryGetValue(victim, out var list))
            {
                var hits = list.Where(h => now - h.time <= KillWindow).ToList();
                float first = hits.Count > 0 ? hits.Min(h => h.time) : now;
                foreach (var g in hits.GroupBy(h => (h.shooter, h.item)))
                    sb.Append($" {Name(g.Key.shooter)} {ItemName(g.Key.item)} {g.Count()} x = {g.Sum(h => h.dealt):N0} (biggest {g.Max(h => h.dealt):N0});");
                sb.Append($" {hits.Sum(h => h.dealt):N0} in all over {now - first:0.0} s");
                recent.Remove(victim);
            }
            else sb.Append(" none logged");
            Debug.Log(sb.ToString());
        }

        /// <summary>/settings and the log: the PvP scaling in one line.</summary>
        public static string Summary =>
            $"all {DamagePercent}%, blasts {BlastPercent}%, beams {BeamPercent}%, missiles {MissilePercent}%, max hit {(MaxHit > 0 ? MaxHit.ToString("N0") : "none")}";

        static string Name(ulong client)
        {
            var p = NetSquad.Find(client);
            return p != null ? p.DisplayName : $"client {client}";
        }

        static string ItemName(int item)
        {
            if (item < 0) return "an unknown weapon";
            var it = NetGame.Db.Item(item);
            return it != null && !string.IsNullOrEmpty(it.name) ? $"{it.name} ({item})" : $"item {item}";
        }
    }
}
