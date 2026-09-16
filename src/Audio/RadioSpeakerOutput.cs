using System;
using UnityEngine;

namespace SailwindRadio
{
    internal sealed class RadioSpeakerProfile
    {
        internal readonly float Gain, Radius, HighPass, LowPass;
        internal readonly bool IsWoofer;
        private RadioSpeakerProfile(float gain, float radius, float highPass, float lowPass, bool woofer = false)
        { Gain = gain; Radius = radius; HighPass = highPass; LowPass = lowPass; IsWoofer = woofer; }
        internal static readonly RadioSpeakerProfile BuiltIn = new RadioSpeakerProfile(.25f, 12f, 650f, 8500f);
        internal static readonly RadioSpeakerProfile Small = new RadioSpeakerProfile(.35f, 15f, 500f, 10000f);
        internal static readonly RadioSpeakerProfile Normal = new RadioSpeakerProfile(.65f, 20f, 20f, 22000f);
        internal static readonly RadioSpeakerProfile TurboWoofer = new RadioSpeakerProfile(1f, 20f, 20f, 140f, true);
        internal static RadioSpeakerProfile ForKind(int kind)
        { return kind == 1 ? Small : kind == 2 ? Normal : kind == 3 ? TurboWoofer : null; }
    }

    /// <summary>An output endpoint never owns or decodes a clip. RadioPlayback owns its timeline.</summary>
    internal sealed class RadioSpeakerOutput : IDisposable
    {
        internal readonly GameObject Emitter;
        internal readonly AudioSource Source;
        internal RadioSpeakerProfile Profile;
        internal Vector3 Position;
        internal bool Carried, Seen, Running, Paused;
        internal double SettlesAt, StartsAt;
        internal int DriftObservations;
        internal float LocalVolume = 1f, Bass = 1f, Obstruction;
        private AudioHighPassFilter highPass;
        private AudioLowPassFilter lowPass;
        private RadioStaticFilter staticFilter;
        private float smoothedObstruction;
        private bool initialized;

        internal RadioSpeakerOutput(string name, RadioSpeakerProfile profile, Action<string> warning)
        {
            Profile = profile;
            Emitter = new GameObject(name);
            UnityEngine.Object.DontDestroyOnLoad(Emitter);
            Source = Emitter.AddComponent<AudioSource>();
            Source.playOnAwake = false;
            Source.loop = true;
            Source.spatialBlend = 1f;
            var curve = AnimationCurve.Constant(0f, 1f, 1f);
            curve.preWrapMode = WrapMode.ClampForever;
            curve.postWrapMode = WrapMode.ClampForever;
            Source.SetCustomCurve(AudioSourceCurveType.CustomRolloff, curve);
            Source.rolloffMode = AudioRolloffMode.Custom;
            Source.minDistance = 1f;
            Source.maxDistance = profile.Radius;
            Source.volume = 0f;
            Source.pitch = 1f;
            Source.dopplerLevel = 0f;
            Source.ignoreListenerPause = false;
            try
            {
                highPass = Emitter.AddComponent<AudioHighPassFilter>();
                if (highPass == null) throw new InvalidOperationException("High-pass component unavailable");
                highPass.cutoffFrequency = profile.HighPass;
                highPass.highpassResonanceQ = 1f;
            }
            catch (Exception ex)
            {
                if (highPass != null) UnityEngine.Object.Destroy(highPass);
                highPass = null;
                warning("Radio high-pass filter unavailable: " + ex.Message);
            }
            try
            {
                lowPass = Emitter.AddComponent<AudioLowPassFilter>();
                if (lowPass == null) throw new InvalidOperationException("Low-pass component unavailable");
                lowPass.cutoffFrequency = profile.LowPass;
                lowPass.lowpassResonanceQ = 1f;
            }
            catch (Exception ex)
            {
                if (lowPass != null) UnityEngine.Object.Destroy(lowPass);
                lowPass = null;
                warning("Radio low-pass filter unavailable: " + ex.Message);
            }
            try
            {
                staticFilter = Emitter.AddComponent<RadioStaticFilter>();
                if (staticFilter == null) throw new InvalidOperationException("Static component unavailable");
            }
            catch (Exception ex)
            {
                if (staticFilter != null) UnityEngine.Object.Destroy(staticFilter);
                staticFilter = null;
                warning("Radio static filter unavailable: " + ex.Message);
            }
        }

        internal void Update(float masterVolume, Vector3 listener, bool listenerKnown, float interferenceGain, float interferenceCutoff, float crackle)
        {
            if (Emitter == null || Source == null) return;
            Emitter.transform.position = Position;
            Source.spatialBlend = Carried ? 0f : 1f;
            Source.maxDistance = Profile.Radius;
            float target = Carried ? 0f : Clamp(Obstruction);
            if (!initialized) { smoothedObstruction = target; initialized = true; }
            float elapsed = Time.unscaledDeltaTime;
            if (!Finite(elapsed) || elapsed < 0) elapsed = 0;
            smoothedObstruction += (target - smoothedObstruction) * (float)(1 - Math.Exp(-elapsed / .25));
            float effectiveObstruction = Carried ? 0f : smoothedObstruction;
            if (highPass != null) highPass.cutoffFrequency = Profile.HighPass;
            float closedCutoff = Math.Min(1500f, Profile.LowPass);
            if (lowPass != null)
                lowPass.cutoffFrequency = Math.Min(Profile.LowPass + (closedCutoff - Profile.LowPass) * effectiveObstruction, interferenceCutoff);
            float distanceGain = !listenerKnown ? 0f : Carried ? 1f : DistanceGain(Vector3.Distance(Position, listener), Profile.Radius);
            float local = Profile.IsWoofer ? 1f : Clamp(LocalVolume);
            float bassGain = Profile.IsWoofer ? (float)Math.Sqrt(Clamp(Bass)) : 1f;
            Source.volume = Profile.Gain * masterVolume * masterVolume * local * local * bassGain * distanceGain *
                (1f - (Profile.IsWoofer ? .4f : .65f) * effectiveObstruction) * interferenceGain;
            if (staticFilter != null)
            {
                staticFilter.SetProfile(AudioSettings.outputSampleRate, Profile.IsWoofer);
                staticFilter.SetLevel(Running ? crackle : 0f, Source.volume);
            }
        }

        internal void Stop(bool clearClip = false)
        {
            Running = Paused = false;
            if (staticFilter != null) staticFilter.SetLevel(0, 0);
            if (Source == null) return;
            Source.Stop();
            if (clearClip) Source.clip = null;
        }

        internal void Pause()
        {
            if (!Running || Source == null) return;
            Source.Pause();
            Running = false;
            Paused = true;
            if (staticFilter != null) staticFilter.SetLevel(0, 0);
        }

        internal void Resume(int sample, double now)
        {
            if (Source == null) return;
            Source.timeSamples = sample;
            Source.UnPause();
            Running = true;
            Paused = false;
            SettlesAt = now + .3;
            StartsAt = now;
        }

        internal static float DistanceGain(float distance, float radius)
        {
            if (!Finite(distance)) return 0f;
            float remaining = 1f - Mathf.Clamp01((distance - 1f) / (radius - 1f));
            return remaining * remaining;
        }
        internal static float Clamp(float value) { return Single.IsNaN(value) ? 0f : Mathf.Clamp01(value); }
        internal static bool Finite(float value) { return !Single.IsNaN(value) && !Single.IsInfinity(value); }
        public void Dispose()
        {
            try { Stop(true); }
            finally
            {
                if (Emitter != null) UnityEngine.Object.Destroy(Emitter);
                highPass = null;
                lowPass = null;
                staticFilter = null;
            }
        }
    }
}
