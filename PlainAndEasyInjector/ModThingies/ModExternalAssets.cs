using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using TMPro;
using UnityEngine;
using UnityEngine.Networking;

namespace SpookyBavaria
{
    public class ModExternalAssets : MonoBehaviour
    {
        private void Awake()
        {
            executingAssembly = typeof(ModExternalAssets).Assembly;
        }

        public static void Load_PNG_From_Ressource_Into_Dictionary(byte[] bytes, string name)
        {
            Texture2D tex = new Texture2D(1, 1);
            if (tex.LoadImage(bytes))
            {
                tex.name = name;
                Textures.Add(name, tex);
                Rect rect = new Rect(0, 0, tex.width, tex.height);
                Vector2 pivot = new Vector2(0.5f, 0.5f);
                Sprite newSprite = Sprite.Create(tex, rect, pivot, 100.0f);
                newSprite.name = name;
                Sprites.Add(name, newSprite);
            }
        }

        public static IEnumerator LoadAudioStream(CreateAudioOnDemand createAudioOnDemand, string filename, Action onDone)
        {
            //string url = "file://" + Path.Combine(Paths.PluginPath, "Bavaria", "Audio", filename + ".ogg");
            // SJSMHDBavarianPatcher installs these files into <data folder>/Bavaria/Audio
            string url = new Uri(Path.Combine(Mod.DataFolder, "Bavaria", "Audio", filename + ".ogg")).AbsoluteUri;
            Debug.Log(url);
            using (UnityWebRequest www = UnityWebRequestMultimedia.GetAudioClip(url, AudioType.OGGVORBIS))
            {
                // DownloadHandlerAudioClip can stream OGG directly without reading whole file into RAM
                ((DownloadHandlerAudioClip)www.downloadHandler).streamAudio = false;

                yield return www.SendWebRequest();

                if (www.result == UnityWebRequest.Result.Success)
                {
                    AudioClip clip = DownloadHandlerAudioClip.GetContent(www);
                    createAudioOnDemand.audioClips.Add(filename,clip);
                    onDone();
                }
                else
                {
                    Debug.LogError($"Failed to load audio: {www.error}");
                }
            }
        }

        //public static Dictionary<string, AudioClip> LoadLZ4Archive(Stream lz4File)
        //{
        //    using var lz4Stream = LZ4Stream.Decode(lz4File); // Decompress LZ4
        //    using var tarStream = new TarInputStream(lz4Stream, System.Text.Encoding.UTF8);

        //    TarEntry entry;
        //    Dictionary<string, AudioClip> audioclips = new Dictionary<string, AudioClip>();
        //    while ((entry = tarStream.GetNextEntry()) != null)
        //    {
        //        if (entry.IsDirectory) continue;

        //        using var memoryStream = new MemoryStream();
        //        tarStream.CopyEntryContents(memoryStream); // Copy entry data
        //        var AudClip = Load_OGG_Audio_From_AudioStream(memoryStream, entry.Name);
        //        audioclips.Add(AudClip.name, AudClip);
        //    }
        //    return audioclips;
        //}

        //public static AudioClip Load_OGG_Audio_From_AudioStream(Stream manifestResourceStream, string Name)
        //{
        //    if (manifestResourceStream == null)
        //    {
        //        Debug.LogError("Failed to load resource stream for " + Name);
        //        return null;
        //    }
        //    AudioClip audioClip2;
        //    using (VorbisReader vorbisReader = new VorbisReader(manifestResourceStream, true))
        //    {
        //        int channels = vorbisReader.Channels;
        //        int sampleRate = vorbisReader.SampleRate;
        //        long totalSamples = vorbisReader.TotalSamples;
        //        float[] array = new float[totalSamples * (long)channels];
        //        int num2;
        //        for (long num = 0L; num < totalSamples * (long)channels; num += (long)num2)
        //        {
        //            num2 = vorbisReader.ReadSamples(array, (int)num, array.Length - (int)num);
        //            if (num2 == 0)
        //            {
        //                break;
        //            }
        //        }
        //        AudioClip audioClip3 = AudioClip.Create(Path.GetFileNameWithoutExtension(Name), (int)totalSamples, channels, sampleRate, false);
        //        audioClip3.SetData(array, 0);
        //        audioClip2 = audioClip3;
        //    }
        //    return audioClip2;
        //}

        //public static AudioClip Load_OGG_Audio_From_LZ4(Stream lz4File, string Name)
        //{
        //    if (lz4File == null)
        //    {
        //        Debug.LogError("Load_OGG_Audio_From_LZ4: The provided lz4File Stream is null!");
        //        return null;
        //    }

        //    if (lz4File.CanSeek)
        //    {
        //        lz4File.Position = 0;
        //    }

        //    using var lz4Stream = LZ4Stream.Decode(lz4File);
        //    using var tarStream = new TarInputStream(lz4Stream, System.Text.Encoding.UTF8);
        //    TarEntry entry;

        //    // Clean input target name
        //    string targetName = Name?.Trim('\0', ' ', '\t', '\r', '\n')
        //                             .Replace('\\', '/')
        //                             .TrimStart('/') ?? string.Empty;

        //    // Target name without extension (e.g. "Das Jägerlied")
        //    string targetNameNoExt = Path.GetFileNameWithoutExtension(targetName);

        //    while ((entry = tarStream.GetNextEntry()) != null)
        //    {
        //        if (entry.IsDirectory) continue;

        //        string entryName = entry.Name?.Trim('\0', ' ', '\t', '\r', '\n')
        //                                     .Replace('\\', '/')
        //                                     .TrimStart('/') ?? string.Empty;

        //        // Entry name without extension (e.g. "Das Jägerlied")
        //        string entryNameNoExt = Path.GetFileNameWithoutExtension(entryName);

        //        // Match exact path, full filename, OR filename without extension
        //        bool matches = string.IsNullOrEmpty(targetName) ||
        //                       entryName.Equals(targetName, StringComparison.OrdinalIgnoreCase) ||
        //                       Path.GetFileName(entryName).Equals(Path.GetFileName(targetName), StringComparison.OrdinalIgnoreCase) ||
        //                       entryNameNoExt.Equals(targetNameNoExt, StringComparison.OrdinalIgnoreCase);

        //        if (!matches) continue;

        //        using var manifestResourceStream = new MemoryStream();
        //        tarStream.CopyEntryContents(manifestResourceStream);
        //        manifestResourceStream.Position = 0; // Reset position for VorbisReader

        //        if (manifestResourceStream.Length == 0)
        //        {
        //            Debug.LogError($"[TAR Debug] Entry '{entryName}' was matched, but stream length is 0 bytes!");
        //            return null;
        //        }

        //        using (VorbisReader vorbisReader = new VorbisReader(manifestResourceStream, false))
        //        {
        //            int channels = vorbisReader.Channels;
        //            int sampleRate = vorbisReader.SampleRate;
        //            long totalSamples = vorbisReader.TotalSamples;
        //            float[] array = new float[totalSamples * (long)channels];
        //            int readCount;

        //            for (long offset = 0L; offset < array.Length; offset += readCount)
        //            {
        //                readCount = vorbisReader.ReadSamples(array, (int)offset, array.Length - (int)offset);
        //                if (readCount == 0) break;
        //            }

        //            AudioClip audioClip = AudioClip.Create(entryNameNoExt, (int)totalSamples, channels, sampleRate, false);
        //            audioClip.SetData(array, 0);

        //            return audioClip;
        //        }
        //    }

        //    Debug.LogError($"Failed to load audio resource: '{Name}'");
        //    return null;
        //}

        //public static IEnumerator Load_OGG_Audio_From_LZ4_Coroutine(Stream lz4File, string Name, Action<AudioClip> onComplete)
        //{
        //    if (lz4File == null)
        //    {
        //        Debug.LogError("Load_OGG_Audio_From_LZ4: The provided lz4File Stream is null!");
        //        onComplete?.Invoke(null);
        //        yield break;
        //    }

        //    if (lz4File.CanSeek)
        //    {
        //        lz4File.Position = 0;
        //    }

        //    using var lz4Stream = LZ4Stream.Decode(lz4File);
        //    using var tarStream = new TarInputStream(lz4Stream, System.Text.Encoding.UTF8);
        //    TarEntry entry;

        //    string targetNameNoExt = Path.GetFileNameWithoutExtension(Name?.Trim('\0', ' ', '\t', '\r', '\n'));
        //    string tempFilePath = null;

        //    // 1. Extract matching OGG file from TAR payload to a temp file
        //    while ((entry = tarStream.GetNextEntry()) != null)
        //    {
        //        if (entry.IsDirectory) continue;

        //        string entryNameNoExt = Path.GetFileNameWithoutExtension(entry.Name?.Trim('\0', ' ', '\t', '\r', '\n'));

        //        bool matches = string.IsNullOrEmpty(targetNameNoExt) ||
        //                       entryNameNoExt.Equals(targetNameNoExt, StringComparison.OrdinalIgnoreCase);

        //        if (!matches) continue;

        //        tempFilePath = Path.Combine(Application.temporaryCachePath, $"{Guid.NewGuid()}.ogg");

        //        using (var tempFileStream = File.Create(tempFilePath))
        //        {
        //            tarStream.CopyEntryContents(tempFileStream);
        //        }
        //        break;
        //    }

        //    if (string.IsNullOrEmpty(tempFilePath) || !File.Exists(tempFilePath))
        //    {
        //        Debug.LogError($"Failed to extract audio resource: '{Name}' from TAR archive.");
        //        onComplete?.Invoke(null);
        //        yield break;
        //    }

        //    // 2. Stream OGG into Unity using UnityWebRequest
        //    string fileUrl = "file://" + tempFilePath;
        //    using (UnityWebRequest www = UnityWebRequestMultimedia.GetAudioClip(fileUrl, AudioType.OGGVORBIS))
        //    {
        //        // Set streamAudio to true for streaming off disk (saves RAM)
        //        ((DownloadHandlerAudioClip)www.downloadHandler).streamAudio = false;

        //        yield return www.SendWebRequest();

        //        if (www.result != UnityWebRequest.Result.Success)
        //        {
        //            Debug.LogError($"UnityWebRequest error loading '{Name}': {www.error}");
        //            onComplete?.Invoke(null);
        //        }
        //        else
        //        {
        //            AudioClip clip = DownloadHandlerAudioClip.GetContent(www);
        //            if (clip != null)
        //            {
        //                clip.name = targetNameNoExt;
        //            }
        //            onComplete?.Invoke(clip);
        //        }
        //    }

        //    // 3. Clean up the temporary file
        //    if (!string.IsNullOrEmpty(tempFilePath) && File.Exists(tempFilePath))
        //    {
        //        try
        //        {
        //            File.Delete(tempFilePath);
        //        }
        //        catch (Exception ex)
        //        {
        //            Debug.LogWarning($"Failed to delete temp file '{tempFilePath}': {ex.Message}");
        //        }
        //    }
        //}

        private static AudioClip Load_WAV_Audio_From_Resource_Into_Dictionary(string manifestResourceName)
        {
            Stream manifestResourceStream = executingAssembly.GetManifestResourceStream(manifestResourceName);
            if (manifestResourceStream == null)
            {
                Debug.LogError("Resource '" + manifestResourceName + "' not found in assembly.");
                return null;
            }
            byte[] array = new byte[manifestResourceStream.Length];
            manifestResourceStream.Read(array, 0, array.Length);
            manifestResourceStream.Close();
            int num = BitConverter.ToInt32(array, 24);
            int num2 = (int)BitConverter.ToInt16(array, 22);
            BitConverter.ToInt16(array, 34);
            int num3 = 44;
            float[] array2 = new float[(array.Length - num3) / 2];
            for (int i = 0; i < array2.Length; i++)
            {
                short num4 = BitConverter.ToInt16(array, num3 + i * 2);
                array2[i] = (float)num4 / 32768f;
            }
            AudioClip audioClip = AudioClip.Create("DynamicClip", array2.Length, num2, num, false);
            audioClip.SetData(array2, 0);
            return audioClip;
        }

        public static Dictionary<string, Texture2D> Textures = [];

        public static Dictionary<string, Sprite> Sprites = [];

        private static Assembly executingAssembly;
    }
}
