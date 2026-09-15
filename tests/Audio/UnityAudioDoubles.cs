using System;
using System.Collections.Generic;

// Narrow lifecycle doubles only. No decoding, real DSP clock or audible output is simulated.
namespace UnityEngine
{
    public class Object
    {
        public bool Destroyed;
        public bool PersistsAcrossScenes;
        public static void Destroy(Object value) { value.Destroyed = true; }
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
    public struct Vector3 { public float x, y, z; public Vector3(float x,float y,float z) { this.x=x;this.y=y;this.z=z; } }
    public class Transform { public Vector3 position; }
    public class GameObject : Object
    {
        public static GameObject Last;
        public Transform transform = new Transform();
        public GameObject(string name) { Last = this; }
        public T AddComponent<T>() where T : new() { return new T(); }
    }
    public enum AudioRolloffMode { Logarithmic }
    public enum AudioDataLoadState { Failed, Loaded, Loading }
    public enum AudioType { MPEG, OGGVORBIS, WAV }
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
        public bool playOnAwake, loop, ignoreListenerPause;
        public float spatialBlend, minDistance, maxDistance, pitch, dopplerLevel, volume;
        public AudioRolloffMode rolloffMode;
        public AudioClip clip;
        private int cursor;
        public int timeSamples
        {
            get { if (Destroyed) throw new InvalidOperationException("Destroyed native AudioSource"); return cursor; }
            set { if (Destroyed) throw new InvalidOperationException("Destroyed native AudioSource"); cursor=value; }
        }
        public int Schedules;
        public double ScheduledAt;
        public void Stop() { timeSamples = 0; }
        public void PlayScheduled(double value) { Schedules++; ScheduledAt=value; }
    }
    public static class Mathf { public static float Clamp01(float v) { return Math.Max(0,Math.Min(v,1)); } }
    public static class AudioListener { public static bool pause; }
    public static class Time { public static float realtimeSinceStartup; }
    public static class AudioSettings
    {
        public static double dspTime;
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
