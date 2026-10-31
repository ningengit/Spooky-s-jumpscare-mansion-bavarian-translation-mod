using System.Globalization;
using AssetsTools.NET;
using AssetsTools.NET.Extra;

namespace SJSMHDBavarianPatcher;

public sealed class Patcher
{
    /// <summary>
    /// null = bundles are compressed again the way the original was (LZ4 stays LZ4, LZMA stays LZMA).
    /// AssetBundleCompressionType.LZ4 is much faster to write but gives slightly bigger bundles than LZMA.
    /// </summary>
    public static AssetBundleCompressionType? BundleCompressionOverride = null;

    private readonly string dataDir;
    private readonly string classDataPath;
    private readonly BackupStore backups;

    public Patcher(string dataDir, string classDataPath)
    {
        this.dataDir = dataDir;
        this.classDataPath = classDataPath;
        backups = new BackupStore(dataDir);
    }

    /// <summary>Patches every file that has mods. Independent files are processed in parallel.</summary>
    public int Apply(IReadOnlyList<Mod> mods)
    {
        var groups = mods.GroupBy(m => m.GameFile, StringComparer.OrdinalIgnoreCase).ToList();
        int failures = 0;

        var options = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Math.Min(Environment.ProcessorCount, 4)) };
        Parallel.ForEach(groups, options, group =>
        {
            Log.Begin();
            try
            {
                Log.Line(group.Key);
                ApplyToFile(group.Key, group.ToList());
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref failures);
                Log.Line($"  FAILED: {ex.Message}");
                Log.Line("  This file was left in its original state.");
            }
            finally
            {
                Log.End();
            }
        });

        backups.Save();
        return failures;
    }

    public int Revert() => backups.Revert();

    /// <summary>Prints the current value of a field (the "getter" from the command line).</summary>
    public string Get(string relFile, string? inner, long pathId, string field)
    {
        string fullPath = PathUtil.Combine(dataDir, relFile);
        if (!File.Exists(fullPath)) throw new FileNotFoundException(fullPath + " not found");

        var am = NewManager();
        try
        {
            var target = new Target(am, fullPath, inner != null, backups);
            return target.Describe(inner, pathId, field);
        }
        finally
        {
            am.UnloadAllAssetsFiles(true);
            am.UnloadAllBundleFiles();
        }
    }

    private AssetsManager NewManager()
    {
        var am = new AssetsManager();
        am.LoadClassPackage(classDataPath);

        // Fields of MonoBehaviour scripts (for example TextMeshPro's m_text) are only described by the game's own
        // DLLs when the file has no stored type tree. This is what UABEA does when it asks for the Managed folder.
        string managed = Path.Combine(dataDir, "Managed");
        if (Directory.Exists(managed))
            am.MonoTempGenerator = new MonoCecilTempGenerator(managed);

        return am;
    }

    private void ApplyToFile(string relFile, List<Mod> mods)
    {
        string fullPath = PathUtil.Combine(dataDir, relFile);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException($"{fullPath} not found");

        bool isBundle = mods[0].Inner != null;
        if (mods.Any(m => (m.Inner != null) != isBundle))
            throw new ModException("mods for the same file must either all use 'inner:' (bundle) or none");

        backups.PrepareOriginal(fullPath);

        var am = NewManager();
        Target? target = null;
        List<(string Temp, string Final)> outputs = new();
        try
        {
            target = new Target(am, fullPath, isBundle, backups);
            foreach (Mod mod in mods)
            {
                try
                {
                    target.Apply(mod);
                }
                catch (Exception ex)
                {
                    throw new ModException($"{mod.Description}: {ex.Message}");
                }
                Log.Line($"  ok  {mod.Description}");
            }

            outputs = target.WriteTemporaryFiles();
        }
        finally
        {
            // release the file handles so the originals can be replaced
            am.UnloadAllAssetsFiles(true);
            am.UnloadAllBundleFiles();
        }

        try
        {
            foreach (var (temp, final) in outputs)
            {
                File.Move(temp, final, true);
                backups.RecordPatched(final);
            }
        }
        finally
        {
            // whatever is left (after a failure, or the uncompressed intermediate of a compressed bundle)
            if (target != null)
                foreach (string t in target.TempFiles)
                    if (File.Exists(t)) File.Delete(t);
        }
    }
}

/// <summary>Progress of the bundle compression, printed in steps of 25 %.</summary>
internal sealed class BundleProgress : IAssetBundleCompressProgress
{
    private readonly string name;
    private int lastStep = -1;

    public BundleProgress(string name) => this.name = name;

    public void SetProgress(float progress)
    {
        float percent = progress <= 1.0f ? progress * 100f : progress;
        int step = (int)(percent / 25f);
        if (step == lastStep || step > 4) return;
        lastStep = step;
        Log.Status($"  [{name}] compressing ... {Math.Min(100, step * 25)}%");
    }
}

/// <summary>Everything we do to one game file (a loose assets file, or a bundle with assets files inside).</summary>
internal sealed class Target
{
    private sealed class Session
    {
        public required string Name { get; init; }
        public required AssetsFileInstance Inst { get; init; }
        public Dictionary<long, AssetTypeValueField> Fields { get; } = new();
        public HashSet<long> Dirty { get; } = new();
    }

    private sealed record StreamUser(long PathId, AssetTypeValueField Offset, AssetTypeValueField Size);

    // asset types that keep their data in .resS / .resource files, and what their location fields are called
    private static readonly (int ClassId, string Prefix, string Source, string Offset, string Size)[] StreamTypes =
    {
        (83,  "m_Resource",          "m_Source", "m_Offset", "m_Size"), // AudioClip
        (329, "m_ExternalResources", "m_Source", "m_Offset", "m_Size"), // VideoClip
        (28,  "m_StreamData",        "path",     "offset",   "size"),   // Texture2D
        (89,  "m_StreamData",        "path",     "offset",   "size"),   // Cubemap
        (117, "m_StreamData",        "path",     "offset",   "size"),   // Texture3D
        (187, "m_StreamData",        "path",     "offset",   "size"),   // Texture2DArray
        (188, "m_StreamData",        "path",     "offset",   "size"),   // CubemapArray
        (43,  "m_StreamData",        "path",     "offset",   "size"),   // Mesh
    };

    private readonly AssetsManager am;
    private readonly string filePath;
    private readonly BackupStore backups;
    private readonly BundleFileInstance? bundle;
    private readonly Dictionary<string, Session> sessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, byte[]> resources = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> modifiedResources = new(StringComparer.OrdinalIgnoreCase);

    public List<string> TempFiles { get; } = new();

    public Target(AssetsManager am, string filePath, bool isBundle, BackupStore backups)
    {
        this.am = am;
        this.filePath = filePath;
        this.backups = backups;
        if (isBundle)
            bundle = am.LoadBundleFile(filePath, true); // true = unpack compressed bundles into memory
    }

    // ------------------------------------------------------------------ applying mods

    public void Apply(Mod mod)
    {
        Session s = GetSession(mod.Inner);

        if (mod.Wav != null)
        {
            ApplyAudio(s, mod);
            return;
        }

        if (mod.Png != null)
        {
            ApplyTexture(s, mod);
            return;
        }

        AssetTypeValueField field = Resolve(GetField(s, mod.PathId), mod.Field);
        mod.Apply!(field);
        s.Dirty.Add(mod.PathId);
    }

    public string Describe(string? inner, long pathId, string fieldPath)
    {
        Session s = GetSession(inner);
        return DescribeField(Resolve(GetField(s, pathId), fieldPath));
    }

    private void ApplyTexture(Session s, Mod mod)
    {
        AssetFileInfo info = s.Inst.file.GetAssetInfo(mod.PathId)
            ?? throw new ModException($"no asset with path ID {mod.PathId} in {s.Name}");
        if (info.TypeId != (int)AssetClassID.Texture2D)
            throw new ModException("that asset is not a Texture2D");

        AssetTypeValueField tex = GetField(s, mod.PathId);
        EncodedTexture enc = TextureImporter.Encode(tex, PathUtil.Local(mod.Png!));

        AssetTypeValueField pathField = tex["m_StreamData.path"];
        AssetTypeValueField sizeField = tex["m_StreamData.size"];
        AssetTypeValueField offsetField = tex["m_StreamData.offset"];
        bool streamed = !pathField.IsDummy && !sizeField.IsDummy && !offsetField.IsDummy
                        && !string.IsNullOrEmpty(pathField.AsString)
                        && FieldNumbers.Get(sizeField) > 0;

        if (streamed)
        {
            // the pixels live in a .resS file: put the new data in place of the old (no unused bytes are left behind)
            string resName = Path.GetFileName(pathField.AsString);
            ReplaceInResource(s, resName, FieldNumbers.Get(offsetField), FieldNumbers.Get(sizeField), enc.Data, mod.PathId);
            TextureImporter.SetFields(tex, enc);
            FieldNumbers.Set(sizeField, enc.Data.Length);
        }
        else
        {
            TextureImporter.SetFields(tex, enc);
            TextureImporter.SetInline(tex, enc);
        }

        s.Dirty.Add(mod.PathId);
        Log.Line("      " + enc.Summary + (streamed ? "" : "  (stored inside the assets file)"));
    }

    /// <summary>
    /// Replaces [offset, offset + size) of a resource file with new data and moves everything that follows,
    /// updating the location of every asset in this assets file that streams from the same resource.
    /// </summary>
    private void ReplaceInResource(Session s, string resName, long offset, long size, byte[] newData, long excludePathId)
    {
        byte[] res = GetResource(resName);
        long oldEnd = offset + size;
        if (offset < 0 || oldEnd > res.Length)
            throw new ModException($"the data range ({offset} + {size}) is outside {resName} ({res.Length} bytes)");

        List<StreamUser> users = FindStreamUsers(s, resName, excludePathId);
        foreach (StreamUser u in users)
        {
            long o = FieldNumbers.Get(u.Offset), osize = FieldNumbers.Get(u.Size);
            if (o < oldEnd && o + osize > offset)
                throw new ModException($"asset {u.PathId} shares this data, which is not supported");
        }

        var later = users.Where(u => FieldNumbers.Get(u.Offset) >= oldEnd).ToList();
        long tailStart = later.Count > 0
            ? later.Min(u => FieldNumbers.Get(u.Offset))
            : Math.Min((oldEnd + 15) & ~15L, res.Length);
        if (tailStart > res.Length)
            throw new ModException($"an asset points past the end of {resName}");

        long newRegion = (newData.Length + 15) & ~15L;   // new data, padded to 16 bytes
        long newTailStart = offset + newRegion;
        long delta = newTailStart - tailStart;

        // [before] [new data + padding] [everything after the old data]
        byte[] newRes = new byte[newTailStart + (res.Length - tailStart)];
        Array.Copy(res, 0, newRes, 0, offset);
        Array.Copy(newData, 0, newRes, offset, newData.Length);
        Array.Copy(res, tailStart, newRes, newTailStart, res.Length - tailStart);

        foreach (StreamUser u in later)
        {
            FieldNumbers.Set(u.Offset, FieldNumbers.Get(u.Offset) + delta);
            s.Dirty.Add(u.PathId);
        }

        resources[resName] = newRes;
        modifiedResources.Add(resName);
    }

    private List<StreamUser> FindStreamUsers(Session s, string resName, long excludePathId)
    {
        var users = new List<StreamUser>();
        foreach (var t in StreamTypes)
        {
            foreach (AssetFileInfo other in s.Inst.file.GetAssetsOfType((AssetClassID)t.ClassId))
            {
                if (other.PathId == excludePathId) continue;

                AssetTypeValueField? of = null;
                try
                {
                    of = s.Fields.TryGetValue(other.PathId, out AssetTypeValueField? cached)
                        ? cached
                        : am.GetBaseField(s.Inst, other);
                }
                catch
                {
                    // an asset we cannot read cannot be one that we know to use this resource
                }
                if (of == null || of.IsDummy) continue;

                AssetTypeValueField src = of[t.Prefix + "." + t.Source];
                AssetTypeValueField off = of[t.Prefix + "." + t.Offset];
                AssetTypeValueField size = of[t.Prefix + "." + t.Size];
                if (src.IsDummy || off.IsDummy || size.IsDummy) continue;
                if (Path.GetFileName(src.AsString ?? "") != resName) continue;

                s.Fields[other.PathId] = of; // keep it, its offset may have to change
                users.Add(new StreamUser(other.PathId, off, size));
            }
        }
        return users;
    }

    private void ApplyAudio(Session s, Mod mod)
    {
        AssetFileInfo info = s.Inst.file.GetAssetInfo(mod.PathId)
            ?? throw new ModException($"no asset with path ID {mod.PathId} in {s.Name}");
        if (info.TypeId != (int)AssetClassID.AudioClip)
            throw new ModException("that asset is not an AudioClip");

        string wavPath = PathUtil.Local(mod.Wav!);
        if (!File.Exists(wavPath))
            throw new ModException($"WAV file not found: {wavPath}");

        byte[] wav = File.ReadAllBytes(wavPath);
        if (!AudioConverter.TryParseAudio(wav, out int channels, out int sampleRate, out byte[] pcm, out string error))
            throw new ModException($"{Path.GetFileName(wavPath)}: {error}");

        byte[] fsb = AudioConverter.BuildPcm16Fsb5(pcm, channels, sampleRate);
        AudioConverter.Validate(fsb);

        AssetTypeValueField clip = GetField(s, mod.PathId);
        AssetTypeValueField Need(string path)
        {
            AssetTypeValueField f = clip[path];
            if (f.IsDummy) throw new ModException($"the AudioClip has no field {path}");
            return f;
        }

        AssetTypeValueField srcField = clip["m_Resource.m_Source"];
        if (srcField.IsDummy)
            throw new ModException("this clip stores its audio inline (m_AudioData), which is not supported");
        string source = srcField.AsString;
        if (string.IsNullOrEmpty(source))
            throw new ModException("this clip has an empty m_Resource.m_Source");

        // works for "sharedassets0.resource" and for "archive:/.../Name.resource"
        string resName = Path.GetFileName(source);

        ulong oldOffset = Need("m_Resource.m_Offset").AsULong;
        ulong oldSize = Need("m_Resource.m_Size").AsULong;
        long oldEnd = (long)(oldOffset + oldSize);

        byte[] res = GetResource(resName);
        if (oldEnd > res.Length)
            throw new ModException($"the clip's range ({oldOffset} + {oldSize}) is outside {resName} ({res.Length} bytes)");

        // every AudioClip / VideoClip in this assets file that keeps its data in the same resource file
        var users = new List<(long PathId, AssetTypeValueField Offset)>();
        foreach (var (classId, prefix) in new[] { (AssetClassID.AudioClip, "m_Resource"), (AssetClassID.VideoClip, "m_ExternalResources") })
        {
            foreach (AssetFileInfo other in s.Inst.file.GetAssetsOfType(classId))
            {
                if (other.PathId == mod.PathId) continue;

                AssetTypeValueField of = GetField(s, other.PathId);
                AssetTypeValueField oSrc = of[prefix + ".m_Source"];
                AssetTypeValueField oOff = of[prefix + ".m_Offset"];
                if (oSrc.IsDummy || oOff.IsDummy) continue;
                if (Path.GetFileName(oSrc.AsString ?? "") != resName) continue;

                long o = (long)oOff.AsULong;
                if (o >= (long)oldOffset && o < oldEnd)
                    throw new ModException($"asset {other.PathId} shares this clip's audio data, which is not supported");

                users.Add((other.PathId, oOff));
            }
        }

        // data that starts after this clip has to move; its start is the lowest offset that follows this clip
        var later = users.Where(u => (long)u.Offset.AsULong >= oldEnd).ToList();
        long tailStart = later.Count > 0
            ? later.Min(u => (long)u.Offset.AsULong)
            : Math.Min((oldEnd + 15) & ~15L, res.Length);
        if (tailStart > res.Length)
            throw new ModException($"an asset points past the end of {resName}");

        long newRegion = (fsb.Length + 15) & ~15L;     // new audio, padded to 16 bytes
        long newTailStart = (long)oldOffset + newRegion;
        long delta = newTailStart - tailStart;

        // [before the clip] [new audio + padding] [everything after the old clip]
        byte[] newRes = new byte[newTailStart + (res.Length - tailStart)];
        Array.Copy(res, 0, newRes, 0, (long)oldOffset);
        Array.Copy(fsb, 0, newRes, (long)oldOffset, fsb.Length);
        Array.Copy(res, tailStart, newRes, newTailStart, res.Length - tailStart);

        foreach (var u in later)
        {
            u.Offset.AsULong = (ulong)((long)u.Offset.AsULong + delta);
            s.Dirty.Add(u.PathId);
        }

        resources[resName] = newRes;
        modifiedResources.Add(resName);

        int sampleCount = pcm.Length / (2 * channels);
        Need("m_Frequency").AsInt = sampleRate;
        Need("m_Channels").AsInt = channels;
        Need("m_Length").AsFloat = (float)sampleCount / sampleRate;
        Need("m_CompressionFormat").AsInt = 0; // PCM
        Need("m_Resource.m_Size").AsULong = (ulong)fsb.Length;
        if (!clip["m_BitsPerSample"].IsDummy) clip["m_BitsPerSample"].AsInt = 16;
        if (!clip["m_SubsoundIndex"].IsDummy) clip["m_SubsoundIndex"].AsInt = 0; // the new FSB has exactly one sound
        s.Dirty.Add(mod.PathId);
    }

    // ------------------------------------------------------------------ loading

    private Session GetSession(string? inner)
    {
        string name = inner ?? Path.GetFileName(filePath);
        if (sessions.TryGetValue(name, out Session? existing)) return existing;

        AssetsFileInstance inst;
        if (bundle == null)
        {
            inst = am.LoadAssetsFile(filePath, false);
        }
        else
        {
            byte[] data = ReadBundleEntry(name)
                ?? throw new ModException($"'{name}' is not inside this bundle. Entries: {string.Join(", ", EntryNames())}");
            inst = am.LoadAssetsFile(new MemoryStream(data), Path.Combine(bundle.path, name), false);
            inst.parentBundle = bundle;
        }

        LoadTypeData(inst);

        var s = new Session { Name = name, Inst = inst };
        sessions[name] = s;
        return s;
    }

    private void LoadTypeData(AssetsFileInstance inst)
    {
        string version = inst.file.Metadata.UnityVersion;
        if (version == "0.0.0" && inst.parentBundle != null)
            version = inst.parentBundle.file.Header.EngineVersion;
        if (version == "0.0.0") return; // rely on the type tree stored in the file

        try { am.LoadClassDatabaseFromPackage(version); }
        catch { /* fine when the file has its own type tree */ }
    }

    private AssetTypeValueField GetField(Session s, long pathId)
    {
        if (s.Fields.TryGetValue(pathId, out AssetTypeValueField? cached)) return cached;

        AssetFileInfo info = s.Inst.file.GetAssetInfo(pathId)
            ?? throw new ModException($"no asset with path ID {pathId} in {s.Name}");

        AssetTypeValueField? field = am.GetBaseField(s.Inst, info);
        if (field == null || field.IsDummy)
            throw new ModException($"asset {pathId} could not be read (type information missing?)");

        s.Fields[pathId] = field;
        return field;
    }

    private static AssetTypeValueField Resolve(AssetTypeValueField root, string path)
    {
        if (string.IsNullOrEmpty(path)) return root;

        AssetTypeValueField cur = root;
        foreach (string part in path.Split('.'))
        {
            if (int.TryParse(part, out int index))
            {
                if (index < 0 || index >= cur.Children.Count)
                    throw new ModException($"index {index} is out of range in '{path}' ({cur.Children.Count} elements)");
                cur = cur.Children[index];
            }
            else
            {
                AssetTypeValueField parent = cur;
                cur = parent[part];
                if (cur.IsDummy)
                {
                    string available = string.Join(", ", parent.Children.Take(40).Select(c => c.TemplateField.Name));
                    throw new ModException(
                        $"field '{part}' (in '{path}') does not exist. Fields available there: {available}. " +
                        "For script (MonoBehaviour) fields the game's Managed folder has to be readable.");
                }
            }
        }
        return cur;
    }

    private static string DescribeField(AssetTypeValueField f)
    {
        string type = f.TemplateField.Type;
        switch (type)
        {
            case "string": return $"string \"{f.AsString}\"";
            case "bool": return $"bool {f.AsBool}";
            case "int":
            case "SInt32": return $"{type} {f.AsInt}";
            case "unsigned int": return $"{type} {f.AsUInt}";
            case "float": return $"float {f.AsFloat.ToString(CultureInfo.InvariantCulture)}";
            case "double": return $"double {f.AsDouble.ToString(CultureInfo.InvariantCulture)}";
            case "UInt64":
            case "FileSize": return $"{type} {f.AsULong}";
            case "SInt64": return $"{type} {f.AsLong}";
            default:
                if (f.Children.Count == 0) return type;
                return $"{type} with {f.Children.Count} children: " +
                       string.Join(", ", f.Children.Take(30).Select(c => c.TemplateField.Name));
        }
    }

    // ------------------------------------------------------------------ resource files (.resource / .resS)

    private string ResourcePath(string resName) => PathUtil.Combine(Path.GetDirectoryName(filePath)!, resName);

    private byte[] GetResource(string resName)
    {
        if (resources.TryGetValue(resName, out byte[]? cached)) return cached;

        byte[] data;
        if (bundle != null)
        {
            data = ReadBundleEntry(resName)
                ?? throw new ModException($"resource '{resName}' is not inside this bundle. Entries: {string.Join(", ", EntryNames())}");
        }
        else
        {
            string path = ResourcePath(resName);
            if (!File.Exists(path))
                throw new ModException($"resource file {path} not found");
            backups.PrepareOriginal(path);
            data = File.ReadAllBytes(path);
        }

        resources[resName] = data;
        return data;
    }

    private IEnumerable<string> EntryNames()
        => bundle!.file.BlockAndDirInfo.DirectoryInfos.Select(d => d.Name);

    private byte[]? ReadBundleEntry(string name)
    {
        foreach (AssetBundleDirectoryInfo d in bundle!.file.BlockAndDirInfo.DirectoryInfos)
        {
            if (d.Name == name)
            {
                AssetsFileReader reader = bundle.file.DataReader;
                reader.Position = d.Offset;
                return reader.ReadBytes((int)d.DecompressedSize);
            }
        }
        return null;
    }

    // ------------------------------------------------------------------ writing

    /// <summary>Hands every changed asset of an assets file to the library; it writes them when the file is saved.</summary>
    private static void ApplyChanges(Session s)
    {
        foreach (long pathId in s.Dirty)
        {
            AssetFileInfo info = s.Inst.file.GetAssetInfo(pathId)!;
            info.SetNewData(s.Fields[pathId]);
        }
    }

    /// <summary>Replaces the content of one entry of the bundle (an assets file or a resource file).</summary>
    private void SetEntryData(string name, byte[] data)
    {
        foreach (AssetBundleDirectoryInfo d in bundle!.file.BlockAndDirInfo.DirectoryInfos)
        {
            if (d.Name == name)
            {
                // if your version has no SetNewData(byte[]) here, use:  d.Replacer = new ContentReplacerFromBuffer(data);
                d.SetNewData(data);
                return;
            }
        }
        throw new ModException($"'{name}' is not inside this bundle. Entries: {string.Join(", ", EntryNames())}");
    }

    /// <summary>Writes every result next to its target as "*.tmp"; the caller moves them into place.</summary>
    public List<(string Temp, string Final)> WriteTemporaryFiles()
    {
        var outputs = new List<(string Temp, string Final)>();

        string TempFor(string final, string suffix)
        {
            string t = final + suffix;
            TempFiles.Add(t);
            return t;
        }

        if (bundle == null)
        {
            Session s = sessions.Values.First();
            if (s.Dirty.Count > 0)
            {
                ApplyChanges(s);
                string temp = TempFor(filePath, ".patched.tmp");
                using (var fs = File.Open(temp, FileMode.Create))
                using (var w = new AssetsFileWriter(fs))
                    s.Inst.file.Write(w);
                outputs.Add((temp, filePath));
            }

            foreach (string name in modifiedResources)
            {
                string final = ResourcePath(name);
                string temp = TempFor(final, ".patched.tmp");
                File.WriteAllBytes(temp, resources[name]);
                outputs.Add((temp, final));
            }
        }
        else
        {
            foreach (Session s in sessions.Values.Where(x => x.Dirty.Count > 0))
            {
                ApplyChanges(s);
                byte[] data;
                using (var ms = new MemoryStream())
                {
                    using (var w = new AssetsFileWriter(ms)) // disposing the writer closes ms, ToArray still works
                        s.Inst.file.Write(w);
                    data = ms.ToArray();
                }
                SetEntryData(s.Name, data);
            }

            foreach (string name in modifiedResources)
                SetEntryData(name, resources[name]);

            // the bundle was unpacked to be edited, so this first result is uncompressed
            string plainTemp = TempFor(filePath, ".plain.tmp");
            using (var fs = File.Open(plainTemp, FileMode.Create))
            using (var w = new AssetsFileWriter(fs))
                bundle.file.Write(w);

            AssetBundleCompressionType compression = Patcher.BundleCompressionOverride ?? bundle.originalCompression;
            if (compression == AssetBundleCompressionType.None)
            {
                outputs.Add((plainTemp, filePath));
            }
            else
            {
                // pack it again like the original was, otherwise the file ends up several times bigger
                string packedTemp = TempFor(filePath, ".patched.tmp");
                string label = Path.GetFileName(filePath);
                Log.Status($"  [{label}] compressing ({compression}) ...");

                BundleFileInstance plain = am.LoadBundleFile(plainTemp, false);
                using (var fs = File.Open(packedTemp, FileMode.Create))
                using (var w = new AssetsFileWriter(fs))
                    // false = block index at the START of the file, like Unity writes it (the original bundles look like that)
                    plain.file.Pack(w, compression, false, new BundleProgress(label));

                // AssetsTools marks blocks it stored uncompressed as LZ4; Unity then reads garbage (e.g. FMOD errors for sounds)
                int fixedBlocks = BundleBlockFixer.Fix(packedTemp);
                if (fixedBlocks > 0) Log.Line($"  [{label}] fixed {fixedBlocks} wrongly marked block(s)");

                outputs.Add((packedTemp, filePath));
            }
        }

        return outputs;
    }
}
