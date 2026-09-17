using System;
using HarmonyLib;
using MelonLoader;
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

        const string TitleName = "DnWVR_VRTitle";
        const string HeightRowName = "DnWVR_HeightRow";

        public static VRSettings Instance { get; private set; }

        static MelonLogger.Instance s_log;
        static AccessTools.FieldRef<ScriptableSettingSpawner, GameObject> s_groupTitle, s_textInput;
        static AccessTools.FieldRef<ScriptableSettingSpawner, bool> s_ready;

        ScriptableSettingSpawner _spawner;
        TMP_InputField _height;
        int _sweep;

        public static void Ensure(MelonLogger.Instance log)
        {
            s_log = log;
            if (Instance != null) return;
            try
            {
                s_groupTitle = AccessTools.FieldRefAccess<ScriptableSettingSpawner, GameObject>("groupTitle");
                s_textInput = AccessTools.FieldRefAccess<ScriptableSettingSpawner, GameObject>("textInput");
                s_ready = AccessTools.FieldRefAccess<ScriptableSettingSpawner, bool>("ready");
            }
            catch (Exception e)
            {
                log.Error("[VRSettings] the game's settings spawner looks different than expected: " + e);
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
            if (title.GetSiblingIndex() < content.childCount - 2)
            {
                title.SetAsLastSibling();
                var row = content.Find(HeightRowName);
                if (row != null) row.SetAsLastSibling();
            }
        }

        void Build(Transform content)
        {
            try
            {
                var titlePrefab = s_groupTitle(_spawner);
                var inputPrefab = s_textInput(_spawner);
                if (titlePrefab == null || inputPrefab == null) return;

                var title = Instantiate(titlePrefab, content);
                title.name = TitleName;
                title.SetActive(true);
                SetLabel(title, "VR");

                var row = Instantiate(inputPrefab, content);
                row.name = HeightRowName;
                row.SetActive(true);
                BuildHeightRow(row);
                s_log?.Msg("[VRSettings] VR section added to the options screen");
            }
            catch (Exception e)
            {
                s_log?.Error("[VRSettings] could not build the VR section: " + e);
                Instance = null;
                Destroy(gameObject);
            }
        }

        /// <summary>Your height in centimetres, with a stepper either side of it and a button that measures it.</summary>
        void BuildHeightRow(GameObject row)
        {
            SetLabel(row, "Height (cm)");
            var label = row.transform.Find("Label") as RectTransform;
            var field = row.transform.Find("TextInput") as RectTransform;
            if (label != null) Place(label, 0f, 0.44f);
            if (field == null) return;
            Place(field, 0.50f, 0.62f);

            _height = field.GetComponent<TMP_InputField>();
            var style = field.GetComponent<Image>();
            if (_height != null)
            {
                _height.contentType = TMP_InputField.ContentType.IntegerNumber;
                _height.characterLimit = 3;
                if (_height.textComponent != null) _height.textComponent.alignment = TextAlignmentOptions.Center;
                _height.onEndEdit.AddListener(text => SetHeight(int.TryParse(text, out int cm) ? cm : VRRig.HeightCm));
            }

            var rect = (RectTransform)row.transform;
            MakeButton(rect, "Down", "<", style, 0.44f, 0.50f).onClick.AddListener(() => SetHeight(VRRig.HeightCm - 1));
            MakeButton(rect, "Up", ">", style, 0.62f, 0.68f).onClick.AddListener(() => SetHeight(VRRig.HeightCm + 1));
            MakeButton(rect, "Calibrate", "Calibrate", style, 0.72f, 1f).onClick.AddListener(Calibrate);
            ShowHeight();
        }

        /// <summary>Takes the height straight off the headset, which the floor-level tracking origin measures for us.</summary>
        void Calibrate()
        {
            if (!VRRig.HasPose || !VRRig.HmdTracked)
            {
                s_log?.Warning("[VRSettings] no headset pose to calibrate against");
                return;
            }
            SetHeight(Mathf.RoundToInt(VRRig.HmdLocalPos.y * 100f));
            s_log?.Msg($"[VRSettings] calibrated to {VRRig.HeightCm} cm from the headset");
        }

        void SetHeight(int cm)
        {
            cm = Mathf.Clamp(cm, MinHeightCm, MaxHeightCm);
            VRRig.HeightCm = cm;
            if (DnWVRMod.PrefPlayerHeightCm != null)
            {
                DnWVRMod.PrefPlayerHeightCm.Value = cm;
                try { MelonPreferences.Save(); }
                catch (Exception e) { s_log?.Warning("[VRSettings] could not save the height: " + e.Message); }
            }
            ShowHeight();
        }

        void ShowHeight()
        {
            if (_height != null) _height.SetTextWithoutNotify(VRRig.HeightCm.ToString());
        }

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
