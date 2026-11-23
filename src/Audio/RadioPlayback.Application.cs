using System;
using UnityEngine;

namespace SailwindRadio.Audio
{
    public sealed partial class RadioPlayback
    {
        private AudioApplication application;
        private ApplicationAudio live;
        private bool applicationWarned;
        // The radio paused the application for a game pause and resumes it afterwards. A pause the player made stays.
        private bool pausedForGame;
        private double resumePendingUntil;

        // The application this radio plays instead of music, or null. Not saved, so a loaded game plays music.
        public AudioApplication PlayingApplication => application;
        // The application's current track from its Windows media session, or null.
        internal MediaControls.Track ApplicationTrack => live == null ? null : live.Media.Current;

        // Sends a button press to the application. False when it has no media session or window to receive it.
        internal bool SendToApplication(MediaControls.Command command) => live != null && live.Media.Send(command);

        /// <summary>Play an application instead of music, or music again for null. The music position is kept, and
        /// an application switched away from is paused if it was playing so it does not carry on at its own volume.</summary>
        public void SetApplication(AudioApplication chosen)
        {
            if (disposed || (chosen == null ? application == null : chosen.Same(application))) return;
            if (application == null)
            {
                CapturePosition();
                ReleaseTrack();
                ReleasePreload();
            }
            StopApplication(false, true);
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
        // Loading only silences the radio, because islands load while the player sails.
        private void TickApplication(bool shouldPlay, bool gamePaused)
        {
            int rate = AudioSettings.outputSampleRate;
            if (live != null && !state.Powered) StopApplication(false);
            // A new output rate needs a new capture. The application is still paused for the game, so the radio still resumes it.
            else if (live != null && live.Ring.SampleRate != rate)
            {
                bool resume = pausedForGame;
                StopApplication(false);
                pausedForGame = resume;
            }
            if (state.Powered && live == null) live = new ApplicationAudio(application, rate);
            Exception fault = live == null ? null : live.Fault;
            if (live != null && fault == null && state.Powered && !state.Paused)
            {
                double now = Time.realtimeSinceStartup;
                if (gamePaused && !pausedForGame && (live.Ring.Sounding || now < resumePendingUntil))
                {
                    pausedForGame = live.Media.Send(MediaControls.Command.Pause);
                    resumePendingUntil = 0;
                }
                else if (!gamePaused && pausedForGame && live.Media.Send(MediaControls.Command.Play))
                {
                    pausedForGame = false;
                    // Cover rapid pause/unpause before the app produces sound again so a second pause still pauses it.
                    resumePendingUntil = now + 1.5;
                }
            }
            bool play = shouldPlay && live != null && fault == null;
            float rawDelay = AppBufferSeconds == null ? 0f : AppBufferSeconds();
            float delay = Single.IsNaN(rawDelay) ? 0f : Math.Min(MaximumAppBufferSeconds, Math.Max(0f, rawDelay));
            if (play) UpdateBufferedApplication(live.Ring, delay);
            else StopBufferedApplication();
            if (fault != null && !applicationWarned)
            {
                applicationWarned = true;
                warning("Unable to play " + application.Label + " through the radio: " + fault.Message);
            }
            Status = !state.Powered ? "Off" : fault != null ? "Unable to play " + application.Label :
                !live.Found ? application.Label + " is not running" : state.Paused ? "Paused" : !shouldPlay ? "Suspended" : "Playing";
        }

        private void StopApplication(bool wait, bool pause = false)
        {
            if (live != null) live.Stop(wait, pause);
            live = null;
            pausedForGame = false;
            resumePendingUntil = 0;
            ReleaseBufferedApplication();
        }

        // A radio sold, destroyed or closed with the game takes the application's signal with it, so it pauses if playing.
        private void DisposeApplication()
        {
            StopApplication(true, true);
            application = null;
        }
    }
}
