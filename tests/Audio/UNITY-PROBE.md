# Isolated Unity editor audio probe

`Run-UnityEditorProbe.ps1` copies the production playback and state files into an ignored project under `.local/audio/unity-probe`. It launches only an owned hidden batchmode editor process, with a 1800-second default deadline. It does not launch Sailwind or modify its installation, profile or saves. It needs a licensed Unity 2019.1.10f1 editor and uses ffmpeg to generate its longer synthetic fixtures on first run.

The probe checks, with output volume at zero:

- decoding of an original PCM WAV, sample progression at time scales 1, 8 and 0.5, pause preservation, manual resume and game suspension
- streamed clip playback at 44.1, 88.2, 96, 176.4 and 192 kHz
- short WAV, MP3, FLAC, M4A and OGG tracks and a 3-frame track streaming through the production engine, with time to playing, saved-position start, real-time progression, drift repairs and main-thread cost per update (under 5 ms)
- streamed long WAV, M4A and 20- and 27-minute OGG tracks starting at a saved position and tracking real time without drift repairs
- which Unity calls trigger a streamed clip's position callback (creation, scheduled play and position writes do, while stop, pause, unpause and clip assignment do not)
- output synchronization: the actual audio content of the radio and speaker outputs is decoded from a position-encoding test signal and must match within one DSP buffer across 13 scenarios, including power on with speakers connected, late joins, pause, power cycling, track changes with and without preload and drift repair
- start-of-track gaps: every format, through a fresh open and an adopted preload, first touch and warm, must start without silent gaps, late starts or audio read before it was decoded

This establishes editor behavior only, never audible quality or acceptance in Sailwind's standalone player.

## Results

The probe passed all 190 checks, including a real music folder passed through `-MusicFolder`, on the 1.2.0 release candidate.
