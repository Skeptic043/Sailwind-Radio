using System;
using System.Collections.Generic;

// Narrow lifecycle doubles only. No decoding, real DSP clock or audible output is simulated.
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
    public enum AudioDataLoadState { Failed, Loaded, Loading }
    public enum AudioType { MPEG, OGGVORBIS, WAV, UNKNOWN }
    public class AudioClip : Object
    {
        public int samples = 480000, channels = 1;
        public int frequency = 48000;
        public AudioDataLoadState loadState = AudioDataLoadState.Loaded;
        public static int CreateThread;
        public int UploadedSamples;
        public static bool RejectUpload;
        public static AudioClip LastCreated;
        public static AudioClip Create(string name, int lengthSamples, int channels, int frequency, bool stream)
        {
            CreateThread = Environment.CurrentManagedThreadId;
            return LastCreated = new AudioClip { samples = lengthSamples, channels = channels, frequency = frequency };
        }
        public bool SetData(float[] values, int offset)
        {
            if (RejectUpload) return false;
            if (offset * channels != UploadedSamples || UploadedSamples + values.Length > samples * channels)
                throw new InvalidOperationException("PCM upload offset or clip dimensions are incorrect");
            UploadedSamples += values.Length;
            return true;
        }
    }
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
            }
        }
        public int Schedules;
        public double ScheduledAt;
        public bool ThrowOnNextStop;
        public bool enabled = true, isVirtual, isPlaying;
        public int Pauses, UnPauses;
        public void Pause() { Pauses++; isPlaying = false; }
        public void UnPause() { UnPauses++; isPlaying = true; }
        public void Stop()
        {
            if (ThrowOnNextStop) { ThrowOnNextStop = false; throw new InvalidOperationException("Injected native Stop failure"); }
            timeSamples = 0; isPlaying = false;
        }
        public void PlayScheduled(double value) { Schedules++; ScheduledAt=value; isPlaying = true; }
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
namespace UnityEngine.Networking
{
    public class DownloadHandlerAudioClip
    {
        public bool streamAudio;
        public UnityEngine.AudioClip Content = new UnityEngine.AudioClip();
        public static UnityEngine.AudioClip GetContent(UnityWebRequest request) { return request.downloadHandler.Content; }
    }
    public class UnityWebRequest : IDisposable
    {
        public static readonly List<UnityWebRequest> All = new List<UnityWebRequest>();
        public bool isDone, isNetworkError, isHttpError, Aborted, Disposed;
        public string error;
        public DownloadHandlerAudioClip downloadHandler = new DownloadHandlerAudioClip();
        public void SendWebRequest() { }
        public void Abort() { Aborted = true; }
        public void Dispose() { Disposed = true; }
    }
    public static class UnityWebRequestMultimedia
    {
        public static string LastUri;
        public static UnityEngine.AudioType LastType;
        public static UnityWebRequest GetAudioClip(string uri, UnityEngine.AudioType type)
        {
            LastUri=uri; LastType=type;
            var result = new UnityWebRequest(); UnityWebRequest.All.Add(result); return result;
        }
    }
}
