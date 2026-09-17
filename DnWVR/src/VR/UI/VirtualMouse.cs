using System;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;

namespace DnWVR.VR
{
    /// <summary>
    /// The mouse the mod pushes the game's UI with, so a panel answers the laser (<see cref="VRLaser"/>) and a poking
    /// fingertip (<see cref="MenuHands"/>) exactly as it answers a real one. Both feed the same device, so whichever
    /// moved last holds the pointer; parking it <see cref="OffScreen"/> leaves every panel unhovered.
    /// </summary>
    public static class VirtualMouse
    {
        /// <summary>Far outside any panel: the position to leave the pointer at when nothing is pointed at.</summary>
        public static readonly Vector2 OffScreen = new Vector2(-1000f, -1000f);

        static Mouse s_mouse;
        static bool s_loggedNoDevice;

        /// <summary>Puts the pointer at a screen position, with or without the left button down.</summary>
        public static void Feed(Vector2 screenPos, bool leftDown)
        {
            if (!EnsureDevice()) return;
            var state = new MouseState { position = screenPos, delta = Vector2.zero };
            if (leftDown) state = state.WithButton(MouseButton.Left, true);
            InputSystem.QueueStateEvent(s_mouse, state);
        }

        /// <summary>Leaves every panel unhovered and the button up.</summary>
        public static void Release() => Feed(OffScreen, false);

        // The game has no mouse of its own in VR, so the mod adds one the first time it points at something.
        static bool EnsureDevice()
        {
            if (s_mouse != null && s_mouse.added) return true;
            s_mouse = Mouse.current;
            if (s_mouse != null) return true;
            try
            {
                s_mouse = InputSystem.AddDevice<Mouse>("DnWVR Mouse");
            }
            catch (Exception e)
            {
                if (!s_loggedNoDevice)
                {
                    s_loggedNoDevice = true;
                    Log.Warning("[VirtualMouse] cannot add a mouse; panels stay unclickable: " + e.Message);
                }
            }
            return s_mouse != null;
        }
    }
}
