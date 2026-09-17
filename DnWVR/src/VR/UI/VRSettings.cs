using System;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using UnityScriptableSettings;

namespace DnWVR.VR
{
    /// <summary>
    /// The mod's own "VR" section at the bottom of the game's options screen. The game spawns its rows from scriptable
    /// objects and rebuilds them every time the screen opens, so this waits for that to finish and then adds a title and
    /// the mod's own rows, built from the very prefabs the game used, after them.
    /// </summary>
    public class VRSettings : MonoBehaviour
    {
        /// <summary>Range the height row allows, in centimetres.</summary>
        public const int MinHeightCm = 120, MaxHeightCm = 220;

        const float MinSnap = 5f, MaxSnap = 90f, SnapStep = 5f;
        const float MinTurnSpeed = 30f, MaxTurnSpeed = 360f, TurnSpeedStep = 10f;

        const string TitleName = "DnWVR_VRTitle";
        const string HeightRowName = "DnWVR_HeightRow";
        const string TurnRowName = "DnWVR_TurnRow";
        const string TurnAmountRowName = "DnWVR_TurnAmountRow";

        public static VRSettings Instance { get; private set; }

        static AccessTools.FieldRef<ScriptableSettingSpawner, GameObject> s_groupTitle, s_textInput;
        static AccessTools.FieldRef<ScriptableSettingSpawner, bool> s_ready;

        ScriptableSettingSpawner _spawner;
        TMP_InputField _height, _turnMode, _turnAmount;
        TextMeshProUGUI _turnAmountLabel;
        int _sweep;

        public static void Ensure()
        {
            if (Instance != null) return;
            try
            {
                s_groupTitle = AccessTools.FieldRefAccess<ScriptableSettingSpawner, GameObject>("groupTitle");
                s_textInput = AccessTools.FieldRefAccess<ScriptableSettingSpawner, GameObject>("textInput");
                s_ready = AccessTools.FieldRefAccess<ScriptableSettingSpawner, bool>("ready");
            }
            catch (Exception e)
            {
                Log.Error("[VRSettings] the game's settings spawner looks different than expected: " + e);
                return;
            }
            var go = new GameObject("DnWVR_VRSettings");
            DontDestroyOnLoad(go);
            Instance = go.AddComponent<VRSettings>();
        }

        void Update()
        {
            // The options screen only exists once a menu scene has built it, and it is cheap to look for now and then.
            if (_spawner == null)
            {
                if (++_sweep % 30 != 0) return;
                _spawner = FindFirstObjectByType<ScriptableSettingSpawner>(FindObjectsInactive.Include);
                if (_spawner == null) return;
            }
            var content = _spawner.transform;
            if (!content.gameObject.activeInHierarchy || !s_ready(_spawner)) return;

            var title = content.Find(TitleName);
            if (title == null)
            {
                Build(content);
                return;
            }
            // The game respawns its own rows when the language changes; ours belong under them either way.
            if (title.GetSiblingIndex() < content.childCount - 4)
            {
                title.SetAsLastSibling();
                foreach (var name in new[] { HeightRowName, TurnRowName, TurnAmountRowName })
                {
                    var row = content.Find(name);
                    if (row != null) row.SetAsLastSibling();
                }
            }
        }

        void Build(Transform content)
        {
            try
            {
                var titlePrefab = s_groupTitle(_spawner);
                if (titlePrefab == null || s_textInput(_spawner) == null) return;

                var title = Instantiate(titlePrefab, content);
                title.name = TitleName;
                title.SetActive(true);
                SetLabel(title, "VR");

                BuildHeightRow(NewRow(content, HeightRowName, "Height (cm)"));
                BuildTurnRow(NewRow(content, TurnRowName, "Turning"));
                BuildTurnAmountRow(NewRow(content, TurnAmountRowName, string.Empty));
                Refresh();
                Log.Msg("[VRSettings] VR section added to the options screen");
            }
            catch (Exception e)
            {
                Log.Error("[VRSettings] could not build the VR section: " + e);
                Instance = null;
                Destroy(gameObject);
            }
        }

        /// <summary>A copy of the game's own text-input row, its label set and its field parked in the value column.</summary>
        GameObject NewRow(Transform content, string name, string label)
        {
            var row = Instantiate(s_textInput(_spawner), content);
            row.name = name;
            row.SetActive(true);
            SetLabel(row, label);
            if (row.transform.Find("Label") is RectTransform text) Place(text, 0f, 0.44f);
            if (row.transform.Find("TextInput") is RectTransform field) Place(field, 0.50f, 0.62f);
            return row;
        }

        /// <summary>Your height in centimetres, with a stepper either side of it and a button that measures it.</summary>
        void BuildHeightRow(GameObject row)
        {
            _height = Number(row, 3, text => SetHeight(int.TryParse(text, out int cm) ? cm : VRRig.HeightCm));
            Stepper(row, () => SetHeight(VRRig.HeightCm - 1), () => SetHeight(VRRig.HeightCm + 1));
            MakeButton((RectTransform)row.transform, "Calibrate", "Calibrate", Style(row), 0.72f, 1f)
                .onClick.AddListener(Calibrate);
        }

        /// <summary>Snap turning or smooth; the row under it follows whichever is chosen.</summary>
        void BuildTurnRow(GameObject row)
        {
            _turnMode = Number(row, 0, null);
            if (_turnMode != null)
            {
                _turnMode.readOnly = true;
                _turnMode.interactable = false;
            }
            Stepper(row, () => SetSmoothTurn(!VRInput.SmoothTurn), () => SetSmoothTurn(!VRInput.SmoothTurn));
        }

        /// <summary>Degrees per snap, or degrees a second while the stick is held - whichever the row above asks for.</summary>
        void BuildTurnAmountRow(GameObject row)
        {
            _turnAmountLabel = row.transform.Find("Label")?.GetComponent<TextMeshProUGUI>();
            _turnAmount = Number(row, 3, text => SetTurnAmount(float.TryParse(text, out float v) ? v : TurnAmount()));
            Stepper(row, () => SetTurnAmount(TurnAmount() - TurnStep()), () => SetTurnAmount(TurnAmount() + TurnStep()));
        }

        /// <summary>Takes the height straight off the headset, which the floor-level tracking origin measures for us.</summary>
        void Calibrate()
        {
            if (!VRRig.HasPose || !VRRig.HmdTracked)
            {
                Log.Warning("[VRSettings] no headset pose to calibrate against");
                return;
            }
            SetHeight(Mathf.RoundToInt(VRRig.HmdLocalPos.y * 100f));
            Log.Msg($"[VRSettings] calibrated to {VRRig.HeightCm} cm from the headset");
        }

        void SetHeight(int cm)
        {
            VRRig.HeightCm = Mathf.Clamp(cm, MinHeightCm, MaxHeightCm);
            Save(Prefs.PlayerHeightCm, VRRig.HeightCm);
            Refresh();
        }

        void SetSmoothTurn(bool smooth)
        {
            VRInput.SmoothTurn = smooth;
            Save(Prefs.SmoothTurn, smooth);
            Refresh();
        }

        void SetTurnAmount(float value)
        {
            if (VRInput.SmoothTurn)
            {
                VRInput.SmoothTurnDegPerSec = Mathf.Clamp(value, MinTurnSpeed, MaxTurnSpeed);
                Save(Prefs.SmoothTurnSpeed, VRInput.SmoothTurnDegPerSec);
            }
            else
            {
                VRInput.SnapTurnDegrees = Mathf.Clamp(value, MinSnap, MaxSnap);
                Save(Prefs.SnapTurnDegrees, VRInput.SnapTurnDegrees);
            }
            Refresh();
        }

        static float TurnAmount() => VRInput.SmoothTurn ? VRInput.SmoothTurnDegPerSec : VRInput.SnapTurnDegrees;

        static float TurnStep() => VRInput.SmoothTurn ? TurnSpeedStep : SnapStep;

        /// <summary>Writes every row from the settings themselves, so the cfg and the screen can never disagree.</summary>
        void Refresh()
        {
            Show(_height, VRRig.HeightCm.ToString());
            Show(_turnMode, VRInput.SmoothTurn ? "Smooth" : "Snap");
            Show(_turnAmount, Mathf.RoundToInt(TurnAmount()).ToString());
            if (_turnAmountLabel != null)
                _turnAmountLabel.text = VRInput.SmoothTurn ? "Turn speed (°/s)" : "Snap angle (°)";
        }

        static void Show(TMP_InputField field, string text)
        {
            if (field != null) field.SetTextWithoutNotify(text);
        }

        static void Save<T>(Pref<T> entry, T value)
        {
            if (entry == null) return;
            entry.Value = value;
            try { Prefs.Save(); }
            catch (Exception e) { Log.Warning("[VRSettings] could not save a setting: " + e.Message); }
        }

        /// <summary>The row's value field: digits only when it takes typing, a plain display when it does not.</summary>
        static TMP_InputField Number(GameObject row, int digits, UnityEngine.Events.UnityAction<string> edited)
        {
            var field = row.transform.Find("TextInput")?.GetComponent<TMP_InputField>();
            if (field == null) return null;
            // A row the player types into takes digits only; one that merely shows a word must not be validated.
            if (digits > 0)
            {
                field.contentType = TMP_InputField.ContentType.IntegerNumber;
                field.characterLimit = digits;
            }
            if (field.textComponent != null) field.textComponent.alignment = TextAlignmentOptions.Center;
            if (edited != null) field.onEndEdit.AddListener(edited);
            return field;
        }

        /// <summary>The arrows either side of a row's value: left takes it down, right up.</summary>
        void Stepper(GameObject row, UnityEngine.Events.UnityAction down, UnityEngine.Events.UnityAction up)
        {
            var rect = (RectTransform)row.transform;
            var style = Style(row);
            MakeButton(rect, "Down", "<", style, 0.44f, 0.50f).onClick.AddListener(down);
            MakeButton(rect, "Up", ">", style, 0.62f, 0.68f).onClick.AddListener(up);
        }

        static Image Style(GameObject row) => row.transform.Find("TextInput")?.GetComponent<Image>();

        Button MakeButton(RectTransform row, string name, string caption, Image style, float left, float right)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(row, false);
            go.layer = row.gameObject.layer;
            var image = go.AddComponent<Image>();
            if (style != null)
            {
                image.sprite = style.sprite;
                image.color = style.color;
                image.type = style.type;
                image.pixelsPerUnitMultiplier = style.pixelsPerUnitMultiplier;
            }
            Place((RectTransform)go.transform, left, right);

            var text = VRWidgets.MakeText(name + "Label", go.transform, 20f);
            var rect = text.rectTransform;
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = rect.offsetMax = Vector2.zero;
            text.text = caption;
            text.enableAutoSizing = true;
            text.fontSizeMin = 10f;
            return go.AddComponent<Button>();
        }

        /// <summary>Stretches a row's child over a horizontal slice of it, with a small gap either side.</summary>
        static void Place(RectTransform rect, float left, float right)
        {
            rect.anchorMin = new Vector2(left, 0f);
            rect.anchorMax = new Vector2(right, 1f);
            rect.offsetMin = new Vector2(2f, 0f);
            rect.offsetMax = new Vector2(-2f, 0f);
        }

        /// <summary>
        /// Writes a row's caption. The game's own rows fill theirs from the localisation tables, which would overwrite
        /// anything the mod puts there, so that component goes.
        /// </summary>
        static void SetLabel(GameObject row, string caption)
        {
            foreach (var behaviour in row.GetComponentsInChildren<MonoBehaviour>(true))
                if (behaviour != null && behaviour.GetType().Name == "LocalizeStringEvent")
                    Destroy(behaviour);
            var label = row.transform.Find("Label");
            var text = label != null ? label.GetComponent<TextMeshProUGUI>() : row.GetComponentInChildren<TextMeshProUGUI>(true);
            if (text != null) text.text = caption;
        }
    }
}
