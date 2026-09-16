using TMPro;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR;

namespace DnWVR.VR
{
    /// <summary>
    /// The pieces the mod's own panels are built from: labels and images on a canvas, the round sprites it draws itself,
    /// and a buzz in a controller. The panels themselves live in <see cref="VRUI"/>.
    /// </summary>
    public static class VRWidgets
    {
        static TMP_FontAsset s_font;

        /// <summary>The game's own font, so the mod's panels read like the rest of the game.</summary>
        public static TMP_FontAsset Font
        {
            get
            {
                if (s_font != null) return s_font;
                try
                {
                    if (TMP_Settings.defaultFontAsset != null) return s_font = TMP_Settings.defaultFontAsset;
                }
                catch { }
                var any = Object.FindFirstObjectByType<TMP_Text>(FindObjectsInactive.Include);
                return s_font = any != null ? any.font : null;
            }
        }

        /// <summary>A centred white label; the caller sets its text and the size of its rect.</summary>
        public static TextMeshProUGUI MakeText(string name, Transform parent, float fontSize)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            go.layer = parent.gameObject.layer;
            var text = go.AddComponent<TextMeshProUGUI>();
            var font = Font;
            if (font != null) text.font = font;
            text.fontSize = fontSize;
            text.alignment = TextAlignmentOptions.Center;
            text.textWrappingMode = TextWrappingModes.Normal;
            text.color = Color.white;
            text.raycastTarget = false;
            return text;
        }

        /// <summary>An image; without a sprite it is a plain rectangle of that colour.</summary>
        public static Image MakeImage(string name, Transform parent, Color color, Sprite sprite = null)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            go.layer = parent.gameObject.layer;
            var image = go.AddComponent<Image>();
            image.sprite = sprite;
            image.color = color;
            return image;
        }

        /// <summary>
        /// A white disc drawn in code, square and <paramref name="size"/> pixels wide, with soft one-pixel edges.
        /// An <paramref name="inner"/> above zero (a fraction of the radius) hollows it out into a ring.
        /// </summary>
        public static Sprite MakeDisc(int size, float inner = 0f)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
            float r = size * 0.5f;
            var px = new Color32[size * size];
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    float dx = x + 0.5f - r, dy = y + 0.5f - r;
                    float d = Mathf.Sqrt(dx * dx + dy * dy);
                    float a = Mathf.Clamp01(r - d);
                    if (inner > 0f) a *= Mathf.Clamp01(d - r * inner);
                    px[y * size + x] = new Color32(255, 255, 255, (byte)(a * 255f));
                }
            tex.SetPixels32(px);
            tex.Apply();
            return Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f));
        }

        /// <summary>A short buzz in one controller; controllers without haptics simply ignore it.</summary>
        public static void Haptic(XRNode node, float amplitude, float seconds)
        {
            try
            {
                var dev = InputDevices.GetDeviceAtXRNode(node);
                if (dev.isValid) dev.SendHapticImpulse(0, amplitude, seconds);
            }
            catch { }
        }
    }
}
