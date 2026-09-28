using System;
using System.Collections.Generic;

// Narrow lifecycle doubles only. No decoding, real DSP clock or audible output is simulated.
// Unity web requests have no double. Production audio never uses them, so a reference fails to compile.
namespace UnityEngine
{
    public class Object
    {
        public bool Destroyed;
        public bool PersistsAcrossScenes;
        public static void Destroy(Object value)
        {
            value.Destroyed = true;
            var gameObject = value as GameObject;
            if (!ReferenceEquals(gameObject, null)) foreach (Object component in gameObject.Components) component.Destroyed = true;
        }
        public static void DontDestroyOnLoad(Object value) { value.PersistsAcrossScenes = true; }
        public static bool operator ==(Object a, Object b)
        {
            bool aNull = ReferenceEquals(a, null) || a.Destroyed;
            bool bNull = ReferenceEquals(b, null) || b.Destroyed;
            return aNull && bNull || !aNull && !bNull && ReferenceEquals(a,b);
        }
        public static bool operator !=(Object a, Object b) { return !(a == b); }
        public override bool Equals(object other) { return ReferenceEquals(this, other); }
        public override int GetHashCode() { return base.GetHashCode(); }
    }
    public class MonoBehaviour : Object { }
    public struct Vector3
    {
        public float x, y, z;
        public Vector3(float x,float y,float z) { this.x=x;this.y=y;this.z=z; }
        public static float Distance(Vector3 a, Vector3 b)
        { double x=a.x-b.x, y=a.y-b.y, z=a.z-b.z; return (float)Math.Sqrt(x*x+y*y+z*z); }
    }
    public class Transform { public Vector3 position; }
    public class GameObject : Object
    {
        public static GameObject Last;
        public static bool RejectFilters;
        public readonly List<Object> Components = new List<Object>();
        public Transform transform = new Transform();
        public bool activeSelf = true; public void SetActive(bool value) { activeSelf = value; }
        public GameObject(string name) { Last = this; }
        public T AddComponent<T>() where T : new()
        {
            if (RejectFilters && (typeof(T) == typeof(AudioHighPassFilter) || typeof(T) == typeof(AudioLowPassFilter)))
                throw new InvalidOperationException("Filter unavailable in fixture");
            var component = new T();
            if (component is Object) Components.Add(component as Object);
            return component;
        }
    }
    public enum AudioRolloffMode { Logarithmic, Linear, Custom }
    public enum AudioSourceCurveType { CustomRolloff }
    public enum WrapMode { ClampForever }
    public struct Keyframe { public float time, value; public Keyframe(float time,float value) { this.time=time;this.value=value; } }
    public class AnimationCurve
    {
        public Keyframe[] keys;
        public WrapMode preWrapMode, postWrapMode;
        public static AnimationCurve Constant(float start, float end, float value)
        { return new AnimationCurve { keys = new[] { new Keyframe(start,value), new Keyframe(end,value) } }; }
    }
    public class AudioHighPassFilter : Object
    {
        public static AudioHighPassFilter Last;
        public AudioHighPassFilter() { Last=this; }
        public float cutoffFrequency, highpassResonanceQ;
    }
    public class AudioLowPassFilter : Object
    {
        public static AudioLowPassFilter Last;
        public AudioLowPassFilter() { Last=this; }
        public float cutoffFrequency, lowpassResonanceQ;
    }
    public class AudioClip : Object
    {
        public delegate void PCMReaderCallback(float[] data);
        public delegate void PCMSetPositionCallback(int position);
        public PCMReaderCallback PcmRead;
        public PCMSetPositionCallback PcmSetPosition;
        public bool Streaming;
        // The Unity 2019.1 editor probe measured a 0.8 s pre-read, in 2048-frame reads, each time Unity
        // positions a streamed clip's reader. A start at sample 0 plays the creation pre-read unchanged.
        public static double PreReadSeconds = .8;
        public int PreReadFrom = -1;
        public float[] PreReadData;
        // Set by the first scheduled play. Unity rewinds a replayed stream before it plays.
        public bool Played;
        public int samples = 480000, channels = 1;
        public int frequency = 48000;
        public static int CreateThread, Created;
        public static bool RejectCreate;
        public static AudioClip LastCreated;
        // Production audio never uploads PCM, so this double deliberately has no SetData.
        // Any SetData call in src/Audio fails to compile in every harness that links it.
        public static AudioClip Create(string name, int lengthSamples, int channels, int frequency, bool stream)
        {
            CreateThread = Environment.CurrentManagedThreadId;
            if (RejectCreate) return null;
            if (lengthSamples <= 0 || channels < 1 || frequency <= 0) throw new ArgumentException("Invalid clip dimensions");
            Created++;
            return LastCreated = new AudioClip { samples = lengthSamples, channels = channels, frequency = frequency, Streaming = stream };
        }
        public static AudioClip Create(string name, int lengthSamples, int channels, int frequency, bool stream,
            PCMReaderCallback read, PCMSetPositionCallback setPosition)
        {
            var clip = Create(name, lengthSamples, channels, frequency, stream);
            if (clip == null) return null;
            clip.PcmRead = read;
            clip.PcmSetPosition = setPosition;
            // The Unity 2019.1 editor probe saw a streamed clip seek its reader to 0 during creation.
            if (stream && setPosition != null) { setPosition(0); clip.PreRead(0); }
            return clip;
        }
        public void Seek(int position)
        {
            if (!Streaming || PcmSetPosition == null) return;
            PcmSetPosition(position);
            PreRead(position);
        }
        private void PreRead(int position)
        {
            if (PcmRead == null) return;
            PreReadFrom = position;
            int frames = (int)Math.Max(0, Math.Min(samples - position, Math.Round(frequency * PreReadSeconds)));
            PreReadData = new float[frames * channels];
            for (int done = 0; done < frames; done += 2048)
            {
                var block = new float[Math.Min(2048, frames - done) * channels];
                PcmRead(block);
                Array.Copy(block, 0, PreReadData, done * channels, block.Length);
            }
        }
    }
    // Set-position callbacks follow the Unity 2019.1 editor probe: a timeSamples write seeks a streamed
    // clip's reader only while the source plays or is paused, PlayScheduled rewinds a stream that already
    // played and then seeks it to a pending nonzero position, and clip assignment, Pause, UnPause and Stop
    // never call it. Every seek pre-reads as AudioClip describes.
    public class AudioSource : Object
    {
        public static AudioSource Last;
        public AudioSource() { Last = this; }
        public bool playOnAwake, loop, ignoreListenerPause, bypassReverbZones;
        public float spatialBlend, minDistance, maxDistance, pitch, dopplerLevel, volume;
        public AudioRolloffMode rolloffMode;
        public AnimationCurve RolloffCurve;
        public AudioSourceCurveType RolloffCurveType;
        public void SetCustomCurve(AudioSourceCurveType type, AnimationCurve curve) { RolloffCurveType=type; RolloffCurve=curve; }
        public AudioClip clip;
        private int cursor;
        public int timeSamples
        {
            get { if (Destroyed) throw new InvalidOperationException("Destroyed native AudioSource"); return cursor; }
            set
            {
                if (Destroyed) throw new InvalidOperationException("Destroyed native AudioSource");
                if (value < 0 || (clip != null && value >= clip.samples)) throw new ArgumentOutOfRangeException("timeSamples");
                cursor=value;
                if ((isPlaying || paused) && clip != null) clip.Seek(value);
            }
        }
        public int Schedules;
        public double ScheduledAt;
        public bool ThrowOnNextStop;
        public bool enabled = true, isVirtual, isPlaying;
        public int Pauses, UnPauses;
        private bool paused;
        public void Pause() { Pauses++; paused = isPlaying || paused; isPlaying = false; }
        public void UnPause() { UnPauses++; paused = false; isPlaying = true; }
        public void Stop()
        {
            if (ThrowOnNextStop) { ThrowOnNextStop = false; throw new InvalidOperationException("Injected native Stop failure"); }
            if (Destroyed) throw new InvalidOperationException("Destroyed native AudioSource");
            cursor = 0; isPlaying = false; paused = false;
        }
        public void PlayScheduled(double value)
        {
            Schedules++; ScheduledAt=value; isPlaying = true; paused = false;
            if (clip == null) return;
            if (clip.Played) clip.Seek(0);
            if (cursor != 0) clip.Seek(cursor);
            clip.Played = true;
        }
    }
    public static class Mathf { public static float Clamp01(float v) { return Math.Max(0,Math.Min(v,1)); } }
    public static class AudioListener { public static bool pause; }
    public static class Time { public static float realtimeSinceStartup; public static float unscaledDeltaTime = 1f / 60; }
    public static class AudioSettings
    {
        public static double dspTime; public static int outputSampleRate = 48000;
        public static void GetDSPBufferSize(out int length, out int count) { length = 1024; count = 4; }
        public static event Action<bool> OnAudioConfigurationChanged;
        public static int Subscribers { get { return OnAudioConfigurationChanged == null ? 0 : OnAudioConfigurationChanged.GetInvocationList().Length; } }
        public static void Reset() { OnAudioConfigurationChanged?.Invoke(true); }
    }
}
