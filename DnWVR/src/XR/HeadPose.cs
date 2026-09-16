using UnityEngine;
using UnityEngine.XR;

namespace DnWVR.XR
{
    /// <summary>Reads HMD and controller poses from the XR input subsystem, in tracking space.</summary>
    public static class HeadPose
    {
        static InputDevice s_head;

        public static bool TryGet(out Vector3 position, out Quaternion rotation) => TryGet(out position, out rotation, out _);

        public static bool TryGet(out Vector3 position, out Quaternion rotation, out bool tracked)
        {
            position = Vector3.zero;
            rotation = Quaternion.identity;
            tracked = false;
            if (!XRBootstrap.IsRunning) return false;

            if (!s_head.isValid)
                s_head = InputDevices.GetDeviceAtXRNode(XRNode.CenterEye);
            if (!s_head.isValid)
                s_head = InputDevices.GetDeviceAtXRNode(XRNode.Head);
            if (!s_head.isValid) return false;

            bool ok = s_head.TryGetFeatureValue(CommonUsages.centerEyePosition, out position)
                    & s_head.TryGetFeatureValue(CommonUsages.centerEyeRotation, out rotation);
            if (!ok)
            {
                ok = s_head.TryGetFeatureValue(CommonUsages.devicePosition, out position)
                   & s_head.TryGetFeatureValue(CommonUsages.deviceRotation, out rotation);
            }
            if (ok)
            {
                if (!s_head.TryGetFeatureValue(CommonUsages.isTracked, out tracked))
                    tracked = position.sqrMagnitude > 1e-4f;
            }
            return ok;
        }

        public static bool TryGetController(XRNode node, out Vector3 position, out Quaternion rotation) => TryGetController(node, out position, out rotation, out _);

        /// <summary>
        /// Returns true when a pose was read, which may be stale; positionValid also requires the position to be
        /// tracked this frame (isTracked and the trackingState Position bit, where the device reports them).
        /// </summary>
        public static bool TryGetController(XRNode node, out Vector3 position, out Quaternion rotation, out bool positionValid)
        {
            position = Vector3.zero;
            rotation = Quaternion.identity;
            positionValid = false;
            var dev = InputDevices.GetDeviceAtXRNode(node);
            if (!dev.isValid) return false;
            bool ok = dev.TryGetFeatureValue(CommonUsages.devicePosition, out position)
                    & dev.TryGetFeatureValue(CommonUsages.deviceRotation, out rotation);
            if (!ok) return false;
            positionValid = true;
            if (dev.TryGetFeatureValue(CommonUsages.isTracked, out bool tracked) && !tracked) positionValid = false;
            if (dev.TryGetFeatureValue(CommonUsages.trackingState, out InputTrackingState state) && (state & InputTrackingState.Position) == 0)
                positionValid = false;
            return true;
        }
    }
}
