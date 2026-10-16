using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GTA;
using GTA.Math;
using GTA.Native;
using GTA.UI;

namespace NpcVoice
{
    /// <summary>
    /// Director, clips mode: short exchanges that look like ordinary gameplay, for showing the mod off. A script
    /// whose blocks start with "clip:" (instead of "scene:") is shot this way:
    /// <code>
    /// clip: Lamar's jacket           a title for the edit
    /// at: grove                      a street spot (Spots), a home (michael / franklin / trevor) or "x, y, z, heading"
    /// time: 19                       hour of day (optional)
    /// a: player                      who speaks first: player, a characters/ name (LamarDavis), or a ped model
    /// b: LamarDavis                    (a_m_y_hipster_01), optionally with a voice: a_m_y_hipster_01/DaveNorton
    /// a_is: a sweaty jogger          for a ped model: who they are (B's persona, or how B sees A)
    /// line: Nice jacket, dog.        what A says (in A's cloned voice)
    /// reply: brag that it's limited edition    how B answers: B's line is written live by the LLM in character
    /// say: [laughing] Limited edition, dog.    or exactly what B says (re-shooting a take with other voices)
    /// do: middle_finger              with say: the action B does (an ActionCatalog name; none = nothing)
    /// </code>
    /// Everyone stands still on the pavement, the gameplay camera stays put behind the player (who is hidden when
    /// neither speaker is the player), the HUD and the game's subtitles stay on, and nothing of the mod shows.
    /// Each clip's reply, action and both voices are worked out before it rolls, then played back to back, so a
    /// clip is one continuous 5-10 s take: clip_start / clip_end in timeline.txt mark it for tools/edit.ts.
    /// </summary>
    public partial class NpcVoiceScript
    {
        private sealed class Who
        {
            public bool IsPlayer;
            public string Model;       // ped model name, when not a character
            public Character Character;
            public string Voice;       // voices/ folder override
            public string Is;          // description, for ped models
            public Ped Ped;
        }

        private sealed class Clip
        {
            public string Title, At, Line, Reply, Say, Do;
            public int Hour = -1;
            public Who A, B;
        }

        // Street spots: pavement nearby is found at run time (GET_SAFE_COORD_FOR_PED), the heading is where the
        // camera looks.
        private static readonly Dictionary<string, (Vector3 Pos, float Heading)> Spots = new Dictionary<string, (Vector3, float)>(StringComparer.OrdinalIgnoreCase)
        {
            ["grove"] = (new Vector3(106.6f, -1941.0f, 20.8f), 50f),         // Grove Street cul-de-sac
            ["strawberry"] = (new Vector3(-14.8f, -1454.5f, 30.5f), 180f),   // outside Franklin's aunt's, Forum Dr
            ["legion"] = (new Vector3(215.8f, -810.1f, 30.7f), 160f),        // Legion Square
            ["vinewood"] = (new Vector3(300.0f, 190.0f, 104.3f), 250f),      // Vinewood Blvd, by the Oriental Theater
            ["pier"] = (new Vector3(-1850.1f, -1231.8f, 13.0f), 140f),       // Del Perro Pier
            ["rockford"] = (new Vector3(-716.0f, -155.0f, 37.4f), 120f),     // Portola Dr shops, Rockford Hills
            ["simeon"] = (new Vector3(-56.5f, -1097.0f, 26.4f), 200f),       // outside Premium Deluxe Motorsport
            ["unicorn"] = (new Vector3(128.0f, -1298.0f, 29.2f), 210f),      // outside the Vanilla Unicorn
            ["sandy"] = (new Vector3(1961.6f, 3740.3f, 32.3f), 300f),        // Sandy Shores
            ["paleto"] = (new Vector3(-110.0f, 6461.0f, 31.6f), 135f),       // Paleto Bay, by the bank
            ["airport"] = (new Vector3(-1037.0f, -2737.0f, 20.2f), 330f),    // LSIA departures
        };

        private Func<ITts> _renderEngine;
        private bool _hideHud;        // the old scripted-camera scenes hide the HUD; clips keep it, like gameplay
        private bool _fixGameplayCam;
        private float _clipCamHeading;   // gameplay camera heading relative to the player while a clip rolls
        private int _clipViewMode = 1;   // follow-cam distance: 0 near, 1 medium

        private static List<Clip> ParseClips(string file, CharacterBook book)
        {
            var clips = new List<Clip>();
            Clip clip = null;
            foreach (string raw in File.ReadAllLines(file))
            {
                string line = raw.Trim();
                int colon = line.IndexOf(':');
                if (line.Length == 0 || line.StartsWith("#") || colon < 0) continue;
                string key = line.Substring(0, colon).Trim().ToLowerInvariant(), value = line.Substring(colon + 1).Trim();
                if (key == "clip") clips.Add(clip = new Clip { Title = value });
                else if (clip == null) continue;
                else if (key == "at") clip.At = value;
                else if (key == "time" && int.TryParse(value, out int hour)) clip.Hour = hour;
                else if (key == "a") clip.A = ParseWho(value, book);
                else if (key == "b") clip.B = ParseWho(value, book);
                else if (key == "a_is" && clip.A != null) clip.A.Is = value;
                else if (key == "b_is" && clip.B != null) clip.B.Is = value;
                else if (key == "line") clip.Line = value;
                else if (key == "reply") clip.Reply = value;
                else if (key == "say") clip.Say = value;
                else if (key == "do") clip.Do = value;
            }
            return clips.Where(c => c.At != null && c.A != null && c.B != null && !c.B.IsPlayer && c.Line != null).ToList();
        }

        private static Who ParseWho(string value, CharacterBook book)
        {
            if (value.Equals("player", StringComparison.OrdinalIgnoreCase)) return new Who { IsPlayer = true };
            string[] parts = value.Split('/');
            Character c = book.ByKey(parts[0]);
            return new Who { Character = c, Model = c == null ? parts[0] : null, Voice = parts.Length > 1 ? parts[1].Trim() : null };
        }

        private IEnumerable<int> ShootClips(List<Clip> clips)
        {
            Ped player = Game.Player.Character;
            bool micMuted = _openMic?.Muted ?? true;
            try
            {
                _clean = true;
                _hideHud = false;
                if (_dev.On) _dev.Close();
                _dev.ClearSpawned();
                _visits.Clear();
                if (_openMic != null) _openMic.Muted = true;
                Game.Player.IsInvincible = true;
                Game.Player.WantedLevel = 0;
                Function.Call(Hash.SET_MAX_WANTED_LEVEL, 0);
                World.Weather = Weather.ExtraSunny;
                Function.Call(Hash.PAUSE_CLOCK, true);
                for (int waited = 0; waited < 120000 && _service.Speaking != _service.Engine; waited += 500) yield return 500;
                Mark("shoot_start\t" + clips.Count);
                yield return 1000;
                _flashUntil = Game.GameTime + 150;
                Beep();
                Mark("clap");
                yield return 1500;

                for (int i = 0; i < clips.Count; i++)
                    foreach (int wait in RunClip(clips[i], i + 1)) yield return wait;
                Mark("shoot_end");
            }
            finally
            {
                _fixGameplayCam = false;
                _clean = false;
                if (player.Exists())
                {
                    player.IsVisible = true;
                    Game.Player.CanControlCharacter = true;
                }
                if (_openMic != null) _openMic.Muted = micMuted;
                Game.Player.IsInvincible = false;
                Function.Call(Hash.SET_MAX_WANTED_LEVEL, 5);
                Function.Call(Hash.PAUSE_CLOCK, false);
                Mark("shoot_done");
                Log.Write("director: clips done");
            }
        }

        private IEnumerable<int> RunClip(Clip clip, int n)
        {
            Ped player = Game.Player.Character;
            bool withPlayer = clip.A.IsPlayer;
            _fixGameplayCam = false;
            Game.Player.CanControlCharacter = true;
            if (clip.Hour >= 0) World.CurrentTimeOfDay = new TimeSpan(clip.Hour, 0, 0);

            // 1. Get there: a home through the visit code (interior handling), a street spot onto the pavement.
            Vector3 spot;
            float heading;
            bool home = clip.At.Equals("michael", StringComparison.OrdinalIgnoreCase) || clip.At.Equals("franklin", StringComparison.OrdinalIgnoreCase)
                        || clip.At.Equals("trevor", StringComparison.OrdinalIgnoreCase);
            Ped resident = null;
            if (home)
            {
                // The visit code puts the resident in a free spot in front of you (interiors are tight); a ped model
                // or the player as B gets nobody here and is placed below like on the street.
                resident = _visits.GoTo(clip.At, withPlayer && clip.B.Character != null ? clip.B.Character.Key : "");
                resident?.AttachedBlip?.Delete();
                spot = player.Position;
                heading = player.Heading;
                Screen.FadeOut(1);
            }
            else
            {
                if (!TryParseSpot(clip.At, out spot, out heading))
                {
                    Log.Write($"director: clip {n}: unknown place '{clip.At}'");
                    yield break;
                }
                Screen.FadeOut(300);
                yield return 350;
                DevMode.Teleport(spot, heading);
                spot = OnPavement(spot);
                player.PositionNoOffset = spot;
                player.Heading = heading;
            }
            if (resident == null) Function.Call(Hash.CLEAR_AREA_OF_PEDS, spot.X, spot.Y, spot.Z, 14f, 1);
            if (resident == null) heading = ClearHeading(spot, heading);
            Vector3 fwd = new Vector3(-(float)Math.Sin(heading * Math.PI / 180), (float)Math.Cos(heading * Math.PI / 180), 0f);
            Vector3 right = new Vector3(fwd.Y, -fwd.X, 0f);

            // 2. Who's in it: the player in front of the camera, or two peds a little way ahead with the player hidden.
            var spawned = new List<Ped>();
            if (withPlayer)
            {
                // B a little to the right so the camera sees them over the player's shoulder, not behind him.
                clip.A.Ped = player;
                clip.B.Ped = resident ?? SpawnWho(clip.B, Ground(spot + fwd * 2.0f + right * 0.95f), heading + 180f);
                _clipViewMode = 1;
            }
            else
            {
                clip.A.Ped = SpawnWho(clip.A, Ground(spot + fwd * 1.9f - right * 0.7f), heading + 90f);
                clip.B.Ped = SpawnWho(clip.B, Ground(spot + fwd * 1.9f + right * 0.7f), heading - 90f);
                _clipViewMode = 0;
            }
            foreach (Who w in new[] { clip.A, clip.B })
                if (!w.IsPlayer && w.Ped != null) spawned.Add(w.Ped);
            if (clip.A.Ped == null || clip.B.Ped == null)
            {
                Log.Write($"director: clip {n} '{clip.Title}': couldn't spawn its peds");
                foreach (Ped p in spawned) p.Delete();
                Screen.FadeIn(300);
                yield break;
            }
            player.IsVisible = withPlayer;
            Game.Player.CanControlCharacter = false;
            FacePeds(clip.A.Ped, clip.B.Ped);
            // The camera keeps looking along the spot's heading, whichever way the player turned to face B.
            _clipCamHeading = withPlayer ? (resident != null ? -34f : Normalize(heading - player.Heading)) : 0f;
            if (!withPlayer) player.Heading = heading;
            _fixGameplayCam = true;
            Screen.FadeIn(500);

            // 3. Work the clip out while the scene settles: B's reply and action, then both voices.
            string aName = NameOf(clip.A);
            string scene = CharacterBook.SceneOf(clip.B.Ped, clip.A.Ped, _visits.PlaceAt(clip.B.Ped.Position));
            PedInfo bInfo = PedInfo.Of(clip.B.Ped);
            bool bFemale = bInfo.Female;
            Voice aVoice = VoiceOf(clip.A, clip.Line), bVoice = null;
            string bModel = Character(clip.B)?.Name ?? (clip.B.Is ?? (bFemale ? "a woman" : "a man"));
            var job = Task.Run(async () =>
            {
                CancellationToken ct = _shutdown.Token;
                var prompt = _book.ConversationPrompt(bInfo, aName, Speech.Plain(clip.Line), scene,
                    clip.B.Is != null && Character(clip.B) == null ? $"You are {clip.B.Is}. You live in Los Santos and you talk like it." : null);
                var orderState = new Dictionary<string, object>
                {
                    ["npc"] = bModel, ["npc_gender"] = bFemale ? "woman" : "man", ["player"] = aName, ["player_said"] = Speech.Plain(clip.Line),
                };
                var (reply, order) = clip.Say != null
                    ? (clip.Say, clip.Do == null || clip.Do == "none" ? null : new JevChoice { Choice = clip.Do, Probability = 1, Confidence = 1 })
                    : await Dialogue.ReplyAsync(_llm, _actionsOn ? _picker : null, prompt, orderState, bFemale, aName,
                        Speech.Plain(clip.Line), Character(clip.B), ct,
                        (clip.Reply ?? "react in character") + ". One sentence, at most 14 words").ConfigureAwait(false);
                JevChoice pick = order;
                if (pick == null && clip.Say == null && _actionsOn && _picker.IsConfigured && !string.IsNullOrWhiteSpace(reply))
                {
                    var mood = new Dictionary<string, object>(orderState)
                    {
                        ["npc_persona"] = Net.Truncate(Character(clip.B)?.Persona ?? clip.B.Is ?? "A Los Santos local.", 400),
                        ["npc_reply"] = Speech.Plain(reply),
                    };
                    pick = await _picker.ChooseAsync(mood, ActionCatalog.Instructions, ActionCatalog.Criteria(), ct).ConfigureAwait(false);
                }
                bVoice = VoiceOf(clip.B, reply);
                using (ITts tts = _renderEngine())
                {
                    var a = await Render(tts, clip.Line, aVoice, ct).ConfigureAwait(false);
                    var b = await Render(tts, reply, bVoice, ct).ConfigureAwait(false);
                    return (reply, pick, a, b);
                }
            });
            yield return 1500;
            for (int waited = 0; !job.IsCompleted && waited < 40000; waited += 100) yield return 100;
            if (job.Status != TaskStatus.RanToCompletion || string.IsNullOrWhiteSpace(job.Result.reply))
            {
                Log.Write($"director: clip {n} '{clip.Title}' failed: {job.Exception?.GetBaseException().Message ?? "no reply"}");
                Cleanup(spawned);
                yield break;
            }
            var (replyText, actionPick, aAudio, bAudio) = job.Result;
            Log.Write($"clip {n} '{clip.Title}' | {aName}: {clip.Line} | {bModel}: {replyText} | action {actionPick?.Choice ?? "none"} {actionPick?.Probability:P0}");

            // 4. Roll: A's line, a beat, B's line with its action.
            Mark($"clip_start\t{n}\t{clip.Title}");
            Play(clip.A.Ped, clip.Line, aAudio, SubtitleColor(clip.A));
            yield return 200;
            for (int waited = 0; _service.Player.IsAudible && waited < 15000; waited += 50) yield return 50;
            yield return 250;
            Play(clip.B.Ped, replyText, bAudio, SubtitleColor(clip.B));
            if (actionPick != null && ActionCatalog.Acts(actionPick, bFemale))
                _actions.Perform(clip.B.Ped, actionPick.Choice, clip.A.Ped);
            yield return 200;
            for (int waited = 0; (_service.Player.IsAudible || _actions.Busy(clip.B.Ped)) && waited < 12000; waited += 50) yield return 50;
            yield return 600;
            Mark($"clip_end\t{n}");
            Cleanup(spawned);
        }

        private void Cleanup(List<Ped> spawned)
        {
            _fixGameplayCam = false;
            EndSpeaker();
            foreach (Ped p in spawned)
            {
                _actions.Stop(p);
                if (p.Exists()) p.Delete();
            }
            Ped player = Game.Player.Character;
            player.IsVisible = true;
            player.Task.ClearAll();
        }

        /// <summary>Keeps the gameplay camera exactly behind the player while a clip rolls (called every frame).</summary>
        private void ClipCameraTick()
        {
            if (_clean && !_hideHud)
            {
                // Story phone calls and their help text would land in the middle of a clip.
                Ped player = Game.Player.Character;
                Function.Call(Hash.HIDE_HELP_TEXT_THIS_FRAME);
                Function.Call(Hash.STOP_PED_RINGTONE, player);
                if (Function.Call<bool>(Hash.IS_MOBILE_PHONE_CALL_ONGOING)) Function.Call(Hash.STOP_SCRIPTED_CONVERSATION, false);
                Function.Call(Hash.DESTROY_MOBILE_PHONE);
                Game.DisableControlThisFrame(Control.Phone);
            }
            if (!_fixGameplayCam) return;
            Function.Call(Hash.SET_FOLLOW_PED_CAM_VIEW_MODE, _clipViewMode);
            Function.Call(Hash.SET_GAMEPLAY_CAM_RELATIVE_HEADING, _clipCamHeading);
            Function.Call(Hash.SET_GAMEPLAY_CAM_RELATIVE_PITCH, -6f, 1f);
            Game.DisableControlThisFrame(Control.LookLeftRight);
            Game.DisableControlThisFrame(Control.LookUpDown);
            Game.DisableControlThisFrame(Control.NextCamera);
        }

        private void Play(Ped ped, string text, (byte[] Pcm, int Rate) audio, string color)
        {
            EndSpeaker();
            _service.Player.BeginLine();
            _service.Player.AddPcm(audio.Pcm, audio.Rate);
            _service.Player.EndLine();
            _speaker = ped;
            _subtitle = color + LineBook.Subtitle(text);
            _subtitleAt = Game.GameTime;
            Mark($"speak\t{(ped == Game.Player.Character ? "player" : "npc")}\t{CharacterName(ped)}\t{text}");
        }

        private static async Task<(byte[] Pcm, int Rate)> Render(ITts tts, string text, Voice voice, CancellationToken ct)
        {
            var pcm = new MemoryStream();
            int rate = 44100;
            await tts.SpeakAsync(text, voice, (chunk, r) =>
            {
                lock (pcm) { pcm.Write(chunk, 0, chunk.Length); rate = r; }
            }, ct).ConfigureAwait(false);
            return (pcm.ToArray(), rate);
        }

        private Ped SpawnWho(Who who, Vector3 pos, float heading)
        {
            var model = who.Character != null ? new Model(who.Character.ModelHash) : new Model(who.Model);
            if (!model.IsValid || !model.Request(5000))
            {
                Log.Write($"director: can't load ped model {who.Model ?? who.Character?.Key}");
                return null;
            }
            Ped ped = World.CreatePed(model, pos, heading);
            model.MarkAsNoLongerNeeded();
            if (ped == null) return null;
            if (who.Character != null) Function.Call(Hash.SET_PED_DEFAULT_COMPONENT_VARIATION, ped);
            else Function.Call(Hash.SET_PED_RANDOM_COMPONENT_VARIATION, ped, 0);
            ped.IsPersistent = true;
            ped.BlockPermanentEvents = true;
            ped.Task.StandStill(-1);
            return ped;
        }

        private static void FacePeds(Ped a, Ped b)
        {
            Vector3 d = b.Position - a.Position;
            a.Heading = (float)(Math.Atan2(-d.X, d.Y) * 180.0 / Math.PI);
            b.Heading = a.Heading + 180f;
            if (a != Game.Player.Character) a.Task.StandStill(-1);
            b.Task.StandStill(-1);
            a.Task.LookAt(b, -1);
            b.Task.LookAt(a, -1);
        }

        private Character Character(Who who) => who.IsPlayer ? _book.Find(Game.Player.Character.Model.Hash) : who.Character;

        private string NameOf(Who who)
        {
            if (who.IsPlayer) return CharacterBook.PlayerName(Game.Player.Character);
            if (who.Character != null) return who.Character.Name;
            return who.Is ?? (who.Ped != null && who.Ped.Gender == Gender.Female ? "some random woman" : "some random guy");
        }

        private Voice VoiceOf(Who who, string text)
        {
            VoiceGroup group = who.Voice != null ? _voices.Find(who.Voice) : null;
            group = group ?? GroupFor(who.Ped);
            return VoiceLibrary.Pick(group, who.Ped.Handle, Speech.Mood(Speech.Tag(text)));
        }

        /// <summary>The game's subtitle colours: Franklin green, Michael blue, Trevor orange, everyone else white.</summary>
        private string SubtitleColor(Who who)
        {
            string key = Character(who)?.Key ?? "";
            return key == "Franklin" ? "~g~" : key == "Michael" ? "~b~" : key == "Trevor" ? "~o~" : "~s~";
        }

        private static float Normalize(float degrees)
        {
            while (degrees > 180f) degrees -= 360f;
            while (degrees < -180f) degrees += 360f;
            return degrees;
        }

        /// <summary>The preferred heading, or the nearest one (45 degree steps) with nothing between the camera
        /// and the people in front of it: room behind for the camera, room ahead for the people.</summary>
        private static float ClearHeading(Vector3 spot, float preferred)
        {
            Ped player = Game.Player.Character;
            Vector3 up = new Vector3(0f, 0f, 0.6f);
            foreach (float turn in new[] { 0f, 45f, -45f, 90f, -90f, 135f, -135f, 180f })
            {
                float h = preferred + turn;
                var f = new Vector3(-(float)Math.Sin(h * Math.PI / 180), (float)Math.Cos(h * Math.PI / 180), 0f);
                var r = new Vector3(f.Y, -f.X, 0f);
                bool clear = !World.Raycast(spot + up, spot + f * 3.2f + up, IntersectFlags.Map | IntersectFlags.Objects, player).DidHit
                    && !World.Raycast(spot + up, spot - f * 3.5f + up * 1.6f, IntersectFlags.Map | IntersectFlags.Objects, player).DidHit
                    && !World.Raycast(spot + up, spot + f * 2.0f + r * 1.2f + up, IntersectFlags.Map | IntersectFlags.Objects, player).DidHit
                    && !World.Raycast(spot + up, spot + f * 2.0f - r * 1.2f + up, IntersectFlags.Map | IntersectFlags.Objects, player).DidHit
                    // beside the player and where the camera sits behind them (kiosks, bus stops, walls)
                    && !World.Raycast(spot + up, spot + r * 1.8f + up, IntersectFlags.Map | IntersectFlags.Objects, player).DidHit
                    && !World.Raycast(spot + up, spot - r * 1.8f + up, IntersectFlags.Map | IntersectFlags.Objects, player).DidHit
                    && !World.Raycast(spot + up, spot - f * 2.5f + r * 1.2f + up * 1.6f, IntersectFlags.Map | IntersectFlags.Objects, player).DidHit
                    && !World.Raycast(spot + up, spot - f * 2.5f - r * 1.2f + up * 1.6f, IntersectFlags.Map | IntersectFlags.Objects, player).DidHit;
                if (clear) return h;
            }
            return preferred;
        }

        private static bool TryParseSpot(string at, out Vector3 pos, out float heading)
        {
            if (Spots.TryGetValue(at, out var s))
            {
                pos = s.Pos;
                heading = s.Heading;
                return true;
            }
            string[] p = at.Split(',');
            pos = Vector3.Zero;
            heading = 0f;
            if (p.Length < 3) return false;
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            bool ok = float.TryParse(p[0], System.Globalization.NumberStyles.Float, inv, out float x)
                      & float.TryParse(p[1], System.Globalization.NumberStyles.Float, inv, out float y)
                      & float.TryParse(p[2], System.Globalization.NumberStyles.Float, inv, out float z);
            if (p.Length > 3) float.TryParse(p[3], System.Globalization.NumberStyles.Float, inv, out heading);
            pos = new Vector3(x, y, z);
            return ok;
        }

        /// <summary>The nearest pavement (within 25 m), at standing height.</summary>
        private static Vector3 OnPavement(Vector3 near)
        {
            var safe = new OutputArgument();
            if (Function.Call<bool>(Hash.GET_SAFE_COORD_FOR_PED, near.X, near.Y, near.Z, true, safe, 1))
            {
                Vector3 s = safe.GetResult<Vector3>();
                if (s.DistanceTo(near) < 25f) near = s;
            }
            return Ground(near);
        }

        /// <summary>Standing height on the floor under this point: a ray down from the player's own height, so an
        /// awning, tree or roof overhead isn't taken for the ground.</summary>
        private static Vector3 Ground(Vector3 at)
        {
            Ped player = Game.Player.Character;
            float from = Math.Max(at.Z, player.Position.Z) + 0.6f;
            RaycastResult floor = World.Raycast(new Vector3(at.X, at.Y, from), new Vector3(at.X, at.Y, from - 4f), IntersectFlags.Map, player);
            at.Z = floor.DidHit ? floor.HitPosition.Z + 1.0f : player.Position.Z;
            return at;
        }
    }
}
