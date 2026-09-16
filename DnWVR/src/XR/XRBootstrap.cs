using System;
using System.Collections.Generic;
using System.Reflection;
using MelonLoader;
using UnityEngine;
using UnityEngine.XR;
using UnityEngine.XR.Management;
using UnityEngine.XR.OpenXR;
using UnityEngine.XR.OpenXR.Features;
using UnityEngine.XR.OpenXR.Features.Interactions;

namespace DnWVR.XR
{
    /// <summary>
    /// Starts Unity's OpenXR plugin in a game that shipped without XR settings assets: the settings, manager and
    /// loader are created in memory and wired into the static slots the serialized assets would normally fill.
    /// </summary>
    public static class XRBootstrap
    {
        const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        public static OpenXRLoader Loader { get; private set; }
        public static XRManagerSettings Manager { get; private set; }
        public static OpenXRSettings Settings { get; private set; }
        public static bool IsRunning => Manager != null && Manager.activeLoader != null;

        static readonly Type[] InteractionProfiles =
        {
            typeof(OculusTouchControllerProfile),
            typeof(MetaQuestTouchProControllerProfile),
            typeof(MetaQuestTouchPlusControllerProfile),
            typeof(ValveIndexControllerProfile),
            typeof(HTCViveControllerProfile),
            typeof(MicrosoftMotionControllerProfile),
            typeof(HPReverbG2ControllerProfile),
            typeof(KHRSimpleControllerProfile),
        };

        public static bool Start(MelonLogger.Instance log, bool singlePassInstanced)
        {
            if (IsRunning) return true;
            try
            {
                Settings = CreateOpenXRSettings(log, singlePassInstanced);

                var general = ScriptableObject.CreateInstance<XRGeneralSettings>();
                general.name = "DnWVR XRGeneralSettings";
                var manager = ScriptableObject.CreateInstance<XRManagerSettings>();
                manager.name = "DnWVR XRManagerSettings";
                manager.automaticLoading = false;
                manager.automaticRunning = false;
                general.Manager = manager;
                SetStaticField(typeof(XRGeneralSettings), "s_RuntimeSettingsInstance", general);

                var loader = ScriptableObject.CreateInstance<OpenXRLoader>();
                loader.name = "DnWVR OpenXR Loader";
                // TryAddLoader only accepts loaders that were "registered" (normally by the serialized asset's Awake).
                var registered = (HashSet<XRLoader>)typeof(XRManagerSettings).GetProperty("registeredLoaders", Any).GetValue(manager);
                registered.Add(loader);
                if (!manager.TryAddLoader(loader))
                {
                    log.Error("XRManagerSettings.TryAddLoader refused the OpenXR loader");
                    return false;
                }

                log.Msg($"Initializing OpenXR (gfx={SystemInfo.graphicsDeviceType}, renderMode={Settings.renderMode})...");
                manager.InitializeLoaderSync();
                if (manager.activeLoader == null)
                {
                    log.Error("OpenXR loader failed to initialize. Is the headset connected and an OpenXR runtime active? See Player.log for the OpenXR diagnostic report.");
                    manager.DeinitializeLoader();
                    // Retry attempts must not pile up orphaned settings/feature/loader objects.
                    DestroyRuntimeObjects(Settings, general, manager, loader);
                    Settings = null;
                    return false;
                }

                manager.StartSubsystems();
                Loader = loader;
                Manager = manager;

                var input = loader.GetLoadedSubsystem<XRInputSubsystem>();
                if (input != null)
                {
                    var modes = input.GetSupportedTrackingOriginModes();
                    bool ok = input.TrySetTrackingOriginMode(TrackingOriginModeFlags.Floor);
                    log.Msg($"Tracking origin: supported={modes} floor={ok} now={input.GetTrackingOriginMode()}");
                }
                var display = loader.GetLoadedSubsystem<XRDisplaySubsystem>();
                log.Msg($"OpenXR started: runtime='{OpenXRRuntime.name}' v{OpenXRRuntime.version} api={OpenXRRuntime.apiVersion} plugin={OpenXRRuntime.pluginVersion}; " +
                        $"display running={display?.running} eyeTex={XRSettings.eyeTextureWidth}x{XRSettings.eyeTextureHeight} XRSettings.enabled={XRSettings.enabled}");
                return true;
            }
            catch (Exception e)
            {
                log.Error("XR bootstrap failed: " + e);
                return false;
            }
        }

        public static void Stop(MelonLogger.Instance log)
        {
            if (Manager == null) return;
            try
            {
                Manager.StopSubsystems();
                Manager.DeinitializeLoader();
                log.Msg("OpenXR stopped");
            }
            catch (Exception e)
            {
                log.Error("XR stop failed: " + e);
            }
            Loader = null;
            Manager = null;
        }

        static void DestroyRuntimeObjects(OpenXRSettings settings, XRGeneralSettings general, XRManagerSettings manager, OpenXRLoader loader)
        {
            try
            {
                if (settings != null)
                    foreach (var f in settings.GetFeatures())
                        if (f != null) UnityEngine.Object.Destroy(f);
                if (settings != null) UnityEngine.Object.Destroy(settings);
                if (loader != null) UnityEngine.Object.Destroy(loader);
                if (manager != null) UnityEngine.Object.Destroy(manager);
                if (general != null) UnityEngine.Object.Destroy(general);
            }
            catch { }
        }

        static OpenXRSettings CreateOpenXRSettings(MelonLogger.Instance log, bool singlePassInstanced)
        {
            // Awake() of OpenXRSettings stores the instance in the static runtime slot.
            var settings = ScriptableObject.CreateInstance<OpenXRSettings>();
            settings.name = "DnWVR OpenXRSettings";
            settings.renderMode = singlePassInstanced ? OpenXRSettings.RenderMode.SinglePassInstanced : OpenXRSettings.RenderMode.MultiPass;
            settings.depthSubmissionMode = OpenXRSettings.DepthSubmissionMode.None;

            var features = new List<OpenXRFeature>();
            foreach (var type in InteractionProfiles)
            {
                var feature = (OpenXRFeature)ScriptableObject.CreateInstance(type);
                feature.name = type.Name;
                CopyAttributeMetadata(feature);
                feature.enabled = true;
                features.Add(feature);
            }
            SetField(settings, "features", features.ToArray());

            if (OpenXRSettings.Instance != settings)
                log.Warning("OpenXRSettings.Instance is not the runtime-created instance; feature registration may be ignored");
            log.Msg($"OpenXR settings created with {features.Count} interaction profiles");
            return settings;
        }

        // OpenXRFeatureAttribute is editor-only (the editor bakes its values into the feature asset), so features
        // created in memory get those fields set by hand. Interaction profiles need no extension strings.
        static void CopyAttributeMetadata(OpenXRFeature feature)
        {
            var type = feature.GetType();
            var featureId = type.GetField("featureId", BindingFlags.Static | BindingFlags.Public)?.GetValue(null) as string;
            SetField(feature, "nameUi", type.Name);
            SetField(feature, "version", "1.16.0");
            SetField(feature, "company", "Unity");
            SetField(feature, "featureIdInternal", featureId ?? type.FullName);
            SetField(feature, "openxrExtensionStrings", string.Empty);
            SetField(feature, "priority", 0);
            SetField(feature, "required", false);
        }

        static FieldInfo FindField(Type type, string name)
        {
            for (var t = type; t != null; t = t.BaseType)
            {
                var f = t.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (f != null) return f;
            }
            return null;
        }

        static void SetField(object target, string name, object value)
        {
            var f = FindField(target.GetType(), name) ?? throw new MissingFieldException(target.GetType().Name, name);
            f.SetValue(target, value);
        }

        static void SetStaticField(Type type, string name, object value)
        {
            var f = type.GetField(name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic) ?? throw new MissingFieldException(type.Name, name);
            f.SetValue(null, value);
        }
    }
}
