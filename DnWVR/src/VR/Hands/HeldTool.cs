using com.gatordragongames.washnwalk.tools;
using UnityEngine;

namespace DnWVR.VR
{
    /// <summary>
    /// Where a tool sits once it is in your hand. Every model is authored around the flat game's single anchor in front of
    /// the camera, which two of them cannot keep in VR: the pressure washer hangs a barrel's length ahead of its grip, and
    /// the ladder stands two metres tall on the anchor, so both are pulled back onto the controller.
    /// </summary>
    public static class HeldTool
    {
        /// <summary>Offset (m, along the aim pose) for the pressure washer: negative z pulls it back into the hand.</summary>
        public static Vector3 SprayerOffset = new Vector3(0f, 0f, -0.2f);

        /// <summary>Offset for the ladder, which stands on the anchor: negative y brings its middle down onto the hand.</summary>
        public static Vector3 LadderOffset = new Vector3(0f, -1f, 0f);

        /// <summary>Extra local offset for the tool anchor, in the aim pose's axes (mirrored with the rest of the pose).</summary>
        public static Vector3 AnchorOffset(ToolModel model)
        {
            if (model is ToolModelSprayer) return SprayerOffset;
            if (model is ToolModelFootstool) return LadderOffset;
            return Vector3.zero;
        }
    }
}
