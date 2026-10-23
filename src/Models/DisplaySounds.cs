using System;
using System.Collections.Generic;
using SailwindRadio.Audio;
using UnityEngine;

namespace SailwindRadio.Models
{
    // Mechanism takes for the regional displays, decoded once at startup from the embedded WAV files.
    // Groups follow the file name before its dash, such as flap-03.wav.
    internal sealed class DisplaySoundBank
    {
        private const string Prefix = "SailwindRadio.DisplaySounds.";
        // Across all radios. Each radio's own pool caps it at its pool size.
        internal readonly VoiceLedger<AudioSource> Voices = new VoiceLedger<AudioSource>(12);
        private readonly Dictionary<string, AudioClip[]> groups = new Dictionary<string, AudioClip[]>(StringComparer.Ordinal);
        private readonly Dictionary<string, int> last = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly System.Random random = new System.Random();
        private readonly Func<float> volume;

        // Mechanism volume, read live so Config Manager edits apply at once.
        internal float Volume => volume();

        internal DisplaySoundBank(Func<float> volume, Action<string> warn)
        {
            this.volume = volume;
            var assembly = typeof(DisplaySoundBank).Assembly;
            var found = new Dictionary<string, List<AudioClip>>(StringComparer.Ordinal);
            var names = new List<string>(assembly.GetManifestResourceNames());
            names.Sort(StringComparer.Ordinal);
            foreach (string name in names)
            {
                if (!name.StartsWith(Prefix, StringComparison.Ordinal) || !name.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
                    continue;
                string file = name.Substring(Prefix.Length);
                int dash = file.IndexOf('-');
                string group = dash > 0 ? file.Substring(0, dash) : file.Substring(0, file.Length - 4);
                try
                {
                    using (var decoder = WavPcmDecoder.Open(assembly.GetManifestResourceStream(name)))
                    {
                        var samples = new float[decoder.Frames * decoder.Channels];
                        int read = 0, got;
                        while (read < samples.Length && (got = decoder.Read(samples, read, samples.Length - read)) > 0)
                            read += got;
                        var clip = AudioClip.Create("Radio display " + file, read / decoder.Channels, decoder.Channels, decoder.SampleRate, false);
                        clip.SetData(samples, 0);
                        if (!found.TryGetValue(group, out var list))
                            found[group] = list = new List<AudioClip>();
                        list.Add(clip);
                    }
                }
                catch (Exception error) { warn("Radio display sound " + file + " could not be loaded: " + error.Message); }
            }
            foreach (var pair in found)
                groups[pair.Key] = pair.Value.ToArray();
        }

        internal float Range(float min, float max) => min + (float)random.NextDouble() * (max - min);

        // A random take of the group, never the one played just before.
        internal AudioClip Pick(string group)
        {
            if (!groups.TryGetValue(group, out var takes) || takes.Length == 0)
                return null;
            int index;
            if (!last.TryGetValue(group, out int previous) || takes.Length == 1)
                index = random.Next(takes.Length);
            else
            {
                index = random.Next(takes.Length - 1);
                if (index >= previous)
                    index++;
            }
            last[group] = index;
            return takes[index];
        }
    }

    // One radio's display voices at the display centre: a small one-shot pool and the scroll's run loop.
    // Voice lifetimes are audio durations, which play in real time whatever the game's time scale, so the
    // voice clock is unscaled. The displays schedule on scaled time and fall silent whenever it stops.
    internal sealed class DisplaySoundPlayer : IDisposable
    {
        private const int PoolSize = 6;
        private const float FadeSeconds = .15f, MinDistance = .25f, MaxDistance = 12f;
        private readonly DisplaySoundBank bank;
        private readonly List<AudioSource> pool = new List<AudioSource>();
        private readonly AudioSource loop;
        private readonly float loopGain;
        private readonly SoundCoalescer coalescer = new SoundCoalescer();
        private string coalescedGroup;
        private float coalescedCents;
        private bool looping, loopActive;
        private float fade;
        private double voicesEnd;
        private AudioSource fadingVoice;
        private AudioClip fadingClip;
        private float fadingVolume, voiceFade;
        private readonly Transform origin;
        // 1 while native 3D rolloff applies. Heard flat from the head, the same logarithmic rolloff by hand.
        private float hearing = 1f;
        private bool flat;

        internal DisplaySoundPlayer(Transform parent, Vector3 localCentre, DisplaySoundBank bank, float loopDecibels)
        {
            this.bank = bank;
            loopGain = Gain(loopDecibels);
            var emitter = new GameObject("Radio display sounds");
            emitter.transform.SetParent(parent, false);
            emitter.transform.localPosition = localCentre;
            origin = emitter.transform;
            for (int i = 0; i < PoolSize; i++)
                pool.Add(Source(emitter, false));
            loop = Source(emitter, true);
            loop.clip = bank.Pick("run");
        }

        private static AudioSource Source(GameObject emitter, bool looped)
        {
            var source = emitter.AddComponent<AudioSource>();
            source.playOnAwake = false;
            source.loop = looped;
            source.spatialBlend = 1f;
            source.rolloffMode = AudioRolloffMode.Logarithmic;
            source.minDistance = MinDistance;
            source.maxDistance = MaxDistance;
            source.dopplerLevel = 0f;
            // Matches the Radio's speakers: kept out of a modded port's global reverb.
            source.bypassReverbZones = true;
            return source;
        }

        internal static float Gain(float decibels) => Mathf.Pow(10f, decibels / 20f);

        // The boat camera's listener moves too fast to pan the voices, so they play flat at the volume the head would hear.
        internal void HearFrom(bool fromHead, Vector3 head)
        {
            if (!origin || (!fromHead && !flat))
                return;
            if (flat != fromHead)
            {
                flat = fromHead;
                foreach (var source in pool)
                    source.spatialBlend = flat ? 0f : 1f;
                loop.spatialBlend = flat ? 0f : 1f;
            }
            hearing = flat ? MinDistance / Mathf.Clamp(Vector3.Distance(origin.position, head), MinDistance, MaxDistance) : 1f;
        }

        // Voices may still sound, a loop runs or fades, or a coalesced burst waits for its window.
        internal bool Busy => loopActive || coalescer.Pending || fadingVoice || (voicesEnd > 0 && Sounding());

        private bool Sounding()
        {
            if (Time.unscaledTime < voicesEnd)
                return true;
            voicesEnd = 0;
            return false;
        }

        internal AudioSource Play(string group, float decibels, float cents)
        {
            var clip = bank.Pick(group);
            if (!clip)
                return null;
            float pitch = Mathf.Pow(2f, cents / 1200f);
            double now = Time.unscaledTime, length = clip.length / pitch;
            var source = bank.Voices.Claim(this, pool, now, length, out var stolen);
            if (stolen)
                stolen.Stop();
            source.Stop();
            source.clip = clip;
            source.pitch = pitch;
            source.volume = Gain(decibels) * bank.Volume * hearing;
            source.Play();
            voicesEnd = Math.Max(voicesEnd, now + length);
            return source;
        }

        // Fades a still sounding voice out over the loop fade, such as an eraser take longer than its pass.
        internal void FadeOut(AudioSource source)
        {
            if (!source || !source.isPlaying)
                return;
            fadingVoice = source;
            fadingClip = source.clip;
            fadingVolume = source.volume;
            voiceFade = 1f;
        }

        // A burst of one sound becomes one voice per window. Take and pitch are chosen per started voice.
        internal void Coalesce(string group, float decibels, float centsRange)
        {
            coalescedGroup = group;
            coalescedCents = centsRange;
            if (coalescer.Add(Time.unscaledTime, decibels, out float start))
                Play(group, start, bank.Range(-centsRange, centsRange));
        }

        internal void StartLoop()
        {
            if (!loop.clip || (looping && loopActive))
                return;
            looping = loopActive = true;
            fade = 1f;
            loop.volume = loopGain * bank.Volume * hearing;
            if (!loop.isPlaying)
                loop.Play();
        }

        internal void EndLoop() => looping = false;

        // Starts a waiting burst, fades a finished run loop and follows the live mechanism volume.
        internal void Tick()
        {
            if (coalescer.Pending && coalescer.Flush(Time.unscaledTime, out float start))
                Play(coalescedGroup, start, bank.Range(-coalescedCents, coalescedCents));
            if (fadingVoice)
            {
                voiceFade -= Time.unscaledDeltaTime / FadeSeconds;
                // A voice stolen for another sound is left alone.
                if (fadingVoice.clip != fadingClip)
                    fadingVoice = null;
                else if (voiceFade <= 0f || !fadingVoice.isPlaying)
                {
                    fadingVoice.Stop();
                    fadingVoice = null;
                }
                else
                    fadingVoice.volume = fadingVolume * voiceFade;
            }
            if (!loopActive)
                return;
            if (!looping)
                fade -= Time.unscaledDeltaTime / FadeSeconds;
            if (fade <= 0f)
            {
                loop.Stop();
                loopActive = false;
                return;
            }
            loop.volume = loopGain * fade * bank.Volume * hearing;
        }

        // Stops every voice at once, for pause, sleep, a slot, a box or shop stock.
        internal void Silence()
        {
            for (int i = 0; i < pool.Count; i++)
                pool[i].Stop();
            loop.Stop();
            looping = loopActive = false;
            fadingVoice = null;
            coalescer.Clear();
            voicesEnd = 0;
            bank.Voices.Release(this);
        }

        public void Dispose() => bank.Voices.Release(this);
    }
}
