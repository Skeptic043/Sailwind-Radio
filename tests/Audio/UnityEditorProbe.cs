#if UNITY_EDITOR
using System;
using System.Collections;
using System.IO;
using UnityEditor;
using UnityEngine;
using SailwindRadio;

// Run only inside the isolated project created by Run-UnityEditorProbe.ps1.
public sealed class RadioUnityEditorProbe : MonoBehaviour
{
    private RadioPlayback playback;
    private RadioState state;
    private bool suspended;
    private int checks;
    private string report = "";
    private string output;

    [InitializeOnLoadMethod]
    private static void Register()
    {
        EditorApplication.playModeStateChanged += delegate(PlayModeStateChange mode)
        {
            if (mode == PlayModeStateChange.EnteredPlayMode && SessionState.GetBool("RadioAudioProbe", false))
                new GameObject("Radio audio probe").AddComponent<RadioUnityEditorProbe>();
        };
    }

    public static void Begin()
    {
        SessionState.SetBool("RadioAudioProbe", true);
        EditorApplication.isPlaying = true;
    }

    private IEnumerator Start()
    {
        output = Path.GetFullPath(Path.Combine(Application.dataPath, "../probe-result.txt"));
        report = "Unity editor audio probe " + Application.unityVersion + Environment.NewLine;
        string fixture = Path.GetFullPath(Path.Combine(Application.dataPath, "../original-tone.wav"));
        WriteTone(fixture);
        new GameObject("Probe listener").AddComponent<AudioListener>();
        state = new RadioState { TrackPath = fixture, Powered = true, Volume = 0 };
        playback = new RadioPlayback(this, state, message => report += "WARNING: " + message + Environment.NewLine);
        float deadline = Time.realtimeSinceStartup + 20;
        while (playback.Status != "Playing" && Time.realtimeSinceStartup < deadline)
            yield return null;
        if (!Check(playback.Status == "Playing", "original PCM WAV decoded")) yield break;
        foreach (float scale in new[] { 1f, 8f, 0.5f })
        {
            Time.timeScale = scale;
            yield return new WaitForSecondsRealtime(0.15f);
            double before = state.PositionSeconds;
            float started = Time.realtimeSinceStartup;
            yield return new WaitForSecondsRealtime(0.5f);
            playback.CapturePosition();
            double elapsed = Time.realtimeSinceStartup - started;
            double moved = state.PositionSeconds - before;
            if (!Check(Math.Abs(moved - elapsed) < 0.18, "timeScale=" + scale + " real=" + elapsed + " samples=" + moved)) yield break;
        }
        state.Paused = true;
        yield return null;
        double paused = state.PositionSeconds;
        yield return new WaitForSecondsRealtime(0.3f);
        if (!Check(Math.Abs(state.PositionSeconds - paused) < 1.0 / 48000, "manual pause preserves sample position")) yield break;
        suspended = true;
        yield return null;
        suspended = false;
        yield return new WaitForSecondsRealtime(0.2f);
        if (!Check(playback.Status == "Paused" && state.PositionSeconds == paused, "suspension cannot undo manual pause")) yield break;
        state.Paused = false;
        yield return new WaitForSecondsRealtime(0.3f);
        if (!Check(state.PositionSeconds > paused + 0.1, "resume advances from retained position")) yield break;
        suspended = true;
        yield return null;
        paused = state.PositionSeconds;
        yield return new WaitForSecondsRealtime(0.2f);
        if (!Check(state.PositionSeconds == paused, "game suspension retains position")) yield break;
        suspended = false;
        yield return new WaitForSecondsRealtime(0.3f);
        if (!Check(state.PositionSeconds > paused + 0.1, "game suspension resumes active radio")) yield break;
        playback.Dispose();
        playback = null;
        Finish(true);
    }

    private void Update() { if (playback != null) playback.Tick(Vector3.zero, suspended); }
    private void OnDestroy() { if (playback != null) playback.Dispose(); }
    private bool Check(bool passed, string detail)
    {
        report += (passed ? "PASS " : "FAIL ") + detail + Environment.NewLine;
        if (!passed) Finish(false);
        else checks++;
        return passed;
    }
    private void Finish(bool passed)
    {
        Time.timeScale = 1;
        SessionState.SetBool("RadioAudioProbe", false);
        File.WriteAllText(output, report + "Checks=" + checks + Environment.NewLine + "Result=" + (passed ? "PASS" : "FAIL"));
        EditorApplication.Exit(passed ? 0 : 2);
    }
    private static void WriteTone(string path)
    {
        const int rate = 48000, samples = rate * 8;
        using (var writer = new BinaryWriter(File.Create(path)))
        {
            writer.Write(System.Text.Encoding.ASCII.GetBytes("RIFF")); writer.Write(36 + samples * 2);
            writer.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt ")); writer.Write(16);
            writer.Write((short)1); writer.Write((short)1); writer.Write(rate); writer.Write(rate * 2);
            writer.Write((short)2); writer.Write((short)16);
            writer.Write(System.Text.Encoding.ASCII.GetBytes("data")); writer.Write(samples * 2);
            for (int i=0; i<samples; i++) writer.Write((short)(Math.Sin(i * 220 * Math.PI * 2 / rate) * 1500));
        }
    }
}
#endif
