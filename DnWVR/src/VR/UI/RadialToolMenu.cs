using System;
using System.Collections.Generic;
using System.Reflection;
using com.gatordragongames.washnwalk.tools;
using HarmonyLib;
using MelonLoader;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR;

namespace DnWVR.VR
{
    /// <summary>
    /// Radial quick-equip menu (hold Y on the left controller, pick with the left stick, release) for the level's tool
    /// stations. Equips through the station's own Interact() so flags, performances and the placed model stay consistent.
    /// </summary>
    public class RadialToolMenu : MonoBehaviour
    {
        const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        public static RadialToolMenu Instance { get; private set; }
        public static float Distance = 0.45f;
        public static float SizeMeters = 0.34f;

        class Item
        {
            public string Label;
            public InteractableToolEquip Station;
            public bool PutAway;
            public TextMeshProUGUI Text;
            public RectTransform Rect;
        }

        static AccessTools.FieldRef<InteractableToolEquip, Tool> s_stationTool;
        static Func<Interactable, Tool, bool> s_canInteract;
        static MelonLogger.Instance s_log;

        Canvas _canvas;
        RectTransform _root;
        Image _background;
        TextMeshProUGUI _center;
        readonly List<Item> _items = new List<Item>();
        int _selected = -1;
        bool _open;
        bool _wasButton;

        public static void Ensure(MelonLogger.Instance log)
        {
            s_log = log;
            if (Instance != null) return;
            try
            {
                s_stationTool = AccessTools.FieldRefAccess<InteractableToolEquip, Tool>("tool");
                s_canInteract = AccessTools.MethodDelegate<Func<Interactable, Tool, bool>>(AccessTools.Method(typeof(Interactable), "CanInteract"), null, true);
            }
            catch (Exception e)
            {
                log.Error("[RadialToolMenu] reflection failed: " + e);
                return;
            }
            var go = new GameObject("DnWVR_RadialToolMenu");
            DontDestroyOnLoad(go);
            Instance = go.AddComponent<RadialToolMenu>();
        }

        void Awake()
        {
            BuildUI();
            _canvas.gameObject.SetActive(false);
        }

        void BuildUI()
        {
            var cgo = new GameObject("Canvas", typeof(RectTransform));
            cgo.transform.SetParent(transform, false);
            cgo.layer = LayerMask.NameToLayer("UI");
            _canvas = cgo.AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.WorldSpace;
            _canvas.sortingOrder = 200;
            _root = (RectTransform)cgo.transform;
            _root.sizeDelta = new Vector2(600f, 600f);
            _root.localScale = Vector3.one * (SizeMeters / 600f);

            _background = VRWidgets.MakeImage("Background", _root, new Color(0f, 0f, 0f, 0.55f), VRWidgets.MakeDisc(256));
            var brt = _background.rectTransform;
            brt.anchorMin = Vector2.zero; brt.anchorMax = Vector2.one; brt.offsetMin = Vector2.zero; brt.offsetMax = Vector2.zero;

            _center = VRWidgets.MakeText("Center", _root, 34f);
            _center.rectTransform.sizeDelta = new Vector2(260f, 120f);
            _center.color = new Color(1f, 0.9f, 0.4f);
        }

        void Update()
        {
            if (!VRRig.Active || !DnWVRMod.PrefDebugRadialMenu.Value)
            {
                if (_open) Close(false);
                return;
            }
            bool button = VRInput.Left.Valid && VRInput.Left.Secondary;
            if (button && !_wasButton && !_open) Open();
            if (_open)
            {
                UpdateSelection(VRInput.Left.Stick);
                if (!button) Close(true);
            }
            _wasButton = button;
        }

        void Open()
        {
            if (!BuildItems()) return;
            _open = true;
            VRInput.SuppressGameInput = true;
            _canvas.gameObject.SetActive(true);
            var cam = Camera.main;
            if (cam != null)
            {
                _root.position = cam.transform.position + cam.transform.forward * Distance;
                _root.rotation = Quaternion.LookRotation(_root.position - cam.transform.position, Vector3.up);
            }
            _selected = -1;
            Highlight();
        }

        void Close(bool activate)
        {
            _open = false;
            VRInput.SuppressGameInput = false;
            _canvas.gameObject.SetActive(false);
            if (activate && _selected >= 0 && _selected < _items.Count)
            {
                try { Activate(_items[_selected]); }
                catch (Exception e) { s_log?.Error("[RadialToolMenu] activate failed: " + e); }
            }
        }

        bool BuildItems()
        {
            foreach (var it in _items) if (it.Rect != null) Destroy(it.Rect.gameObject);
            _items.Clear();
            Tool current = null, empty = null;
            try { current = ToolManager.GetCurrentTool(); empty = ToolManager.GetEmptyTool(); }
            catch { return false; }
            if (empty == null) return false;

            bool holding = current != null && current.name != empty.name;
            if (holding)
                _items.Add(new Item { Label = "Put away\n" + current.name, PutAway = true });

            foreach (var st in FindObjectsByType<InteractableToolEquip>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (!st.gameObject.activeInHierarchy || !st.placedModelActive) continue;
                var tool = s_stationTool(st);
                if (tool == null) continue;
                bool ok;
                try { ok = s_canInteract(st, empty); } catch { ok = false; }
                if (!ok) continue;
                _items.Add(new Item { Label = tool.name, Station = st });
            }
            if (_items.Count == 0)
            {
                s_log?.Msg("[RadialToolMenu] nothing to equip here");
                return false;
            }

            int n = _items.Count;
            float radius = 215f;
            for (int i = 0; i < n; i++)
            {
                float ang = (90f - i * 360f / n) * Mathf.Deg2Rad;
                var t = VRWidgets.MakeText("Item" + i, _root, 30f);
                t.rectTransform.sizeDelta = new Vector2(170f, 90f);
                t.rectTransform.anchoredPosition = new Vector2(Mathf.Cos(ang) * radius, Mathf.Sin(ang) * radius);
                t.text = _items[i].Label;
                _items[i].Text = t;
                _items[i].Rect = t.rectTransform;
            }
            return true;
        }

        void UpdateSelection(Vector2 stick)
        {
            int prev = _selected;
            if (stick.magnitude < 0.45f)
            {
                _selected = -1;
            }
            else
            {
                int n = _items.Count;
                float deg = Mathf.Atan2(stick.x, stick.y) * Mathf.Rad2Deg; // 0 = up, clockwise positive
                if (deg < 0f) deg += 360f;
                _selected = Mathf.RoundToInt(deg / (360f / n)) % n;
            }
            if (_selected != prev)
            {
                Highlight();
                if (_selected >= 0) VRWidgets.Haptic(XRNode.LeftHand, 0.25f, 0.04f);
            }
        }

        void Highlight()
        {
            for (int i = 0; i < _items.Count; i++)
            {
                bool sel = i == _selected;
                _items[i].Text.color = sel ? new Color(1f, 0.9f, 0.4f) : Color.white;
                _items[i].Rect.localScale = Vector3.one * (sel ? 1.25f : 1f);
            }
            _center.text = _selected >= 0 ? _items[_selected].Label : "";
        }

        void Activate(Item item)
        {
            var empty = ToolManager.GetEmptyTool();
            var current = ToolManager.GetCurrentTool();
            bool holding = current != null && current.name != empty.name;

            if (holding)
            {
                if (!PutAway(current))
                {
                    // Unequipping without a station would destroy the model and lose the tool for the level.
                    s_log?.Msg($"[RadialToolMenu] no station accepts {current.name}; keeping it");
                    return;
                }
                if (item.PutAway) return;
            }
            if (item.Station == null || !item.Station.placedModelActive) return;
            item.Station.Interact(ToolManager.GetEmptyTool());
            VRWidgets.Haptic(XRNode.RightHand, 0.5f, 0.08f);
            s_log?.Msg($"[RadialToolMenu] equipped {item.Label} from {item.Station.name}");
        }

        static bool PutAway(Tool current)
        {
            foreach (var st in FindObjectsByType<InteractableToolEquip>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (!st.gameObject.activeInHierarchy || st.placedModelActive) continue;
                var tool = s_stationTool(st);
                if (tool == null || tool.name != current.name) continue;
                bool ok;
                try { ok = s_canInteract(st, current); } catch { ok = false; }
                if (!ok) continue;
                st.Interact(current);
                return true;
            }
            return false;
        }
    }
}
