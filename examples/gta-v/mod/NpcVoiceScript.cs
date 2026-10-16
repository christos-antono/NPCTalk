// NpcVoice: talk to anyone in GTA V. Walk up to a character and:
//   press B   type what you say
//   hold N    say it into your mic; release to send (speech-to-text)
// They answer in character (Cerebras LLM, per-NPC memory) in their own cloned voice (fal TTS), with
// their face animating in time with the audio.
// F9 dev mode: a scene with every voiced character around you, a menu to go to any of them, a marker
// on who you talk to and the timing of each step. Optional ambient mode swaps NPC chatter for AI lines.
// Single-player only.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using GTA;
using GTA.Math;
using GTA.Native;
using GTA.UI;

namespace NpcVoice
{
    public partial class NpcVoiceScript : Script
    {
        private const string TalkDict = "mp_facial", TalkAnim = "mic_chatter";
        private const string IdleDict = "facials@gen_male@base", IdleAnim = "mood_normal_1";
        private const float MouthOpenLevel = 0.04f;
        private const int MouthCloseDelayMs = 180;
        private const int EarlySubtitleMs = 1000; // show the reply even if the voice is still generating

        private sealed class PendingLine
        {
            public Ped Ped;
            public string Text;
            public string Speaker;
            public bool Priority; // conversation replies cut off ambient lines
            public Exchange Exchange;
        }

        private readonly VoiceService _service;
        private readonly VoiceLibrary _voices;
        private readonly LineBook _lines;
        private readonly CharacterBook _book;
        private readonly LlmClient _llm;
        private readonly ISpeechToText _stt;
        private readonly MicRecorder _mic = new MicRecorder();
        private readonly DevMode _dev;
        private readonly Visits _visits;
        private readonly NpcActions _actions;
        private readonly IActionPicker _picker;
        private readonly bool _actionsOn;
        private int _testAction = 1;
        private string _devNote; // last dev-mode tool message
        private readonly CancellationTokenSource _shutdown = new CancellationTokenSource();
        private readonly Dictionary<int, int> _nextAllowed = new Dictionary<int, int>();
        private readonly List<string> _warnings = new List<string>();
        private readonly Keys _toggleKey, _testKey, _devKey, _devPanelsKey, _typeKey, _micKey;
        private readonly float _radius, _fadeStart, _fadeEnd;
        private readonly int _pedCooldownMs;
        private readonly bool _subtitles, _ambient, _autoStart;

        private volatile bool _enabled = true;
        private int _startAt = -1;
        private bool _started;

        // B: held down -> recording; released quickly -> type instead
        private volatile bool _micHeld;

        // Open mic (proximity chat): always listening; utterances queue up for the game thread.
        private readonly OpenMic _openMic;
        private readonly Keys _openMicKey;
        private readonly System.Collections.Concurrent.ConcurrentQueue<Tuple<byte[], double, float>> _utterances = new System.Collections.Concurrent.ConcurrentQueue<Tuple<byte[], double, float>>();
        private volatile bool _typing;
        private int _quietUntil; // Environment.TickCount; the open mic stays paused a moment after an NPC line
        private Ped _micTarget;
        private Ped _typeTarget;
        private bool _wantType;
        private int _ignoreKeysUntil;

        private Ped _partner;           // who you last talked to; stays the target while close
        private Ped _target;            // who B would talk to right now (refreshed a few times a second)
        private int _nextTargetScan;

        private Ped _speaker;
        private string _subtitle;
        private int _subtitleAt;
        private bool _mouthOpen;
        private int _lastLoudMs, _nextMouthRefresh;

        private volatile PendingLine _pending;
        private volatile bool _thinking;
        private volatile string _status;
        private volatile string _heard;
        private volatile Exchange _last;

        public NpcVoiceScript()
        {
            Net.Init();
            string dataDir = Path.Combine(BaseDirectory, "NpcVoice");
            Log.Init(Path.Combine(dataDir, "NpcVoice.log"));

            _toggleKey = ReadKey("Toggle", Keys.F7);
            _testKey = ReadKey("Test", Keys.F8);
            _devKey = ReadKey("DevMode", Keys.F9);
            _devPanelsKey = ReadKey("DevPanels", Keys.F10);
            _typeKey = ReadKey("Type", Keys.B);
            _micKey = ReadKey("PushToTalk", Keys.N);
            _openMicKey = ReadKey("MicToggle", Keys.M);
            _radius = Settings.GetValue("Npc", "Radius", 25f);
            _pedCooldownMs = Settings.GetValue("Npc", "CooldownSeconds", 8) * 1000;
            _subtitles = Settings.GetValue("Npc", "Subtitles", true);
            _ambient = Settings.GetValue("Npc", "Ambient", false);
            _fadeStart = Settings.GetValue("Audio", "FadeStart", 4f);
            _fadeEnd = Settings.GetValue("Audio", "FadeEnd", 30f);
            _autoStart = Settings.GetValue("Dev", "AutoStart", true);

            FalApi.Key = Key(Settings.GetValue("Fal", "ApiKeyEnv", "FAL_KEY"), Settings.GetValue("Fal", "ApiKey", ""));
            EngineConfig.Setting ini = (section, key, fallback) =>
            {
                string v = Settings.GetValue(section, key, fallback);
                return string.IsNullOrWhiteSpace(v) ? fallback : v;
            };

            _lines = new LineBook(Path.Combine(dataDir, "lines.txt"));
            _book = new CharacterBook(Path.Combine(dataDir, "characters"), Warn);
            _voices = new VoiceLibrary(Path.Combine(dataDir, "voices"),
                List(Settings.GetValue("Voices", "MalePool", "Hillbilly, Marine")),
                List(Settings.GetValue("Voices", "FemalePool", "FemaleCop")), Warn);

            _llm = EngineConfig.Llm(ini);
            _stt = EngineConfig.SpeechToText(ini, FalApi.Key);

            string engine = Settings.GetValue("Tts", "Engine", "modal");
            VoiceGroup.PreferLongReference = engine.Equals("modal", StringComparison.OrdinalIgnoreCase) || engine.Equals("fish", StringComparison.OrdinalIgnoreCase);
            string appId = Settings.GetValue("Fal", "AppId", "");
            string falPath = Settings.GetValue("Fal", "Path", "realtime");
            int luxRef = Settings.GetValue("Tts", "LuxReferenceSeconds", 10);
            string modalUrl = Net.HttpsUrl(Settings.GetValue("Modal", "Url", ""), "");
            string modalToken = Key(Settings.GetValue("Modal", "TokenEnv", "NPCVOICE_TOKEN"), "");
            ITts tts;
            try { tts = Tts.Create(engine, appId, falPath, luxRef, modalUrl, modalToken); }
            catch (ArgumentException ex)
            {
                Warn(ex.Message + "; using minimax");
                tts = Tts.Create("minimax", appId, falPath, luxRef);
            }
            string fallbackName = Settings.GetValue("Tts", "Fallback", "minimax");
            ITts fallback = string.IsNullOrWhiteSpace(fallbackName) || fallbackName.Equals(tts.Name, StringComparison.OrdinalIgnoreCase)
                ? null : Tts.Create(fallbackName, appId, falPath, luxRef, modalUrl, modalToken);
            _service = new VoiceService(tts, fallback, Settings.GetValue("Tts", "KeepWarmSeconds", 90));
            string renderEngine = tts.Name;
            int renderBestOf = Settings.GetValue("Tts", "DirectorBestOf", 1);
            _renderEngine = () => Tts.Create(renderEngine, appId, falPath, luxRef, modalUrl, modalToken, renderBestOf); // director clips render ahead

            if (Settings.GetValue("Mic", "OpenMic", true))
            {
                _openMic = new OpenMic(Settings.GetValue("Mic", "SensitivityDb", 12f), Settings.GetValue("Mic", "SilenceMs", 700),
                    OpenMicPaused, (wav, seconds, peak) => _utterances.Enqueue(Tuple.Create(wav, seconds, peak)));
                try { _openMic.Start(); }
                catch (Exception ex) { Warn("open mic didn't start: " + ex.Message); }
            }

            _dev = new DevMode(_book, VoiceLabel,
                ReadVector(Settings.GetValue("Dev", "Location", "215.76, -810.12, 30.73"), new Vector3(215.76f, -810.12f, 30.73f)),
                Settings.GetValue("Dev", "Heading", 160f),
                List(Settings.GetValue("Dev", "Scene", "Franklin, Michael, Trevor, LamarDavis, LesterCrest, AmandaTownley, JimmyDisanto, TracyDisanto, DaveNorton, SiemonYetarian, Cop01SFY, Hillbilly01AMM, Marine02SMY")));

            _actionsOn = Settings.GetValue("Actions", "Enabled", true);
            _picker = EngineConfig.ActionPicker(ini, _llm);
            _visits = new Visits(_book, ReadKey("VisitMichael", Keys.J), ReadKey("VisitFranklin", Keys.K), ReadKey("VisitTrevor", Keys.L));
            _actions = new NpcActions(Path.Combine(dataDir, "undress.txt"), p => _dev.SpawnedPeds.Contains(p) || _visits.Peds.Contains(p));

            if (!_stt.IsConfigured) Warn(_stt is FalSpeechToText ? "no fal key: set the FAL_KEY environment variable" : "speech-to-text needs a key: set [Stt] ApiKeyEnv");
            if (!_llm.IsConfigured) Warn("no LLM key: set CEREBRAS_API_KEY, or point [Llm] BaseUrl at a server on this PC");
            if (_actionsOn && !_picker.IsConfigured)
                Warn(_picker is JevClient ? "NPC actions need a Jev key: set TYPESAFE_API_KEY, or [Actions] Provider = llm" : "NPC actions need the LLM: see [Llm]");
            if (_voices.Groups.Count == 0) Warn("no voices in scripts/NpcVoice/voices/ (run voices.ts --install)");
            Log.Write($"start: tts {_service.Engine}, {_voices.Groups.Count} voices ({_voices.ClipCount} clips), {_book.All.Count} characters ({_book.GameLineCount} game lines), " +
                      $"llm {_llm.Model} at {_llm.Url} {(_llm.IsConfigured ? "ok" : "MISSING KEY")}, stt {_stt.Name} {(_stt.IsConfigured ? "ok" : "MISSING KEY")}, fal key {(string.IsNullOrEmpty(FalApi.Key) ? "MISSING" : "ok")}, " +
                      $"actions {(_actionsOn ? _picker.Name + (_picker.IsConfigured ? " ok" : " MISSING KEY") : "off")}");

            if (!string.IsNullOrEmpty(FalApi.Key))
            {
                var sw = Stopwatch.StartNew();
                _voices.UploadAllAsync(Warn).ContinueWith(_ =>
                {
                    Log.Write($"uploaded {_voices.Uploaded}/{_voices.ClipCount} clips in {sw.Elapsed.TotalSeconds:F1}s");
                    _service.Message = $"{_voices.Uploaded}/{_voices.ClipCount} voice clips ready, warming up {_service.Engine}...";
                    if (_service.Engine == "fish") Prewarm(() => Tts.Create("fish", appId, falPath, 0), parallel: 1);
                    else if (_service.Engine == "modal") Prewarm(() => Tts.Create("modal", null, null, 0, modalUrl, modalToken), parallel: 4);
                    else _service.KeepWarm(AnyVoice, force: true);
                });
            }

            Function.Call(Hash.REQUEST_ANIM_DICT, TalkDict);
            Function.Call(Hash.REQUEST_ANIM_DICT, IdleDict);

            Tick += OnTick;
            KeyDown += OnKeyDown;
            KeyUp += OnKeyUp;
            Aborted += (s, e) =>
            {
                _shutdown.Cancel();
                _director?.Dispose();
                EndSpeaker();
                if (_dev.On) _dev.Close();
                _dev.ClearSpawned();
                _visits.Clear();
                _actions.StopAll();
                _openMic?.Dispose();
                _mic.Dispose();
                _service.Dispose();
            };
        }

        /// <summary>
        /// Dev mode action tools: PgUp/PgDn pick an action and End makes the targeted NPC do it;
        /// [ and ] cycle the target's top and pants and save that as their shirt-off / pants-off look.
        /// They work with NpcVoice turned off (F7) too: they don't need the voices.
        /// </summary>
        private bool HandleActionKey(Keys key)
        {
            Ped target = _enabled ? _target : TalkTarget();
            switch (key)
            {
                case Keys.PageUp:
                case Keys.PageDown:
                    int n = ActionCatalog.All.Length - 1; // skip "none"
                    _testAction = (_testAction - 1 + (key == Keys.PageDown ? 1 : n - 1)) % n + 1;
                    return true;
                case Keys.End:
                    if (target == null) _devNote = "nobody to do it: stand within 5 m of someone, or look right at them";
                    else
                    {
                        _actions.Perform(target, ActionCatalog.All[_testAction].Name);
                        _devNote = $"{CharacterName(target)}: {ActionCatalog.All[_testAction].Name}";
                    }
                    return true;
                case Keys.OemOpenBrackets:
                case Keys.OemCloseBrackets:
                    _devNote = target == null ? "nobody to change clothes on: stand within 5 m of someone"
                        : _actions.CycleClothes(target, key == Keys.OemOpenBrackets ? "shirt" : "pants");
                    return true;
                default:
                    return false;
            }
        }

        private string ActionHint() =>
            $"PgUp/PgDn action: ~y~{ActionCatalog.All[_testAction].Name}~s~   End: do it   [ ] clothes" + (_devNote != null ? $"~n~~c~{_devNote}" : "");

        /// <summary>
        /// Capture thread: the open mic ignores what it hears while you hold push-to-talk or type, while
        /// a reply is being worked out, and while (and just after) an NPC talks, so their voice coming
        /// out of the speakers isn't taken for yours.
        /// </summary>
        private bool OpenMicPaused() =>
            !_enabled || _micHeld || _typing || _thinking || _service.IsBusy || unchecked(Environment.TickCount - _quietUntil) < 0;

        // ---------------------------------------------------------------- input

        private void OnKeyDown(object sender, KeyEventArgs e)
        {
            // Keys typed into the on-screen keyboard arrive after it closes; drop them.
            if (unchecked(Environment.TickCount - _ignoreKeysUntil) < 0) return;
            if (_dev.On && _dev.HandleKey(e.KeyCode)) return;
            if (_dev.On && HandleActionKey(e.KeyCode)) return;
            if (_visits.HandleKey(e.KeyCode)) return;

            if (e.KeyCode == _toggleKey)
            {
                _enabled = !_enabled;
                if (!_enabled) { _service.Cancel(); EndSpeaker(); }
                Note("NpcVoice " + (_enabled ? "~g~on" : "~r~off"));
            }
            else if (e.KeyCode == _openMicKey && _openMic != null)
            {
                _openMic.Muted = !_openMic.Muted;
                Note(_openMic.Muted
                    ? "NpcVoice: open mic ~r~off~s~ (hold ~b~N~s~ to talk, ~b~M~s~ to turn it back on)"
                    : "NpcVoice: open mic ~g~on~s~: just talk to whoever's near you");
            }
            else if (e.KeyCode == _devPanelsKey && _dev.On)
            {
                _dev.Panels = !_dev.Panels;
            }
            else if (e.KeyCode == _devKey)
            {
                if (_dev.On) _dev.Close();
                else _dev.Open();
            }
            else if (e.KeyCode == _testKey && _enabled)
            {
                Ped nearest = Nearest(Game.Player.Character, 10f);
                if (nearest == null) Note("NpcVoice: no NPC nearby");
                else if (!TryAmbient(nearest, ignoreCooldown: true)) Note("NpcVoice: busy or no voice ready");
            }
            else if (e.KeyCode == _typeKey && _enabled && !_micHeld && !_wantType)
            {
                Ped target = _thinking ? null : TalkTarget();
                if (_thinking) Note("NpcVoice: still answering the last thing you said");
                else if (target == null) Note("NpcVoice: nobody close enough to talk to");
                else
                {
                    target.Task.LookAt(Game.Player.Character, 10000);
                    _typeTarget = target;
                    _wantType = true; // GetUserInput yields, so it runs from Tick
                }
            }
            else if (e.KeyCode == _micKey && _enabled && !_micHeld)
            {
                _micHeld = true; // also swallows key auto-repeat while held
                _micTarget = _thinking ? null : TalkTarget();
                if (_thinking) Note("NpcVoice: still answering the last thing you said");
                else if (_micTarget == null) Note("NpcVoice: nobody close enough to talk to");
                else
                {
                    string muted = MicRecorder.MutedDefaultMic();
                    if (muted != null)
                        Note($"~r~Your mic is muted in Windows~s~ ({muted}). Press your laptop's mic-mute key or unmute it in Settings > Sound > Input.");
                    _micTarget.Task.LookAt(Game.Player.Character, 10000);
                    try { _mic.Start(); }
                    catch (Exception ex) { Warn("mic didn't start: " + ex.Message); }
                }
            }
        }

        private void OnKeyUp(object sender, KeyEventArgs e)
        {
            if (e.KeyCode != _micKey || !_micHeld) return;
            _micHeld = false;
            Ped target = _micTarget;
            _micTarget = null;
            byte[] wav = _mic.IsRecording ? _mic.Stop() : null;
            if (target == null || !target.Exists() || target.IsDead) return;

            if (wav == null)
            {
                Note("NpcVoice: the mic didn't start (Windows Settings > Privacy > Microphone)");
                return;
            }
            double seconds = MicRecorder.Seconds(wav);
            float peak = _mic.Peak;
            Log.Write($"mic {seconds:F1}s peak {peak:F3}");
            try { File.WriteAllBytes(Path.Combine(BaseDirectory, "NpcVoice", "last_mic.wav"), wav); } catch { } // for checking what the mic heard
            if (seconds < 0.4) Note("NpcVoice: too short, hold N while you talk");
            else if (peak < 0.003) Note("NpcVoice: ~r~your mic recorded silence~s~. Is it muted? (laptop mic key, or Settings > Sound > Input)");
            else Converse(target, wav, null);
        }

        // ---------------------------------------------------------------- per frame

        private void OnTick(object sender, EventArgs e)
        {
            if (!_clean) ShowMessages();
            Ped player = Game.Player.Character;
            if (player == null || !player.Exists() || Game.IsLoading) return;
            DirectorTick();

            if (!_started)
            {
                if (_startAt < 0) _startAt = Game.GameTime + 1500;
                if (Game.GameTime >= _startAt && player.IsAlive)
                {
                    _started = true;
                    Note(_openMic != null
                        ? $"~y~NPC Talk~s~ ready. ~g~Open mic~s~: just talk to whoever's near you (~b~M~s~ mutes). ~b~B~s~ type, ~b~F9~s~ dev mode. Homes: {_visits.KeysHint}"
                        : $"~y~NPC Talk~s~ ready. ~b~B~s~ type, hold ~b~N~s~ to speak, ~b~F9~s~ dev mode. Homes: {_visits.KeysHint}");
                    if (_autoStart) _dev.Open();
                    if (_actionsOn) _actions.Audit();
                }
            }

            _dev.Tick();
            _visits.Tick();
            if (Game.GameTime >= _nextTargetScan)
            {
                _nextTargetScan = Game.GameTime + 150;
                _target = _enabled ? TalkTarget() : null;
                if (_target != null) _service.Prepare(VoiceLibrary.Pick(GroupFor(_target), _target.Handle, null));
            }
            Ped shown = _micHeld ? _micTarget : _target;
            if (_clean) { }
            else if (_dev.On) _dev.Draw(shown, shown != null ? TargetLabel(shown) : null, _last, Engines(), Status(), ActionHint());
            else if (shown != null && !_micHeld && !_thinking && _book.Find(shown.Model.Hash) != null
                     && shown.Position.DistanceTo(player.Position) < 4f)
                GTA.UI.Screen.ShowHelpTextThisFrame(_openMic != null && !_openMic.Muted
                    ? $"Just talk to {CharacterName(shown)}, or press ~b~B~s~ to type."
                    : $"Press ~b~B~s~ to type to {CharacterName(shown)}, hold ~b~N~s~ to speak.", false);

            if (_clean) _heard = null;
            else DrawStatus();
            if (!_enabled) return;

            if (_wantType)
            {
                _wantType = false;
                Ped target = _typeTarget;
                _typeTarget = null;
                _typing = true;
                string said = Game.GetUserInput(WindowTitle.EnterMessage60, "", 120);
                _typing = false;
                _ignoreKeysUntil = Environment.TickCount + 300;
                if (!string.IsNullOrWhiteSpace(said) && target != null && target.Exists()) Converse(target, null, said.Trim());
            }

            DispatchPending();
            _actions.Tick();
            UpdateSpeaker(player);
            _service.KeepWarm(AnyVoice);

            // Open mic: hold off a moment after an NPC stops so the end of their line isn't heard as you.
            if (_service.IsBusy) _quietUntil = Environment.TickCount + 600;
            while (_utterances.TryDequeue(out var heard)) OnOpenMic(heard.Item1, heard.Item2, heard.Item3);

            // Leave mission dialogue alone; only ambient chatter gets replaced.
            if (!_ambient || Function.Call<bool>(Hash.IS_SCRIPTED_CONVERSATION_ONGOING)) return;
            foreach (Ped ped in World.GetNearbyPeds(player, _radius))
            {
                if (ped.Handle == player.Handle || !ped.IsHuman || ped.IsDead) continue;
                if (!Function.Call<bool>(Hash.IS_ANY_SPEECH_PLAYING, ped)) continue;

                // Every NPC within range is muted; one at a time gets the AI voice.
                Function.Call(Hash.STOP_CURRENT_PLAYING_AMBIENT_SPEECH, ped);
                TryAmbient(ped, ignoreCooldown: false);
            }
        }

        private void DrawStatus()
        {
            if (_mic.IsRecording && _micHeld)
            {
                string who = _micTarget != null && _micTarget.Exists() ? CharacterName(_micTarget) : "them";
                CenterText($"~r~o~s~ Talking to {who}... release ~b~N~s~ to send", 600f);
                float level = Math.Min(1f, _mic.Level * 4f);
                new ContainerElement(new PointF(540f, 628f), new SizeF(200f, 8f), Color.FromArgb(160, 0, 0, 0)).Draw();
                new ContainerElement(new PointF(540f, 628f), new SizeF(200f * level, 8f), Color.FromArgb(220, 80, 200, 120)).Draw();
            }
            else if (_openMic != null && _openMic.Hearing)
            {
                string who = _target != null && _target.Exists() ? CharacterName(_target) : "nobody near you";
                CenterText($"~g~o~s~ Listening... ~c~(to {who})", 600f);
                new ContainerElement(new PointF(540f, 628f), new SizeF(200f, 8f), Color.FromArgb(160, 0, 0, 0)).Draw();
                new ContainerElement(new PointF(540f, 628f), new SizeF(200f * Math.Min(1f, _openMic.Level * 4f), 8f), Color.FromArgb(220, 80, 200, 120)).Draw();
            }
            else if (_thinking && _status != null) CenterText(_status, 600f);

            // Off (F7) is easy to miss: the notification goes by in a few seconds and then nobody answers.
            string state = !_enabled ? "~r~NpcVoice off~s~ (F7)" : _openMic == null ? null : _openMic.Muted ? "~r~mic off~s~ (M)" : "~g~mic on~s~ (M)";
            if (state != null)
                new TextElement(state, new PointF(1262f, 694f), 0.3f, Color.White, GTA.UI.Font.ChaletLondon, Alignment.Right) { Outline = true }.Draw();

            string heard = _heard;
            if (heard != null)
            {
                _heard = null;
                Note("~b~You:~s~ " + heard);
            }
        }

        private static void CenterText(string text, float y) =>
            new TextElement(text, new PointF(640f, y), 0.45f, Color.White, GTA.UI.Font.ChaletLondon, Alignment.Center) { Outline = true }.Draw();

        // ---------------------------------------------------------------- lines

        /// <summary>Ambient: a known character gets an LLM line for the situation, anyone else a scripted line.</summary>
        private bool TryAmbient(Ped ped, bool ignoreCooldown)
        {
            int now = Game.GameTime;
            if (!ignoreCooldown && _nextAllowed.TryGetValue(ped.Handle, out int next) && now < next) return false;
            if (_service.IsBusy || _thinking || _pending != null) return false;

            string situation = Situation(ped);
            PedInfo info = PedInfo.Of(ped);
            Character c = _book.Find(info.ModelHash);
            _nextAllowed[ped.Handle] = now + _pedCooldownMs;

            if (c != null && _llm.IsConfigured)
            {
                string playerName = CharacterBook.PlayerName(Game.Player.Character), scene = CharacterBook.SceneOf(ped, Game.Player.Character, _visits.PlaceAt(ped.Position));
                Generate(ped, c, priority: false, status: null, exchange: null, produce: async ct =>
                    Dialogue.Clean(await _llm.CompleteAsync(_book.SituationPrompt(info, situation, playerName, scene), ct).ConfigureAwait(false), c));
                return true;
            }

            string text = _lines.Pick(situation);
            return text != null && Speak(ped, text, null, null);
        }

        /// <summary>Something the open mic heard: it goes to whoever B/N would talk to right now.</summary>
        private void OnOpenMic(byte[] wav, double seconds, float peak)
        {
            if (!_enabled || _thinking || _service.IsBusy) return;
            Ped target = TalkTarget();
            Log.Write($"open mic {seconds:F1}s peak {peak:F3} -> {(target != null ? CharacterName(target) : "nobody near")}");
            if (target != null) Converse(target, wav, null, openMic: true);
        }

        /// <summary>The open mic also hears coughs, the game and the TV; only pass on things that look like words to someone.</summary>
        private static bool LooksLikeSpeech(string said)
        {
            string t = Regex.Replace((said ?? "").ToLowerInvariant(), @"[^a-z' ]", " ").Trim();
            int words = Regex.Matches(t, "[a-z']+").Count;
            if (words == 0) return false;
            return words > 1 || !Regex.IsMatch(t, @"^(u+h+|u+m+|h+m+|m+|a+h+|o+h+|e+h+|huh|you|the|a|and|so|yeah|okay|ok|thanks)$");
        }

        private sealed class NotSpeechException : Exception
        {
            public NotSpeechException(string heard) : base("not speech: " + heard) { }
        }

        /// <summary>Mic (wav) or typed text: transcribe if needed, answer in character, then speak.</summary>
        private void Converse(Ped ped, byte[] wav, string typed, bool openMic = false, string direction = null)
        {
            if (!_llm.IsConfigured)
            {
                Note("NpcVoice: set CEREBRAS_API_KEY to talk to characters");
                return;
            }

            PedInfo info = PedInfo.Of(ped);
            Character c = _book.Find(info.ModelHash);
            string playerName = CharacterBook.PlayerName(Game.Player.Character), scene = CharacterBook.SceneOf(ped, Game.Player.Character, _visits.PlaceAt(ped.Position));
            string who = CharacterName(ped);
            var ex = new Exchange { Who = who, Mode = openMic ? "open mic" : wav != null ? "mic" : "text", Heard = typed };
            if (!openMic) // the open mic only commits once it knows it heard words
            {
                _last = ex;
                _partner = ped;
                ped.Task.TurnTo(Game.Player.Character, 2000);
                ped.Task.LookAt(Game.Player.Character, 10000);
            }

            string status = openMic ? null : wav != null ? "Listening back..." : $"{who} is thinking...";
            Generate(ped, c, priority: true, status: status, exchange: ex, produce: async ct =>
            {
                var sw = Stopwatch.StartNew();
                string said = typed;
                if (said == null)
                {
                    said = await _stt.TranscribeAsync(wav, ct).ConfigureAwait(false);
                    ex.Stt = sw.Elapsed.TotalSeconds;
                    ex.Heard = said;
                }
                if (openMic)
                {
                    if (!LooksLikeSpeech(said)) throw new NotSpeechException(said);
                    _last = ex;
                    _partner = ped;
                }
                if (string.IsNullOrWhiteSpace(said)) throw new IOException("didn't catch that");
                _heard = said;
                _status = $"{who} is thinking...";

                sw.Restart();
                var (reply, order) = await Dialogue.ReplyAsync(_llm, _actionsOn ? _picker : null, _book.ConversationPrompt(info, playerName, said, scene),
                    new Dictionary<string, object>
                    {
                        ["npc"] = who,
                        ["npc_gender"] = info.Female ? "woman" : "man",
                        ["player"] = playerName,
                        ["player_said"] = said,
                    }, info.Female, playerName, said, c, ct, direction).ConfigureAwait(false);
                ex.Llm = sw.Elapsed.TotalSeconds;
                ex.Reply = reply;
                Log.Write($"talk {ex.Mode}{(wav != null ? $" {MicRecorder.Seconds(wav):F1}s stt {ex.Stt:F2}s" : "")} llm {ex.Llm:F2}s" +
                          $"{(order != null ? $" told to {order.Choice} ({order.Probability:P0}, {_picker.Name} {order.Seconds:F2}s)" : "")} | you: {said} | {who}: {reply}");
                _book.Remember(info.Handle, reply);
                if (order != null)
                    ex.ActionPick = Task.FromResult(order);
                else if (_actionsOn && _picker.IsConfigured)
                    ex.ActionPick = _picker.ChooseAsync(new Dictionary<string, object>
                    {
                        ["npc"] = who,
                        ["npc_gender"] = info.Female ? "woman" : "man",
                        ["npc_persona"] = Net.Truncate(c?.Persona ?? "A random Los Santos local going about their day.", 400),
                        ["player"] = playerName,
                        ["player_said"] = said,
                        ["npc_reply"] = Speech.Plain(reply),
                    }, ActionCatalog.Instructions, ActionCatalog.Criteria(), ct);
                return reply;
            });
        }

        private void Generate(Ped ped, Character c, bool priority, string status, Exchange exchange, Func<CancellationToken, Task<string>> produce)
        {
            _thinking = true;
            _status = status;
            Task.Run(async () =>
            {
                try
                {
                    string text = await produce(_shutdown.Token).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(text))
                        _pending = new PendingLine { Ped = ped, Text = text, Speaker = c?.Name, Priority = priority, Exchange = exchange };
                    else if (exchange != null) exchange.Error = "no reply (the model refused or returned nothing)";
                }
                catch (NotSpeechException ex)
                {
                    Log.Write("open mic ignored, " + ex.Message);
                }
                catch (Exception ex) when (!_shutdown.IsCancellationRequested)
                {
                    if (exchange != null) exchange.Error = ex.Message;
                    Log.Write("generate failed: " + ex);
                    _service.Message = "~r~" + ex.Message;
                }
                finally
                {
                    _thinking = false;
                    _status = null;
                }
            });
        }

        private void DispatchPending()
        {
            PendingLine p = _pending;
            if (p == null) return;
            if (!p.Ped.Exists() || p.Ped.IsDead)
            {
                _pending = null;
                return;
            }
            if (_service.IsBusy)
            {
                if (p.Priority && !_holdReplies) _service.Preempt();
                return;
            }
            _pending = null;
            Speak(p.Ped, p.Text, p.Speaker, p.Exchange);
        }

        private bool Speak(Ped ped, string text, string speakerName, Exchange ex)
        {
            Voice voice = VoiceLibrary.Pick(GroupFor(ped), ped.Handle, Speech.Mood(Speech.Tag(text)));
            if (voice == null)
            {
                if (ex != null) ex.Error = "no voice ready yet (clips still uploading?)";
                if (speakerName != null) Note($"NpcVoice: no voice ready for {speakerName} yet");
                return false;
            }
            if (!_service.TrySpeak(text, voice, seconds =>
                {
                    if (ex == null) return;
                    ex.Tts = seconds;
                    ex.Total = ex.Clock.Elapsed.TotalSeconds;
                    Log.Write($"timing {ex.Who}: stt {ex.Stt:F2}s llm {ex.Llm:F2}s tts {ex.Tts:F2}s total {ex.Total:F2}s");
                }))
                return false;
            if (ex != null) ex.Voice = voice.Name;
            if (ex?.ActionPick != null) _actions.Queue(ped, ex.ActionPick, ex, ped.Gender == Gender.Female);
            Log.Write($"speak [{voice.Name}] {speakerName ?? "npc"}: {text}");
            if (_clean) Mark($"speak	{(ped == Game.Player.Character ? "player" : "npc")}	{speakerName ?? CharacterName(ped)}	{text}");

            EndSpeaker();
            _speaker = ped;
            string line = LineBook.Subtitle(text);
            _subtitle = _subtitles && !_clean ? (speakerName != null ? $"~y~{speakerName}:~s~ {line}" : line) : null;
            _subtitleAt = Game.GameTime + EarlySubtitleMs;
            if (ped != Game.Player.Character) ped.Task.LookAt(Game.Player.Character, 6000);
            return true;
        }

        private VoiceGroup GroupFor(Ped ped)
        {
            Character c = _book.Find(ped.Model.Hash);
            return _voices.Find(c?.Voice) ?? _voices.Pool(ped.Gender == Gender.Female, ped.Handle);
        }

        private Voice AnyVoice() => (_voices.Find("Franklin") ?? _voices.Groups.FirstOrDefault())?.Pick(0);

        /// <summary>
        /// Fish S2 Pro (Modal or the fal app) pays ~8 s the first time it hears a reference clip. Say a
        /// word in every clip in the background, on separate engine instances, so no conversation does:
        /// each voice's first clip first, then the rest. The first request also wakes a cold GPU.
        /// </summary>
        private void Prewarm(Func<ITts> newEngine, int parallel)
        {
            // Only the clips voices actually speak with: each voice's own clip first, then the mood clips.
            var queue = new System.Collections.Concurrent.ConcurrentQueue<Voice>(_voices.Groups
                .Select(g => g.Pick(0))
                .Concat(_voices.Groups.SelectMany(g => g.Moods.Values).Select(m => m.Pick(0)))
                .Where(v => v != null));
            int total = queue.Count, done = 0;
            var sw = Stopwatch.StartNew();
            Task.Run(async () =>
            {
                await Task.WhenAll(Enumerable.Range(0, parallel).Select(_ => Task.Run(async () =>
                {
                    using (ITts warm = newEngine())
                        while (!_shutdown.IsCancellationRequested && queue.TryDequeue(out Voice v))
                        {
                            try { await warm.SpeakAsync("Yeah.", v, (pcm, rate) => { }, _shutdown.Token).ConfigureAwait(false); _service.MarkPrimaryReady(); }
                            catch (Exception ex) when (!_shutdown.IsCancellationRequested) { warm.Abort(); Log.Write($"prewarm {v.Name}: {ex.Message}"); }
                            if (Interlocked.Increment(ref done) == 1) _service.Message = $"{_service.Engine} voice engine is up ({sw.Elapsed.TotalSeconds:F0}s), preparing voices...";
                        }
                }))).ConfigureAwait(false);
                Log.Write($"prewarm {_service.Engine}: {done}/{total} clips in {sw.Elapsed.TotalSeconds:F0}s");
                if (!_shutdown.IsCancellationRequested) _service.Message = $"all {total} voice clips ready ({sw.Elapsed.TotalSeconds:F0}s)";
            });
        }

        private static string Situation(Ped ped)
        {
            Player player = Game.Player;
            if (ped.IsFleeing) return "fleeing";
            if (Function.Call<bool>(Hash.IS_PLAYER_FREE_AIMING_AT_ENTITY, player.Handle, ped)) return "threatened";
            if (ped.IsInCombat) return "combat";
            if (ped.Health < ped.MaxHealth * 0.6f) return "hurt";
            if (ped.IsInVehicle()) return "driving";
            if (ped.Position.DistanceTo(player.Character.Position) < 4f) return "greet";
            return "chatter";
        }

        // ---------------------------------------------------------------- speaker: audio position + face

        private void UpdateSpeaker(Ped player)
        {
            if (_speaker == null) return;
            if (!_speaker.Exists() || _speaker.IsDead || _speaker.Position.DistanceTo(player.Position) > _radius * 1.5f)
            {
                _service.Cancel();
                EndSpeaker();
                return;
            }
            if (!_service.IsBusy)
            {
                EndSpeaker();
                return;
            }

            Function.Call(Hash.STOP_CURRENT_PLAYING_AMBIENT_SPEECH, _speaker);

            // 3D-ish audio: fade with distance from the camera, pan by where the NPC is on screen.
            Vector3 toPed = _speaker.Position + new Vector3(0f, 0f, 0.6f) - GameplayCamera.Position;
            float dist = toPed.Length();
            _service.Player.Volume = _clean ? 1f : Clamp(1f - (dist - _fadeStart) / Math.Max(0.01f, _fadeEnd - _fadeStart), 0f, 1f);
            _service.Player.Pan = _clean ? 0f : Clamp(Vector3.Dot(toPed.Normalized, GameplayCamera.RightVector) * 0.8f, -1f, 1f);

            if (_subtitle != null && (_service.Player.IsAudible || Game.GameTime >= _subtitleAt))
            {
                GTA.UI.Screen.ShowSubtitle(_subtitle, Math.Max(3000, _subtitle.Length * 80));
                _subtitle = null;
            }

            UpdateMouth(_service.Player.Level);
        }

        /// <summary>Open the mouth while the audio is loud, close it in the gaps between words.</summary>
        private void UpdateMouth(float level)
        {
            int now = Game.GameTime;
            if (level >= MouthOpenLevel)
            {
                _lastLoudMs = now;
                if (!_mouthOpen || now >= _nextMouthRefresh)
                {
                    PlayFace(TalkDict, TalkAnim);
                    _mouthOpen = true;
                    _nextMouthRefresh = now + 1500;
                }
            }
            else if (_mouthOpen && now - _lastLoudMs > MouthCloseDelayMs)
            {
                PlayFace(IdleDict, IdleAnim);
                _mouthOpen = false;
            }
        }

        private void PlayFace(string dict, string anim)
        {
            if (!Function.Call<bool>(Hash.HAS_ANIM_DICT_LOADED, dict))
            {
                Function.Call(Hash.REQUEST_ANIM_DICT, dict);
                return;
            }
            Function.Call(Hash.PLAY_FACIAL_ANIM, _speaker, anim, dict);
        }

        private void EndSpeaker()
        {
            if (_speaker != null && _speaker.Exists() && _mouthOpen) PlayFace(IdleDict, IdleAnim);
            _speaker = null;
            _subtitle = null;
            _mouthOpen = false;
        }

        // ---------------------------------------------------------------- helpers

        /// <summary>
        /// Who B talks to: the last person you talked to while they're close, otherwise the best of the
        /// people around you, preferring who the camera points at, then named characters, then nearest.
        /// </summary>
        private Ped TalkTarget()
        {
            Ped player = Game.Player.Character;
            if (_partner != null && _partner.Exists() && !_partner.IsDead && _partner.Position.DistanceTo(player.Position) < 6f)
            {
                Vector3 toPartner = (_partner.Position - player.Position).Normalized;
                if (Vector3.Dot(toPartner, GameplayCamera.Direction) > 0.2f) return _partner;
            }

            Vector3 camForward = GameplayCamera.Direction;
            Ped best = null;
            float bestScore = float.MaxValue;
            foreach (Ped p in World.GetNearbyPeds(player, 12f))
            {
                if (p.Handle == player.Handle || !p.IsHuman || p.IsDead) continue;
                Vector3 to = p.Position - player.Position;
                float d = to.Length();
                float facing = Vector3.Dot(to.Normalized, camForward); // 1 = straight ahead
                if (d > 5f && facing < 0.85f) continue;                // further away only if you look at them
                float score = d * (2.2f - facing) - (_book.Find(p.Model.Hash) != null ? 2f : 0f);
                if (score < bestScore) { bestScore = score; best = p; }
            }
            return best;
        }

        private string CharacterName(Ped ped)
        {
            Character c = _book.Find(ped.Model.Hash);
            return c != null ? c.Name : ped.Gender == Gender.Female ? "her" : "him";
        }

        private string TargetLabel(Ped ped)
        {
            Character c = _book.Find(ped.Model.Hash);
            VoiceGroup g = GroupFor(ped);
            string name = c?.Name ?? (ped.Gender == Gender.Female ? "Pedestrian (woman)" : "Pedestrian (man)");
            return $"{name}~n~~c~voice: {g?.Name ?? "none"}   ~b~B~c~ type  ~b~N~c~ speak";
        }

        private string VoiceLabel(Character c)
        {
            VoiceGroup g = _voices.Find(c.Voice);
            if (g == null) return "~c~pool voice";
            return g.Moods.Count > 0 ? $"voice +{string.Join("+", g.Moods.Keys)}" : "voice";
        }

        private string Engines() => $"TTS {_service.Speaking}   LLM {_llm.Model}   STT {_stt.Name.Split('/').Last()}";

        private string Status()
        {
            if (!_enabled) return "~r~NpcVoice is off~s~: F7 turns it back on";
            if (_thinking) return _status ?? "thinking...";
            if (_service.IsBusy) return "speaking";
            return $"{_voices.Uploaded}/{_voices.ClipCount} voice clips uploaded";
        }

        private static Ped Nearest(Ped player, float radius)
        {
            Ped best = null;
            float bestDist = float.MaxValue;
            foreach (Ped p in World.GetNearbyPeds(player, radius))
            {
                if (p.Handle == player.Handle || !p.IsHuman || p.IsDead) continue;
                float d = p.Position.DistanceToSquared(player.Position);
                if (d < bestDist) { bestDist = d; best = p; }
            }
            return best;
        }

        private void Warn(string message)
        {
            Log.Write("warn: " + message);
            lock (_warnings) _warnings.Add(message);
        }

        private void ShowMessages()
        {
            lock (_warnings)
            {
                foreach (string w in _warnings) Note("~y~NpcVoice:~s~ " + w);
                _warnings.Clear();
            }
            string msg = _service.Message;
            if (msg != null)
            {
                _service.Message = null;
                Log.Write("status: " + msg);
                Note("NpcVoice: " + msg);
            }
        }

        private static string Key(string envName, string fallback) => EngineConfig.Key(envName, fallback);

        private Keys ReadKey(string name, Keys fallback) =>
            Enum.TryParse(Settings.GetValue("Keys", name, fallback.ToString()), true, out Keys k) ? k : fallback;

        private static List<string> List(string csv) =>
            (csv ?? "").Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).ToList();

        private static Vector3 ReadVector(string csv, Vector3 fallback)
        {
            var parts = List(csv);
            if (parts.Count == 3 && float.TryParse(parts[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float x)
                && float.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float y)
                && float.TryParse(parts[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float z))
                return new Vector3(x, y, z);
            return fallback;
        }

        private static float Clamp(float v, float min, float max) => v < min ? min : v > max ? max : v;
    }
}
