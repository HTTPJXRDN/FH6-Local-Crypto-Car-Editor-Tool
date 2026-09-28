using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;

namespace ClipdScissorTool;

public readonly record struct EndpointOffset(Vector3 StartTranslation, Vector3 EndTranslation,
    Quaternion StartRotationOffset, Quaternion EndRotationOffset)
{
    public EndpointOffset(Vector3 startTranslation, Vector3 endTranslation,
        Vector3 startRotationDegrees, Vector3 endRotationDegrees)
        : this(startTranslation, endTranslation, ClipdEndpointCodec.RotationFromDegrees(startRotationDegrees),
            ClipdEndpointCodec.RotationFromDegrees(endRotationDegrees)) { }
}

public sealed class DecodedClip
{
    public required ulong[] BoneHashes { get; init; }
    public required (Vector3 Position, Quaternion Rotation, Vector3 Scale)[][] Tracks { get; init; }
    public required int Samples { get; init; }
    public required float Duration { get; init; }
}

// Edits ACL keyframes, not GR2 files. Based on the verified headlight CLIPD
// experiment: decompress aligned ACL data, edit sampled transforms, recompress,
// then rebuild only the selected BSI curve record.
public static class ClipdEndpointCodec
{
    const ulong AnimationHash = 0xEAA09FC7E4E9009E;
    const ulong DataHash = 0xCBE977A85A54EB5E;

    [DllImport("forzatech_acl", EntryPoint = "acl_decompress_pose_samples", CallingConvention = CallingConvention.Cdecl)]
    private static extern int DecompressNative(nint compressed, int compressedSize, uint[] boneHashes,
        int boneCount, int sampleCount, float duration, [Out] float[] output, int outputLength);

    public static DecodedClip Decode(Channel channel)
    {
        var fields = AclFields(channel.Curve.LeafBytes);
        byte[] acl = ReadVector(fields[0]);
        ulong[] hashes = ReadHashes(fields[6]);
        int samples = checked((int)BitConverter.ToUInt32(fields[9]));
        float duration = BitConverter.ToSingle(fields[10]);
        if (hashes.Length == 0 || hashes.Length > 128 || samples < 2 || samples > 4096 ||
            !float.IsFinite(duration) || duration <= 0 || acl.Length < 32 ||
            BitConverter.ToUInt32(acl, 0) != acl.Length || BitConverter.ToUInt32(acl, 8) != 0xAC11AC11)
            throw new InvalidDataException("This animation does not have a supported ACL transform clip.");
        int expected = checked(hashes.Length * samples * 10);
        float[] values = new float[expected];
        nint allocation = Marshal.AllocHGlobal(acl.Length + 16);
        try
        {
            nint aligned = (nint)(((long)allocation + 15L) & ~15L);
            Marshal.Copy(acl, 0, aligned, acl.Length);
            int result = DecompressNative(aligned, acl.Length, hashes.Select(h => (uint)h).ToArray(),
                hashes.Length, samples, duration, values, values.Length);
            if (result != expected)
                throw new InvalidDataException($"ACL decompression failed (code {result}). No animation was changed.");
        }
        finally { Marshal.FreeHGlobal(allocation); }

        var tracks = new (Vector3 Position, Quaternion Rotation, Vector3 Scale)[hashes.Length][];
        for (int track = 0; track < hashes.Length; track++)
        {
            tracks[track] = new (Vector3, Quaternion, Vector3)[samples];
            for (int sample = 0; sample < samples; sample++)
            {
                int i = (sample * hashes.Length + track) * 10;
                var rotation = new Quaternion(values[i + 3], values[i + 4], values[i + 5], values[i + 6]);
                if (values.Skip(i).Take(10).Any(v => !float.IsFinite(v)) || rotation.LengthSquared() < 1e-12f)
                    throw new InvalidDataException("Decoded animation contains an invalid pose.");
                tracks[track][sample] = (new Vector3(values[i], values[i + 1], values[i + 2]),
                    Quaternion.Normalize(rotation), new Vector3(values[i + 7], values[i + 8], values[i + 9]));
            }
        }
        return new DecodedClip { BoneHashes = hashes, Tracks = tracks, Samples = samples, Duration = duration };
    }

    public static byte[] RebuildCurve(Channel channel, DecodedClip clip, int trackIndex, EndpointOffset offset)
    {
        if (trackIndex < 0 || trackIndex >= clip.Tracks.Length)
            throw new ArgumentOutOfRangeException(nameof(trackIndex));
        Vector3[] vectors = [offset.StartTranslation, offset.EndTranslation];
        if (vectors.Any(v => !float.IsFinite(v.X) || !float.IsFinite(v.Y) || !float.IsFinite(v.Z)))
            throw new InvalidDataException("Endpoint offsets must be finite numbers.");
        if (!ValidRotation(offset.StartRotationOffset) || !ValidRotation(offset.EndRotationOffset))
            throw new InvalidDataException("Endpoint rotations must be valid.");
        // Constant needle translations are stored as raw floats in some ACL clips.
        // Patch those floats directly so the channel keeps its exact byte length and
        // its other tracks/rotations remain untouched. Fall back to recompression
        // for animated translations or clips with a different ACL layout.
        if (TryPatchConstantTranslation(channel, clip, trackIndex, offset, out byte[]? patched))
            return patched;
        var tracks = clip.Tracks.Select(track => track.ToArray()).ToArray();
        Quaternion startRotation = Quaternion.Normalize(offset.StartRotationOffset);
        Quaternion endRotation = Quaternion.Normalize(offset.EndRotationOffset);
        for (int sample = 0; sample < clip.Samples; sample++)
        {
            float blend = (float)sample / (clip.Samples - 1);
            var pose = tracks[trackIndex][sample];
            Vector3 position = pose.Position + Vector3.Lerp(offset.StartTranslation, offset.EndTranslation, blend);
            Quaternion rotation = Quaternion.Normalize(Quaternion.Slerp(startRotation, endRotation, blend) * pose.Rotation);
            tracks[trackIndex][sample] = (position, rotation, pose.Scale);
        }
        byte[] acl = Compress(tracks, clip);
        byte[] curve = ReplaceAcl(channel.Curve.LeafBytes, acl);
        // Validate both BSI wrapper and the newly compressed ACL before the UI
        // replaces a channel. The outer CLIPD remains untouched on failure.
        var check = new Channel { Desc = channel.Desc, Curve = new Node
        {
            TypeHash = (byte[])channel.Curve.TypeHash.Clone(), IsLeaf = true, LeafBytes = curve
        } };
        DecodedClip decoded = Decode(check);
        if (decoded.Samples != clip.Samples || decoded.Tracks.Length != clip.Tracks.Length ||
            !decoded.BoneHashes.SequenceEqual(clip.BoneHashes))
            throw new InvalidDataException("Recompressed animation has inconsistent tracks or samples.");
        for (int sampleIndex = 0; sampleIndex < clip.Samples; sampleIndex += clip.Samples - 1)
        {
            var want = tracks[trackIndex][sampleIndex];
            var got = decoded.Tracks[trackIndex][sampleIndex];
            float positionError = Vector3.Distance(want.Position, got.Position);
            float rotationSimilarity = MathF.Abs(Quaternion.Dot(want.Rotation, got.Rotation));
            if (positionError > 0.005f || rotationSimilarity < 0.999f)
                throw new InvalidDataException($"Recompressed endpoint {sampleIndex} differs too much (position error {positionError:F4}m, rotation similarity {rotationSimilarity:F4}; requested {want.Position}, got {got.Position}).");
        }
        return curve;
    }

    static bool TryPatchConstantTranslation(Channel channel, DecodedClip clip, int trackIndex,
        EndpointOffset offset, out byte[] patched)
    {
        patched = Array.Empty<byte>();
        if (offset.StartTranslation != offset.EndTranslation ||
            !IsIdentity(offset.StartRotationOffset) || !IsIdentity(offset.EndRotationOffset)) return false;
        Vector3 original = clip.Tracks[trackIndex][0].Position;
        if (clip.Tracks[trackIndex].Any(pose => pose.Position != original)) return false;

        byte[] needle = new byte[12];
        BitConverter.TryWriteBytes(needle.AsSpan(0, 4), original.X);
        BitConverter.TryWriteBytes(needle.AsSpan(4, 4), original.Y);
        BitConverter.TryWriteBytes(needle.AsSpan(8, 4), original.Z);
        int at = channel.Curve.LeafBytes.AsSpan().IndexOf(needle);
        if (at < 0 || channel.Curve.LeafBytes.AsSpan(at + 1).IndexOf(needle) >= 0) return false;

        patched = (byte[])channel.Curve.LeafBytes.Clone();
        Vector3 desired = original + offset.StartTranslation;
        BitConverter.TryWriteBytes(patched.AsSpan(at, 4), desired.X);
        BitConverter.TryWriteBytes(patched.AsSpan(at + 4, 4), desired.Y);
        BitConverter.TryWriteBytes(patched.AsSpan(at + 8, 4), desired.Z);
        var check = new Channel { Desc = channel.Desc, Curve = new Node
        {
            TypeHash = (byte[])channel.Curve.TypeHash.Clone(), IsLeaf = true, LeafBytes = patched
        } };
        DecodedClip decoded = Decode(check);
        if (decoded.Samples != clip.Samples || !decoded.BoneHashes.SequenceEqual(clip.BoneHashes))
            throw new InvalidDataException("In-place needle edit changed the animation structure.");
        for (int track = 0; track < clip.Tracks.Length; track++)
        for (int sample = 0; sample < clip.Samples; sample++)
        {
            var before = clip.Tracks[track][sample];
            var after = decoded.Tracks[track][sample];
            Vector3 expected = before.Position + (track == trackIndex ? offset.StartTranslation : Vector3.Zero);
            if (Vector3.Distance(expected, after.Position) > 0.00001f ||
                MathF.Abs(Quaternion.Dot(before.Rotation, after.Rotation)) < 0.999999f ||
                Vector3.Distance(before.Scale, after.Scale) > 0.00001f)
                throw new InvalidDataException("In-place needle edit affected another pose; no change was applied.");
        }
        return true;
    }

    static bool IsIdentity(Quaternion q) => MathF.Abs(Quaternion.Dot(q, Quaternion.Identity)) > 0.9999999f;

    static bool ValidRotation(Quaternion q) => float.IsFinite(q.X) && float.IsFinite(q.Y) &&
        float.IsFinite(q.Z) && float.IsFinite(q.W) && q.LengthSquared() > 1e-12f;

    public static Quaternion RotationFromDegrees(Vector3 degrees) => Quaternion.Normalize(
        Quaternion.CreateFromYawPitchRoll(Degrees(degrees.Y), Degrees(degrees.X), Degrees(degrees.Z)));
    static float Degrees(float value) => value * MathF.PI / 180f;

    public static Vector3 RotationToDegrees(Quaternion rotation)
    {
        var matrix = Matrix4x4.CreateFromQuaternion(Quaternion.Normalize(rotation));
        float pitch = MathF.Asin(Math.Clamp(-matrix.M32, -1f, 1f));
        float yaw, roll;
        if (MathF.Abs(MathF.Cos(pitch)) < 1e-4f)
        {
            yaw = MathF.Atan2(-matrix.M13, matrix.M11);
            roll = 0;
        }
        else
        {
            yaw = MathF.Atan2(matrix.M31, matrix.M33);
            roll = MathF.Atan2(matrix.M12, matrix.M22);
        }
        const float toDegrees = 180f / MathF.PI;
        return new Vector3(pitch * toDegrees, yaw * toDegrees, roll * toDegrees);
    }

    static byte[] Compress((Vector3 Position, Quaternion Rotation, Vector3 Scale)[][] tracks, DecodedClip clip)
    {
        string exe = Path.Combine(AppContext.BaseDirectory, "acl_compressor.exe");
        if (!File.Exists(exe)) throw new FileNotFoundException("Bundled ACL compressor is missing.", exe);
        string parent = Path.Combine(Path.GetTempPath(), "FH6LocalCryptoTool", "acl-edits");
        string work = Path.Combine(parent, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            string sjson = Path.Combine(work, "animation.acl.sjson");
            string output = Path.Combine(work, "animation.acl");
            string config = Path.Combine(work, "precision.config.sjson");
            File.WriteAllText(sjson, WriteSjson(tracks, clip), new UTF8Encoding(false));
            File.WriteAllText(config, """
                version = 2
                algorithm_name = "uniformly_sampled"
                level = "Highest"
                rotation_format = "quatf_drop_w_variable"
                translation_format = "vector3f_full"
                scale_format = "vector3f_full"
                regression_error_threshold = 0.001
                """, new UTF8Encoding(false));
            using var process = Process.Start(new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                ArgumentList = { $"-acl={sjson}", $"-config={config}", $"-out={output}" }
            }) ?? throw new InvalidOperationException("ACL compressor could not start.");
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(30000))
            {
                process.Kill(entireProcessTree: true);
                throw new TimeoutException("ACL compression took too long.");
            }
            string stdout = stdoutTask.GetAwaiter().GetResult();
            string stderr = stderrTask.GetAwaiter().GetResult();
            if (process.ExitCode != 0 || !File.Exists(output))
                throw new InvalidDataException($"ACL compression failed: {stdout} {stderr}");
            byte[] acl = File.ReadAllBytes(output);
            if (acl.Length < 32 || BitConverter.ToUInt32(acl, 0) != acl.Length ||
                BitConverter.ToUInt32(acl, 8) != 0xAC11AC11 ||
                BitConverter.ToUInt32(acl, 16) != tracks.Length ||
                BitConverter.ToUInt32(acl, 20) != clip.Samples)
                throw new InvalidDataException("Compressor returned an invalid ACL animation.");
            return acl;
        }
        finally
        {
            string allowed = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string resolved = Path.GetFullPath(work);
            if (resolved.StartsWith(allowed, StringComparison.OrdinalIgnoreCase) && Directory.Exists(resolved))
                Directory.Delete(resolved, recursive: true);
        }
    }

    static string WriteSjson((Vector3 Position, Quaternion Rotation, Vector3 Scale)[][] tracks, DecodedClip clip)
    {
        var s = new StringBuilder();
        float rate = (clip.Samples - 1) / clip.Duration;
        s.AppendLine("version = 5\nclip =\n{");
        s.AppendLine("    name = \"edited_clipd_channel\"");
        s.AppendLine($"    num_samples = {clip.Samples}");
        s.AppendLine($"    sample_rate = {F(rate)}");
        s.AppendLine("    is_binary_exact = false\n}");
        s.AppendLine("settings =\n{\n    algorithm_name = \"uniformly_sampled\"\n    rotation_format = \"quatf_drop_w_variable\"\n    translation_format = \"vector3f_full\"\n    scale_format = \"vector3f_full\"\n    error_threshold = 0.00001\n}");
        s.AppendLine("bones =\n[");
        for (int track = 0; track < tracks.Length; track++)
        {
            s.AppendLine("    {");
            s.AppendLine($"        name = \"track_{track}\"");
            s.AppendLine("        parent = \"\"\n        vertex_distance = 100.0");
            s.AppendLine("        bind_rotation = [ 0, 0, 0, 1 ]\n        bind_translation = [ 0, 0, 0 ]\n        bind_scale = [ 1, 1, 1 ]");
            s.AppendLine("    }");
        }
        s.AppendLine("]\ntracks =\n[");
        for (int track = 0; track < tracks.Length; track++)
        {
            s.AppendLine("    {");
            s.AppendLine($"        name = \"track_{track}\"");
            s.AppendLine("        rotations =\n        [");
            foreach (var frame in tracks[track]) s.AppendLine($"            [ {F(frame.Rotation.X)}, {F(frame.Rotation.Y)}, {F(frame.Rotation.Z)}, {F(frame.Rotation.W)} ]");
            s.AppendLine("        ]\n        translations =\n        [");
            foreach (var frame in tracks[track]) s.AppendLine($"            [ {F(frame.Position.X)}, {F(frame.Position.Y)}, {F(frame.Position.Z)} ]");
            s.AppendLine("        ]\n        scales =\n        [");
            foreach (var frame in tracks[track]) s.AppendLine($"            [ {F(frame.Scale.X)}, {F(frame.Scale.Y)}, {F(frame.Scale.Z)} ]");
            s.AppendLine("        ]\n    }");
        }
        s.AppendLine("]");
        return s.ToString();
    }
    static string F(float f) => f.ToString("R", CultureInfo.InvariantCulture);

    static byte[] ReplaceAcl(byte[] curve, byte[] acl)
    {
        var outer = ReadFields(curve);
        var (outerHash, animationFields) = ReadObject(outer[0]);
        if (outerHash != AnimationHash || animationFields.Count == 0)
            throw new InvalidDataException("Unexpected ACL animation wrapper.");
        var (innerHash, fields) = ReadObject(animationFields[0]);
        if (innerHash != DataHash || fields.Count < 13) throw new InvalidDataException("Unexpected ACL data object.");
        fields[0] = Join(BitConverter.GetBytes((uint)acl.Length), acl);
        animationFields[0] = WriteObject(innerHash, fields);
        outer[0] = WriteObject(outerHash, animationFields);
        return Join(outer.Select(Field).ToArray());
    }

    static List<byte[]> AclFields(byte[] curve)
    {
        var outer = ReadFields(curve);
        if (outer.Count == 0) throw new InvalidDataException("CLIPD channel has no ACL data.");
        var (outerHash, animationFields) = ReadObject(outer[0]);
        if (outerHash != AnimationHash || animationFields.Count == 0)
            throw new InvalidDataException("CLIPD channel is not a supported ACL animation.");
        var (innerHash, fields) = ReadObject(animationFields[0]);
        if (innerHash != DataHash || fields.Count < 13)
            throw new InvalidDataException("CLIPD channel has an unsupported ACL data layout.");
        return fields;
    }
    static byte[] ReadVector(byte[] data)
    {
        if (data.Length < 4 || BitConverter.ToUInt32(data, 0) != data.Length - 4)
            throw new InvalidDataException("ACL vector length is invalid.");
        return data[4..];
    }
    static ulong[] ReadHashes(byte[] data)
    {
        if (data.Length < 4 || BitConverter.ToUInt32(data, 0) != (data.Length - 4) / 8 ||
            (data.Length - 4) % 8 != 0)
            throw new InvalidDataException("ACL bone hashes are malformed.");
        return Enumerable.Range(0, (data.Length - 4) / 8)
            .Select(i => BitConverter.ToUInt64(data, 4 + i * 8)).ToArray();
    }
    static (ulong Hash, List<byte[]> Fields) ReadObject(byte[] data)
    {
        if (data.Length < 12 || BitConverter.ToUInt32(data, 0) != data.Length - 12)
            throw new InvalidDataException("CLIPD object length is invalid.");
        return (BitConverter.ToUInt64(data, 4), ReadFields(data[12..]));
    }
    static List<byte[]> ReadFields(byte[] data)
    {
        var fields = new List<byte[]>();
        for (int p = 0; p < data.Length;)
        {
            if (p > data.Length - 4) throw new InvalidDataException("Truncated CLIPD field.");
            int len = checked((int)BitConverter.ToUInt32(data, p));
            p += 4;
            if (len > data.Length - p) throw new InvalidDataException("CLIPD field is out of bounds.");
            fields.Add(data.AsSpan(p, len).ToArray());
            p += len;
        }
        return fields;
    }
    static byte[] WriteObject(ulong hash, IEnumerable<byte[]> fields)
    {
        byte[] body = Join(fields.Select(Field).ToArray());
        return Join(BitConverter.GetBytes((uint)body.Length), BitConverter.GetBytes(hash), body);
    }
    static byte[] Field(byte[] bytes) => Join(BitConverter.GetBytes((uint)bytes.Length), bytes);
    static byte[] Join(params byte[][] parts)
    {
        byte[] joined = new byte[parts.Sum(p => p.Length)];
        int cursor = 0;
        foreach (byte[] part in parts) { Buffer.BlockCopy(part, 0, joined, cursor, part.Length); cursor += part.Length; }
        return joined;
    }
}
