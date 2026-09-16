using System;
using HarmonyLib;
using MelonLoader;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace DnWVR.VR
{
    /// <summary>
    /// Debug scene picker (pref DebugSceneMenu): left Y opens a panel in front of the head, and the laser picks a scene
    /// that the game then loads through its own menu intents, with its loading screen and save, as the story does, or a
    /// day to start over. Sex scene intents only work in a loaded game (the main menu ignores them). A scene the story
    /// starts is preceded by a save of the new day, so the game's return to that save afterwards loses nothing; started
    /// from here, in the middle of a day, the same return lands on the day's beginning, and the scene counts as watched
    /// in that save - which the panel says in as many words.
    /// </summary>
    public class DebugSceneMenu : MonoBehaviour
    {
        const float Distance = 1.2f;
        const float WidthMeters = 0.8f;
        const float WidthPixels = 800f;
        const float RowPixels = 84f;
        const float GapPixels = 12f;
        const float HeaderPixels = 110f;
        const float WarningPixels = 90f;
        const float CaptionPixels = 46f;
        const float FooterPixels = 90f;

        struct Entry
        {
            public readonly string Label, Intent;
            public Entry(string label, string intent) { Label = label; Intent = intent; }
        }

        // Menu intents: the sex scenes are handled by every in-game menu (Menu.OnEvent), Play by the unpaused game menu,
        // Continue by the main menu.
        static readonly Entry[] Entries =
        {
            new Entry("Ryan sex scene", "RyanSexScene"),
            new Entry("Alexander sex scene (ends in the credits)", "AlexanderSexScene"),
            new Entry("Conrad sex scene", "ConradSexScene"),
            new Entry("Ryan + Conrad (watch)", "ConradRyanSexScene"),
            new Entry("Washing: reload the save", "Play"),
            new Entry("Main menu: continue the last save", "Continue"),
        };

        public static DebugSceneMenu Instance { get; private set; }
        /// <summary>The panel is open (the laser pointer shows while it is).</summary>
        public static bool IsOpen => Instance != null && Instance._open;

        static MelonLogger.Instance s_log;
        static AccessTools.FieldRef<WalkNWashSceneState> s_sceneState;
        static AccessTools.FieldRef<WalkNWashSceneState, LevelFlow> s_levelFlow;
        static AccessTools.FieldRef<WalkNWashSceneState, WalkNWashSceneState.DragonState> s_dragonState;
        static Action<WalkNWashSceneState> s_startLevel;

        Canvas _canvas;
        RectTransform _root;
        RectTransform _buttons;
        TextMeshProUGUI _status;
        bool _open;
        bool _wasButton;

        public static void Ensure(MelonLogger.Instance log)
        {
            s_log = log;
            if (Instance != null) return;
            try
            {
                s_sceneState = AccessTools.StaticFieldRefAccess<WalkNWashSceneState>(AccessTools.Field(typeof(WalkNWashSceneState), "instance"));
                s_levelFlow = AccessTools.FieldRefAccess<WalkNWashSceneState, LevelFlow>("levelFlow");
                s_dragonState = AccessTools.FieldRefAccess<WalkNWashSceneState, WalkNWashSceneState.DragonState>("dragonState");
                s_startLevel = AccessTools.MethodDelegate<Action<WalkNWashSceneState>>(AccessTools.Method(typeof(WalkNWashSceneState), "StartCurrentLevel"));
            }
            catch (Exception e)
            {
                s_sceneState = null;
                log.Warning("[DebugSceneMenu] the level flow is not reachable; the day buttons stay out: " + e.Message);
            }
            var go = new GameObject("DnWVR_DebugSceneMenu");
            DontDestroyOnLoad(go);
            Instance = go.AddComponent<DebugSceneMenu>();
        }

        void Awake()
        {
            BuildUI();
            _canvas.gameObject.SetActive(false);
        }

        // The panel itself; its buttons are built each time it opens, because the days come from the loaded game.
        void BuildUI()
        {
            var go = new GameObject("DnWVR_DebugScenes", typeof(RectTransform));
            go.transform.SetParent(transform, false);
            go.layer = LayerMask.NameToLayer("UI");
            _canvas = go.AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.WorldSpace;
            // Above every game panel, so the laser's clicks land here first.
            _canvas.sortingOrder = 32000;
            go.AddComponent<GraphicRaycaster>();
            _root = (RectTransform)go.transform;
            _root.localScale = Vector3.one * (WidthMeters / WidthPixels);

            var background = VRWidgets.MakeImage("Background", _root, new Color(0.05f, 0.06f, 0.09f, 0.92f)).rectTransform;
            background.anchorMin = Vector2.zero;
            background.anchorMax = Vector2.one;
            background.offsetMin = Vector2.zero;
            background.offsetMax = Vector2.zero;

            var buttons = new GameObject("Buttons", typeof(RectTransform));
            buttons.transform.SetParent(_root, false);
            buttons.layer = _root.gameObject.layer;
            _buttons = (RectTransform)buttons.transform;
            _buttons.anchorMin = Vector2.zero;
            _buttons.anchorMax = Vector2.one;
            _buttons.offsetMin = Vector2.zero;
            _buttons.offsetMax = Vector2.zero;

            Populate();
        }

        // Rebuilds the rows and sizes the panel to them. Days are only offered while a game is loaded.
        void Populate()
        {
            // Out of the panel first: Destroy only takes effect at the end of the frame, and a row left in place
            // would be drawn over the one replacing it.
            for (int i = _buttons.childCount - 1; i >= 0; i--)
            {
                var old = _buttons.GetChild(i);
                old.SetParent(null, false);
                Destroy(old.gameObject);
            }

            int days = DayCount();
            float dayBlock = days > 0 ? CaptionPixels + RowPixels + GapPixels : 0f;
            float height = HeaderPixels + WarningPixels + (Entries.Length + 1) * (RowPixels + GapPixels) + dayBlock + FooterPixels;
            _root.sizeDelta = new Vector2(WidthPixels, height);

            var title = VRWidgets.MakeText("Title", _buttons, 42f);
            title.text = "DnWVR debug: load a scene";
            Place(title.rectTransform, height * 0.5f - HeaderPixels * 0.5f, HeaderPixels);

            // The game saves a day before it starts a scene of its own, so it returns the player to that save afterwards.
            // Started from here, mid-day, the same return lands on the day's beginning - and the scene is marked watched.
            var warning = VRWidgets.MakeText("Warning", _buttons, 26f);
            warning.text = "Sends you back to the last save (the start of this day) and counts as watched in it";
            warning.color = new Color(1f, 0.72f, 0.45f);
            Place(warning.rectTransform, height * 0.5f - HeaderPixels - WarningPixels * 0.5f, WarningPixels);

            float y = height * 0.5f - HeaderPixels - WarningPixels - RowPixels * 0.5f;
            foreach (var entry in Entries)
            {
                var e = entry;
                MakeButton(e.Label, y, () => Load(e));
                y -= RowPixels + GapPixels;
            }

            if (days > 0)
            {
                var caption = VRWidgets.MakeText("Days", _buttons, 26f);
                caption.text = "Start a day over (the days before it count as done, nothing is cleared)";
                caption.color = new Color(0.75f, 0.82f, 1f);
                Place(caption.rectTransform, y + RowPixels * 0.5f - CaptionPixels * 0.5f, CaptionPixels);
                y -= CaptionPixels;

                float room = WidthPixels - 80f;
                float width = (room - GapPixels * (days - 1)) / days;
                float left = -room * 0.5f + width * 0.5f;
                for (int day = 0; day < days; day++)
                {
                    int d = day;
                    MakeButton((day + 1).ToString(), y, () => Jump(d), width, left + day * (width + GapPixels), 30f);
                }
                y -= RowPixels + GapPixels;
            }

            MakeButton("Close", y, Close);

            _status = VRWidgets.MakeText("Status", _buttons, 28f);
            _status.color = new Color(1f, 0.85f, 0.45f);
            Place(_status.rectTransform, -height * 0.5f + FooterPixels * 0.5f, FooterPixels);
        }

        static int DayCount()
        {
            if (s_sceneState == null) return 0;
            try
            {
                var state = s_sceneState();
                var flow = state != null ? s_levelFlow(state) : null;
                return flow != null ? flow.GetLevelCount() : 0;
            }
            catch
            {
                return 0;
            }
        }

        void MakeButton(string label, float y, UnityAction onClick, float width = WidthPixels - 80f, float x = 0f, float fontSize = 32f)
        {
            var image = VRWidgets.MakeImage("Button " + label, _buttons, Color.white);
            Place(image.rectTransform, y, RowPixels, width, x);
            var button = image.gameObject.AddComponent<Button>();
            button.targetGraphic = image;
            button.navigation = new Navigation { mode = Navigation.Mode.None };
            var colors = button.colors;
            colors.normalColor = new Color(0.18f, 0.21f, 0.3f, 1f);
            colors.highlightedColor = new Color(0.32f, 0.4f, 0.58f, 1f);
            colors.pressedColor = new Color(0.85f, 0.65f, 0.2f, 1f);
            colors.selectedColor = colors.normalColor;
            colors.fadeDuration = 0.05f;
            button.colors = colors;
            button.onClick.AddListener(onClick);
            var text = VRWidgets.MakeText("Label", image.rectTransform, fontSize);
            var rect = text.rectTransform;
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = new Vector2(16f, 0f);
            rect.offsetMax = new Vector2(-16f, 0f);
            text.text = label;
        }

        static void Place(RectTransform rect, float y, float height, float width = WidthPixels - 60f, float x = 0f)
        {
            rect.sizeDelta = new Vector2(width, height);
            rect.anchoredPosition = new Vector2(x, y);
        }

        void Update()
        {
            // With the radial tool menu on, Y stays its button.
            bool usable = VRRig.Active && DnWVRMod.PrefDebugSceneMenu.Value && !DnWVRMod.PrefDebugRadialMenu.Value;
            bool button = usable && VRInput.Left.Valid && VRInput.Left.Secondary;
            if (!usable && _open) Close();
            if (button && !_wasButton)
            {
                if (_open) Close();
                else Open();
            }
            _wasButton = button;
        }

        void Open()
        {
            _open = true;
            Populate();
            _status.text = "Laser + trigger picks. Y closes.";
            _canvas.gameObject.SetActive(true);
            VRUI.AddPanel(_canvas);
            VRUI.PlaceInFrontOfHead(_root, Distance);
        }

        void Close()
        {
            _open = false;
            _canvas.gameObject.SetActive(false);
        }

        void Load(Entry entry)
        {
            string menu = MenuManager.GetCurrentMenuName();
            try
            {
                MenuManager.TriggerEvent(new MenuEventUserIntent(entry.Intent));
            }
            catch (Exception e)
            {
                _status.text = "Failed: " + e.Message;
                s_log?.Warning($"[DebugSceneMenu] {entry.Intent} failed in {menu}: {e.Message}");
                return;
            }
            if (MenuManager.GetCurrentMenuName() == menu)
            {
                _status.text = $"{menu} ignores this: sex scenes need a loaded game, Continue needs the main menu";
                s_log?.Msg($"[DebugSceneMenu] {entry.Intent} ignored by {menu}");
                return;
            }
            s_log?.Msg($"[DebugSceneMenu] {entry.Label}: {entry.Intent} from {menu}");
            Close();
        }

        // Starts a day the way the game starts one: the days before it hand over their end flags (that is what the story
        // reads later), the level is set, and the game spawns its dragon and walks it in. Flags a later day had already
        // set are left alone, so jumping back keeps them - it is a debug jump, not a rewind.
        //
        // The dragon's state machine only accepts one step at a time (SetDragonState refuses anything else and says so in
        // the log), and a day starts by asking for WaitingToAppear - the step after Exited. Mid-day the machine sits
        // somewhere in the middle, the ask is refused, and the day's dialogue and walk-in never happen. So the machine is
        // put on Exited first, straight into the field, without the event that would end the day and advance the level.
        void Jump(int day)
        {
            try
            {
                var state = s_sceneState();
                var flow = state != null ? s_levelFlow(state) : null;
                if (flow == null) { _status.text = "No loaded game to jump in"; return; }
                for (int i = 0; i < day; i++)
                    foreach (var flag in flow.GetEndFlags(i))
                        WalkNWashSceneState.SetFlag(flag, value: true);
                WalkNWashSceneState.SetLevel(day);
                if (s_dragonState != null) s_dragonState(state) = WalkNWashSceneState.DragonState.Exited;
                VRFader.Flash(1.2f);
                s_startLevel(state);
                s_log?.Msg($"[DebugSceneMenu] day {day + 1} started, {day} day(s) before it marked done");
            }
            catch (Exception e)
            {
                _status.text = "Failed: " + e.Message;
                s_log?.Warning($"[DebugSceneMenu] day {day + 1} failed: {e}");
                return;
            }
            Close();
        }
    }
}
