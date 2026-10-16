using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using GTA;
using GTA.Math;
using GTA.Native;
using GTA.UI;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace NpcVoice
{
    /// <summary>
    /// "The game is running": unix ms in director/alive.txt, written every frame the mod ticks (throttled) and
    /// during long waits like teleports. It stops while the game is paused, which is how tools/shoot.ps1 knows.
    /// </summary>
    internal static class Heartbeat
    {
        public static string File;
        private static int _last;

        public static void Beat()
        {
            if (File == null || unchecked(Environment.TickCount - _last) < 400) return;
            _last = Environment.TickCount;
            try { System.IO.File.WriteAllText(File, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString()); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// Director: plays scripted scenes for recording a video. Drop NpcVoice/director/go.txt (first word: the
    /// script file in that folder, default script.txt) and it shoots every scene in it:
    /// <code>
    /// scene: Lamar's abs          a title for the log
    /// place: franklin             michael / franklin / trevor (a home, as with J/K/L)
    /// with: LamarDavis            who you talk to (a characters/ file name)
    /// you: [laughing] Take your shirt off, dog.     what you say, in your own character's voice
    /// you: Bend over. >> bend over and call Franklin "daddy"     after ">>": how they react (steers their reply)
    /// </code>
    /// You say each line in your character's cloned voice; they answer live (LLM, action pick, voice) like
    /// any conversation. While shooting nothing of the mod is on screen (no HUD, help text, notifications or
    /// subtitles) and scripted cameras cut between whoever is talking, framed so the subject is in the middle
    /// (for a 9:16 crop), with a wide shot of them while they act something out. It starts with a white
    /// flash and a beep for syncing the screen and sound recordings, and writes director/timeline.txt
    /// (unix ms, event) for editing.
    /// </summary>
    public partial class NpcVoiceScript
    {
        private sealed class Scene
        {
            public string Title, Place, With;
            public readonly List<string> Lines = new List<string>();
        }

        private IEnumerator<int> _director;
        private int _directorWake, _nextDirectorPoll, _flashUntil, _lastHeartbeat;
        private bool _clean;          // recording: nothing of the mod on screen, no HUD
        private bool _holdReplies;    // a reply waits for your line to finish instead of cutting it off
        private bool _wasAudible;
        private Ped _sceneNpc;
        private Camera _cam;
        private string _shot;
        private readonly object _timelineLock = new object();

        private string DirectorDir => Path.Combine(BaseDirectory, "NpcVoice", "director");

        /// <summary>Every frame, first thing.</summary>
        private void DirectorTick()
        {
            if (Game.GameTime < _flashUntil) new ContainerElement(PointF.Empty, new SizeF(1280f, 720f), Color.White).Draw();
            ClipCameraTick();
            if (_clean)
            {
                if (_hideHud) Function.Call(Hash.HIDE_HUD_AND_RADAR_THIS_FRAME);
                bool audible = _service.Player.IsAudible;
                if (audible != _wasAudible)
                {
                    _wasAudible = audible;
                    Mark(audible ? "audio_on" : "audio_off");
                }
                UpdateShot();
            }

            // The recording script (tools/shoot.ps1) watches this to tell whether the game is running or
            // paused, and asks for a sync flash whenever it (re)starts the screen capture.
            if (Environment.TickCount - _lastHeartbeat > 500)
            {
                _lastHeartbeat = Environment.TickCount;
                try
                {
                    Directory.CreateDirectory(DirectorDir);
                    Heartbeat.File = Path.Combine(DirectorDir, "alive.txt");
                    Heartbeat.Beat();
                    string flash = Path.Combine(DirectorDir, "flash.txt");
                    if (File.Exists(flash))
                    {
                        File.Delete(flash);
                        _flashUntil = Game.GameTime + 150;
                        Beep();
                        Mark("clap");
                    }
                }
                catch (IOException) { }
            }

            if (_director == null)
            {
                if (Game.GameTime < _nextDirectorPoll) return;
                _nextDirectorPoll = Game.GameTime + 1000;
                string go = Path.Combine(DirectorDir, "go.txt");
                if (!File.Exists(go)) return;
                string[] words = File.ReadAllText(go).Split(new[] { ' ', '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                File.Delete(go);
                string script = Path.Combine(DirectorDir, words.Length > 0 ? words[0] : "script.txt");
                if (File.Exists(script) && File.ReadAllLines(script).Any(l => l.TrimStart().StartsWith("clip:")))
                {
                    var clips = ParseClips(script, _book);
                    Log.Write($"director: {clips.Count} clips");
                    if (clips.Count > 0) _director = ShootClips(clips).GetEnumerator();
                    return;
                }
                List<Scene> scenes = ParseScenes(script);
                Log.Write($"director: {scenes.Count} scenes");
                if (scenes.Count > 0) _director = Shoot(scenes).GetEnumerator();
                return;
            }

            if (Game.GameTime < _directorWake) return;
            bool more;
            try { more = _director.MoveNext(); }
            catch (Exception ex)
            {
                Log.Write("director failed: " + ex);
                more = false;
            }
            if (more) _directorWake = Game.GameTime + Math.Max(0, _director.Current);
            else
            {
                _director.Dispose(); // the shoot's finally puts everything back
                _director = null;
            }
        }

        private IEnumerable<int> Shoot(List<Scene> scenes)
        {
            bool micMuted = _openMic?.Muted ?? true;
            try
            {
                _clean = true;
                _hideHud = true;
                _holdReplies = true;
                if (_dev.On) _dev.Close();
                _dev.ClearSpawned();
                if (_openMic != null) _openMic.Muted = true; // your room isn't in the scene
                Game.Player.IsInvincible = true;
                Game.Player.WantedLevel = 0;
                Function.Call(Hash.SET_MAX_WANTED_LEVEL, 0);
                World.CurrentTimeOfDay = new TimeSpan(15, 0, 0);
                World.Weather = Weather.ExtraSunny;
                Function.Call(Hash.PAUSE_CLOCK, true);
                // Wait for the main voice engine: lines that fall back to another engine sound different.
                for (int waited = 0; waited < 90000 && _service.Speaking != _service.Engine; waited += 500) yield return 500;
                Mark("shoot_start\t" + scenes.Count);
                yield return 1500;

                _flashUntil = Game.GameTime + 150;
                Beep();
                Mark("clap");
                yield return 2000;

                for (int i = 0; i < scenes.Count; i++)
                    foreach (int wait in RunScene(scenes[i], i + 1)) yield return wait;
                Mark("shoot_end");
            }
            finally
            {
                World.RenderingCamera = null;
                World.DestroyAllCameras();
                _cam = null;
                _shot = null;
                _sceneNpc = null;
                _clean = false;
                _hideHud = false;
                _holdReplies = false;
                if (_openMic != null) _openMic.Muted = micMuted;
                Game.Player.IsInvincible = false;
                Function.Call(Hash.SET_MAX_WANTED_LEVEL, 5);
                Function.Call(Hash.PAUSE_CLOCK, false);
                Mark("shoot_done");
                Log.Write("director: done");
            }
        }

        private IEnumerable<int> RunScene(Scene s, int n)
        {
            Ped player = Game.Player.Character;
            string playerName = CharacterBook.PlayerName(player);
            World.RenderingCamera = null;
            World.DestroyAllCameras();
            _cam = null;
            _shot = null;
            _sceneNpc = null;

            Ped npc = _visits.GoTo(s.Place, s.With); // fades out and in; the edit starts after scene_start
            if (npc == null)
            {
                Mark($"scene_skip\t{n}\t{s.Title}");
                Log.Write($"director: scene {n} '{s.Title}': nobody to talk to ({s.With} at {s.Place})");
                yield break;
            }
            _sceneNpc = npc;
            _partner = npc;
            FaceEachOther(player, npc);
            yield return 1500;
            Mark($"scene_start\t{n}\t{s.Title}\t{CharacterName(npc)}");

            foreach (string scripted in s.Lines)
            {
                int split = scripted.IndexOf(">>", StringComparison.Ordinal);
                string line = (split >= 0 ? scripted.Substring(0, split) : scripted).Trim();
                string direction = split >= 0 ? scripted.Substring(split + 2).Trim() : null;
                if (!npc.Exists() || npc.IsDead) break;
                if (npc.Position.DistanceTo(player.Position) > 4f || npc.IsRagdoll || player.IsRagdoll)
                {
                    // Ran off or got knocked over last time: back in front of each other (cut in the edit).
                    yield return 1500;
                    npc.Position = player.Position + player.ForwardVector * 1.9f;
                    FaceEachOther(player, npc);
                    yield return 800;
                }

                for (int waited = 0; _service.IsBusy && waited < 10000; waited += 100) yield return 100;
                if (!Speak(player, line, playerName, null))
                {
                    Log.Write($"director: couldn't say '{line}'");
                    continue;
                }
                Converse(npc, null, Speech.Plain(line), direction: direction); // they think while you talk; the reply waits for you to finish
                yield return 300;
                for (int waited = 0; waited < 30000 && (_thinking || _pending != null || _service.IsBusy || _actions.Busy(npc)); waited += 100)
                    yield return 100;
                yield return 700;
            }
            yield return 1000;
            Mark($"scene_end\t{n}");
            _sceneNpc = null;
        }

        private static void FaceEachOther(Ped player, Ped npc)
        {
            Vector3 to = npc.Position - player.Position;
            player.Heading = (float)(Math.Atan2(-to.X, to.Y) * 180.0 / Math.PI);
            npc.Heading = player.Heading + 180f;
            player.Task.LookAt(npc, -1);
            npc.Task.StandStill(-1);
            npc.Task.LookAt(player, -1);
        }

        // ------------------------------------------------------------ cameras

        /// <summary>Over the listener's shoulder onto whoever talks; wide on the NPC while they act something out.</summary>
        private void UpdateShot()
        {
            Ped npc = _sceneNpc, player = Game.Player.Character;
            if (npc == null || !npc.Exists()) return;
            string want = _actions.ActingOn(npc) != null ? "wide"
                : _speaker == player ? "player"
                : _speaker == npc ? "npc"
                : _shot ?? "npc";
            if (want == _shot && _cam != null) return;

            Ped subject = want == "player" ? player : npc, other = want == "player" ? npc : player;
            Vector3 s = subject.Bones[Bone.SkelHead].Position, o = other.Bones[Bone.SkelHead].Position;
            Vector3 dir = s - o;
            dir.Z = 0f;
            if (dir.Length() < 0.1f) dir = subject.ForwardVector * -1f;
            dir.Normalize();
            Vector3 right = Vector3.Cross(dir, Vector3.WorldUp);
            right.Normalize();

            // Try the shot from both sides and keep the one with more room (rooms have walls and furniture);
            // if the camera still has to come in close, widen the lens so they stay in frame.
            bool wide = want == "wide";
            Vector3 aim = wide ? new Vector3(0f, 0f, 0.05f) : new Vector3(0f, 0f, 0.42f); // their middle / upper chest
            Vector3 target = subject.Position + aim;
            Vector3 pos = Vector3.Zero;
            float bestRoom = -1f;
            foreach (float side in new[] { 1f, -1f })
            {
                Vector3 p = wide
                    ? s - dir * 1.2f + right * (2.9f * side) + new Vector3(0f, 0f, -0.15f)  // side on, a little toward you: front three-quarters
                    : o - dir * 0.6f + right * (0.45f * side) + new Vector3(0f, 0f, 0.03f); // over the listener's shoulder
                float full = (p - target).Length();
                RaycastResult hit = World.Raycast(target, p, IntersectFlags.Map, subject);
                if (hit.DidHit) p = hit.HitPosition + (target - hit.HitPosition).Normalized * 0.3f;
                float room = (p - target).Length() / full;
                if (room > bestRoom + 0.05f) { bestRoom = room; pos = p; }
            }
            float distance = (pos - target).Length();
            float fov = wide ? Clamp(50f * 2.9f / Math.Max(1.2f, distance), 50f, 75f) : (bestRoom < 0.7f ? 50f : 38f);

            Camera cam = World.CreateCamera(pos, Vector3.Zero, fov);
            cam.PointAt(subject, aim);
            World.RenderingCamera = cam;
            _cam?.Delete();
            _cam = cam;
            _shot = want;
            Mark("shot\t" + want);
        }

        // ------------------------------------------------------------ timeline, sync, script

        private void Mark(string what)
        {
            try
            {
                lock (_timelineLock)
                {
                    Directory.CreateDirectory(DirectorDir);
                    File.AppendAllText(Path.Combine(DirectorDir, "timeline.txt"),
                        DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + "\t" + what + Environment.NewLine);
                }
            }
            catch (Exception ex) { Log.Write("timeline: " + ex.Message); }
        }

        /// <summary>A short 1 kHz beep, heard by the sound recording at the moment the screen flashes white.</summary>
        private static void Beep()
        {
            var output = new WaveOutEvent { DesiredLatency = 100 };
            var tone = new SignalGenerator(44100, 1) { Frequency = 1000, Gain = 0.5, Type = SignalGeneratorType.Sin }
                .Take(TimeSpan.FromMilliseconds(200));
            output.Init(tone);
            output.PlaybackStopped += (s, e) => output.Dispose();
            output.Play();
        }

        private static List<Scene> ParseScenes(string file)
        {
            var scenes = new List<Scene>();
            if (!File.Exists(file))
            {
                Log.Write("director: no script at " + file);
                return scenes;
            }
            Scene scene = null;
            foreach (string raw in File.ReadAllLines(file))
            {
                string line = raw.Trim();
                int colon = line.IndexOf(':');
                if (line.Length == 0 || line.StartsWith("#") || colon < 0) continue;
                string key = line.Substring(0, colon).Trim().ToLowerInvariant(), value = line.Substring(colon + 1).Trim();
                if (key == "scene") scenes.Add(scene = new Scene { Title = value });
                else if (scene == null) continue;
                else if (key == "place") scene.Place = value;
                else if (key == "with") scene.With = value;
                else if (key == "you" && value.Length > 0) scene.Lines.Add(value);
            }
            return scenes.Where(s => s.Place != null && s.With != null && s.Lines.Count > 0).ToList();
        }

        /// <summary>Notifications, unless the director is recording.</summary>
        private void Note(string message)
        {
            if (!_clean) Notification.Show(message);
        }
    }
}
