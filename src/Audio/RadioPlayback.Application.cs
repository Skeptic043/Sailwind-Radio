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
        private ApplicationPauseSync appPause;

        // The application this radio plays instead of music, or null. Not saved, so a loaded game plays music.
        public AudioApplication PlayingApplication => application;
        // The application's current track from its Windows media session, or null.
        internal MediaControls.Track ApplicationTrack => live == null ? null : live.Media.Current;

        // Sends a button press to the application. False when it has no media session or window to receive it.
        internal bool SendToApplication(MediaControls.Command command)
        {
            if (live == null) return false;
            if (command == MediaControls.Command.PlayPause || command == MediaControls.Command.Pause)
                appPause.ClearResume();
            return live.Media.Send(command);
        }

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
                bool resume = appPause.PausedForGame;
                StopApplication(false);
                appPause.PausedForGame = resume;
            }
            if (state.Powered && live == null) live = new ApplicationAudio(application, rate);
            Exception fault = live == null ? null : live.Fault;
            if (live != null && fault == null && state.Powered && !state.Paused)
            {
                appPause.Tick(gamePaused, Time.realtimeSinceStartup, live.Ring.Sounding,
                    live.Ring.SoundedWithin(250), live.Ring.LastSoundEndFrame, live.Media.SessionPlaying, live.Media.Send);
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
            appPause.Reset();
            ReleaseBufferedApplication();
        }

        // A radio sold, destroyed or closed with the game takes the application's signal with it, so it pauses if playing.
        private void DisposeApplication()
        {
            StopApplication(true, true);
            application = null;
        }
    }

    // Coordinates game pause/unpause with an external media app. Some players (such as Spotify) fade out on
    // Pause and can ignore a Play sent during that transition, so unpausing retries idempotent Play briefly
    // if fresh sound does not return.
    internal struct ApplicationPauseSync
    {
        private const double ResumePendingSeconds = 2.0;
        private const double ResumeRetryIntervalSeconds = 0.5;
        private const int MaxResumeRetries = 3;

        internal bool PausedForGame;
        private double resumePendingUntil, resumeRetryAt;
        private int resumeRetries;
        private long resumeSoundBaseline;

        internal void Reset()
        {
            PausedForGame = false;
            ClearResume();
        }

        internal void ClearResume()
        {
            resumePendingUntil = 0;
            resumeRetryAt = 0;
            resumeRetries = 0;
            resumeSoundBaseline = 0;
        }

        internal void Tick(bool gamePaused, double now, bool sounding, bool recentSound, long soundEndFrame,
            bool? sessionPlaying, Func<MediaControls.Command, bool> send)
        {
            if (gamePaused)
            {
                bool active = sessionPlaying.HasValue ? sessionPlaying.Value : sounding;
                if (!PausedForGame && (active || now < resumePendingUntil))
                {
                    PausedForGame = send(MediaControls.Command.Pause);
                    ClearResume();
                }
                return;
            }
            if (PausedForGame)
            {
                if (send(MediaControls.Command.Play))
                {
                    PausedForGame = false;
                    resumePendingUntil = now + ResumePendingSeconds;
                    resumeRetryAt = now + ResumeRetryIntervalSeconds;
                    resumeRetries = MaxResumeRetries;
                    resumeSoundBaseline = soundEndFrame;
                }
                return;
            }
            if (resumeRetries <= 0 || now < resumeRetryAt) return;
            // Confirmed playing: when a media session exists, require both sessionPlaying == true and recent audio
            // so a pause fade-out tail never cancels a needed Play retry.
            bool confirmedPlaying = sessionPlaying.HasValue
                ? sessionPlaying.Value && recentSound && soundEndFrame > resumeSoundBaseline
                : recentSound && soundEndFrame > resumeSoundBaseline;
            if (confirmedPlaying)
            {
                resumeRetries = 0;
                return;
            }
            if (send(MediaControls.Command.Play))
            {
                resumeRetries--;
                resumeRetryAt = now + ResumeRetryIntervalSeconds;
                resumePendingUntil = Math.Max(resumePendingUntil, now + ResumePendingSeconds);
            }
        }
    }
}
