using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace NpcVoice
{
    /// <summary>
    /// The physical actions NPCs can do while they talk, as the action picker (Jev or an open LLM) sees them:
    /// name, what it means, and how sure the picker must be before it happens. No game code here, so the check
    /// tool can ask the same question (npcvoice-check actions); NpcActions plays them in game.
    /// </summary>
    internal static class ActionCatalog
    {
        internal sealed class Kind
        {
            public string Name;
            public string Description;      // what the picker reads
            public double MinProbability;   // the picker must be at least this sure
            public bool MenOnly;
            public string Doing;            // for the LLM when told to do it: "you're <Doing> right now"
        }

        public static readonly Kind[] All =
        {
            K("none", "No physical action, they just talk. Normal conversation, jokes, arguing, or threats that stay words.", 0, null),
            K("punch", "Throws punches at the player and starts a fistfight: the reply says or clearly means they're about to beat the player up.", 0.45, "throwing punches at them"),
            K("slap", "Slaps the player across the face: insulted, disrespected or offended, putting the player in their place without a full fight.", 0.45, "slapping them across the face"),
            K("headbutt", "Headbutts the player: sudden close-range rage.", 0.5, "headbutting them"),
            K("shoot", "Pulls a gun and shoots the player: only when the reply clearly means killing or shooting them right now.", 0.6, "pulling your gun and shooting at them"),
            K("threaten_gun", "Pulls a gun and points it at the player without firing: threats, a robbery, 'don't move', 'say that again'.", 0.45, "pulling out your gun and pointing it at them"),
            K("middle_finger", "Flips the player off with the middle finger: dismissive contempt, 'fuck you', 'get lost'.", 0.7, "flipping them the bird"),
            K("facepalm", "Facepalms: exasperated or embarrassed by something stupid the player said.", 0.45, "facepalming"),
            K("square_up", "Raises their fists and squares up, shadow-boxing: ready to fight but no punch thrown yet.", 0.45, "putting your fists up, ready to fight"),
            K("shirt_off", "Pulls their shirt off before a street fight, like a man getting ready to throw down. Men only.", 0.5, "pulling your shirt off", menOnly: true),
            K("pants_off", "Drops their pants down to their underwear: a crude joke, mooning, showing off or taking a dare.", 0.5, "dropping your pants down to your underwear"),
            K("bend_over", "Turns around and bends over until their hands touch their shoes: crude mockery, 'kiss my ass', teasing.", 0.5, "turning around and bending over to touch your shoes"),
            K("hug", "Hugs the player: warm, friendly, a reunion, comfort or making up.", 0.45, "hugging them"),
            K("kiss", "Kisses the player: flirting, romance, seduction.", 0.45, "kissing them"),
            K("kneel_beg", "Drops to their knees and begs for mercy: terrified, pleading for their life.", 0.5, "dropping to your knees and begging"),
            K("run_away", "Runs away from the player in fear. Rare: only when they're panicking and fleeing.", 0.6, "running off"),
        };

        public const string Instructions =
            "In a GTA V scene, `npc` has just said `npc_reply` out loud to `player`, right after `player` said `player_said`. " +
            "Which one physical action does `npc` do toward `player` while saying it? " +
            "If `player_said` tells or asks `npc` to do one of these actions (\"take your shirt off\", \"get on your knees\", \"give me a hug\", " +
            "\"slap me\", \"pull your gun out\", \"run\"), `npc` does exactly that, even if `npc_reply` complains about it. " +
            "Otherwise pick what their words and mood most clearly call for, the way a GTA character would act. Pick none when it's just talk.";

        /// <summary>Asked about the player's words alone, before the reply: is this an order?</summary>
        public const string OrderInstructions =
            "In a GTA V scene, `player` just said `player_said` to `npc`. Is `player` telling or asking `npc` to physically do one of these " +
            "actions right now, e.g. \"take your shirt off\", \"get on your knees\", \"give me a hug\", \"slap me\", \"kiss me\", \"show me your gun\", " +
            "\"drop your pants\", \"bend over\", \"run\"? Pick that action. Pick none when it isn't a request for one of them: small talk, questions, " +
            "insults, threats, or talk about something else.";

        private const double OrderMinProbability = 0.6;

        public static IDictionary<string, string> Criteria() => All.ToDictionary(k => k.Name, k => k.Description);

        private static Kind K(string name, string description, double minProbability, string doing, bool menOnly = false) =>
            new Kind { Name = name, Description = description, MinProbability = minProbability, MenOnly = menOnly, Doing = doing };

        public static Kind Find(string name) => All.FirstOrDefault(k => k.Name == name);

        /// <summary>Whether the mod acts on the pick for this ped.</summary>
        public static bool Acts(JevChoice pick, bool female, double minProbability = 0)
        {
            Kind kind = pick != null ? Find(pick.Choice) : null;
            return kind != null && kind.Name != "none" && !(kind.MenOnly && female)
                && pick.Probability >= System.Math.Max(kind.MinProbability, minProbability);
        }

        /// <summary>The action the player just told the npc to do, or null (with the pick, for the action queue).</summary>
        public static async Task<JevChoice> OrderAsync(IActionPicker picker, IDictionary<string, object> state, bool female, CancellationToken ct)
        {
            JevChoice pick = await picker.ChooseAsync(state, OrderInstructions, Criteria(), ct).ConfigureAwait(false);
            if (!Acts(pick, female, OrderMinProbability)) return null;
            pick.Ordered = true;
            return pick;
        }
    }
}
