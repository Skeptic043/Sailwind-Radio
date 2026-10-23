using System;
using UnityEngine;

namespace SailwindRadio.Audio
{
    internal sealed class RadioSpeakerProfile
    {
        internal const float MinimumRadius = 5f, MaximumRadius = 50f;
        internal readonly float Gain, DefaultRadius, HighPass, LowPass;
        internal readonly bool IsWoofer;
        // Endpoints compare profiles by reference, so a setting changes the radius in place.
        internal float Radius { get; private set; }
        private RadioSpeakerProfile(float gain, float radius, float highPass, float lowPass, bool woofer = false)
        { Gain = gain; Radius = DefaultRadius = radius; HighPass = highPass; LowPass = lowPass; IsWoofer = woofer; }
        internal void SetRadius(float radius)
        { Radius = Single.IsNaN(radius) || Single.IsInfinity(radius) ? DefaultRadius : Math.Min(MaximumRadius, Math.Max(MinimumRadius, radius)); }
        // The two smallest cabinets use a restrained native bandpass that keeps vocals intelligible and sets
        // their character apart from the full-size outputs.
        internal static readonly RadioSpeakerProfile BuiltIn = new RadioSpeakerProfile(.25f, 12f, 900f, 5000f);
        internal static readonly RadioSpeakerProfile Small = new RadioSpeakerProfile(.35f, 15f, 600f, 9000f);
        internal static readonly RadioSpeakerProfile Normal = new RadioSpeakerProfile(.65f, 20f, 160f, 18000f);
        internal static readonly RadioSpeakerProfile TurboWoofer = new RadioSpeakerProfile(1f, 20f, 20f, 140f, true);
        internal static RadioSpeakerProfile ForKind(int kind)
        {
            var role = DeviceKind.Role(kind);
            return role == DeviceRole.SmallSpeaker ? Small : role == DeviceRole.Speaker ? Normal : role == DeviceRole.Woofer ? TurboWoofer : null;
        }
    }

    /// <summary>One emitter and its own stream clip. It never decodes, and RadioPlayback owns the timeline.</summary>
    internal sealed class RadioSpeakerOutput : IDisposable
    {
        internal readonly GameObject Emitter;
        internal readonly AudioSource Source;
        internal AudioClip StreamClip;
        internal RadioSpeakerProfile Profile;
        internal Vector3 Position;
        // Flat outputs play without panning but keep their distance gain, for a listening point away from the native listener.
        internal bool Carried, Flat, Seen, Running, Paused;
        internal double SettlesAt, StartsAt;
        internal int DriftObservations;
        internal float LocalVolume = 1f, Bass = 1f, Obstruction;
        private AudioHighPassFilter highPass;
        private AudioLowPassFilter lowPass;
        private RadioBassFilter bassFilter;
        private float smoothedObstruction;
        private bool initialized;

        internal RadioSpeakerOutput(string name, RadioSpeakerProfile profile, Action<string> warning)
        {
            Profile = profile;
            Emitter = new GameObject(name);
            UnityEngine.Object.DontDestroyOnLoad(Emitter);
            Source = Emitter.AddComponent<AudioSource>();
            Source.playOnAwake = false;
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
            // Native pause retains the voice, while a modded port's global reverb can
            // keep sounding after it. Keep Radio's own signal out of reverb zones.
            Source.bypassReverbZones = true;
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
            if (profile.IsWoofer) try
            {
                bassFilter = Emitter.AddComponent<RadioBassFilter>();
                if (bassFilter == null) throw new InvalidOperationException("Bass component unavailable");
            }
            catch (Exception ex)
            {
                if (bassFilter != null) UnityEngine.Object.Destroy(bassFilter);
                bassFilter = null;
                warning("Radio bass filter unavailable: " + ex.Message);
            }
        }

        internal void Update(float masterVolume, Vector3 listener, bool listenerKnown)
        {
            if (Emitter == null || Source == null) return;
            Emitter.transform.position = Position;
            Source.spatialBlend = Carried || Flat ? 0f : 1f;
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
                lowPass.cutoffFrequency = Profile.LowPass + (closedCutoff - Profile.LowPass) * effectiveObstruction;
            float distanceGain = !listenerKnown ? 0f : Carried ? 1f : DistanceGain(Vector3.Distance(Position, listener), Profile.Radius);
            float local = Profile.IsWoofer ? 1f : Clamp(LocalVolume);
            float bassGain = Profile.IsWoofer ? (float)Math.Sqrt(Clamp(Bass)) : 1f;
            Source.volume = Profile.Gain * masterVolume * masterVolume * local * local * bassGain * distanceGain *
                (1f - (Profile.IsWoofer ? .4f : .65f) * effectiveObstruction);
            if (bassFilter != null) bassFilter.SetProfile(AudioSettings.outputSampleRate, Profile.IsWoofer);
        }

        internal void Stop(bool clearClip = false)
        {
            Running = Paused = false;
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
                ReleaseStreamClip();
                if (Emitter != null) UnityEngine.Object.Destroy(Emitter);
                highPass = null;
                lowPass = null;
                bassFilter = null;
            }
        }

        internal void ReleaseStreamClip()
        {
            if (StreamClip != null) UnityEngine.Object.Destroy(StreamClip);
            StreamClip = null;
        }
    }
}
