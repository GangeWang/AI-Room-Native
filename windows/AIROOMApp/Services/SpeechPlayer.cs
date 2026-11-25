using System.Runtime.InteropServices.WindowsRuntime;
using AIROOMCore;
using Windows.Media.Core;
using Windows.Media;
using Windows.Media.Playback;
using Windows.Media.SpeechSynthesis;

namespace AIROOMApp.Services;

/// <summary>
/// Synthesises each line to PCM first so the mouth follows real loudness, then
/// plays it on the default output device (OBS picks it up as desktop audio).
/// Windows counterpart of the macOS AVSpeechSynthesizer player: the platform
/// synthesizer emits a whole WAV per line instead of streaming buffers, so a
/// line starts after synthesis (~1–2 s) rather than mid-sentence; the mouth
/// curve, rate/pitch mapping and per-member voice choice match the macOS side.
/// </summary>
public sealed class SpeechPlayer : IDisposable
{
    public const double MouthFrameRate = 30.0;

    readonly MediaPlayer player = new();
    SpeechSynthesizer synthesizer = new();
    CancellationTokenSource? speaking;
    int generation;

    public sealed record VoiceOption(string Id, string Name, string Language);

    public static VoiceOption[] Voices()
    {
        try
        {
            return [.. SpeechSynthesizer.AllVoices
                .Where(voice => voice.Language.StartsWith("zh", StringComparison.OrdinalIgnoreCase)
                             || voice.Language.StartsWith("en", StringComparison.OrdinalIgnoreCase)
                             || voice.Language.StartsWith("ja", StringComparison.OrdinalIgnoreCase))
                .Select(voice => new VoiceOption(voice.Id, voice.DisplayName, voice.Language))
                .OrderBy(voice => voice.Language).ThenBy(voice => voice.Name)];
        }
        catch
        {
            return [];
        }
    }

    public void Stop()
    {
        generation += 1;
        speaking?.Cancel();
        speaking = null;
        try { player.Pause(); player.Source = null; } catch { }
    }

    /// <summary>Returns once playback finishes or Stop() is called. `onMouth`
    /// receives 0…1 openness at ~30 Hz and a final 0.</summary>
    public async Task SpeakAsync(string text, LiveCharacterStage character, InterfaceLanguage language,
        Action<double> onMouth, CancellationToken token = default)
    {
        var spoken = LiveSpeechText.Speakable(text);
        if (spoken.Length == 0) return;
        generation += 1;
        var mine = generation;
        speaking = CancellationTokenSource.CreateLinkedTokenSource(token);
        var myToken = speaking.Token;
        try
        {
            if (synthesizer.Voice?.Id != character.VoiceIdentifier)
            {
                var voice = SpeechSynthesizer.AllVoices.FirstOrDefault(item => item.Id == character.VoiceIdentifier)
                    ?? SpeechSynthesizer.AllVoices.FirstOrDefault(item => item.Language.StartsWith(
                        language == InterfaceLanguage.English ? "en-US" : "zh-TW", StringComparison.OrdinalIgnoreCase));
                synthesizer.Voice = voice ?? synthesizer.Voice;
            }
            // The stored rate is the macOS AVSpeech scale (0.3–0.65); map it onto
            // this synthesizer's multiplier so the setting keeps one meaning.
            synthesizer.Options.SpeakingRate = Math.Clamp(0.85 + (character.SpeechRate - 0.3) * (2.0 - 0.85) / (0.65 - 0.3), 0.5, 4.0);
            synthesizer.Options.AudioPitch = Math.Clamp(character.Pitch, 0.0, 2.0);

            using var result = await synthesizer.SynthesizeTextToStreamAsync(spoken);
            byte[] bytes;
            using (var reader = result.AsStreamForRead())
            {
                using var memory = new MemoryStream();
                await reader.CopyToAsync(memory, myToken);
                bytes = memory.ToArray();
            }
            if (mine != generation) return;
            var wav = ParseWav(bytes);
            if (wav is null) return;
            var (samples, rate) = wav.Value;
            var window = Math.Max(1, (int)(rate / MouthFrameRate));
            var levels = Rms(samples, window);
            var peak = levels.Length == 0 ? 0f : levels.Max();

            var playback = new MemoryStream(bytes).AsRandomAccessStream();
            player.Source = MediaSource.CreateFromStream(playback, "audio/wav");
            player.Play();

            // Mouth follows the playback position through the RMS windows.
            while (true)
            {
                if (mine != generation || myToken.IsCancellationRequested) break;
                var position = player.PlaybackSession?.Position ?? TimeSpan.Zero;
                var natural = player.PlaybackSession?.NaturalDuration ?? TimeSpan.Zero;
                if (natural > TimeSpan.Zero && position >= natural - TimeSpan.FromMilliseconds(120)) break;
                if (levels.Length > 0)
                {
                    var index = (int)(position.TotalSeconds * rate) / window;
                    index = Math.Clamp(index, 0, levels.Length - 1);
                    onMouth(LipSync.Mouth(levels[index], peak));
                }
                await Task.Delay((int)(1000 / MouthFrameRate), myToken);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            // A failed line stays silent; the live loop continues on the next event.
        }
        finally
        {
            try { player.Pause(); player.Source = null; } catch { }
            speaking?.Dispose();
            if (speaking is not null) speaking = null;
            onMouth(0);
        }
    }

    public void Dispose()
    {
        Stop();
        player.Dispose();
        synthesizer.Dispose();
    }

    static float[] Rms(short[] samples, int window)
    {
        var result = new List<float>(samples.Length / window + 1);
        for (var start = 0; start < samples.Length; start += window)
        {
            var end = Math.Min(samples.Length, start + window);
            double sum = 0;
            for (var index = start; index < end; index++) sum += (samples[index] / 32768f) * (samples[index] / 32768f);
            result.Add((float)Math.Sqrt(sum / Math.Max(1, end - start)));
        }
        return [.. result];
    }

    /// <summary>Minimal RIFF reader: fmt sample rate and 16-bit PCM samples.
    /// The platform synthesizer emits mono 16-bit WAV by default.</summary>
    static (short[] Samples, int Rate)? ParseWav(byte[] bytes)
    {
        if (bytes.Length < 44 || BitConverter.ToInt32(bytes, 0) != 0x46464952) return null; // "RIFF"
        int offset = 12;
        int rate = 16_000, bits = 16;
        while (offset + 8 <= bytes.Length - 8)
        {
            var id = BitConverter.ToInt32(bytes, offset);
            var size = BitConverter.ToInt32(bytes, offset + 4);
            if (id == 0x02074666) // "fmt "
            {
                rate = BitConverter.ToInt32(bytes, offset + 12);
                bits = BitConverter.ToUInt16(bytes, offset + 22);
            }
            else if (id == 0x61746164) // "data"
            {
                if (bits != 16) return null;
                var count = Math.Min(size, bytes.Length - offset - 8);
                var samples = new short[count / 2];
                for (var index = 0; index < samples.Length; index++)
                {
                    samples[index] = BitConverter.ToInt16(bytes, offset + 8 + index * 2);
                }
                return (samples, rate);
            }
            offset += 8 + size + (size % 2);
        }
        return null;
    }
}
