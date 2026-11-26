using Hopkey.Core.Exclusion;
using Hopkey.Core.Native;

namespace Hopkey.Core.Indexing;

/// <summary>Raw MFT records → parent resolution → memoized exclusion → compact VolumeIndex (sorted by FRN).</summary>
internal static class IndexBuilder
{
    public static VolumeIndex BuildFromRaw(string rootPath, ulong rootFrn, RawRecords raw, ExclusionMatcher m, CancellationToken ct)
    {
        int n = raw.Count;
        // 1) sort by FRN
        var keys = new ulong[n];
        Array.Copy(raw.Frn, keys, n);
        var order = new int[n];
        for (int i = 0; i < n; i++) order[i] = i;
        Array.Sort(keys, order);
        ct.ThrowIfCancellationRequested();

        // 2) parent positions in sorted space; -1 = root; -2 = unresolved (orphan / metafile)
        var parentPos = new int[n];
        Parallel.For(0, n, k =>
        {
            ulong p = raw.ParentFrn[order[k]];
            if (p == rootFrn) { parentPos[k] = -1; return; }
            int pos = Array.BinarySearch(keys, p);
            parentPos[k] = pos >= 0 ? pos : -2;
        });
        ct.ThrowIfCancellationRequested();

        // 3) memoized exclusion resolution (iterative, top-down along parent chains)
        var state = new byte[n]; // 0 unknown, 1 keep, 2 drop
        var dirPath = new string?[n];
        var stack = new Stack<int>(64);
        string rootStr = rootPath;
        for (int i = 0; i < n; i++)
        {
            if (state[i] != 0) continue;
            int j = i;
            byte ps = 0;
            int guard = 0;
            while (true)
            {
                if (state[j] != 0) { ps = state[j]; break; }
                stack.Push(j);
                int pp = parentPos[j];
                if (pp == -1) { ps = 1; break; }       // reached root
                if (pp == -2 || ++guard > 512) { ps = 2; break; } // orphan or cycle
                j = pp;
            }
            while (stack.Count > 0)
            {
                int k = stack.Pop();
                if (ps == 2) { state[k] = 2; continue; }
                int src = order[k];
                uint attrs = raw.Attrs[src];
                bool isDir = (attrs & UsnNative.FILE_ATTRIBUTE_DIRECTORY) != 0;
                string? parentPath = parentPos[k] == -1 ? rootStr : dirPath[parentPos[k]];
                bool excluded = m.IsExcludedEntry(raw.Name(src), parentPath, isDir, (FileAttributes)attrs);
                state[k] = excluded ? (byte)2 : (byte)1;
                if (!excluded && isDir && parentPath != null)
                {
                    var nm = raw.Name(src);
                    dirPath[k] = parentPath.EndsWith('\\') ? string.Concat(parentPath.AsSpan(), nm) : string.Concat(parentPath.AsSpan(), "\\", nm);
                }
                ps = state[k];
            }
            if ((i & 0xFFFF) == 0) ct.ThrowIfCancellationRequested();
        }
        dirPath = null;

        // 4) compact
        int keep = 0;
        long arena = 4;
        for (int k = 0; k < n; k++) if (state[k] == 1) { keep++; arena += raw.NameLen[order[k]] + 1; }
        var newIndex = new int[n];
        int next = 1;
        for (int k = 0; k < n; k++) newIndex[k] = state[k] == 1 ? next++ : -1;

        var v = new VolumeIndex(rootPath, IndexSourceKind.Usn, keep + 1 + Math.Max(1024, keep / 20), (int)Math.Min(int.MaxValue - 64, arena + arena / 20 + 4096));
        v.AppendRawNoIndex(-1, rootPath.TrimEnd('\\'), IndexEntryFlags.Directory, rootFrn);
        for (int k = 0; k < n; k++)
        {
            if (state[k] != 1) continue;
            int src = order[k];
            int pp = parentPos[k];
            int parent = pp == -1 ? 0 : newIndex[pp];
            v.AppendRawNoIndex(parent, raw.Name(src), FlagsUtil.FromAttributes((FileAttributes)raw.Attrs[src]), raw.Frn[src]);
        }
        v.BaseCount = v.CountInternal;
        v.RebuildLinkMap();
        v.BuildPinyin();
        return v;
    }
}
