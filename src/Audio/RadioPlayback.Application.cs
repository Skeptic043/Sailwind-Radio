using System;
using UnityEngine;

namespace SailwindRadio.Audio
{
    public sealed partial class RadioPlayback
    {
        private AudioApplication application;
        private ApplicationAudio live;
        private AudioClip carrier;
        private bool applicationWarned;
        // The radio paused the application for a game pause and resumes it afterwards. A pause the player made stays.
        private bool pausedForGame;

        // The application this radio plays instead of music, or null. Not saved, so a loaded game plays music.
        public AudioApplication PlayingApplication => application;
        // The application's current track from its Windows media session, or null.
        internal MediaControls.Track ApplicationTrack => live == null ? null : live.Media.Current;

        // Sends a button press to the application. False when it has no media session to receive it.
        internal bool SendToApplication(MediaControls.Command command) => live != null && live.Media.Send(command);

        /// <summary>Play an application instead of music, or music again for null. The music position is kept.</summary>
        public void SetApplication(AudioApplication chosen)
        {
            if (disposed || (chosen == null ? application == null : chosen.Same(application))) return;
            if (application == null)
            {
                CapturePosition();
                ReleaseTrack();
                ReleasePreload();
            }
            StopApplication(false);
            application = chosen;
            applicationWarned = false;
            failure = null;
            FailedPath = null;
            TrackEnded = false;
            // Music reopens at its saved position on the next Tick.
            requestedPath = null;
            TrackLabel = chosen == null ? "No track" : chosen.Label;
        }

        // Capture runs only while the radio is on, so turning it off gives the application its volume back. While the
        // game is paused the application is paused too, as music is, so nothing plays on unheard and the radio resumes in step.
        private void TickApplication(bool shouldPlay, bool gamePaused)
        {
            int rate = AudioSettings.outputSampleRate;
            if (live != null && (!state.Powered || live.Ring.SampleRate != rate)) StopApplication(false);
            if (state.Powered && live == null) live = new ApplicationAudio(application, rate);
            Exception fault = live == null ? null : live.Fault;
            if (live != null && fault == null && state.Powered && !state.Paused)
            {
                if (gamePaused && !pausedForGame && live.Ring.Sounding) pausedForGame = live.Media.Send(MediaControls.Command.Pause);
                else if (!gamePaused && pausedForGame && live.Media.Send(MediaControls.Command.Play)) pausedForGame = false;
            }
            bool play = shouldPlay && live != null && fault == null;
            PlayCarrier(builtIn, play);
            foreach (var endpoint in endpoints.Values) PlayCarrier(endpoint, play);
            if (fault != null && !applicationWarned)
            {
                applicationWarned = true;
                warning("Unable to play " + application.Label + " through the radio: " + fault.Message);
            }
            Status = !state.Powered ? "Off" : fault != null ? "Unable to play " + application.Label :
                !live.Found ? application.Label + " is not running" : state.Paused ? "Paused" : !shouldPlay ? "Suspended" : "Playing";
        }

        private void PlayCarrier(RadioSpeakerOutput output, bool play)
        {
            if (output.Source == null || output.Input == null) return;
            if (!play)
            {
                output.Stop();
                return;
            }
            if (output.Source.clip != Carrier())
            {
                output.Stop(true);
                output.Source.clip = Carrier();
                output.Source.loop = true;
            }
            var reader = output.Input.Reader;
            if (reader == null || reader.Ring != live.Ring) output.Input.Reader = new ApplicationRing.Reader(live.Ring);
            if (!output.Source.isPlaying) output.Source.Play();
            output.Running = true;
        }

        // A constant half on both channels. Unity pans it, and the output's input filter replaces it with the application.
        // Unity fills a clip that is not streamed once, at creation.
        private AudioClip Carrier()
        {
            if (carrier == null)
                carrier = AudioClip.Create("Sailwind Radio application carrier", 4096, 2, AudioSettings.outputSampleRate, false,
                    data => { for (int i = 0; i < data.Length; i++) data[i] = .5f; }, null);
            return carrier;
        }

        private void StopApplication(bool wait)
        {
            if (live != null) live.Stop(wait);
            live = null;
            pausedForGame = false;
            ReleaseCarrier(builtIn);
            foreach (var endpoint in endpoints.Values) ReleaseCarrier(endpoint);
        }

        private void ReleaseCarrier(RadioSpeakerOutput output)
        {
            if (output == null || output.Source == null || carrier == null || output.Source.clip != carrier) return;
            output.Stop(true);
            output.Source.loop = false;
            if (output.Input != null) output.Input.Reader = null;
        }

        private void DisposeApplication()
        {
            StopApplication(true);
            application = null;
            if (carrier != null) UnityEngine.Object.Destroy(carrier);
            carrier = null;
        }
    }
}
