using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using GTA;
using GTA.Math;
using GTA.Native;

namespace NpcVoice
{
    /// <summary>
    /// Physical actions NPCs do along with what they say. After a reply is written, the action picker (Jev or an open LLM) picks one from
    /// ActionCatalog.All (a Choice over the player's words and the reply); the action then plays while
    /// the line is spoken. Each action is a short step sequence on the game thread (walk up, face the
    /// player, animation, back to normal). Animations are listed as candidates and checked against the
    /// game's files at startup (<see cref="Audit"/>), so a missing one falls back instead of doing nothing.
    /// </summary>
    internal sealed class NpcActions
    {
        // Candidate animations (dict, clip); the first one in the game's files is used.
        private static readonly Dictionary<string, (string Dict, string Clip)[]> Anims = new Dictionary<string, (string, string)[]>
        {
            ["slap"] = new[] { ("melee@unarmed@streamed_variations", "plyr_takedown_front_slap") },
            ["slap_victim"] = new[] { ("melee@unarmed@streamed_variations", "victim_takedown_front_slap") },
            ["headbutt"] = new[] { ("melee@unarmed@streamed_variations", "plyr_takedown_front_headbutt") },
            ["headbutt_victim"] = new[] { ("melee@unarmed@streamed_variations", "victim_takedown_front_headbutt") },
            ["middle_finger"] = new[] { ("anim@mp_player_intcelebrationmale@finger", "finger"), ("mp_player_intfinger", "mp_player_int_finger"), ("anim@mp_player_intselfiethe_bird", "idle_a") },
            ["facepalm"] = new[] { ("anim@mp_player_intcelebrationmale@face_palm", "face_palm"), ("anim@mp_player_intupperface_palm", "idle_a") },
            ["square_up"] = new[] { ("anim@mp_player_intcelebrationmale@shadow_boxing", "shadow_boxing"), ("anim@mp_player_intupperknuckle_crunch", "idle_a") },
            // GTA Online's safehouse shower clip: the character strips off. Cut before they reach for the water.
            ["shirt_off"] = new[] { ("mp_safehouseshower@male@", "male_shower_undress_&_turn_on_water"), ("clothingshirt", "try_shirt_positive_d") },
            ["pants_off"] = new[] { ("mp_safehouseshower@male@", "male_shower_undress_&_turn_on_water"), ("clothingtrousers", "try_trousers_neutral_a") },
            ["pants_off_female"] = new[] { ("mp_safehouseshower@female@", "shower_undress_&_turn_on_water"), ("clothingtrousers", "try_trousers_neutral_a") },
            // Bend down to the floor, frozen at the bottom (hands at the shoes); the bin lean is the fallback.
            ["bend_over"] = new[] { ("pickup_object", "pickup_low"), ("amb@prop_human_bum_bin@base", "base") },
            ["hug"] = new[] { ("mp_ped_interaction", "hugs_guy_a") },
            ["hug_player"] = new[] { ("mp_ped_interaction", "hugs_guy_b") },
            ["kiss"] = new[] { ("mp_ped_interaction", "kisses_guy_a") },
            ["kiss_player"] = new[] { ("mp_ped_interaction", "kisses_guy_b") },
            // Drop to the knees, hands up, then get back up.
            ["kneel_enter"] = new[] { ("random@arrests@busted", "enter") },
            ["kneel_beg"] = new[] { ("random@arrests@busted", "idle_a"), ("rcmextreme3", "idle"), ("amb@medic@standing@kneel@base", "base") },
            ["kneel_exit"] = new[] { ("random@arrests@busted", "exit") },
        };

        private sealed class Running
        {
            public Ped Ped;
            public string Name;
            public IEnumerator<int> Steps;
            public int WakeAt;
        }

        private sealed class Queued
        {
            public Ped Ped;
            public Task<JevChoice> Pick;
            public Exchange Exchange;
            public bool Female;
            public int Deadline;
        }

        private readonly List<Running> _running = new List<Running>();
        private readonly List<Queued> _queued = new List<Queued>();
        private readonly Dictionary<int, (string Name, int At)> _lastDone = new Dictionary<int, (string, int)>(); // ped -> last action it chose itself
        private const int RepeatMs = 60000; // the same mood action twice in a minute gets old; orders always happen
        private readonly Dictionary<string, (string Dict, string Clip)> _resolved = new Dictionary<string, (string, string)>();
        private readonly Func<Ped, bool> _isScenePed;
        private readonly string _undressFile;
        private readonly Dictionary<int, Dictionary<string, int[][]>> _undress = new Dictionary<int, Dictionary<string, int[][]>>();    // model -> part -> component changes
        private readonly Dictionary<int, Dictionary<string, int[][]>> _overrides = new Dictionary<int, Dictionary<string, int[][]>>(); // saved in dev mode

        /// <param name="undressFile">Per-model clothing variants for shirt_off / pants_off (dev mode [ and ] save them).</param>
        /// <param name="isScenePed">Peds the dev scene spawned: they go back to standing still after an action.</param>
        public NpcActions(string undressFile, Func<Ped, bool> isScenePed)
        {
            _undressFile = undressFile;
            _isScenePed = isScenePed;
            LoadUndress();
        }



        /// <summary>The picker is choosing an action for this line; it starts when the pick comes back (game thread).</summary>
        public void Queue(Ped ped, Task<JevChoice> pick, Exchange exchange, bool female)
        {
            _queued.Add(new Queued { Ped = ped, Pick = pick, Exchange = exchange, Female = female, Deadline = Environment.TickCount + 8000 });
        }

        /// <summary>Every frame: start actions whose pick arrived, advance running ones.</summary>
        public void Tick()
        {
            for (int i = _queued.Count - 1; i >= 0; i--)
            {
                Queued q = _queued[i];
                if (q.Pick.IsCompleted)
                {
                    _queued.RemoveAt(i);
                    OnPick(q);
                }
                else if (unchecked(Environment.TickCount - q.Deadline) > 0)
                {
                    _queued.RemoveAt(i);
                    Log.Write("action: the picker took too long; skipped");
                }
            }

            int now = Environment.TickCount;
            for (int i = _running.Count - 1; i >= 0; i--)
            {
                Running r = _running[i];
                if (unchecked(now - r.WakeAt) < 0) continue;
                bool more;
                try { more = (r.Ped == null || r.Ped.Exists()) && r.Steps.MoveNext(); }
                catch (Exception ex)
                {
                    Log.Write($"action {r.Name} failed: {ex.Message}");
                    more = false;
                }
                if (more) r.WakeAt = now + Math.Max(0, r.Steps.Current);
                else
                {
                    r.Steps.Dispose(); // runs the sequence's finally: back to normal
                    _running.RemoveAt(i);
                }
            }
        }

        /// <summary>Start an action on a ped now (a pick, or the dev-mode test key), aimed at the player or at
        /// <paramref name="target"/> (another NPC, for scenes between NPCs).</summary>
        public void Perform(Ped ped, string name, Ped target = null)
        {
            if (ped == null || !ped.Exists() || ped.IsDead || ActionCatalog.Find(name) == null || name == "none") return;
            Stop(ped);
            Log.Write($"action {name} -> {ped.Model.Hash}");
            _running.Add(new Running { Ped = ped, Name = name, Steps = Sequence(ped, name, target ?? Game.Player.Character).GetEnumerator(), WakeAt = Environment.TickCount });
        }

        /// <summary>What this ped is doing right now (null if nothing), for the director's camera.</summary>
        public string ActingOn(Ped ped) => _running.FirstOrDefault(r => r.Ped == ped)?.Name;

        /// <summary>The picker is still choosing, or an action is still playing, for this ped.</summary>
        public bool Busy(Ped ped) => _queued.Any(q => q.Ped == ped) || _running.Any(r => r.Ped == ped);

        public void Stop(Ped ped)
        {
            for (int i = _running.Count - 1; i >= 0; i--)
            {
                if (_running[i].Ped != ped) continue;
                _running[i].Steps.Dispose();
                _running.RemoveAt(i);
            }
        }

        public void StopAll()
        {
            foreach (Running r in _running) r.Steps.Dispose();
            _running.Clear();
            _queued.Clear();
        }

        /// <summary>Check every candidate animation against the game files once, and log what each action uses.</summary>
        public void Audit()
        {
            _running.Add(new Running { Name = "audit", Steps = AuditSteps().GetEnumerator(), WakeAt = Environment.TickCount });
        }

        private IEnumerable<int> AuditSteps()
        {
            var report = new List<string>();
            foreach (string key in Anims.Keys.ToList())
            {
                foreach (int w in Resolve(key)) yield return w;
                report.Add(_resolved.TryGetValue(key, out var a) ? $"{key}={a.Dict}/{a.Clip}" : $"{key}=MISSING");
            }
            Log.Write("action anims: " + string.Join(", ", report));
        }

        private void OnPick(Queued q)
        {
            if (q.Pick.IsFaulted)
            {
                Log.Write("action: the picker failed: " + q.Pick.Exception?.GetBaseException().Message);
                if (q.Exchange != null) q.Exchange.Action = "pick error";
                return;
            }
            JevChoice c = q.Pick.Result;
            ActionCatalog.Kind kind = ActionCatalog.Find(c.Choice);
            bool act = ActionCatalog.Acts(c, q.Female);
            bool repeat = act && !c.Ordered && _lastDone.TryGetValue(q.Ped.Handle, out var last) && last.Name == c.Choice
                && unchecked(Environment.TickCount - last.At) < RepeatMs;
            if (repeat) act = false;
            if (act && !c.Ordered) _lastDone[q.Ped.Handle] = (c.Choice, Environment.TickCount);
            string summary = $"{(c.Ordered ? "told: " : "")}{c.Choice} {c.Probability:P0}{(repeat ? " (again, skipped)" : act ? "" : " (no)")}";
            if (q.Exchange != null)
            {
                q.Exchange.Action = summary;
                q.Exchange.ActionSeconds = c.Seconds;
            }
            Log.Write($"action pick: {summary}, confidence {c.Confidence:F2}, jev {c.Seconds:F2}s");
            if (act && q.Ped.Exists() && !q.Ped.IsDead) Perform(q.Ped, kind.Name);
        }

        // ------------------------------------------------------------ the actions

        private IEnumerable<int> Sequence(Ped ped, string name, Ped player) // player: whoever it's aimed at
        {
            try
            {
                switch (name)
                {
                    case "punch":
                        Unarmed(ped);
                        Function.Call(Hash.TASK_COMBAT_PED, ped, player, 0, 16);
                        yield return 4500;
                        break;

                    case "slap":
                    case "headbutt":
                        foreach (int w in Resolve(name)) yield return w;
                        foreach (int w in Resolve(name + "_victim")) yield return w;
                        foreach (int w in Approach(ped, player, 0.85f)) yield return w;
                        Face(ped, player);
                        Face(player, ped);
                        int hit = Play(ped, name, 0, -1);
                        Play(player, name + "_victim", 0, -1);
                        if (!_resolved.ContainsKey(name + "_victim")) Function.Call(Hash.SHAKE_GAMEPLAY_CAM, "SMALL_EXPLOSION_SHAKE", 0.25f);
                        yield return Math.Max(hit, 1200);
                        break;

                    case "shoot":
                        ped.Weapons.Give(WeaponHash.Pistol, 60, true, true);
                        Face(ped, player);
                        yield return 500;
                        ped.Task.ShootAt(player, 3000, FiringPattern.FullAuto);
                        yield return 3500;
                        break;

                    case "threaten_gun":
                        ped.Weapons.Give(WeaponHash.Pistol, 60, true, true);
                        yield return 300;
                        Function.Call(Hash.TASK_AIM_GUN_AT_ENTITY, ped, player, 7000, false);
                        yield return 7000;
                        break;

                    case "middle_finger":
                    case "facepalm":
                    case "square_up":
                        foreach (int w in Resolve(name)) yield return w;
                        Face(ped, player);
                        yield return Math.Max(Play(ped, name, 0, name == "square_up" ? 4000 : -1), 1500);
                        break;

                    case "kneel_beg":
                        foreach (string key in new[] { "kneel_enter", "kneel_beg", "kneel_exit" })
                            foreach (int w in Resolve(key)) yield return w;
                        Face(ped, player);
                        yield return Math.Max(Play(ped, "kneel_enter", 2, -1) - 100, 0); // down onto the knees
                        Play(ped, "kneel_beg", 1, 6000);                                     // hands up, pleading
                        yield return 6000;
                        yield return Play(ped, "kneel_exit", 0, -1);                        // back up
                        break;

                    case "bend_over":
                        foreach (int w in Resolve(name)) yield return w;
                        Face(ped, player);
                        ped.Heading += 180f; // back to the player
                        if (_resolved.TryGetValue(name, out var bend) && bend.Dict == "pickup_object")
                        {
                            // Bend down as if picking something off the floor and hold the lowest point.
                            int length = Play(ped, name, 0, -1);
                            yield return 150;
                            Function.Call(Hash.SET_ENTITY_ANIM_CURRENT_TIME, ped, bend.Dict, bend.Clip, 0.42f);
                            Function.Call(Hash.SET_ENTITY_ANIM_SPEED, ped, bend.Dict, bend.Clip, 0f);
                            yield return 5000;
                            Function.Call(Hash.SET_ENTITY_ANIM_SPEED, ped, bend.Dict, bend.Clip, 1f);
                            yield return Math.Max((int)(length * 0.58f), 400);
                        }
                        else
                        {
                            Play(ped, name, 1, 6000);
                            yield return 6000;
                        }
                        break;

                    case "shirt_off":
                    case "pants_off":
                        string part = name == "shirt_off" ? "shirt" : "pants";
                        string clip = name == "pants_off" && ped.Gender == Gender.Female ? "pants_off_female" : name;
                        foreach (int w in Resolve(clip)) yield return w;
                        Face(ped, player);
                        bool hasLook = HasLook(ped, part);
                        if (!hasLook && part == "shirt")
                        {
                            // Nothing under this character's shirt in the game files: square up instead.
                            foreach (int w in Resolve("square_up")) yield return w;
                            yield return Math.Max(Play(ped, "square_up", 0, 4000), 1500);
                            break;
                        }
                        int full = Play(ped, clip, 0, -1);
                        bool shower = _resolved.TryGetValue(clip, out var undressAnim) && undressAnim.Dict.StartsWith("mp_safehouseshower");
                        // The shower clip strips first and then reaches for the tap: swap clothes mid-strip, stop before the tap.
                        int swapAt = shower ? (int)(full * 0.4f) : Math.Min(1500, Math.Max(full / 2, 500));
                        int stopAt = shower ? (int)(full * 0.62f) : full;
                        yield return swapAt;
                        Undress(ped, part);
                        yield return Math.Max(stopAt - swapAt, 600);
                        if (part == "shirt")
                        {
                            foreach (int w in Resolve("square_up")) yield return w;
                            Play(ped, "square_up", 0, 3000);
                            yield return 3000;
                        }
                        break;

                    case "hug":
                    case "kiss":
                        foreach (int w in Resolve(name)) yield return w;
                        foreach (int w in Resolve(name + "_player")) yield return w;
                        foreach (int w in Approach(ped, player, 1.0f)) yield return w;
                        // Face to face, close together, then both play their half.
                        Vector3 toPed = ped.Position - player.Position;
                        toPed.Z = 0f;
                        if (toPed.Length() < 0.1f) toPed = player.ForwardVector;
                        ped.PositionNoOffset = player.Position + toPed.Normalized * 0.6f + new Vector3(0f, 0f, ped.Position.Z - player.Position.Z);
                        Face(ped, player);
                        Face(player, ped);
                        int together = Play(ped, name, 0, -1);
                        Play(player, name + "_player", 0, -1);
                        yield return Math.Max(together, 2000);
                        break;

                    case "run_away":
                        Function.Call(Hash.TASK_SMART_FLEE_PED, ped, player, 150f, 15000, false, false);
                        yield return 15000;
                        break;
                }
            }
            finally
            {
                Restore(ped, name);
            }
        }

        // ------------------------------------------------------------ helpers

        /// <summary>Find the first candidate animation that exists and load its dictionary.</summary>
        private IEnumerable<int> Resolve(string key)
        {
            if (_resolved.ContainsKey(key) || !Anims.TryGetValue(key, out var candidates)) yield break;
            foreach (var (dict, clip) in candidates)
            {
                if (!Function.Call<bool>(Hash.DOES_ANIM_DICT_EXIST, dict)) continue;
                Function.Call(Hash.REQUEST_ANIM_DICT, dict);
                for (int i = 0; i < 40 && !Function.Call<bool>(Hash.HAS_ANIM_DICT_LOADED, dict); i++) yield return 25;
                if (!Function.Call<bool>(Hash.HAS_ANIM_DICT_LOADED, dict)) continue;
                if (Function.Call<float>(Hash.GET_ANIM_DURATION, dict, clip) <= 0f) continue;
                _resolved[key] = (dict, clip);
                yield break;
            }
        }

        /// <summary>Play a resolved animation; returns its length in ms (0 if it isn't available).</summary>
        private int Play(Ped ped, string key, int flags, int durationMs)
        {
            if (!_resolved.TryGetValue(key, out var a)) return 0;
            if (!Function.Call<bool>(Hash.HAS_ANIM_DICT_LOADED, a.Dict)) Function.Call(Hash.REQUEST_ANIM_DICT, a.Dict);
            Function.Call(Hash.TASK_PLAY_ANIM, ped, a.Dict, a.Clip, 4f, -4f, durationMs, flags, 0f, false, false, false);
            return durationMs > 0 ? durationMs : (int)(Function.Call<float>(Hash.GET_ANIM_DURATION, a.Dict, a.Clip) * 1000);
        }

        private static IEnumerable<int> Approach(Ped ped, Ped player, float distance)
        {
            if (ped.Position.DistanceTo(player.Position) <= distance + 0.4f) yield break;
            Function.Call(Hash.TASK_GO_TO_ENTITY, ped, player, 6000, distance, 2.0f, 1073741824f, 0);
            for (int t = 0; t < 6000 && ped.Exists() && ped.Position.DistanceTo(player.Position) > distance + 0.3f; t += 100)
                yield return 100;
        }

        private static void Face(Ped ped, Entity target)
        {
            Vector3 d = target.Position - ped.Position;
            if (d.X * d.X + d.Y * d.Y < 0.0001f) return;
            ped.Heading = (float)(Math.Atan2(-d.X, d.Y) * 180.0 / Math.PI);
        }

        private static void Unarmed(Ped ped) =>
            Function.Call(Hash.SET_CURRENT_PED_WEAPON, ped, (uint)WeaponHash.Unarmed, true);

        private void Restore(Ped ped, string name)
        {
            if (ped == null || !ped.Exists() || ped.IsDead) return;
            if (name == "run_away") return; // they stay wherever they ran to
            Unarmed(ped);
            ped.Task.ClearAll();
            if (_isScenePed(ped))
            {
                ped.Task.StandStill(-1);
                ped.Task.LookAt(Game.Player.Character, -1);
            }
        }

        // ------------------------------------------------------------ clothes

        /// <summary>
        /// Undressed looks found in the game files (CodeWalker export of each model's clothing textures,
        /// checked by eye): component, drawable, texture. 3 = torso, 4 = legs, 11 = jacket (0 is empty
        /// for the protagonists). Women only lose their pants; nobody goes nude. Models without a bare
        /// torso or underwear in the game have no entry.
        /// </summary>
        private static readonly Dictionary<string, Dictionary<string, int[][]>> BuiltInLooks = new Dictionary<string, Dictionary<string, int[][]>>
        {
            ["player_one"] = Look(shirt: new[] { new[] { 3, 26, 0 }, new[] { 11, 0, 0 } }, pants: new[] { new[] { 4, 18, 0 } }),      // Franklin: bare chest, white briefs
            ["player_zero"] = Look(shirt: new[] { new[] { 3, 26, 0 }, new[] { 11, 0, 0 } }, pants: new[] { new[] { 4, 18, 0 } }),     // Michael: bare chest, striped boxers
            ["player_two"] = Look(shirt: new[] { new[] { 3, 16, 0 } }, pants: new[] { new[] { 4, 22, 0 } }),                          // Trevor: bare chest, briefs
            ["ig_devin"] = Look(shirt: new[] { new[] { 3, 5, 0 } }, pants: new[] { new[] { 4, 4, 0 } }),                              // Devin: bare chest, boxers
            ["ig_lamardavis"] = Look(pants: new[] { new[] { 4, 1, 0 } }),                                                              // Lamar: shorts
            ["ig_jimmydisanto"] = Look(pants: new[] { new[] { 4, 1, 0 } }),                                                            // Jimmy: shorts
            ["ig_amandatownley"] = Look(pants: new[] { new[] { 4, 2, 0 } }),                                                           // Amanda: underwear
            ["ig_tracydisanto"] = Look(pants: new[] { new[] { 4, 2, 0 } }),                                                            // Tracey: underwear
            ["s_m_y_marine_02"] = Look(shirt: new[] { new[] { 3, 0, 0 } }),                                                            // Marine: bare chest
            ["a_m_m_hillbilly_01"] = Look(shirt: new[] { new[] { 3, 1, 0 } }),                                                         // Hillbilly: tank top and suspenders
        };

        private static Dictionary<string, int[][]> Look(int[][] shirt = null, int[][] pants = null)
        {
            var look = new Dictionary<string, int[][]>();
            if (shirt != null) look["shirt"] = shirt;
            if (pants != null) look["pants"] = pants;
            return look;
        }

        /// <summary>GTA's model-name hash (Jenkins one-at-a-time), so the table needs no game call.</summary>
        private static int Joaat(string name)
        {
            uint h = 0;
            foreach (char c in name.ToLowerInvariant())
            {
                h += c;
                h += h << 10;
                h ^= h >> 6;
            }
            h += h << 3;
            h ^= h >> 11;
            h += h << 15;
            return unchecked((int)h);
        }

        private bool HasLook(Ped ped, string part) => _undress.TryGetValue(ped.Model.Hash, out var parts) && parts.ContainsKey(part);

        /// <summary>Switch to the model's undressed look (built in, or saved in dev mode with [ and ]).</summary>
        private void Undress(Ped ped, string part)
        {
            if (!_undress.TryGetValue(ped.Model.Hash, out var parts) || !parts.TryGetValue(part, out int[][] changes))
            {
                Log.Write($"action: no {part}-off look for model {ModelName(ped)} (in dev mode, [ and ] cycle and save one)");
                return;
            }
            foreach (int[] v in changes)
            {
                if (Function.Call<bool>(Hash.IS_PED_COMPONENT_VARIATION_VALID, ped, v[0], v[1], v[2]))
                    Function.Call(Hash.SET_PED_COMPONENT_VARIATION, ped, v[0], v[1], v[2], 0);
                else
                    Log.Write($"action: {ModelName(ped)} has no variation {v[0]}:{v[1]}:{v[2]}");
            }
        }

        /// <summary>
        /// Dev mode: show the target's next torso (component 3, "shirt") or legs (component 4, "pants")
        /// variation and save it as that model's undressed look. Returns a label for the screen.
        /// </summary>
        public string CycleClothes(Ped ped, string part)
        {
            int component = part == "shirt" ? 3 : 4;
            int count = Function.Call<int>(Hash.GET_NUMBER_OF_PED_DRAWABLE_VARIATIONS, ped, component);
            if (count <= 1) return $"{ModelName(ped)} has no other {part} variations";
            int current = Function.Call<int>(Hash.GET_PED_DRAWABLE_VARIATION, ped, component);
            int next = current;
            for (int i = 0; i < count; i++)
            {
                next = (next + 1) % count;
                if (Function.Call<bool>(Hash.IS_PED_COMPONENT_VARIATION_VALID, ped, component, next, 0)) break;
            }
            Function.Call(Hash.SET_PED_COMPONENT_VARIATION, ped, component, next, 0, 0);

            var change = new[] { new[] { component, next, 0 } };
            if (!_overrides.TryGetValue(ped.Model.Hash, out var saved)) _overrides[ped.Model.Hash] = saved = new Dictionary<string, int[][]>();
            saved[part] = change;
            if (!_undress.TryGetValue(ped.Model.Hash, out var parts)) _undress[ped.Model.Hash] = parts = new Dictionary<string, int[][]>();
            parts[part] = change;
            SaveUndress();
            return $"{ModelName(ped)} {part}: look {next + 1}/{count}, saved as their {part}-off look";
        }

        /// <summary>Built-in looks, then the ones saved in dev mode (undress.txt) on top.</summary>
        private void LoadUndress()
        {
            foreach (var model in BuiltInLooks)
                _undress[Joaat(model.Key)] = new Dictionary<string, int[][]>(model.Value);
            if (!File.Exists(_undressFile)) return;
            foreach (string raw in File.ReadAllLines(_undressFile))
            {
                string line = raw.Split('#')[0].Trim();
                string[] f = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (f.Length < 5 || !int.TryParse(f[0], out int model)) continue;
                var change = new[] { new[] { int.Parse(f[2]), int.Parse(f[3]), int.Parse(f[4]) } };
                if (!_overrides.TryGetValue(model, out var saved)) _overrides[model] = saved = new Dictionary<string, int[][]>();
                saved[f[1]] = change;
                if (!_undress.TryGetValue(model, out var parts)) _undress[model] = parts = new Dictionary<string, int[][]>();
                parts[f[1]] = change;
            }
        }

        private void SaveUndress()
        {
            var lines = new List<string> { "# model-hash part component drawable texture   # saved in dev mode with [ (shirt) and ] (pants); overrides the built-in looks" };
            foreach (var model in _overrides)
                foreach (var part in model.Value)
                    lines.Add($"{model.Key} {part.Key} {part.Value[0][0]} {part.Value[0][1]} {part.Value[0][2]}   # {(PedHash)model.Key}");
            try { File.WriteAllLines(_undressFile, lines); }
            catch (Exception ex) { Log.Write("undress save failed: " + ex.Message); }
        }

        private static string ModelName(Ped ped) => ((PedHash)ped.Model.Hash).ToString();
    }
}
