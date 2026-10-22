// Minimal editor for Android's compiled binary XML (AndroidManifest.xml inside an APK). It does
// exactly one job: register wbmods.PhoneImportActivity, with intent filters for opening and
// sharing mod files, inside <application>. No general-purpose AXML library is pulled in for this.
//
// Format, as far as this needs it: an 8-byte file header, then chunks (u16 type, u16 headerSize,
// u32 size): one string pool, one resource map (attribute resource ids, parallel to the first
// strings of the pool), then one chunk per XML node. New strings are APPENDED to the pool, so no
// existing string index changes and every other chunk can be copied through untouched. The one
// attribute the manifest doesn't already use (android:mimeType) needs a resource id, and ids are
// looked up by string index - so the resource map is padded with zeros ("no id") up to the new
// string's index instead of renumbering anything.

using System.Text;

namespace ApkPatcher;

public static class AxmlEditor
{
    public const string ImportActivityClass = "wbmods.PhoneImportActivity";

    private const ushort ChunkStringPool = 0x0001;
    private const ushort ChunkResourceMap = 0x0180;
    private const ushort ChunkStartElement = 0x0102;
    private const ushort ChunkEndElement = 0x0103;
    private const uint NoIndex = 0xFFFFFFFF;
    private const string AndroidNs = "http://schemas.android.com/apk/res/android";

    private const uint AttrTheme = 0x01010000;
    private const uint AttrLabel = 0x01010001;
    private const uint AttrName = 0x01010003;
    private const uint AttrExported = 0x01010010;
    private const uint AttrMimeType = 0x01010026;
    private const uint ThemeNoDisplay = 0x01030055;

    private const byte TypeReference = 0x01;
    private const byte TypeString = 0x03;
    private const byte TypeBoolean = 0x12;

    private static readonly string[] MimeTypes =
        ["application/zip", "application/x-zip-compressed", "application/json", "application/octet-stream"];

    public static bool HasImportActivity(byte[] manifest) =>
        ReadStrings(manifest, out _, out _, out _).Contains(ImportActivityClass);

    /// <summary>Returns the manifest with the import activity registered. Throws InvalidDataException
    /// if the file isn't shaped the way this expects - the caller treats that as "skip this part".</summary>
    public static byte[] AddImportActivity(byte[] manifest)
    {
        List<string> strings = ReadStrings(manifest, out int poolStart, out int poolSize, out bool utf8);
        int mapStart = poolStart + poolSize;
        if (ReadU16(manifest, mapStart) != ChunkResourceMap)
        {
            throw new InvalidDataException("resource map not where expected");
        }
        int mapSize = (int)ReadU32(manifest, mapStart + 4);
        var resourceIds = new List<uint>();
        for (int i = mapStart + 8; i < mapStart + mapSize; i += 4)
        {
            resourceIds.Add(ReadU32(manifest, i));
        }

        uint Str(string value)
        {
            int index = strings.IndexOf(value);
            if (index < 0)
            {
                strings.Add(value);
                index = strings.Count - 1;
            }
            return (uint)index;
        }

        // An attribute NAME must be the string whose index carries that attribute's resource id.
        uint Attr(string name, uint resourceId)
        {
            int index = resourceIds.IndexOf(resourceId);
            if (index >= 0) return (uint)index;
            strings.Add(name);
            index = strings.Count - 1;
            while (resourceIds.Count < index) resourceIds.Add(0);
            resourceIds.Add(resourceId);
            return (uint)index;
        }

        // Walk the node chunks: find where <application> ends and what its label is.
        int nodesStart = mapStart + mapSize;
        int applicationEnd = -1;
        (byte Type, uint Data)? label = null;
        int applicationName = strings.IndexOf("application");
        for (int pos = nodesStart; pos + 8 <= manifest.Length;)
        {
            ushort type = ReadU16(manifest, pos);
            int size = (int)ReadU32(manifest, pos + 4);
            if (size < 8) throw new InvalidDataException("bad chunk size");
            if (type == ChunkStartElement && ReadU32(manifest, pos + 20) == applicationName)
            {
                int attributeCount = ReadU16(manifest, pos + 28);
                for (int a = 0; a < attributeCount; a++)
                {
                    int at = pos + 36 + (a * 20);
                    uint nameIndex = ReadU32(manifest, at + 4);
                    if (nameIndex < resourceIds.Count && resourceIds[(int)nameIndex] == AttrLabel)
                    {
                        label = (manifest[at + 15], ReadU32(manifest, at + 16));
                    }
                }
            }
            else if (type == ChunkEndElement && ReadU32(manifest, pos + 20) == applicationName)
            {
                applicationEnd = pos;
            }
            pos += size;
        }
        if (applicationName < 0 || applicationEnd < 0)
        {
            throw new InvalidDataException("no <application> element");
        }

        uint ns = Str(AndroidNs);
        var nodes = new MemoryStream();

        void Start(string element, params (uint Name, uint Raw, byte Type, uint Data)[] attributes)
        {
            var w = new BinaryWriter(nodes);
            w.Write(ChunkStartElement);
            w.Write((ushort)16);
            w.Write((uint)(36 + (attributes.Length * 20)));
            w.Write((uint)1);       // line number
            w.Write(NoIndex);       // comment
            w.Write(NoIndex);       // element namespace
            w.Write(Str(element));
            w.Write((ushort)20);    // attribute start
            w.Write((ushort)20);    // attribute size
            w.Write((ushort)attributes.Length);
            w.Write((ushort)0);     // id / class / style attribute indices: none
            w.Write((ushort)0);
            w.Write((ushort)0);
            foreach (var (name, raw, type, data) in attributes)
            {
                w.Write(ns);
                w.Write(name);
                w.Write(raw);
                w.Write((ushort)8);
                w.Write((byte)0);
                w.Write(type);
                w.Write(data);
            }
        }

        void End(string element)
        {
            var w = new BinaryWriter(nodes);
            w.Write(ChunkEndElement);
            w.Write((ushort)16);
            w.Write((uint)24);
            w.Write((uint)1);
            w.Write(NoIndex);
            w.Write(NoIndex);
            w.Write(Str(element));
        }

        (uint, uint, byte, uint) StringAttr(uint name, string value)
        {
            uint index = Str(value);
            return (name, index, TypeString, index);
        }

        uint nameAttr = Attr("name", AttrName);
        uint mimeAttr = Attr("mimeType", AttrMimeType);

        // Attributes must be in ascending resource-id order: theme, label, name, exported.
        var activityAttributes = new List<(uint, uint, byte, uint)>
        {
            (Attr("theme", AttrTheme), NoIndex, TypeReference, ThemeNoDisplay),
        };
        if (label is not null)
        {
            activityAttributes.Add((Attr("label", AttrLabel), NoIndex, label.Value.Type, label.Value.Data));
        }
        activityAttributes.Add(StringAttr(nameAttr, ImportActivityClass));
        activityAttributes.Add((Attr("exported", AttrExported), NoIndex, TypeBoolean, 0xFFFFFFFF));

        Start("activity", activityAttributes.ToArray());
        // Exact zip/json types for "Open with". Sharing is looser on purpose: when several files
        // are shared together the sending app labels them with one common type, and file managers
        // differ - Android's own Files app says application/zip, others say application/* or */*,
        // and the game was then simply missing from the share sheet (reported by a player). The
        // activity itself skips anything that isn't a .zip or .json.
        foreach (var (action, extraMimes) in new[]
        {
            ("android.intent.action.VIEW", Array.Empty<string>()),
            ("android.intent.action.SEND", new[] { "application/*" }),
            ("android.intent.action.SEND_MULTIPLE", new[] { "application/*", "*/*" }),
        })
        {
            Start("intent-filter");
            Start("action", StringAttr(nameAttr, action));
            End("action");
            Start("category", StringAttr(nameAttr, "android.intent.category.DEFAULT"));
            End("category");
            foreach (string mime in MimeTypes.Concat(extraMimes))
            {
                Start("data", StringAttr(mimeAttr, mime));
                End("data");
            }
            End("intent-filter");
        }
        End("activity");

        byte[] pool = WriteStringPool(strings, utf8);
        var output = new MemoryStream();
        var writer = new BinaryWriter(output);
        writer.Write(manifest, 0, 8);
        writer.Write(pool);
        writer.Write(ChunkResourceMap);
        writer.Write((ushort)8);
        writer.Write((uint)(8 + (resourceIds.Count * 4)));
        foreach (uint id in resourceIds) writer.Write(id);
        writer.Write(manifest, nodesStart, applicationEnd - nodesStart);
        writer.Write(nodes.ToArray());
        writer.Write(manifest, applicationEnd, manifest.Length - applicationEnd);

        byte[] result = output.ToArray();
        BitConverter.GetBytes((uint)result.Length).CopyTo(result, 4);
        return result;
    }

    private static List<string> ReadStrings(byte[] data, out int poolStart, out int poolSize, out bool utf8)
    {
        if (data.Length < 16 || ReadU16(data, 0) != 0x0003)
        {
            throw new InvalidDataException("not a binary XML file");
        }
        poolStart = ReadU16(data, 2);
        if (ReadU16(data, poolStart) != ChunkStringPool)
        {
            throw new InvalidDataException("string pool not where expected");
        }
        poolSize = (int)ReadU32(data, poolStart + 4);
        int count = (int)ReadU32(data, poolStart + 8);
        if (ReadU32(data, poolStart + 12) != 0)
        {
            throw new InvalidDataException("styled string pool not supported");
        }
        utf8 = (ReadU32(data, poolStart + 16) & 0x100) != 0;
        int stringsStart = poolStart + (int)ReadU32(data, poolStart + 20);

        var strings = new List<string>(count);
        for (int i = 0; i < count; i++)
        {
            int at = stringsStart + (int)ReadU32(data, poolStart + 28 + (i * 4));
            if (utf8)
            {
                at += (data[at] & 0x80) != 0 ? 2 : 1;               // length in characters
                int bytes = data[at++];
                if ((bytes & 0x80) != 0) bytes = ((bytes & 0x7F) << 8) | data[at++];
                strings.Add(Encoding.UTF8.GetString(data, at, bytes));
            }
            else
            {
                int chars = ReadU16(data, at);
                at += 2;
                if ((chars & 0x8000) != 0)
                {
                    chars = ((chars & 0x7FFF) << 16) | ReadU16(data, at);
                    at += 2;
                }
                strings.Add(Encoding.Unicode.GetString(data, at, chars * 2));
            }
        }
        return strings;
    }

    private static byte[] WriteStringPool(List<string> strings, bool utf8)
    {
        var body = new MemoryStream();
        var offsets = new List<uint>();
        foreach (string s in strings)
        {
            offsets.Add((uint)body.Length);
            if (utf8)
            {
                byte[] bytes = Encoding.UTF8.GetBytes(s);
                WriteUtf8Length(body, s.Length);
                WriteUtf8Length(body, bytes.Length);
                body.Write(bytes);
                body.WriteByte(0);
            }
            else
            {
                if (s.Length >= 0x8000)
                {
                    body.Write(BitConverter.GetBytes((ushort)(0x8000 | (s.Length >> 16))));
                }
                body.Write(BitConverter.GetBytes((ushort)(s.Length & 0xFFFF)));
                body.Write(Encoding.Unicode.GetBytes(s));
                body.Write(new byte[2]);
            }
        }
        while (body.Length % 4 != 0) body.WriteByte(0);

        int headerAndOffsets = 28 + (strings.Count * 4);
        var pool = new MemoryStream();
        var w = new BinaryWriter(pool);
        w.Write(ChunkStringPool);
        w.Write((ushort)28);
        w.Write((uint)(headerAndOffsets + body.Length));
        w.Write((uint)strings.Count);
        w.Write((uint)0);                       // no styles
        w.Write(utf8 ? 0x100u : 0u);            // "sorted" is deliberately not set: strings were appended
        w.Write((uint)headerAndOffsets);
        w.Write((uint)0);
        foreach (uint offset in offsets) w.Write(offset);
        w.Write(body.ToArray());
        return pool.ToArray();
    }

    private static void WriteUtf8Length(Stream stream, int length)
    {
        if (length > 0x7F)
        {
            stream.WriteByte((byte)(0x80 | (length >> 8)));
        }
        stream.WriteByte((byte)(length & 0xFF));
    }

    private static ushort ReadU16(byte[] data, int at) => BitConverter.ToUInt16(data, at);
    private static uint ReadU32(byte[] data, int at) => BitConverter.ToUInt32(data, at);
}
