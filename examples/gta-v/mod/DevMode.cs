using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using Keys = System.Windows.Forms.Keys;
using GTA;
using GTA.Math;
using GTA.Native;
using GTA.UI;

namespace NpcVoice
{
    /// <summary>One thing you said and the reply, with how long each step took (shown in dev mode).</summary>
    internal sealed class Exchange
    {
        public readonly Stopwatch Clock = Stopwatch.StartNew();
        public string Who, Mode, Heard, Reply, Voice, Error;
        public double Stt = -1, Llm = -1, Tts = -1, Total = -1;
        public System.Threading.Tasks.Task<JevChoice> ActionPick; // Jev choosing what they physically do
        public string Action;                                     // what the action picker picked, for the dev panel
        public double ActionSeconds = -1;
    }

    /// <summary>
    /// Dev mode (F9). The first time it opens it fades out, puts you in an open spot ([Dev] Location),
    /// and spawns every character in [Dev] Scene in a ring around you, facing you, so you can walk up
    /// to anyone and press B. While it's on: no police, you can't die, and the panel shows a menu
    /// (Enter goes to that character, spawning them if needed), a marker on who B talks to, and the
    /// timing of every step of the last exchange.
    /// </summary>
    internal sealed class DevMode
    {
        private const int VisibleRows = 14;

        private readonly CharacterBook _book;
        private readonly Func<Character, string> _voiceLabel;
        private readonly Vector3 _location;
        private readonly float _heading;
        private readonly List<string> _sceneKeys;
        private readonly Dictionary<string, Ped> _spawned = new Dictionary<string, Ped>(StringComparer.OrdinalIgnoreCase);
        private readonly List<Character> _menu;
        private int _index, _top;
        private Action _pending; // scene and teleport work runs from Tick, where Script.Wait is safe

        public bool On { get; private set; }
        public bool Panels { get; set; } = true; // F10: the menu (top left) and the timings (top right) on or off

        public DevMode(CharacterBook book, Func<Character, string> voiceLabel, Vector3 location, float heading, IEnumerable<string> sceneKeys)
        {
            _book = book;
            _voiceLabel = voiceLabel;
            _location = location;
            _heading = heading;
            _sceneKeys = sceneKeys.Select(k => k.Trim()).Where(k => k.Length > 0).ToList();

            // Scene cast first, in scene order, then everyone else alphabetically.
            var cast = _sceneKeys.Select(book.ByKey).Where(c => c != null).ToList();
            _menu = cast.Concat(book.All.Where(c => !cast.Contains(c))).ToList();
        }

        public IEnumerable<Ped> SpawnedPeds => _spawned.Values.Where(p => p != null && p.Exists());

        public void Open()
        {
            On = true;
            if (!SpawnedPeds.Any()) _pending = BuildScene;
        }

        public void Close()
        {
            On = false;
            Game.Player.IsInvincible = false;
            Function.Call(Hash.SET_MAX_WANTED_LEVEL, 5);
        }

        public bool HandleKey(Keys key)
        {
            switch (key)
            {
                case Keys.Up: Move(-1); return true;
                case Keys.Down: Move(1); return true;
                case Keys.Enter:
                    if (_menu.Count > 0) { Character c = _menu[_index]; _pending = () => GoTo(c); }
                    return true;
                case Keys.Delete:
                    _pending = BuildScene;
                    return true;
                default: return false;
            }
        }

        /// <summary>Every frame while the mod runs.</summary>
        public void Tick()
        {
            Action pending = _pending;
            _pending = null;
            pending?.Invoke();

            if (!On) return;
            Game.DisableControlThisFrame(Control.Phone); // Up arrow would open the phone
            Game.Player.IsInvincible = true;
            if (Game.Player.WantedLevel > 0) Game.Player.WantedLevel = 0;
            Function.Call(Hash.SET_MAX_WANTED_LEVEL, 0);
        }

        public void ClearSpawned()
        {
            foreach (Ped p in _spawned.Values)
            {
                if (p == null || !p.Exists()) continue;
                p.AttachedBlip?.Delete();
                p.Delete();
            }
            _spawned.Clear();
        }

        // ------------------------------------------------------------ scene

        private void BuildScene()
        {
            Ped player = Game.Player.Character;
            Screen.FadeOut(400);
            { Script.Wait(450); Heartbeat.Beat(); }

            ClearSpawned();
            Teleport(_location, _heading);
            Function.Call(Hash.CLEAR_AREA, _location.X, _location.Y, _location.Z, 30f, true, false, false, false);
            Function.Call(Hash.CLEAR_AREA_OF_PEDS, _location.X, _location.Y, _location.Z, 30f, 1);
            World.CurrentTimeOfDay = new TimeSpan(13, 0, 0);
            World.Weather = Weather.ExtraSunny;
            Game.Player.WantedLevel = 0;

            var cast = _sceneKeys.Select(_book.ByKey).Where(c => c != null && c.ModelHash != player.Model.Hash).ToList();
            float radius = Math.Max(3.5f, cast.Count * 1.8f / (2f * (float)Math.PI));
            for (int i = 0; i < cast.Count; i++)
            {
                float angle = _heading + i * 360f / cast.Count; // the first character stands right in front of you
                double rad = angle * Math.PI / 180.0;
                var pos = new Vector3(_location.X - (float)Math.Sin(rad) * radius, _location.Y + (float)Math.Cos(rad) * radius, _location.Z);
                float ground = World.GetGroundHeight(pos + new Vector3(0f, 0f, 2f));
                if (ground > 0f && Math.Abs(ground - (_location.Z - 1f)) < 3f) pos.Z = ground + 1f;
                Spawn(cast[i], pos, angle + 180f);
            }

            Log.Write($"dev: scene at {_location} with {cast.Count} characters");
            Screen.FadeIn(600);
            Screen.ShowHelpText("~y~NPC Talk dev mode~s~~n~Walk up to anyone: ~b~B~s~ to type, hold ~b~N~s~ to speak.~n~~b~F9~s~ menu: Enter goes to a character, Del resets the scene.", 12000, true, false);
            Screen.ShowSubtitle("Talk to the ~y~characters~s~.", 6000);
        }

        /// <summary>Go to a character: in front of them if they're spawned, otherwise spawn them in front of you.</summary>
        private void GoTo(Character c)
        {
            Ped player = Game.Player.Character;
            if (_spawned.TryGetValue(c.Key, out Ped p) && p != null && p.Exists() && !p.IsDead)
            {
                if (p.Position.DistanceTo(player.Position) > 4f)
                {
                    Screen.FadeOut(250);
                    { Script.Wait(260); Heartbeat.Beat(); }
                    Teleport(p.Position + p.ForwardVector * 1.8f, p.Heading + 180f);
                    Screen.FadeIn(300);
                }
                p.Task.TurnTo(player);
                p.Task.LookAt(player, -1);
                return;
            }
            Spawn(c, player.Position + player.ForwardVector * 2.2f, player.Heading + 180f);
        }

        private void Spawn(Character c, Vector3 pos, float heading)
        {
            var model = new Model(c.ModelHash);
            if (!model.Request(3000))
            {
                Notification.Show($"NpcVoice: couldn't load the model for {c.Name}");
                return;
            }
            Ped ped = World.CreatePed(model, pos, heading);
            model.MarkAsNoLongerNeeded();
            if (ped == null) return;

            Function.Call(Hash.SET_PED_DEFAULT_COMPONENT_VARIATION, ped);
            ped.IsPersistent = true;
            ped.BlockPermanentEvents = true; // don't flee or fight; stand there and talk
            ped.IsInvincible = true;
            ped.Task.StandStill(-1);
            ped.Task.LookAt(Game.Player.Character, -1);

            Blip blip = ped.AddBlip();
            blip.Name = c.Name;
            blip.Color = BlipColor.Yellow;
            blip.Scale = 0.7f;
            blip.IsShortRange = true;

            if (_spawned.TryGetValue(c.Key, out Ped old) && old != null && old.Exists() && old != ped)
            {
                old.AttachedBlip?.Delete();
                old.Delete();
            }
            _spawned[c.Key] = ped;
        }

        internal static void Teleport(Vector3 pos, float heading)
        {
            Ped player = Game.Player.Character;
            if (player.IsInVehicle()) Function.Call(Hash.CLEAR_PED_TASKS_IMMEDIATELY, player); // warps you out
            player.Position = pos;
            player.Heading = heading;
            Function.Call(Hash.REQUEST_COLLISION_AT_COORD, pos.X, pos.Y, pos.Z);
            for (int i = 0; i < 60 && !Function.Call<bool>(Hash.HAS_COLLISION_LOADED_AROUND_ENTITY, player); i++)
                { Script.Wait(50); Heartbeat.Beat(); }
        }

        private void Move(int delta)
        {
            if (_menu.Count == 0) return;
            _index = ((_index + delta) % _menu.Count + _menu.Count) % _menu.Count;
            if (_index < _top) _top = _index;
            if (_index >= _top + VisibleRows) _top = _index - VisibleRows + 1;
        }

        // ------------------------------------------------------------ drawing

        public void Draw(Ped target, string targetLabel, Exchange last, string engines, string status, string actionHint)
        {
            Ped player = Game.Player.Character;
            if (Panels)
            {
                DrawMenu(player);
                DrawHud(last, engines, status, actionHint);
            }
            if (target != null && target.Exists()) DrawTarget(target, targetLabel);
        }

        private void DrawMenu(Ped player)
        {
            const float x = 28f, y = 24f, w = 360f, row = 22f;
            int rows = Math.Min(VisibleRows, _menu.Count);
            new ContainerElement(new PointF(x - 10f, y - 8f), new SizeF(w, 64f + row * rows), Color.FromArgb(185, 0, 0, 0)).Draw();
            Text("NPC TALK  ~c~dev mode", x, y, 0.45f, Color.Gold);
            Text("Up/Down select   Enter go to   Del reset scene   F9 off   F10 panels", x, y + 24f, 0.28f, Color.LightGray);

            for (int i = 0; i < rows; i++)
            {
                int idx = _top + i;
                Character c = _menu[idx];
                bool sel = idx == _index;
                string where = _spawned.TryGetValue(c.Key, out Ped p) && p != null && p.Exists()
                    ? $"{p.Position.DistanceTo(player.Position):F0} m" : "spawn";
                Color color = sel ? Color.DeepSkyBlue : Color.White;
                float ry = y + 46f + row * i;
                Text((sel ? "> " : "  ") + c.Name, x, ry, 0.33f, color);
                Text($"{_voiceLabel(c)}  ~c~{where}", x + w - 22f, ry, 0.3f, sel ? Color.DeepSkyBlue : Color.Silver, Alignment.Right);
            }
            if (_menu.Count > rows)
                Text($"{_index + 1}/{_menu.Count}", x + w - 22f, y, 0.3f, Color.Gray, Alignment.Right);
        }

        private static void DrawHud(Exchange e, string engines, string status, string actionHint)
        {
            const float right = 1262f, y = 24f, w = 430f;
            new ContainerElement(new PointF(right - w, y - 8f), new SizeF(w + 8f, 196f), Color.FromArgb(185, 0, 0, 0)).Draw();
            Text(engines, right - 8f, y, 0.3f, Color.Gold, Alignment.Right);
            Text(status ?? "", right - 8f, y + 20f, 0.3f, Color.LightGray, Alignment.Right);
            if (actionHint != null) Text(actionHint, right - 8f, y + 136f, 0.28f, Color.LightGray, Alignment.Right);
            if (e == null)
            {
                Text("Walk up to someone and press B", right - 8f, y + 46f, 0.32f, Color.White, Alignment.Right);
                return;
            }
            Text($"{e.Who} ~c~({e.Mode}{(e.Voice != null ? ", " + e.Voice : "")})", right - 8f, y + 44f, 0.32f, Color.White, Alignment.Right);
            Text($"STT {Sec(e.Stt)}   LLM {Sec(e.Llm)}   TTS {Sec(e.Tts)}   ~y~total {Sec(e.Total)}~s~   ~b~{e.Action ?? (e.ActionPick != null ? "picking..." : "")}", right - 8f, y + 66f, 0.32f, Color.White, Alignment.Right);
            Text("~b~You:~s~ " + Clip(e.Heard, 60), right - 8f, y + 90f, 0.29f, Color.White, Alignment.Right);
            Text(e.Error != null ? "~r~" + Clip(e.Error, 64) : "~y~Reply:~s~ " + Clip(e.Reply, 60), right - 8f, y + 110f, 0.29f, Color.White, Alignment.Right);
        }

        private static void DrawTarget(Ped target, string label)
        {
            Vector3 head = target.Position + new Vector3(0f, 0f, 1.15f);
            World.DrawMarker(MarkerType.UpsideDownCone, head, Vector3.Zero, Vector3.Zero, new Vector3(0.22f, 0.22f, 0.18f),
                Color.FromArgb(200, 255, 200, 0), true, true, false, null, null, false);
            PointF screen = Screen.WorldToScreen(head + new Vector3(0f, 0f, 0.25f));
            if (screen != PointF.Empty)
                new TextElement(label, screen, 0.34f, Color.White, GTA.UI.Font.ChaletLondon, Alignment.Center) { Outline = true }.Draw();
        }

        private static void Text(string s, float x, float y, float scale, Color color, Alignment align = Alignment.Left) =>
            new TextElement(s, new PointF(x, y), scale, color, GTA.UI.Font.ChaletLondon, align) { Outline = true }.Draw();

        private static string Sec(double s) => s < 0 ? "--" : $"{s:F2}s";

        private static string Clip(string s, int max) => string.IsNullOrEmpty(s) ? "..." : s.Length > max ? s.Substring(0, max - 3) + "..." : s;
    }
}
